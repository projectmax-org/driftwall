using System.Runtime.InteropServices;
using System.Windows.Media;
using Driftwall.Core;
using static Driftwall.Wallpaper.NativeMethods;

namespace Driftwall.Wallpaper;

/// <summary>What ended up on one monitor, kept so the UI can show it and "refresh" can redo it.</summary>
public sealed record MonitorAssignment(MonitorInfo Monitor, PhotoItem Photo, string RenderedPath);

/// <summary>The complete result of one wallpaper change.</summary>
public sealed record WallpaperApplication(IReadOnlyList<MonitorAssignment> Assignments, DateTimeOffset AppliedAt)
{
    public PhotoItem? PrimaryPhoto =>
        Assignments.FirstOrDefault(a => a.Monitor.IsPrimary)?.Photo ?? Assignments.FirstOrDefault()?.Photo;
}

/// <summary>
/// Renders photos to each monitor's exact pixel size and hands them to Windows.
/// <para>
/// Everything runs off the UI thread, one change at a time. Applying is serialised through a
/// semaphore because two overlapping changes would fight over the same rendered files, and because
/// doing more than one at once is never what the user wants.
/// </para>
/// </summary>
public sealed class WallpaperEngine
{
    /// <summary>
    /// The longest one change may hold the apply lock. Several callers pass no token of their own, and
    /// a download that stalls mid-way would otherwise hold it for good, so every later change waits.
    /// </summary>
    private static readonly TimeSpan ApplyDeadline = TimeSpan.FromMinutes(3);

    private readonly ImageCache _cache;
    private readonly SemaphoreSlim _applyLock = new(1, 1);

    private IReadOnlyList<MonitorInfo>? _monitors;

    /// <summary>
    /// The last file set through the empty id. Windows does not reliably report it back once the
    /// displays show different pictures, so the clean-up keeps it from here instead. Restored from
    /// <see cref="UnnamedRecordFile"/> at start-up.
    /// </summary>
    private string? _unnamedPath;
    private long _renderSequence;

    public WallpaperEngine(ImageCache cache)
    {
        _cache = cache;

        // Carry on from the highest render number on disk. Starting again at 1 would reuse a path that
        // may still be on screen from the last run, and Windows can ignore a change to the same path.
        _renderSequence = HighestRenderSequence();

        // The display reached only through the empty id may still show a file from the last run.
        _unnamedPath = ReadUnnamedRecord();
    }

    /// <summary>The most recent successful application, or null if nothing has been set this session.</summary>
    public WallpaperApplication? Current { get; private set; }

    public event EventHandler<WallpaperApplication>? Applied;

    /// <summary>Connected displays, cached until <see cref="InvalidateMonitors"/> is called.</summary>
    public IReadOnlyList<MonitorInfo> Monitors => _monitors ??= MonitorEnumerator.Enumerate();

    /// <summary>Called when Windows reports a display change so the next apply re-detects monitors.</summary>
    public void InvalidateMonitors()
    {
        _monitors = null;
        Log.Info("Display configuration changed; monitor list invalidated.");
    }

