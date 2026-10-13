using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Driftwall.Scheduling;

namespace Driftwall.Core;

public enum UpdateState
{
    /// <summary>Nothing known yet.</summary>
    Idle,
    Checking,
    UpToDate,
    /// <summary>A newer release exists; nothing downloaded.</summary>
    Available,
    Downloading,
    /// <summary>Downloaded and verified; waiting for a good moment to install.</summary>
    Ready,
    Installing,
    Failed,
}

/// <summary>One published release, as far as the updater cares.</summary>
public sealed record UpdateInfo(
    Version Version,
    string Tag,
    string ReleaseUrl,
    string Notes,
    string InstallerName,
    string InstallerUrl,
    string PortableName,
    string PortableUrl,
    string? ChecksumsUrl);

/// <summary>
/// Keeps the app current from the project's GitHub releases.
/// <para>
/// The check is one request a day to the releases feed, nothing more; an app that does nothing
/// between wallpaper changes should not be polling for updates either. A new version is downloaded
/// to the data folder, its SHA-256 compared with the sums the release publishes, and, if the running
/// copy is signed, the new one must be signed by the same publisher. Installing then hands over to
/// the same setup wizard people download, run silently, which replaces the files and starts the app
/// again; a portable copy is swapped in place instead.
/// </para>
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string Repository = "projectmax-org/driftwall";
    public static readonly string ReleasesUrl = $"https://github.com/{Repository}/releases";

    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan RetryWhenReady = TimeSpan.FromMinutes(10);

    // HttpClient's own timeout ends once the headers are in. A download whose connection dies without
    // a reset would otherwise wait for ever, and hold up every check and install after it.
    private static readonly TimeSpan DownloadStallTimeout = TimeSpan.FromSeconds(60);

    private readonly SettingsStore _settings;
    private readonly HttpClient _http;
    private readonly Timer _timer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private UpdateState _state;
    private string? _message;
    private double _progress;
    private string? _downloadedPath;
    private Version? _notifiedVersion;
    private bool _disposed;
    private int _setupPending;

    public UpdateService(SettingsStore settings, HttpClient http)
    {
        _settings = settings;
        _http = http;
        _timer = new Timer(_ => _ = AutoCheckAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Raised on a worker thread whenever State, Message or Progress changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Set by the app: whether the main window is on screen (an update then waits).</summary>
    public Func<bool>? IsWindowVisible { get; set; }

    /// <summary>Set by the app: shuts the app down cleanly so the installer can replace it.</summary>
    public Action? ExitForUpdate { get; set; }

    /// <summary>Set by the app: tells the user a version is waiting when it cannot be installed unasked.</summary>
    public Action<UpdateInfo>? NotifyAvailable { get; set; }

    /// <summary>Set by the app: saves settings and collections before setup may stop the process.</summary>
    public Action? SaveBeforeUpdate { get; set; }

    public UpdateState State
    {
        get => _state;
        private set { _state = value; Changed?.Invoke(this, EventArgs.Empty); }
    }

    /// <summary>The newest release known, when it is newer than the running copy.</summary>
    public UpdateInfo? Available { get; private set; }

    /// <summary>One line for the user about the last thing that happened.</summary>
    public string? Message
    {
        get => _message;
        private set { _message = value; Changed?.Invoke(this, EventArgs.Empty); }
    }

    /// <summary>0 to 1 while downloading.</summary>
    public double Progress => _progress;

    public DateTimeOffset? LastChecked => _settings.Settings.LastUpdateCheckUtc;

    public static Version CurrentVersion
    {
        get
        {
            var v = typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    private static string ExecutablePath => Environment.ProcessPath ?? throw new InvalidOperationException("The executable path is unknown.");

    /// <summary>True when this copy was put here by the setup wizard, which then also does the updating.</summary>
    public static bool IsInstalled =>
        File.Exists(Path.Combine(Path.GetDirectoryName(ExecutablePath) ?? string.Empty, "unins000.exe"));

    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{6F2E9D0C-3B7A-4E15-9C4D-2A8F1B5D7E30}_is1";

    /// <summary>
    /// True when this copy was installed for all users, as the uninstall registration in HKLM records.
    /// The folder alone cannot tell: an administrator can install for everyone anywhere.
    /// </summary>
    public static bool IsInstalledForAllUsers
    {
        get
        {
            if (!IsInstalled) return false;
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(UninstallKey);
                if (key?.GetValue("InstallLocation") is not string location || location.Length == 0) return false;

                var registered = Path.GetFullPath(location).TrimEnd('\\');
                var here = Path.GetFullPath(Path.GetDirectoryName(ExecutablePath) ?? string.Empty).TrimEnd('\\');
                return string.Equals(registered, here, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log.Warn("Could not read the install registration.", ex);
                return false;
            }
        }
    }

    /// <summary>
    /// True when an update can go in without asking anyone: a per-user install, an all-users install
    /// running elevated, or a portable copy in a folder this account can write to. Anything else
    /// would raise a UAC prompt nobody asked for, or fail after the app had already closed.
    /// </summary>
    public static bool CanReplaceItself => IsInstalled || IsFolderWritable(Path.GetDirectoryName(ExecutablePath));

    /// <summary>Whether the setup wizard that installed this copy had the named task ticked, as Inno Setup records it.</summary>
    public static bool InstallerSelectedTask(string task)
    {
        if (!IsInstalled) return false;
        try
        {
            var hive = IsInstalledForAllUsers ? Microsoft.Win32.Registry.LocalMachine : Microsoft.Win32.Registry.CurrentUser;
            using var key = hive.OpenSubKey(UninstallKey);
            return key?.GetValue("Inno Setup: Selected Tasks") is string tasks &&
                   tasks.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Contains(task, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the tasks setup recorded.", ex);
            return false;
        }
    }

    public static bool CanInstallUnattended =>
        IsInstalled ? !IsInstalledForAllUsers || IsElevated : IsFolderWritable(Path.GetDirectoryName(ExecutablePath));

    private static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private static bool IsFolderWritable(string? directory)
    {
        if (string.IsNullOrEmpty(directory)) return false;
        try
        {
            var probe = Path.Combine(directory, $".driftwall-write-test-{Environment.ProcessId}.tmp");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Starts the daily check. Called once the app is up and the first wallpaper is on its way.</summary>
    public void Start()
    {
        CleanDownloads();
        _timer.Change(FirstCheckDelay, CheckInterval);
    }

    // ---------------- checking ----------------

    /// <summary>Asks GitHub for the newest release. Returns it when it is newer than this copy.</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false)) return Available;

        try
        {
            State = UpdateState.Checking;
            Message = "Checking for updates...";

            var latest = await FetchLatestAsync(ct).ConfigureAwait(false);
            _settings.Update(s => s.LastUpdateCheckUtc = DateTimeOffset.UtcNow);

            if (latest is null)
            {
                Available = null;
                State = UpdateState.UpToDate;
                Message = "No release found yet.";
                return null;
            }

            if (latest.Version <= CurrentVersion)
            {
                Available = null;
                DiscardDownload();
                State = UpdateState.UpToDate;
                Message = $"Driftwall {CurrentVersion} is the latest version.";
                return null;
            }

            // A download from an earlier check is still good if it is the same version.
            if (Available?.Version != latest.Version) DiscardDownload();
            Available = latest;
            State = _downloadedPath is not null ? UpdateState.Ready : UpdateState.Available;
            Message = _downloadedPath is not null
                ? $"Driftwall {latest.Version} is downloaded and ready to install."
                : $"Driftwall {latest.Version} is available.";
            return latest;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Only a cancellation the caller asked for is quiet. A timeout is a failure like any other.
            State = UpdateState.Idle;
            Message = null;
            return null;
        }
        catch (UnreadableReleaseException ex)
        {
            Log.Warn("Update check failed: " + ex.Message);
            State = UpdateState.Failed;
            Message = ex.Message;
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn("Update check failed.", ex);
            State = UpdateState.Failed;
            Message = "Could not reach GitHub to check for updates.";
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<UpdateInfo?> FetchLatestAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        // The small body is read as part of the request, so HttpClient's timeout covers it as well:
        // a connection that stalls half-way through cannot leave the check waiting for ever.
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
        var root = json.RootElement;

        // A pre-release tag such as v1.1.0-beta published as the latest release is not "no release",
        // and saying so would hide why nothing is offered.
        var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) throw new UnreadableReleaseException(tag);
        version = new Version(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build));

        string? installerName = null, installerUrl = null, portableName = null, portableUrl = null, sumsUrl = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? string.Empty;
                var url = asset.GetProperty("browser_download_url").GetString() ?? string.Empty;
                if (name.StartsWith("DriftwallSetup-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    installerName = name;
                    installerUrl = url;
                }
                else if (name.Equals("Driftwall.exe", StringComparison.OrdinalIgnoreCase))
                {
                    portableName = name;
                    portableUrl = url;
                }
                else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                {
                    sumsUrl = url;
                }
            }
        }

        if (installerUrl is null || portableUrl is null) return null;

        return new UpdateInfo(
            version, tag,
            root.TryGetProperty("html_url", out var html) ? html.GetString() ?? ReleasesUrl : ReleasesUrl,
            root.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty,
            installerName!, installerUrl, portableName!, portableUrl, sumsUrl);
    }

    // ---------------- downloading ----------------

    /// <summary>Downloads and verifies the release found by the last check.</summary>
    public async Task<bool> DownloadAsync(CancellationToken ct = default)
    {
        var info = Available;
        if (info is null) return false;
        if (_downloadedPath is not null && File.Exists(_downloadedPath)) return true;
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false)) return false;

        try
        {
            State = UpdateState.Downloading;
            _progress = 0;
            Message = $"Downloading Driftwall {info.Version}...";

            bool installed = IsInstalled;
            var name = installed ? info.InstallerName : info.PortableName;
            var url = installed ? info.InstallerUrl : info.PortableUrl;
            var target = Path.Combine(Paths.UpdatesDirectory, name);
            var partial = target + ".part";

            using (var stall = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? -1;

                // Started again with every chunk, so it limits a silence, not the whole download.
                stall.CancelAfter(DownloadStallTimeout);
                await using var network = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
                await using var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

                var buffer = new byte[1 << 16];
                long written = 0;
                int lastPercent = -1;
                int read;
                while ((read = await network.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
                {
                    stall.CancelAfter(DownloadStallTimeout);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    written += read;
                    if (total > 0)
                    {
                        int percent = (int)(written * 100 / total);
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            _progress = written / (double)total;
                            Changed?.Invoke(this, EventArgs.Empty);
                        }
                    }
                }
            }

            Message = "Verifying the download...";
            var problem = await VerifyAsync(partial, name, info.ChecksumsUrl, ct).ConfigureAwait(false);
            if (problem is not null)
            {
                TryDelete(partial);
                Log.Warn("Rejected update download: " + problem);
                State = UpdateState.Failed;
                Message = "The download did not verify: " + problem;
                return false;
            }

            if (File.Exists(target)) File.Delete(target);
            File.Move(partial, target);
            _downloadedPath = target;
            _progress = 1;
            State = UpdateState.Ready;
            Message = $"Driftwall {info.Version} is downloaded and ready to install.";
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Back to waiting quietly only when the caller cancelled. A stall or a timeout is a
            // failure: logged, and tried again on a later pass.
            State = UpdateState.Available;
            Message = $"Driftwall {info.Version} is available.";
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("Update download failed.", ex);
            State = UpdateState.Failed;
            Message = "The download failed. It will be tried again later.";
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The published SHA-256 must match, and if this copy carries a publisher's signature the new
    /// one must carry the same publisher's. Returns a reason when the file must not be used.
    /// </summary>
    private async Task<string?> VerifyAsync(string path, string name, string? checksumsUrl, CancellationToken ct)
    {
        if (checksumsUrl is null) return "the release publishes no checksums";

        var sums = await _http.GetStringAsync(checksumsUrl, ct).ConfigureAwait(false);
        string? expected = null;
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                expected = parts[0].Trim().ToLowerInvariant();
                break;
            }
        }
        if (expected is null) return $"no checksum is published for {name}";

        string actual;
        await using (var stream = File.OpenRead(path))
        {
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
        }
        if (actual != expected) return "its checksum does not match the one published with the release";

        // Once this copy is signed, a download must carry a signature Windows verifies, from the same
        // publisher. That holds after this copy's own certificate expires: having been signed at all
        // is what counts, so the check never quietly switches itself off.
        var currentPublisher = PublisherOf(ExecutablePath);
        if (currentPublisher is not null)
        {
            if (!HasValidSignature(path)) return "this copy is signed but the download has no valid signature";
            if (!string.Equals(PublisherOf(path), currentPublisher, StringComparison.Ordinal))
                return "the download is signed by someone else";
        }

        return null;
    }

    /// <summary>
    /// Whether Windows verifies a file's Authenticode signature: intact, chained to a trusted root,
    /// and valid when it was made, which a timestamp proves even once the certificate has expired.
    /// This is what stops a copied certificate, or a signature pasted onto another file, from passing.
    /// </summary>
    private static bool HasValidSignature(string path)
    {
        var file = new WintrustFileInfo { cbStruct = Marshal.SizeOf<WintrustFileInfo>(), pcwszFilePath = path };
        IntPtr filePointer = Marshal.AllocHGlobal(file.cbStruct);
        bool marshalled = false;
        try
        {
            Marshal.StructureToPtr(file, filePointer, false);
            marshalled = true;

            var data = new WintrustData
            {
                cbStruct = Marshal.SizeOf<WintrustData>(),
                dwUIChoice = 2,          // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                dwUnionChoice = 1,       // WTD_CHOICE_FILE
                pFile = filePointer,
                dwStateAction = 1,       // WTD_STATEACTION_VERIFY
                dwProvFlags = 0x40,      // WTD_REVOCATION_CHECK_NONE
            };

            int result = WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, ref data);
            data.dwStateAction = 2;      // WTD_STATEACTION_CLOSE
            WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, ref data);
            return result == 0;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not verify the signature of " + path, ex);
            return false;
        }
        finally
        {
            if (marshalled) Marshal.DestroyStructure<WintrustFileInfo>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WintrustData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustFileInfo
    {
        public int cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public int cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public int dwUIChoice;
        public int fdwRevocationChecks;
        public int dwUnionChoice;
        public IntPtr pFile;
        public int dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public int dwProvFlags;
        public int dwUIContext;
        public IntPtr pSignatureSettings;
    }

    /// <summary>
    /// The subject of the certificate in a file's Authenticode signature, or null when it has none.
    /// It says nothing about validity: <see cref="HasValidSignature"/> does that.
    /// </summary>
    private static string? PublisherOf(string path)
    {
        try
        {
            // Reads the leaf certificate out of the Authenticode signature. .NET marks this reader
            // obsolete in favour of X509CertificateLoader, which has no equivalent for signed files.
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return certificate.Subject;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    // ---------------- installing ----------------

    /// <summary>
    /// Replaces this copy with the downloaded one and starts it again. The app exits as part of
    /// this; the caller does not get control back on success.
    /// </summary>
    public bool Install(bool relaunchVisible)
    {
        var path = _downloadedPath;
        if (path is null || !File.Exists(path) || Available is null) return false;

        // One setup at a time: a second would raise a second approval prompt and race the first.
        if (Volatile.Read(ref _setupPending) != 0) return false;

        try
        {
            var exeDirectory = Path.GetDirectoryName(ExecutablePath);
            if (!IsInstalled && !IsFolderWritable(exeDirectory))
            {
                // Leaving now would only close the app: the swap script could not replace the file.
                State = UpdateState.Ready;
                Message = $"Driftwall cannot replace itself in {exeDirectory}. Download {Available.Version} from the releases page instead.";
                Log.Warn(Message);
                return false;
            }

            State = UpdateState.Installing;
            Message = $"Installing Driftwall {Available.Version}...";
            _settings.Save();
            SaveBeforeUpdate?.Invoke();

            if (IsInstalled)
            {
                bool allUsers = IsInstalledForAllUsers;

                // The wizard itself, silently. It stops this process, replaces the files, and the
                // RELAUNCH switch makes it start the new version the way the user last saw it.
                var arguments = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART {(allUsers ? "/ALLUSERS" : "/CURRENTUSER")} /RELAUNCH={(relaunchVisible ? 2 : 1)}";
                var setup = Process.Start(new ProcessStartInfo(path, arguments) { UseShellExecute = true });

                if (allUsers && !IsElevated)
                {
                    // Setup asks for administrator approval itself. Exiting now would leave nothing
                    // running if the prompt were refused, so the app stays up: an approved setup stops
                    // it, and a refused one simply ends, which WatchSetupAsync notices.
                    Log.Info($"Waiting for administrator approval to install the {Available.Version} update.");
                    Message = $"Installing Driftwall {Available.Version} needs administrator approval...";
                    Interlocked.Exchange(ref _setupPending, 1);
                    _ = WatchSetupAsync(setup, Available.Version);
                    return true;
                }
            }
            else
            {
                // A portable copy: a small script waits for this process to end, swaps the file and
                // starts the new one, or starts the old one again if the swap fails. cmd rather than
                // PowerShell so it needs no policy. The paths travel in environment variables, which
                // cmd receives as UTF-16; written into the script they would be read in the console
                // code page and garbled for a user name in Korean, say. The new file is copied in, not
                // moved: a move keeps the permissions it had in this user's profile, and another account
                // sharing the folder could then no longer start it. The script removes itself.
                var script = Path.Combine(Paths.UpdatesDirectory, "apply-update.cmd");
                File.WriteAllText(script, string.Join("\r\n", new[]
                {
                    "@echo off",
                    ":wait",
                    "tasklist /FI \"PID eq %DW_PID%\" 2>nul | find \" %DW_PID% \" >nul",
                    "if not errorlevel 1 (timeout /t 1 /nobreak >nul & goto wait)",
                    "move /y \"%DW_EXE%\" \"%DW_EXE%.old\" >nul || (start \"\" \"%DW_EXE%\" %DW_ARGS% & exit /b 1)",
                    "copy /y \"%DW_NEW%\" \"%DW_EXE%\" >nul || (move /y \"%DW_EXE%.old\" \"%DW_EXE%\" >nul & start \"\" \"%DW_EXE%\" %DW_ARGS% & exit /b 1)",
                    "del \"%DW_NEW%\" >nul 2>nul",
                    "start \"\" \"%DW_EXE%\" %DW_ARGS%",
                    "del \"%DW_EXE%.old\" >nul 2>nul",
                    "(goto) 2>nul & del \"%~f0\"",
                }) + "\r\n", Encoding.ASCII);

                // /s keeps the quotes around a script path with ( ) & ^ in it, as in "C:\Users\Max (Home)",
                // and /v:off stops a machine-wide delayed-expansion setting from eating ! in the paths.
                var start = new ProcessStartInfo("cmd.exe", $"/d /v:off /s /c \"\"{script}\"\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Paths.UpdatesDirectory,
                };
                start.Environment["DW_PID"] = Environment.ProcessId.ToString();
                start.Environment["DW_EXE"] = ExecutablePath;
                start.Environment["DW_NEW"] = path;
                start.Environment["DW_ARGS"] = relaunchVisible ? "--show" : "--minimized";
                Process.Start(start);
            }

            Log.Info($"Handing over to the {Available.Version} update.");
            ExitForUpdate?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not start the update.", ex);
            State = UpdateState.Ready;
            Message = "The update could not be started. See the log for details.";
            return false;
        }
    }

    /// <summary>Check, download and install in one go: what the button in Settings does.</summary>
    public async Task InstallLatestAsync(bool relaunchVisible, CancellationToken ct = default)
    {
        if (Available is null && await CheckAsync(ct).ConfigureAwait(false) is null) return;

        if (!CanReplaceItself)
        {
            // Downloading would only end in the same refusal, so say so before fetching anything.
            Message = $"Driftwall cannot replace itself in {Path.GetDirectoryName(ExecutablePath)}. Download {Available!.Version} from the releases page instead.";
            return;
        }

        if (!await DownloadAsync(ct).ConfigureAwait(false)) return;
        Install(relaunchVisible);
    }

    /// <summary>
    /// Follows a setup that is waiting for administrator approval. Approved, it stops this process
    /// before it finishes; refused, it ends with a failure code and the update is back to waiting.
    /// </summary>
    private async Task WatchSetupAsync(Process? setup, Version version)
    {
        int? exitCode = null;
        try
        {
            if (setup is null) return;
            using (setup)
            {
                await setup.WaitForExitAsync().ConfigureAwait(false);
                exitCode = setup.ExitCode;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not follow the update setup.", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _setupPending, 0);
        }

        if (exitCode == 0) return;

        // Refused approval and a failed install look the same from here; the code is what tells them apart.
        Log.Warn($"The {version} update setup ended with exit code {exitCode?.ToString() ?? "unknown"}.");
        State = UpdateState.Ready;
        Message = $"Driftwall {version} was not installed. Installing it for all users needs administrator approval; if it was given, see the log.";
    }

    // ---------------- the daily pass ----------------

    private async Task AutoCheckAsync()
    {
        if (_disposed) return;

        // Never overlap with a check, download or setup already under way, the user's or an earlier pass's.
        if (State is UpdateState.Installing or UpdateState.Checking or UpdateState.Downloading) return;

        try
        {
            if (!SystemConditions.HasNetwork()) return;

            var settings = _settings.Settings;
            var info = State == UpdateState.Ready ? Available : await CheckAsync().ConfigureAwait(false);
            if (info is null) return;

            if (!settings.AutoUpdate || !CanInstallUnattended)
            {
                // An all-users install needs administrator approval, and a portable copy in a folder
                // it cannot write to cannot swap itself. Say a version is waiting; Settings installs it.
                TellOnce(info);
                return;
            }

            if (settings.PauseOnMeteredNetwork && SystemConditions.IsNetworkMetered())
            {
                Message = $"Driftwall {info.Version} is available; it will be downloaded on an unmetered connection.";
                _timer.Change(RetryWhenReady, CheckInterval);
                return;
            }

            if (State != UpdateState.Ready && !await DownloadAsync().ConfigureAwait(false)) return;

            // Never pull the app out from under someone who is using it or playing something. Only the
            // open window is worth a word: the notice says closing it installs the update. A game asks
            // nothing of anyone, as the retry installs the update once it is over.
            bool windowOpen = IsWindowVisible?.Invoke() == true;
            if (windowOpen || SystemConditions.IsFullscreenAppRunning())
            {
                if (windowOpen) TellOnce(info);
                _timer.Change(RetryWhenReady, CheckInterval);
                return;
            }

            Install(relaunchVisible: false);
        }
        catch (Exception ex)
        {
            Log.Warn("Automatic update pass failed.", ex);
        }
    }

    /// <summary>Called by the app when the window goes away: the moment a waiting update may go in.</summary>
    public void OnWindowHidden()
    {
        if (State != UpdateState.Ready || !_settings.Settings.AutoUpdate || !CanInstallUnattended) return;
        if (SystemConditions.IsFullscreenAppRunning()) return;
        Install(relaunchVisible: false);
    }

    private void TellOnce(UpdateInfo info)
    {
        if (_notifiedVersion == info.Version) return;
        _notifiedVersion = info.Version;
        NotifyAvailable?.Invoke(info);
    }

    // ---------------- housekeeping ----------------

    private void DiscardDownload()
    {
        if (_downloadedPath is not null) TryDelete(_downloadedPath);
        _downloadedPath = null;
        _progress = 0;
    }

    /// <summary>
    /// Leftovers from earlier updates: partial files, superseded downloads, the swap script. Only
    /// what is older than a few minutes, so the script that just started this very process, and may
    /// still be on its last lines, is left alone.
    /// </summary>
    private static void CleanDownloads()
    {
        try
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(5);
            foreach (var file in Directory.EnumerateFiles(Paths.UpdatesDirectory))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff) TryDelete(file);
            }

            var old = ExecutablePath + ".old";
            if (File.Exists(old)) TryDelete(old);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not clean the updates folder.", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Log.Warn("Could not delete " + path, ex); }
    }

    /// <summary>The latest release has a tag this copy cannot read as a version.</summary>
    private sealed class UnreadableReleaseException(string tag)
        : Exception($"The latest release ({tag}) has a version this copy cannot read.");


    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
        _gate.Dispose();
    }
}
