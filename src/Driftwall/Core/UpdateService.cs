using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
        catch (OperationCanceledException)
        {
            State = UpdateState.Idle;
            Message = null;
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

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
        var root = json.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
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

            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? -1;

                await using var network = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

                var buffer = new byte[1 << 16];
                long written = 0;
                int lastPercent = -1;
                int read;
                while ((read = await network.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
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
        catch (OperationCanceledException)
        {
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

        var currentSigner = SignerOf(ExecutablePath);
        if (currentSigner is not null)
        {
            var newSigner = SignerOf(path);
            if (newSigner is null) return "this copy is signed but the download is not";
            if (!string.Equals(newSigner, currentSigner, StringComparison.Ordinal))
                return "the download is signed by someone else";
        }

        return null;
    }

    /// <summary>
    /// The subject of a file's Authenticode signer, or null when the file is unsigned or the
    /// certificate does not chain to a root Windows trusts. The chain check is what stops a
    /// self-made certificate with a copied name from passing as the same publisher.
    /// </summary>
    private static string? SignerOf(string path)
    {
        try
        {
            // Reads the leaf certificate out of the Authenticode signature. .NET marks this reader
            // obsolete in favour of X509CertificateLoader, which has no equivalent for signed files.
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057

            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.3")); // code signing
            return chain.Build(certificate) ? certificate.Subject : null;
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

        try
        {
            State = UpdateState.Installing;
            Message = $"Installing Driftwall {Available.Version}...";
            _settings.Save();

            if (IsInstalled)
            {
                var exeDir = Path.GetDirectoryName(ExecutablePath) ?? string.Empty;
                bool allUsers = IsUnder(exeDir, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
                             || IsUnder(exeDir, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));

                // The wizard itself, silently. It stops this process, replaces the files, and the
                // RELAUNCH switch makes it start the new version the way the user last saw it.
                var arguments = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART {(allUsers ? "/ALLUSERS" : "/CURRENTUSER")} /RELAUNCH={(relaunchVisible ? 2 : 1)}";
                Process.Start(new ProcessStartInfo(path, arguments) { UseShellExecute = true });
            }
            else
            {
                // A portable copy: a small script waits for this process to end, swaps the file
                // and starts the new one. cmd rather than PowerShell so it needs no policy. A batch
                // file treats % specially, so any in a path is doubled; quotes cannot occur in
                // Windows paths. The script removes itself on its last line.
                var exe = ExecutablePath;
                var script = Path.Combine(Paths.UpdatesDirectory, "apply-update.cmd");
                var launch = relaunchVisible ? " --show" : " --minimized";
                string B(string s) => s.Replace("%", "%%");
                File.WriteAllText(script, string.Join("\r\n", new[]
                {
                    "@echo off",
                    ":wait",
                    $"tasklist /FI \"PID eq {Environment.ProcessId}\" 2>nul | find \" {Environment.ProcessId} \" >nul",
                    "if not errorlevel 1 (timeout /t 1 /nobreak >nul & goto wait)",
                    $"move /y \"{B(exe)}\" \"{B(exe)}.old\" >nul || exit /b 1",
                    $"move /y \"{B(path)}\" \"{B(exe)}\" >nul || (move /y \"{B(exe)}.old\" \"{B(exe)}\" >nul & exit /b 1)",
                    $"start \"\" \"{B(exe)}\"{launch}",
                    $"del \"{B(exe)}.old\" >nul 2>nul",
                    "(goto) 2>nul & del \"%~f0\"",
                }) + "\r\n", Encoding.Default);

                Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c \"{script}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Paths.UpdatesDirectory,
                });
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
        if (!await DownloadAsync(ct).ConfigureAwait(false)) return;
        Install(relaunchVisible);
    }

    // ---------------- the daily pass ----------------

    private async Task AutoCheckAsync()
    {
        if (_disposed) return;

        try
        {
            if (!SystemConditions.HasNetwork()) return;

            var settings = _settings.Settings;
            var info = State == UpdateState.Ready ? Available : await CheckAsync().ConfigureAwait(false);
            if (info is null) return;

            if (!settings.AutoUpdate)
            {
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

            // Never pull the app out from under someone who is using it or playing something.
            if (IsWindowVisible?.Invoke() == true || SystemConditions.IsFullscreenAppRunning())
            {
                TellOnce(info);
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
        if (State != UpdateState.Ready || !_settings.Settings.AutoUpdate) return;
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

    private static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrEmpty(root)) return false;
        var full = Path.GetFullPath(path).TrimEnd('\\') + "\\";
        var rootFull = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
        _gate.Dispose();
    }
}