    /// <summary>Monitors the user has not switched off in settings.</summary>
    public IReadOnlyList<MonitorInfo> ActiveMonitors(AppSettings settings)
    {
        var disabled = settings.Monitors
            .Where(m => !m.Enabled)
            .Select(m => m.MonitorId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var active = Monitors.Where(m => !disabled.Contains(m.Id)).ToList();
        return active.Count > 0 ? active : Monitors;
    }

    /// <summary>
    /// Applies photos to the desktop. <paramref name="photoForMonitor"/> is asked for a photo per
    /// monitor; returning null for a monitor leaves that monitor untouched.
    /// </summary>
    public async Task<WallpaperApplication?> ApplyAsync(
        Func<MonitorInfo, PhotoItem?> photoForMonitor, AppSettings settings, CancellationToken ct)
    {
        await _applyLock.WaitAsync(ct).ConfigureAwait(false);

        // From here on the change is bounded whatever the caller passed: see ApplyDeadline.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ApplyDeadline);
        var token = deadline.Token;

        int monitorCount = 0;
        try
        {
            var monitors = ActiveMonitors(settings);
            monitorCount = monitors.Count;
            if (monitors.Count == 0) return null;

            var application = settings.MultiMonitor == MultiMonitorMode.SpanAcrossMonitors
                ? await ApplySpanAsync(photoForMonitor(monitors[0]), monitors, settings, token).ConfigureAwait(false)
                : await ApplyPerMonitorAsync(photoForMonitor, monitors, settings, token).ConfigureAwait(false);

            if (application is null || application.Assignments.Count == 0)
            {
                // A change no display took may still have rendered its files. They are swept like any
                // other old render; what is on screen stays protected, see WallpapersInUse.
                CleanupRenderDirectory(monitors.Count);
                return null;
            }

            Current = application;
            Applied?.Invoke(this, application);
            CleanupRenderDirectory(monitors.Count);
            return application;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // The caller did not cancel, so this is a change that failed rather than one called off.
            Log.Warn($"Applying the wallpaper took longer than {ApplyDeadline.TotalMinutes:0} minutes, so it was abandoned.");
            if (monitorCount > 0) CleanupRenderDirectory(monitorCount);
            return null;
        }
        finally
        {
            _applyLock.Release();
            MemoryTrimmer.RequestCollect();
        }
    }

    private async Task<WallpaperApplication?> ApplyPerMonitorAsync(
        Func<MonitorInfo, PhotoItem?> photoForMonitor, IReadOnlyList<MonitorInfo> monitors,
        AppSettings settings, CancellationToken ct)
    {
        var assignments = new List<MonitorAssignment>(monitors.Count);
        long sequence = Interlocked.Increment(ref _renderSequence);

        // In SameOnAllMonitors the first monitor's photo is reused, but each monitor still gets its
        // own render so a 4K display is not fed a 1080p file, and a portrait display is not cropped
        // to a landscape monitor's framing.
        PhotoItem? shared = settings.MultiMonitor == MultiMonitorMode.SameOnAllMonitors
            ? photoForMonitor(monitors[0])
            : null;

        foreach (var monitor in monitors)
        {
            ct.ThrowIfCancellationRequested();

            var photo = shared ?? photoForMonitor(monitor);
            if (photo is null) continue;

            var sourcePath = await _cache.GetFullAsync(photo, ct).ConfigureAwait(false);
            if (sourcePath is null)
            {
                Log.Warn("No local file available for " + photo.Key);
                continue;
            }

            var outputPath = RenderPath(monitor, sequence);
            var scaling = ScalingFor(monitor, settings);

            bool composed = await ImageComposer.ComposeAsync(
                sourcePath, outputPath, monitor.Width, monitor.Height,
                scaling, settings.LetterboxStyle, ParseColor(settings.LetterboxColor), ct).ConfigureAwait(false);

            if (!composed) continue;

            assignments.Add(new MonitorAssignment(monitor, photo, outputPath));
        }

        if (assignments.Count == 0) return null;

        // A cancelled change never reaches the desktop, even when every render finished.
        ct.ThrowIfCancellationRequested();
        var applied = SetWallpaperFiles(assignments);
        if (applied.Count == 0) return null;

        NotifyProviders(applied);
        return new WallpaperApplication(applied, DateTimeOffset.Now);
    }

    private async Task<WallpaperApplication?> ApplySpanAsync(
        PhotoItem? photo, IReadOnlyList<MonitorInfo> monitors, AppSettings settings, CancellationToken ct)
    {
        if (photo is null) return null;

        var sourcePath = await _cache.GetFullAsync(photo, ct).ConfigureAwait(false);
        if (sourcePath is null) return null;

        long sequence = Interlocked.Increment(ref _renderSequence);
        var paths = monitors.ToDictionary(m => m.Id, m => RenderPath(m, sequence), StringComparer.OrdinalIgnoreCase);

        // The photo is composed once across the whole virtual desktop and then sliced per monitor.
        // Slicing ourselves rather than using the shell's SPAN position keeps it correct when the
        // displays have different DPI, which is where the built-in span visibly misaligns.
        bool composed = await ImageComposer.ComposeSpanAsync(
            sourcePath, monitors, m => paths[m.Id],
            settings.Scaling, settings.LetterboxStyle, ParseColor(settings.LetterboxColor), ct).ConfigureAwait(false);

        if (!composed) return null;

        var assignments = monitors.Select(m => new MonitorAssignment(m, photo, paths[m.Id])).ToList();
        ct.ThrowIfCancellationRequested();
        var applied = SetWallpaperFiles(assignments);
        if (applied.Count == 0) return null;

        NotifyProviders(applied);
        return new WallpaperApplication(applied, DateTimeOffset.Now);
    }

    // ---------------- talking to Windows ----------------

    /// <summary>
    /// Hands the files to Windows and returns the assignments it took. A display whose call failed, or
    /// that had to be left as it was, is not reported as changed.
    /// </summary>
    private List<MonitorAssignment> SetWallpaperFiles(IReadOnlyList<MonitorAssignment> assignments)
    {
        var applied = assignments.ToList();
        IDesktopWallpaper? shell = null;
        try
        {
            shell = (IDesktopWallpaper)new DesktopWallpaperClass();

            // Every file already matches its monitor exactly, so Fill is a no-op that also protects
            // against Windows deciding to letterbox after a resolution change.
            try { shell.SetPosition(DesktopWallpaperPosition.Fill); }
            catch (COMException ex) { Log.Warn("SetPosition failed.", ex); }

            // Displays with no shell id at all can only take the single-image legacy API, which covers
            // the whole desktop; calling it once per display would leave only the last one standing.
            var legacy = assignments.Where(a => a.Monitor.ShellId is null).OrderByDescending(a => a.Monitor.IsPrimary).FirstOrDefault();
            if (legacy is not null && !ApplyLegacy(legacy.RenderedPath))
                applied.RemoveAll(a => a.Monitor.ShellId is null);

            // Every display the shell knows by its own id, with the file it should end up showing.
            var targets = assignments
                .Where(a => !string.IsNullOrEmpty(a.Monitor.ShellId))
                .Select(a => (Id: a.Monitor.ShellId!, Path: a.RenderedPath, Name: a.Monitor.Name))
                .ToList();

            // A display the shell lists without a device path can only be reached with an empty id,
            // and Windows applies that to every display. So it goes first, and every other display is
            // then set by its own id: the ones in this change get their new file, the ones it leaves
            // alone get back what they showed before.
            var unnamed = assignments.FirstOrDefault(a => a.Monitor.ShellId == string.Empty);
            bool landed = true;
            if (unnamed is not null)
            {
                var before = CurrentWallpapersByShellId(shell);
                var assigned = targets.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var leftAlone = Monitors.Where(m => !string.IsNullOrEmpty(m.ShellId) && !assigned.Contains(m.ShellId)).ToList();

                // A display this change leaves alone would be overwritten too. If what it shows now
                // cannot be put back (a solid colour, a slideshow, a file since deleted), the display
                // the user chose to leave alone matters more than this change to the unnamed one.
                var stranded = leftAlone.Where(m => !before.TryGetValue(m.ShellId!, out var p) || !File.Exists(p)).ToList();
                if (stranded.Count > 0)
                {
                    Log.Warn($"{unnamed.Monitor.Name} was not changed: Windows can reach it only together with every display, " +
                             $"and what {string.Join(", ", stranded.Select(m => m.Name))} shows now could not be put back afterwards.");
                    unnamed = null;
                }
                else
                {
                    targets.AddRange(leftAlone.Select(m => (Id: m.ShellId!, Path: before[m.ShellId!], Name: m.Name)));
                    if (assignments.Count(a => a.Monitor.ShellId == string.Empty) > 1)
                        Log.Warn("More than one display is listed without a device path; they all show the same photo.");

                    try
                    {
                        shell.SetWallpaper(string.Empty, unnamed.RenderedPath);
                        _unnamedPath = unnamed.RenderedPath;
                        RecordUnnamed(unnamed.RenderedPath);

                        // Explorer applies an empty-id call a moment after it returns. A display set by
                        // its own id before then is overwritten by it, so wait until it has landed.
                        landed = WaitForUnnamedToLand(shell, unnamed.RenderedPath, TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"SetWallpaper failed for {unnamed.Monitor.Name}.", ex);
                        unnamed = null;
                    }
                }

                // Left as it was, so not reported as changed.
                if (unnamed is null) applied.RemoveAll(a => a.Monitor.ShellId == string.Empty);
            }

            var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            SetEach(shell, targets, failed);

            if (unnamed is not null)
            {
                if (!landed)
                {
                    // It is still on its way. When it lands it covers the displays just set, so wait a
                    // while longer and set them again if it does.
                    Log.Warn("Windows took more than 5 seconds to apply the wallpaper of the display it lists without a device path.");
                    if (WaitForUnnamedToLand(shell, unnamed.RenderedPath, TimeSpan.FromSeconds(10))) SetEach(shell, targets, failed);
                }

                // A last look: any display still showing something else is set once more.
                foreach (var (id, path, name) in targets)
                {
                    try
                    {
                        if (!string.Equals(shell.GetWallpaper(id), path, StringComparison.OrdinalIgnoreCase))
                        {
                            // It shows something else, so it has not changed unless this call goes through.
                            failed.Add(id);
                            Thread.Sleep(300);
                            shell.SetWallpaper(id, path);
                        }

                        failed.Remove(id);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Could not confirm the wallpaper of {name}.", ex);
                    }
                }
            }

            applied.RemoveAll(a => !string.IsNullOrEmpty(a.Monitor.ShellId) && failed.Contains(a.Monitor.ShellId));
        }
        catch (Exception ex)
        {
            // Pre-Windows 8, or a broken shell. One wallpaper for everything is the only option left,
            // and it either reaches every display or none.
            Log.Warn("IDesktopWallpaper unavailable; falling back to SystemParametersInfo.", ex);
            bool reached = assignments.Count > 0 && ApplyLegacy(assignments[0].RenderedPath);
            applied = reached ? assignments.ToList() : new List<MonitorAssignment>();
        }
        finally
        {
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }

        return applied;
    }

    /// <summary>
    /// Sets each display by its own id; one failing does not stop the rest. <paramref name="failed"/>
    /// holds each id's latest outcome, so a later attempt that succeeds clears an earlier failure.
    /// </summary>
    private static void SetEach(IDesktopWallpaper shell, IEnumerable<(string Id, string Path, string Name)> targets, HashSet<string> failed)
    {
        foreach (var (id, path, name) in targets)
        {
            // Failed HRESULTs arrive as several exception types. Only this display is affected, so the
            // single-image legacy path, which would cover all of them, is not used here.
            try
            {
                shell.SetWallpaper(id, path);
                failed.Remove(id);
            }
            catch (Exception ex)
            {
                Log.Warn($"SetWallpaper failed for {name}.", ex);
                failed.Add(id);
            }
        }
    }

    /// <summary>
    /// Waits until a display with its own id reports <paramref name="path"/>: the sign that an
    /// empty-id SetWallpaper, which covers every display, has been applied. False on time-out.
    /// </summary>
    private bool WaitForUnnamedToLand(IDesktopWallpaper shell, string path, TimeSpan timeout)
    {
        var named = Monitors.FirstOrDefault(m => !string.IsNullOrEmpty(m.ShellId));
        if (named is null) return true;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            try
            {
                if (string.Equals(shell.GetWallpaper(named.ShellId!), path, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (Exception)
            {
                // Asked again below.
            }

            Thread.Sleep(100);
        }

        return false;
    }

    /// <summary>What each named display shows now, by the id the shell knows it by.</summary>
    private Dictionary<string, string> CurrentWallpapersByShellId(IDesktopWallpaper shell)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var monitor in Monitors)
        {
            if (string.IsNullOrEmpty(monitor.ShellId)) continue;
            try
            {
                var path = shell.GetWallpaper(monitor.ShellId);
                if (!string.IsNullOrEmpty(path)) map[monitor.ShellId] = path;
            }
            catch (Exception)
            {
                // Nothing of its own to put back.
            }
        }

        return map;
    }

    /// <summary>Sets one wallpaper for the whole desktop. False when Windows refused it.</summary>
    private static bool ApplyLegacy(string path)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", writable: true);
            key?.SetValue("WallpaperStyle", "10"); // fill
            key?.SetValue("TileWallpaper", "0");
        }
        catch (Exception ex)
        {
            Log.Warn("Could not write wallpaper style to the registry.", ex);
        }

