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
    private readonly ImageCache _cache;
    private readonly SemaphoreSlim _applyLock = new(1, 1);

    private IReadOnlyList<MonitorInfo>? _monitors;
    private long _renderSequence;

    public WallpaperEngine(ImageCache cache) => _cache = cache;

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
        try
        {
            var monitors = ActiveMonitors(settings);
            if (monitors.Count == 0) return null;

            var application = settings.MultiMonitor == MultiMonitorMode.SpanAcrossMonitors
                ? await ApplySpanAsync(photoForMonitor(monitors[0]), monitors, settings, ct).ConfigureAwait(false)
                : await ApplyPerMonitorAsync(photoForMonitor, monitors, settings, ct).ConfigureAwait(false);

            if (application is null || application.Assignments.Count == 0) return null;

            Current = application;
            Applied?.Invoke(this, application);
            CleanupRenderDirectory(monitors.Count);
            return application;
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
                scaling, settings.LetterboxStyle, ParseColor(settings.LetterboxColor),
                settings.OutputQuality, ct).ConfigureAwait(false);

            if (!composed) continue;

            assignments.Add(new MonitorAssignment(monitor, photo, outputPath));
        }

        if (assignments.Count == 0) return null;

        SetWallpaperFiles(assignments);
        NotifyProviders(assignments);
        return new WallpaperApplication(assignments, DateTimeOffset.Now);
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
            settings.Scaling, settings.LetterboxStyle, ParseColor(settings.LetterboxColor),
            settings.OutputQuality, ct).ConfigureAwait(false);

        if (!composed) return null;

        var assignments = monitors.Select(m => new MonitorAssignment(m, photo, paths[m.Id])).ToList();
        SetWallpaperFiles(assignments);
        NotifyProviders(assignments);
        return new WallpaperApplication(assignments, DateTimeOffset.Now);
    }

    // ---------------- talking to Windows ----------------

    private void SetWallpaperFiles(IReadOnlyList<MonitorAssignment> assignments)
    {
        IDesktopWallpaper? shell = null;
        try
        {
            shell = (IDesktopWallpaper)new DesktopWallpaperClass();

            // Every file already matches its monitor exactly, so Fill is a no-op that also protects
            // against Windows deciding to letterbox after a resolution change.
            try { shell.SetPosition(DesktopWallpaperPosition.Fill); }
            catch (COMException ex) { Log.Warn("SetPosition failed.", ex); }

            foreach (var assignment in assignments)
            {
                if (string.IsNullOrEmpty(assignment.Monitor.Id))
                {
                    ApplyLegacy(assignment.RenderedPath);
                    continue;
                }

                try
                {
                    shell.SetWallpaper(assignment.Monitor.Id, assignment.RenderedPath);
                }
                catch (COMException ex)
                {
                    Log.Warn($"SetWallpaper failed for {assignment.Monitor.Name}; using the legacy path.", ex);
                    ApplyLegacy(assignment.RenderedPath);
                }
            }
        }
        catch (Exception ex)
        {
            // Pre-Windows 8, or a broken shell. One wallpaper for everything is the only option left.
            Log.Warn("IDesktopWallpaper unavailable; falling back to SystemParametersInfo.", ex);
            if (assignments.Count > 0) ApplyLegacy(assignments[0].RenderedPath);
        }
        finally
        {
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void ApplyLegacy(string path)
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

        if (!SystemParametersInfoW(SPI_SETDESKWALLPAPER, 0, path, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE))
            Log.Error("SystemParametersInfo(SPI_SETDESKWALLPAPER) failed for " + path);
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

    private static ScalingMode ScalingFor(MonitorInfo monitor, AppSettings settings) =>
        settings.Monitors.FirstOrDefault(m => string.Equals(m.MonitorId, monitor.Id, StringComparison.OrdinalIgnoreCase))
            ?.ScalingOverride ?? settings.Scaling;

    /// <summary>
    /// Each apply writes to a new file name. Windows caches the wallpaper by path and will sometimes
    /// ignore a change when the path is unchanged, so reusing one name makes rotation look broken.
    /// </summary>
    private static string RenderPath(MonitorInfo monitor, long sequence) =>
        Path.Combine(Paths.RenderDirectory, $"m{monitor.Index}-{sequence}.jpg");

    private static void CleanupRenderDirectory(int monitorCount)
    {
        try
        {
            var files = new DirectoryInfo(Paths.RenderDirectory).GetFiles("*.jpg");
            int keep = Math.Max(monitorCount * 2, 4);
            if (files.Length <= keep) return;

            foreach (var file in files.OrderByDescending(f => f.LastWriteTimeUtc).Skip(keep))
            {
                // The two most recent generations are kept: Windows may still have the previous
                // wallpaper file open while it transitions.
                try { file.Delete(); } catch { /* it will be caught by the next sweep */ }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not clean the render directory.", ex);
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
