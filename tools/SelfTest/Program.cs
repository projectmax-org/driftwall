using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

        TestResampler();
        await TestUnfilteredPlacementAsync().ConfigureAwait(false);
        await TestFillEdgeCasesAsync().ConfigureAwait(false);

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
            var output = Path.Combine(Path.GetTempPath(), $"driftwall-selftest-{mode}{ImageComposer.OutputExtension}");

            var clock = System.Diagnostics.Stopwatch.StartNew();
            bool composed = await ImageComposer.ComposeAsync(
                sourceFile, output, monitor.Width, monitor.Height,
                mode, LetterboxStyle.Blur, Colors.Black).ConfigureAwait(false);
            clock.Stop();

            if (!composed)
            {
                Fail($"{mode}: composition returned false");
                continue;
            }

            var (width, height) = ImageComposer.ReadDimensions(output);
            long kb = new FileInfo(output).Length / 1024;
            Check($"{mode,-8} renders exactly {monitor.Width}x{monitor.Height} ({width}x{height}, {kb} KB, {clock.ElapsedMilliseconds} ms)",
                  width == monitor.Width && height == monitor.Height);
            Check($"{mode,-8} is saved as PNG", IsPng(output));

            TryDelete(output);
        }

        TryDelete(sourceFile);
    }

    private static void TestResampler()
    {
        // A flat colour has to stay exactly that colour at any scale, which holds only if every row
        // of filter weights sums to one and the edges repeat correctly.
        var flat = SolidBitmap(37, 23, 0xC0, 0x80, 0x40);
        foreach (var (w, h) in new[] { (111, 69), (12, 8), (3840, 17), (5, 400), (36, 24) })
        {
            var resized = Resampler.Resize(flat, new System.Windows.Rect(0, 0, 37, 23), w, h);
            var pixels = new byte[w * h * 4];
            new FormatConvertedBitmap(resized, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, w * 4, 0);

            bool exact = resized.PixelWidth == w && resized.PixelHeight == h;
            for (int i = 0; exact && i < pixels.Length; i += 4)
                exact = pixels[i] == 0xC0 && pixels[i + 1] == 0x80 && pixels[i + 2] == 0x40 && pixels[i + 3] == 0xFF;

            Check($"resampler keeps a flat colour exact at {w}x{h}", exact);
        }

        Check("resampler hands back the source untouched at 1:1",
              ReferenceEquals(Resampler.Resize(flat, new System.Windows.Rect(0, 0, 37, 23), 37, 23), flat));

        Check("a 1280x853 photo is enlarged 3x to fill a 3840x2160 display",
              Math.Abs(ImageComposer.EnlargementFactor(1280, 853, 3840, 2160, ScalingMode.Fill) - 3.0) < 1e-9);
        Check("a 3840x2560 photo fills a 3840x2160 display without enlargement",
              ImageComposer.EnlargementFactor(3840, 2560, 3840, 2160, ScalingMode.Fill) <= 1.0);
        Check("Fit only has to reach one edge",
              Math.Abs(ImageComposer.EnlargementFactor(1920, 800, 3840, 2160, ScalingMode.Fit) - 2.0) < 1e-9);
    }

    /// <summary>
    /// A photo that already has the size Fit gives it must reach the canvas pixel for pixel. Any
    /// second round of filtering by WPF would soften it, and noise shows that at once.
    /// </summary>
    private static async Task TestUnfilteredPlacementAsync()
    {
        var sourceFile = Path.Combine(Path.GetTempPath(), "driftwall-selftest-noise.png");
        var output = Path.Combine(Path.GetTempPath(), "driftwall-selftest-noise-fit.png");

        var noise = new byte[400 * 200 * 3];
        new Random(1234).NextBytes(noise);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(400, 200, 96, 96, PixelFormats.Bgr24, null, noise, 400 * 3)));
        using (var stream = File.Create(sourceFile)) encoder.Save(stream);

        try
        {
            bool composed = await ImageComposer.ComposeAsync(
                sourceFile, output, 400, 300, ScalingMode.Fit, LetterboxStyle.Blur, Colors.Black).ConfigureAwait(false);
            if (!composed)
            {
                Fail("Fit composition of a synthetic image returned false");
                return;
            }

            var actual = await RenderThread.InvokeAsync(() => Bgr24(output)).ConfigureAwait(false);
            bool same = actual.Width == 400 && actual.Height == 300;
            for (int y = 0; same && y < 200; y++)
                same = actual.Data.AsSpan((y + 50) * 400 * 3, 400 * 3).SequenceEqual(noise.AsSpan(y * 400 * 3, 400 * 3));

            Check("Fit places an exact-size photo pixel for pixel", same);
        }
        finally
        {
            TryDelete(sourceFile);
            TryDelete(output);
        }
    }

    /// <summary>
    /// Fill of a photo one row taller than the canvas has to crop whole rows, pixel for pixel. A
    /// transparent photo has to come out over black, not as the colour stored under its alpha.
    /// </summary>
    private static async Task TestFillEdgeCasesAsync()
    {
        var tall = Path.Combine(Path.GetTempPath(), "driftwall-selftest-tall.png");
        var clear = Path.Combine(Path.GetTempPath(), "driftwall-selftest-clear.png");
        var output = Path.Combine(Path.GetTempPath(), "driftwall-selftest-fill.png");

        var noise = new byte[400 * 301 * 3];
        new Random(99).NextBytes(noise);
        SavePng(BitmapSource.Create(400, 301, 96, 96, PixelFormats.Bgr24, null, noise, 400 * 3), tall);

        var white = new byte[64 * 36 * 4];
        Array.Fill(white, (byte)0xFF);
        for (int i = 3; i < white.Length; i += 4) white[i] = 0;
        SavePng(BitmapSource.Create(64, 36, 96, 96, PixelFormats.Bgra32, null, white, 64 * 4), clear);

        try
        {
            if (await ImageComposer.ComposeAsync(tall, output, 400, 300, ScalingMode.Fill, LetterboxStyle.Blur, Colors.Black).ConfigureAwait(false))
            {
                var actual = await RenderThread.InvokeAsync(() => Bgr24(output)).ConfigureAwait(false);
                bool same = actual.Width == 400 && actual.Height == 300;
                for (int y = 0; same && y < 300; y++)
                    same = actual.Data.AsSpan(y * 1200, 1200).SequenceEqual(noise.AsSpan(y * 1200, 1200));
                Check("Fill crops an unscaled photo on whole pixels", same);
            }
            else
            {
                Fail("Fill composition of a synthetic image returned false");
            }

            if (await ImageComposer.ComposeAsync(clear, output, 64, 36, ScalingMode.Fill, LetterboxStyle.Blur, Colors.Black).ConfigureAwait(false))
            {
                var actual = await RenderThread.InvokeAsync(() => Bgr24(output)).ConfigureAwait(false);
                Check("Fill shows transparent areas as black", actual.Data.All(b => b == 0));
            }
            else
            {
                Fail("Fill composition of a transparent image returned false");
            }
        }
        finally
        {
            TryDelete(tall);
            TryDelete(clear);
            TryDelete(output);
        }
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static BitmapSource SolidBitmap(int width, int height, byte b, byte g, byte r)
    {
        var data = new byte[width * height * 4];
        for (int i = 0; i < data.Length; i += 4)
        {
            data[i] = b;
            data[i + 1] = g;
            data[i + 2] = r;
            data[i + 3] = 0xFF;
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, data, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static (int Width, int Height, byte[] Data) Bgr24(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path));
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgr24, null, 0);
        var data = new byte[converted.PixelWidth * 3 * converted.PixelHeight];
        converted.CopyPixels(data, converted.PixelWidth * 3, 0);
        return (converted.PixelWidth, converted.PixelHeight, data);
    }

    private static bool IsPng(string path)
    {
        var header = new byte[4];
        using var stream = File.OpenRead(path);
        return stream.Read(header, 0, 4) == 4 && header[0] == 0x89 && header[1] == (byte)'P' && header[2] == (byte)'N' && header[3] == (byte)'G';
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

        if (SlideshowOrSpotlightActive())
        {
            Console.WriteLine("  SKIP  the desktop runs a slideshow or Windows Spotlight, which this test could not put back");
            return;
        }

        var original = CaptureCurrentWallpaper(monitors);

        // A display showing a solid colour reports no path, and one on a slideshow reports only its
        // current slide. Neither can be put back exactly, so the desktop is left alone.
        var unknown = monitors.Where(m => !string.IsNullOrEmpty(m.Id) && !original.ContainsKey(m.Id)).ToList();
        if (unknown.Count > 0)
        {
            Console.WriteLine($"  SKIP  {unknown.Count} display(s) show no picture this test could restore afterwards");
            return;
        }

        if (original.Values.Any(path => !File.Exists(path)))
        {
            Console.WriteLine("  SKIP  a display shows a picture whose file no longer exists, which this test could not restore");
            return;
        }

        var backups = BackUpWallpaperFiles(original);
        if (backups is null)
        {
            Console.WriteLine("  SKIP  the current wallpaper files could not be backed up, so the test would not be able to restore them");
            return;
        }
        var originalPosition = CaptureCurrentPosition();
        Console.WriteLine($"  captured {original.Count} existing wallpaper path(s), position {originalPosition}");

        var gate = new object();
        bool restored = false;
        void Restore()
        {
            // The signal handler and the finally block can both get here. The lock makes the second
            // wait for the first to finish, rather than return while the desktop is still the test's.
            lock (gate)
            {
                if (restored) return;
                restored = true;

                // Position matters as much as the file: the engine sets Fill, and leaving that behind
                // would silently change how the user's own wallpaper is framed.
                RestoreMissingFiles(backups);
                RestoreWallpaper(original, originalPosition);
                foreach (var copy in backups.Values) TryDelete(copy);
                DeleteOwnRenders(monitors);
                Console.WriteLine($"  original wallpaper and position ({originalPosition}) restored");
            }
        }

        // Every interruption cancels the test first, so its apply stops before reaching the desktop and
        // the finally block below restores once. Ctrl+C and Ctrl+Break can be held off for that. Closing
        // the console allows about five seconds, so if the test has not unwound by then, restore from here.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var finished = new ManualResetEventSlim(false);
        void Interrupted()
        {
            cts.Cancel();
            if (!finished.Wait(TimeSpan.FromSeconds(4))) Restore();
        }

        var signals = new[] { PosixSignal.SIGINT, PosixSignal.SIGQUIT, PosixSignal.SIGHUP }
            .Select(signal => PosixSignalRegistration.Create(signal, context =>
            {
                if (context.Signal is PosixSignal.SIGINT or PosixSignal.SIGQUIT)
                {
                    context.Cancel = true;
                    cts.Cancel();
                }
                else
                {
                    Interrupted();
                }
            }))
            .ToList();

        // Signing out or shutting down never reaches a console handler in a process that loads WPF, so
        // the session-ending notice, which SystemEvents receives on a hidden window, stands in for it.
        Microsoft.Win32.SessionEndingEventHandler sessionEnding = (_, _) => Interrupted();
        Microsoft.Win32.SystemEvents.SessionEnding += sessionEnding;

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

            // Windows copies a wallpaper into its own cache before showing it, and re-encodes JPEG
            // there at quality 85. A PNG has to come through with every pixel unchanged.
            foreach (var assignment in application.Assignments)
            {
                cts.Token.ThrowIfCancellationRequested();
                bool identical = await WindowsShowsIdenticalPixelsAsync(assignment.RenderedPath, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                Check($"Windows shows the {assignment.Monitor.ResolutionLabel} render pixel for pixel", identical);
            }
        }
        finally
        {
            Restore();
            finished.Set();
            foreach (var registration in signals) registration.Dispose();
            Microsoft.Win32.SystemEvents.SessionEnding -= sessionEnding;
        }
    }

    /// <summary>Waits for a file in Windows' wallpaper cache whose pixels equal the render exactly.</summary>
    private static async Task<bool> WindowsShowsIdenticalPixelsAsync(string renderedPath, TimeSpan timeout)
    {
        var themes = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Themes");
        var expected = await RenderThread.InvokeAsync(() => Bgr24(renderedPath)).ConfigureAwait(false);
        var deadline = DateTime.UtcNow + timeout;

        do
        {
            foreach (var file in Directory.EnumerateFiles(themes, "Transcoded_*"))
            {
                try
                {
                    var actual = await RenderThread.InvokeAsync(() => Bgr24(file)).ConfigureAwait(false);
                    if (actual.Width == expected.Width && actual.Height == expected.Height &&
                        actual.Data.AsSpan().SequenceEqual(expected.Data))
                        return true;
                }
                catch (Exception)
                {
                    // Windows may still be writing the file.
                }
            }

            await Task.Delay(500).ConfigureAwait(false);
        }
        while (DateTime.UtcNow < deadline);

        return false;
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

    /// <summary>
    /// Copies the current wallpaper files aside. When they are Driftwall's own renders, the engine's
    /// clean-up during the test can delete them, and restoring a path that no longer exists would
    /// leave the desktop blank.
    /// </summary>
    private static Dictionary<string, string>? BackUpWallpaperFiles(Dictionary<string, string> original)
    {
        var backups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in original.Values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;

            var copy = Path.Combine(Path.GetTempPath(), $"driftwall-selftest-backup-{Guid.NewGuid():N}{Path.GetExtension(path)}");
            try
            {
                File.Copy(path, copy);
                backups[path] = copy;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Without every copy the test must not go on: its clean-up could remove an original.
                Console.WriteLine($"  WARNING: could not back up {path}: {ex.Message}");
                foreach (var made in backups.Values) TryDelete(made);
                return null;
            }
        }

        return backups;
    }

    private static void RestoreMissingFiles(Dictionary<string, string> backups)
    {
        foreach (var (path, copy) in backups)
        {
            try
            {
                // Put the original back if the test deleted it or wrote over it.
                if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(File.ReadAllBytes(copy)))
                    File.Copy(copy, path, overwrite: true);
            }
            catch (Exception ex)
            {
                // Never let one file stop the wallpaper itself being restored.
                Console.WriteLine($"  WARNING: could not put back {path}: {ex.Message}");
            }
        }
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
                // A display the running app has changed since the test put its render up is left as
                // the app set it: the old picture would contradict what the app says is showing.
                string? current = null;
                try { current = shell.GetWallpaper(monitorId); } catch (Exception) { /* restore it anyway */ }
                if (!string.IsNullOrEmpty(current) && !IsOwnRender(current) &&
                    !string.Equals(current, path, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"  {monitorId[..Math.Min(24, monitorId.Length)]}... was changed by Driftwall during the test; left as it is");
                    continue;
                }

                // Failed HRESULTs arrive as several exception types; one display must not stop the rest.
                try { shell.SetWallpaper(monitorId, path); }
                catch (Exception ex) { Console.WriteLine($"  WARNING: could not restore {monitorId}: {ex.Message}"); }
            }

            try { shell.SetPosition(position); }
            catch (Exception ex) { Console.WriteLine($"  WARNING: could not restore the position: {ex.Message}"); }
        }
        finally
        {
            Release(shell);
        }
    }

    /// <summary>A file this test process rendered: the engine puts the process id in every name.</summary>
    private static bool IsOwnRender(string path)
    {
        try
        {
            return string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(Paths.RenderDirectory), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(path).Contains($"-{Environment.ProcessId}-", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Removes the test's renders, except any a display still shows because a restore failed.</summary>
    private static void DeleteOwnRenders(IReadOnlyList<MonitorInfo> monitors)
    {
        try
        {
            // If Windows could not say what every display shows, nothing is safe to delete.
            var current = CaptureCurrentWallpaper(monitors);
            if (current.Count < monitors.Count(m => !string.IsNullOrEmpty(m.Id))) return;

            var shown = current.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(Paths.RenderDirectory).Where(IsOwnRender).ToList())
            {
                if (!shown.Contains(file)) TryDelete(file);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  WARNING: could not remove the test's renders: {ex.Message}");
        }
    }

    /// <summary>
    /// A slideshow or Windows Spotlight reports only its current picture, and putting that back would
    /// replace the slideshow with a still.
    /// </summary>
    private static bool SlideshowOrSpotlightActive()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers");
            // 0 picture, 1 solid colour, 2 slideshow, 3 Windows Spotlight.
            return key?.GetValue("BackgroundType") is int type && type is 2 or 3;
        }
        catch (Exception)
        {
            return false;
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