        if (SystemParametersInfoW(SPI_SETDESKWALLPAPER, 0, path, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE)) return true;

        Log.Error("SystemParametersInfo(SPI_SETDESKWALLPAPER) failed for " + path);
        return false;
    }

    /// <summary>Fires any usage pings a provider's terms require (Unsplash in particular).</summary>
    private static void NotifyProviders(IEnumerable<MonitorAssignment> assignments)
    {
        foreach (var photo in assignments.Select(a => a.Photo).DistinctBy(p => p.Key))
        {
            if (string.IsNullOrEmpty(photo.DownloadTrackUrl)) continue;
            Net.TrackDownload(Net.Client, photo.DownloadTrackUrl);
        }
    }

    // ---------------- helpers ----------------

    /// <summary>The scaling mode a monitor uses: its own override, or the global setting.</summary>
    public static ScalingMode ScalingFor(MonitorInfo monitor, AppSettings settings) =>
        settings.Monitors.FirstOrDefault(m => string.Equals(m.MonitorId, monitor.Id, StringComparison.OrdinalIgnoreCase))
            ?.ScalingOverride ?? settings.Scaling;

    /// <summary>
    /// Each apply writes to a new file name. Windows caches the wallpaper by path and will sometimes
    /// ignore a change when the path is unchanged, so reusing one name makes rotation look broken.
    /// <para>
    /// The process id keeps two processes apart (the app and the self-test can run at once), and the
    /// sequence, seeded from disk, keeps one process from reusing a path from an earlier run.
    /// </para>
    /// </summary>
    private static string RenderPath(MonitorInfo monitor, long sequence) =>
        Path.Combine(Paths.RenderDirectory, $"m{monitor.Index}-{Environment.ProcessId}-{sequence}{ImageComposer.OutputExtension}");

    private void CleanupRenderDirectory(int monitorCount)
    {
        try
        {
            var inUse = WallpapersInUse();
            var directory = new DirectoryInfo(Paths.RenderDirectory);
            var files = directory.GetFiles("*" + ImageComposer.OutputExtension);
            int keep = Math.Max(monitorCount * 2, 4);

            foreach (var file in files.OrderByDescending(f => f.LastWriteTimeUtc).Skip(keep))
            {
                // The two most recent generations are kept: Windows may still have the previous
                // wallpaper file open while it transitions.
                if (inUse.Contains(file.FullName)) continue;
                try { file.Delete(); } catch { /* it will be caught by the next sweep */ }
            }

            // Another Driftwall process can be writing here too: an older version still installed, or
            // the self-test. Its newest files may not have reached Windows yet, so they are left alone.
            var settled = DateTime.UtcNow - TimeSpan.FromMinutes(10);

            // Versions before 1.0.2 rendered JPEG. Each goes once no display shows it any more.
            foreach (var legacy in directory.GetFiles("*.jpg"))
            {
                if (inUse.Contains(legacy.FullName) || legacy.LastWriteTimeUtc > settled) continue;
                try { legacy.Delete(); } catch { /* still open; the next sweep gets it */ }
            }

            // Half-written files from an encode that was interrupted: an exit, an update, a crash.
            foreach (var partial in directory.GetFiles("*.tmp"))
            {
                if (partial.LastWriteTimeUtc > settled) continue;
                try { partial.Delete(); } catch { /* the next sweep gets it */ }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not clean the render directory.", ex);
        }
    }

    /// <summary>
    /// Every file Windows reports as a monitor's wallpaper, plus this session's latest renders. A
    /// display this change did not render (switched off in Displays, or its photo failed) is still
    /// showing its old file, and deleting that file could leave it blank the next time Windows reloads it.
    /// </summary>
    private HashSet<string> WallpapersInUse()
    {
        var inUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Current is not null)
        {
            foreach (var assignment in Current.Assignments)
                inUse.Add(Path.GetFullPath(assignment.RenderedPath));
        }

        // Only while a display is still reached through the empty id: once Windows lists it by its own
        // path again, that old render is just another file to sweep.
        if (_unnamedPath is not null && Monitors.Any(m => m.ShellId == string.Empty)) inUse.Add(Path.GetFullPath(_unnamedPath));

        IDesktopWallpaper? shell = null;
        try
        {
            shell = (IDesktopWallpaper)new DesktopWallpaperClass();
            foreach (var monitor in Monitors)
            {
                // An empty shell id is a real entry here: the display the shell lists without a path.
                if (monitor.ShellId is null) continue;
                try
                {
                    var path = shell.GetWallpaper(monitor.ShellId);
                    if (!string.IsNullOrEmpty(path)) inUse.Add(Path.GetFullPath(path));
                }
                catch (Exception)
                {
                    // A monitor without a wallpaper of its own, or a path Windows reports oddly.
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not ask Windows which wallpapers are in use.", ex);
        }
        finally
        {
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }

        return inUse;
    }

    private static long HighestRenderSequence()
    {
        long highest = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(Paths.RenderDirectory))
            {
                // Render files are named m{monitor}-{process}-{sequence}.{extension}; before 1.0.2,
                // m{monitor}-{sequence}.jpg. Either way the sequence follows the last dash.
                var name = Path.GetFileNameWithoutExtension(file);
                int dash = name.LastIndexOf('-');
                if (name.StartsWith('m') && dash > 0 && long.TryParse(name.AsSpan(dash + 1), out long sequence) && sequence > highest)
                    highest = sequence;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the render directory.", ex);
        }

        return highest;
    }

    /// <summary>
    /// Where the last file set through the empty id is remembered between runs. Windows does not report
    /// it back, so after a restart nothing else would keep the clean-up away from it. The clean-up only
    /// deletes images and half-written files, so this one stays.
    /// </summary>
    private static string UnnamedRecordFile => Path.Combine(Paths.RenderDirectory, "unnamed-display.txt");

    private static string? ReadUnnamedRecord()
    {
        try
        {
            if (!File.Exists(UnnamedRecordFile)) return null;

            var path = File.ReadAllText(UnnamedRecordFile).Trim();
            return path.Length > 0 && File.Exists(path) ? path : null;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read which file the display without a device path shows.", ex);
            return null;
        }
    }

    private static void RecordUnnamed(string path)
    {
        try
        {
            File.WriteAllText(UnnamedRecordFile, path);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not record which file the display without a device path shows.", ex);
        }
    }

    public static Color ParseColor(string value)
    {
        try
        {
            if (ColorConverter.ConvertFromString(value) is Color color) return color;
        }
        catch
        {
            // Fall through to the default below.
        }
        return Color.FromRgb(0x10, 0x10, 0x14);
    }
}
