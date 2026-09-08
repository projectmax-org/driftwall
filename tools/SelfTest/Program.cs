using System.Runtime.InteropServices;
using Driftwall.Core;
using Driftwall.Sources;
using Driftwall.Wallpaper;

namespace Driftwall.SelfTest;

/// <summary>
/// End-to-end check of the parts that cannot be verified by looking at the UI: monitor detection,
/// image composition at exact monitor resolution, and actually handing the file to Windows.
/// <para>
/// The desktop wallpaper is captured before the test and restored afterwards, so running this leaves
/// the machine exactly as it was found. That restore runs in a finally block — a test that changes
/// someone's desktop and then crashes is worse than no test.
/// </para>
/// Run with: dotnet run --project tools/SelfTest
/// </summary>
internal static class Program
{
    private static int _failures;

    [STAThread]
    private static int Main()
    {
        Console.WriteLine("Driftwall self-test");
        Console.WriteLine(new string('-', 60));

        try
        {
            return MainAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine("FATAL: " + ex);
            return 1;
        }
        finally
        {
            RenderThread.Shutdown();
        }
    }

    private static async Task<int> MainAsync()
    {
        var monitors = TestMonitorDetection();
        await TestCompositionAsync(monitors).ConfigureAwait(false);
        await TestWallpaperRoundTripAsync(monitors).ConfigureAwait(false);

        Console.WriteLine(new string('-', 60));
        Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    // ---------------- monitors ----------------

    private static IReadOnlyList<MonitorInfo> TestMonitorDetection()
    {
        Section("Monitor detection");

        var monitors = MonitorEnumerator.Enumerate();
        Check("at least one monitor found", monitors.Count > 0);

        foreach (var monitor in monitors)
        {
            Console.WriteLine($"  [{monitor.Index}] {monitor.Name}");
            Console.WriteLine($"      {monitor.ResolutionLabel} at ({monitor.X},{monitor.Y})" +
                              (monitor.IsPrimary ? "  primary" : string.Empty));
            Console.WriteLine($"      id: {Shorten(monitor.Id)}");

            Check($"monitor {monitor.Index} has a usable size", monitor.Width > 0 && monitor.Height > 0);
            Check($"monitor {monitor.Index} has a shell id", !string.IsNullOrEmpty(monitor.Id));
        }

        return monitors;
    }

    // ---------------- composition ----------------

    private static async Task TestCompositionAsync(IReadOnlyList<MonitorInfo> monitors)
    {
        Section("Image composition");

        var monitor = monitors[0];
        var sourceFile = await FetchSamplePhotoAsync().ConfigureAwait(false);
        if (sourceFile is null)
        {
            Fail("could not obtain a sample photo (is the network available?)");
            return;
        }

        var (sourceWidth, sourceHeight) = ImageComposer.ReadDimensions(sourceFile);
        Console.WriteLine($"  source: {sourceWidth}x{sourceHeight}  {new FileInfo(sourceFile).Length / 1024} KB");
        Check("source dimensions read from the header", sourceWidth > 0 && sourceHeight > 0);

        // Every scaling mode must produce a file whose pixels match the monitor exactly, because the
        // whole point of composing ourselves is that Windows never has to rescale.
        foreach (var mode in Enum.GetValues<ScalingMode>())
        {
            var output = Path.Combine(Path.GetTempPath(), $"driftwall-selftest-{mode}.jpg");

            bool composed = await ImageComposer.ComposeAsync(
                sourceFile, output, monitor.Width, monitor.Height,
                mode, LetterboxStyle.Blur, System.Windows.Media.Colors.Black, 92).ConfigureAwait(false);

            if (!composed)
            {
                Fail($"{mode}: composition returned false");
                continue;
            }

            var (width, height) = ImageComposer.ReadDimensions(output);
            long kb = new FileInfo(output).Length / 1024;
            Check($"{mode,-8} renders exactly {monitor.Width}x{monitor.Height} ({width}x{height}, {kb} KB)",
                  width == monitor.Width && height == monitor.Height);

            TryDelete(output);
        }

        TryDelete(sourceFile);
    }

    private static async Task<string?> FetchSamplePhotoAsync()
    {
        try
        {
            var source = new WallhavenSource();
            var settings = AppSettings.CreateDefault();
            var selection = settings.Sources.First(s => s.ProviderId == "wallhaven");

            var context = new SourceContext
            {
                Http = Net.Client,
                GetApiKey = _ => null,
                Selection = selection,
                Settings = settings,
                TargetWidth = 1920,
                TargetHeight = 1080,
            };

            var query = new SourceQuery { Mode = QueryMode.Top, PageSize = 5, TimeRange = "month" };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var page = await source.FetchAsync(query, context, cts.Token).ConfigureAwait(false);

            Check("Wallhaven returned photos", page.Items.Count > 0);
            if (page.Items.Count == 0) return null;

            var photo = page.Items[0];
            Console.WriteLine($"  photo: {photo.Title ?? photo.Id} ({photo.Width}x{photo.Height}) from {photo.ProviderName}");

            var cache = new ImageCache(Net.Client);
            return await cache.GetFullAsync(photo, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Fail("fetching a sample photo threw: " + ex.Message);
            return null;
        }
    }

    // ---------------- the real thing ----------------

    private static async Task TestWallpaperRoundTripAsync(IReadOnlyList<MonitorInfo> monitors)
    {
        Section("Applying the wallpaper (original is restored afterwards)");

        var original = CaptureCurrentWallpaper(monitors);
        var originalPosition = CaptureCurrentPosition();
        Console.WriteLine($"  captured {original.Count} existing wallpaper path(s), position {originalPosition}");

        try
        {
            var settings = AppSettings.CreateDefault();
            settings.MultiMonitor = MultiMonitorMode.DifferentPerMonitor;

            var cache = new ImageCache(Net.Client);
            var engine = new WallpaperEngine(cache);

            var photo = await FetchPhotoItemAsync().ConfigureAwait(false);
            if (photo is null)
            {
                Fail("no photo available to apply");
                return;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var application = await engine.ApplyAsync(_ => photo, settings, cts.Token).ConfigureAwait(false);

            Check("apply returned an assignment", application is { Assignments.Count: > 0 });
            if (application is null) return;

            foreach (var assignment in application.Assignments)
            {
                Console.WriteLine($"  {assignment.Monitor.Name} -> {Path.GetFileName(assignment.RenderedPath)}");
                Check($"rendered file exists for {assignment.Monitor.Name}", File.Exists(assignment.RenderedPath));

                var (width, height) = ImageComposer.ReadDimensions(assignment.RenderedPath);
                Check($"rendered file matches {assignment.Monitor.ResolutionLabel}",
                      width == assignment.Monitor.Width && height == assignment.Monitor.Height);
            }

            // The decisive check: ask Windows what the wallpaper is now, and confirm it is ours.
            var applied = CaptureCurrentWallpaper(monitors);
            bool windowsAccepted = application.Assignments.Any(a =>
                applied.TryGetValue(a.Monitor.Id, out var current) &&
                string.Equals(current, a.RenderedPath, StringComparison.OrdinalIgnoreCase));

            Check("Windows reports our file as the current wallpaper", windowsAccepted);
        }
        finally
        {
            // Position matters as much as the file: the engine sets Fill, and leaving that behind
            // would silently change how the user's own wallpaper is framed.
            RestoreWallpaper(original, originalPosition);
            Console.WriteLine($"  original wallpaper and position ({originalPosition}) restored");
        }
    }

    private static NativeMethods.DesktopWallpaperPosition CaptureCurrentPosition()
    {
        var shell = CreateShell();
        if (shell is null) return NativeMethods.DesktopWallpaperPosition.Fill;

        try
        {
            return shell.GetPosition();
        }
        catch (COMException)
        {
            return NativeMethods.DesktopWallpaperPosition.Fill;
        }
        finally
        {
            Release(shell);
        }
    }

    private static async Task<PhotoItem?> FetchPhotoItemAsync()
    {
        var source = new WallhavenSource();
        var settings = AppSettings.CreateDefault();
        var selection = settings.Sources.First(s => s.ProviderId == "wallhaven");

        var context = new SourceContext
        {
            Http = Net.Client,
            GetApiKey = _ => null,
            Selection = selection,
            Settings = settings,
            TargetWidth = 3840,
            TargetHeight = 2160,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var page = await source.FetchAsync(
            new SourceQuery { Mode = QueryMode.Top, PageSize = 5 }, context, cts.Token).ConfigureAwait(false);

        return page.Items.FirstOrDefault();
    }

    private static Dictionary<string, string> CaptureCurrentWallpaper(IReadOnlyList<MonitorInfo> monitors)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var shell = CreateShell();
        if (shell is null) return map;

        try
        {
            foreach (var monitor in monitors)
            {
                try
                {
                    var path = shell.GetWallpaper(monitor.Id);
                    if (!string.IsNullOrEmpty(path)) map[monitor.Id] = path;
                }
                catch (COMException)
                {
                    // A monitor with no wallpaper of its own; nothing to restore for it.
                }
            }
        }
        finally
        {
            Release(shell);
        }

        return map;
    }

    private static void RestoreWallpaper(
        Dictionary<string, string> original, NativeMethods.DesktopWallpaperPosition position)
    {
        var shell = CreateShell();
        if (shell is null) return;

        try
        {
            foreach (var (monitorId, path) in original)
            {
                try { shell.SetWallpaper(monitorId, path); }
                catch (COMException ex) { Console.WriteLine($"  WARNING: could not restore {monitorId}: {ex.Message}"); }
            }

            try { shell.SetPosition(position); }
            catch (COMException ex) { Console.WriteLine($"  WARNING: could not restore the position: {ex.Message}"); }
        }
        finally
        {
            Release(shell);
        }
    }

    // ---------------- COM helpers ----------------

    private static NativeMethods.IDesktopWallpaper? CreateShell()
    {
        try { return (NativeMethods.IDesktopWallpaper)new NativeMethods.DesktopWallpaperClass(); }
        catch (Exception ex) { Fail("could not create IDesktopWallpaper: " + ex.Message); return null; }
    }

    private static void Release(object com)
    {
        if (Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com);
    }

    // ---------------- reporting ----------------

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine(new string('.', title.Length));
    }

    private static void Check(string what, bool ok)
    {
        Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + what);
        if (!ok) _failures++;
    }

    private static void Fail(string what)
    {
        Console.WriteLine("  FAIL  " + what);
        _failures++;
    }

    private static string Shorten(string value) => value.Length <= 62 ? value : value[..59] + "...";

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* temp file */ }
    }
}
