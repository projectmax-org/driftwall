using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Driftwall.Core;

namespace Driftwall.Wallpaper;

/// <summary>
/// Turns a source photo into a file whose pixel dimensions exactly match a monitor.
/// <para>
/// Rendering at the monitor's native size and handing Windows an exact-fit image means Windows never
/// rescales it, which is why the result stays sharp — the desktop's own "Fill" is a low-quality
/// bilinear stretch. All work happens on <see cref="RenderThread"/>.
/// </para>
/// </summary>
public static class ImageComposer
{
    /// <summary>Width the blur backdrop is downsampled to before being stretched back up.</summary>
    private const int BlurSeedWidth = 48;

    /// <summary>How much the blurred backdrop is darkened so the photo stays the focus.</summary>
    private const double BlurDarkenOpacity = 0.38;

    /// <summary>Reads pixel dimensions from the file header without decoding pixel data.</summary>
    public static (int Width, int Height) ReadDimensions(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read image dimensions: " + path, ex);
            return (0, 0);
        }
    }

    /// <summary>
    /// Composes <paramref name="sourcePath"/> onto a <paramref name="targetWidth"/> x
    /// <paramref name="targetHeight"/> canvas and writes it to <paramref name="outputPath"/>.
    /// Returns false when the source could not be decoded.
    /// </summary>
    public static Task<bool> ComposeAsync(
        string sourcePath,
        string outputPath,
        int targetWidth,
        int targetHeight,
        ScalingMode mode,
        LetterboxStyle letterbox,
        Color letterboxColor,
        int jpegQuality,
        CancellationToken ct = default)
        => RenderThread.InvokeAsync(
            () => Compose(sourcePath, outputPath, targetWidth, targetHeight, mode, letterbox, letterboxColor, jpegQuality, ct),
            ct);

    /// <summary>
    /// Composes one photo across the whole virtual desktop, then slices it so each monitor receives
    /// the portion that lands on it. Used by <see cref="MultiMonitorMode.SpanAcrossMonitors"/>.
    /// </summary>
    public static Task<bool> ComposeSpanAsync(
        string sourcePath,
        IReadOnlyList<MonitorInfo> monitors,
        Func<MonitorInfo, string> outputPathFor,
        ScalingMode mode,
        LetterboxStyle letterbox,
        Color letterboxColor,
        int jpegQuality,
        CancellationToken ct = default)
        => RenderThread.InvokeAsync(
            () => ComposeSpan(sourcePath, monitors, outputPathFor, mode, letterbox, letterboxColor, jpegQuality, ct),
            ct);

    private static bool Compose(
        string sourcePath, string outputPath, int targetWidth, int targetHeight,
        ScalingMode mode, LetterboxStyle letterbox, Color letterboxColor, int jpegQuality,
        CancellationToken ct)
    {
        if (targetWidth <= 0 || targetHeight <= 0) return false;

        try
        {
            ct.ThrowIfCancellationRequested();

            var source = Decode(sourcePath, targetWidth, targetHeight, mode);
            if (source is null) return false;

            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            RenderOptions.SetEdgeMode(visual, EdgeMode.Unspecified);

            var canvas = new Rect(0, 0, targetWidth, targetHeight);

            using (var dc = visual.RenderOpen())
            {
                DrawBackdrop(dc, source, canvas, mode, letterbox, letterboxColor);
                DrawPhoto(dc, source, canvas, mode);
            }

            ct.ThrowIfCancellationRequested();
            var rendered = Rasterize(visual, targetWidth, targetHeight);
            Encode(rendered, outputPath, jpegQuality);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error("Composition failed for " + sourcePath, ex);
            return false;
        }
        finally
        {
            // A 4K Pbgra32 render target is ~33 MB. Hand it back promptly instead of waiting for
            // a gen-2 collection that may not happen for minutes on an otherwise idle app.
            MemoryTrimmer.RequestCollect();
        }
    }

    private static bool ComposeSpan(
        string sourcePath, IReadOnlyList<MonitorInfo> monitors, Func<MonitorInfo, string> outputPathFor,
        ScalingMode mode, LetterboxStyle letterbox, Color letterboxColor, int jpegQuality,
        CancellationToken ct)
    {
        if (monitors.Count == 0) return false;

        int left = monitors.Min(m => m.X);
        int top = monitors.Min(m => m.Y);
        int right = monitors.Max(m => m.X + m.Width);
        int bottom = monitors.Max(m => m.Y + m.Height);
        int spanWidth = right - left;
        int spanHeight = bottom - top;
        if (spanWidth <= 0 || spanHeight <= 0) return false;

        try
        {
            ct.ThrowIfCancellationRequested();

            var source = Decode(sourcePath, spanWidth, spanHeight, mode);
            if (source is null) return false;

            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);

            var canvas = new Rect(0, 0, spanWidth, spanHeight);
            using (var dc = visual.RenderOpen())
            {
                DrawBackdrop(dc, source, canvas, mode, letterbox, letterboxColor);
                DrawPhoto(dc, source, canvas, mode);
            }

            var spanned = Rasterize(visual, spanWidth, spanHeight);

            foreach (var monitor in monitors)
            {
                ct.ThrowIfCancellationRequested();

                // Monitor rects are in virtual-desktop coordinates; shift them into canvas space.
                var slice = new System.Windows.Int32Rect(
                    monitor.X - left, monitor.Y - top, monitor.Width, monitor.Height);

                if (slice.X < 0 || slice.Y < 0 ||
                    slice.X + slice.Width > spanned.PixelWidth ||
                    slice.Y + slice.Height > spanned.PixelHeight)
                {
                    Log.Warn("Span slice out of bounds for " + monitor.Name + "; falling back to full canvas.");
                    Encode(spanned, outputPathFor(monitor), jpegQuality);
                    continue;
                }

                var cropped = new CroppedBitmap(spanned, slice);
                cropped.Freeze();
                Encode(cropped, outputPathFor(monitor), jpegQuality);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error("Span composition failed for " + sourcePath, ex);
            return false;
        }
        finally
        {
            MemoryTrimmer.RequestCollect();
        }
    }

    // ---------------- decoding ----------------

    /// <summary>
    /// Decodes only as many pixels as the target actually needs. A 45-megapixel photo scaled onto a
    /// 1080p monitor is decoded at ~2 megapixels, which is the single biggest memory saving here.
    /// </summary>
    private static BitmapSource? Decode(string path, int targetWidth, int targetHeight, ScalingMode mode)
    {
        if (!File.Exists(path)) return null;

        var (naturalWidth, naturalHeight) = ReadDimensions(path);

        int decodeWidth = 0;
        if (naturalWidth > 0 && naturalHeight > 0 && mode is not (ScalingMode.Center or ScalingMode.Tile))
        {
            double scale = mode == ScalingMode.Fit
                ? Math.Min((double)targetWidth / naturalWidth, (double)targetHeight / naturalHeight)
                : Math.Max((double)targetWidth / naturalWidth, (double)targetHeight / naturalHeight);

            // Only ever downscale at decode time; upscaling here would waste memory for no quality gain.
            if (scale < 1.0) decodeWidth = Math.Max(1, (int)Math.Ceiling(naturalWidth * scale));
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
            image.EndInit();
            image.Freeze();

            return ApplyExifOrientation(image, path);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not decode " + path, ex);
            return null;
        }
    }

    /// <summary>
    /// WPF's decoders do not rotate by the EXIF orientation tag, so photos straight off a phone in a
    /// local folder would otherwise appear sideways.
    /// </summary>
    private static BitmapSource ApplyExifOrientation(BitmapSource image, string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (frame.Metadata is not BitmapMetadata metadata) return image;
            if (!metadata.ContainsQuery("/app1/ifd/{ushort=274}")) return image;
            if (metadata.GetQuery("/app1/ifd/{ushort=274}") is not ushort orientation) return image;

            var transform = orientation switch
            {
                3 => new RotateTransform(180),
                6 => new RotateTransform(90),
                8 => new RotateTransform(270),
                _ => null,
            };
            if (transform is null) return image;

            transform.Freeze();
            var rotated = new TransformedBitmap(image, transform);
            rotated.Freeze();
            return rotated;
        }
        catch
        {
            // Missing or malformed EXIF is normal; the un-rotated image is the right answer.
            return image;
        }
    }

    // ---------------- drawing ----------------

    private static void DrawBackdrop(
        DrawingContext dc, BitmapSource source, Rect canvas,
        ScalingMode mode, LetterboxStyle letterbox, Color letterboxColor)
    {
        // Fill, Stretch and Tile always cover every pixel, so a backdrop would never be visible.
        if (mode is ScalingMode.Fill or ScalingMode.Stretch or ScalingMode.Tile)
        {
            dc.DrawRectangle(Brushes.Black, null, canvas);
            return;
        }

        switch (letterbox)
        {
            case LetterboxStyle.Blur:
                DrawBlurredBackdrop(dc, source, canvas);
                break;

            case LetterboxStyle.AverageColor:
                var average = new SolidColorBrush(ComputeAverageColor(source));
                average.Freeze();
                dc.DrawRectangle(average, null, canvas);
                break;

            default:
                var solid = new SolidColorBrush(letterboxColor);
                solid.Freeze();
                dc.DrawRectangle(solid, null, canvas);
                break;
        }
    }

    /// <summary>
    /// Downsamples the photo to a few dozen pixels and stretches it back over the whole canvas.
    /// High-quality upscaling of a tiny image is a convincing blur for a fraction of the cost of a
    /// real gaussian, which matters because this runs while the user may be doing something else.
    /// </summary>
    private static void DrawBlurredBackdrop(DrawingContext dc, BitmapSource source, Rect canvas)
    {
        double scale = (double)BlurSeedWidth / source.PixelWidth;
        BitmapSource seed = source;
        if (scale < 1.0)
        {
            var transform = new ScaleTransform(scale, scale);
            transform.Freeze();
            var small = new TransformedBitmap(source, transform);
            small.Freeze();
            seed = small;
        }

        // Cover the canvas so the blur has no edges, even at a very different aspect ratio.
        var cover = FitRect(seed.PixelWidth, seed.PixelHeight, canvas, cover: true);
        dc.PushClip(new RectangleGeometry(canvas));
        dc.DrawImage(seed, cover);
        dc.DrawRectangle(new SolidColorBrush(Color.FromScRgb((float)BlurDarkenOpacity, 0, 0, 0)), null, canvas);
        dc.Pop();
    }

    private static void DrawPhoto(DrawingContext dc, BitmapSource source, Rect canvas, ScalingMode mode)
    {
        switch (mode)
        {
            case ScalingMode.Stretch:
                dc.DrawImage(source, canvas);
                break;

            case ScalingMode.Tile:
            {
                var brush = new ImageBrush(source)
                {
                    TileMode = TileMode.Tile,
                    Viewport = new Rect(0, 0, source.PixelWidth, source.PixelHeight),
                    ViewportUnits = BrushMappingMode.Absolute,
                    Stretch = Stretch.None,
                };
                brush.Freeze();
                dc.DrawRectangle(brush, null, canvas);
                break;
            }

            case ScalingMode.Center:
            {
                var rect = new Rect(
                    canvas.X + (canvas.Width - source.PixelWidth) / 2.0,
                    canvas.Y + (canvas.Height - source.PixelHeight) / 2.0,
                    source.PixelWidth,
                    source.PixelHeight);
                dc.PushClip(new RectangleGeometry(canvas));
                dc.DrawImage(source, rect);
                dc.Pop();
                break;
            }

            case ScalingMode.Fit:
                dc.DrawImage(source, FitRect(source.PixelWidth, source.PixelHeight, canvas, cover: false));
                break;

            default: // Fill
                dc.PushClip(new RectangleGeometry(canvas));
                dc.DrawImage(source, FitRect(source.PixelWidth, source.PixelHeight, canvas, cover: true));
                dc.Pop();
                break;
        }
    }

    /// <summary>Centres a source rectangle inside the canvas, either covering it or contained by it.</summary>
    private static Rect FitRect(double sourceWidth, double sourceHeight, Rect canvas, bool cover)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) return canvas;

        double scaleX = canvas.Width / sourceWidth;
        double scaleY = canvas.Height / sourceHeight;
        double scale = cover ? Math.Max(scaleX, scaleY) : Math.Min(scaleX, scaleY);

        double width = sourceWidth * scale;
        double height = sourceHeight * scale;

        return new Rect(
            canvas.X + (canvas.Width - width) / 2.0,
            canvas.Y + (canvas.Height - height) / 2.0,
            width,
            height);
    }

    // ---------------- output ----------------

    private static BitmapSource Rasterize(Visual visual, int width, int height)
    {
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static void Encode(BitmapSource image, string outputPath, int quality)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Paths.EnsureDirectory(directory);

        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 60, 100) };
        encoder.Frames.Add(BitmapFrame.Create(image));

        // Write to a temporary file first: Windows sometimes has the previous wallpaper file open,
        // and a half-written file would show as a corrupt desktop.
        var temp = outputPath + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
        {
            encoder.Save(stream);
        }

        if (File.Exists(outputPath)) File.Delete(outputPath);
        File.Move(temp, outputPath);
    }

    /// <summary>Average colour, computed by letting WIC downsample the image to a single pixel.</summary>
    public static Color ComputeAverageColor(BitmapSource source)
    {
        try
        {
            var scaled = new TransformedBitmap(source, new ScaleTransform(1.0 / source.PixelWidth, 1.0 / source.PixelHeight));
            var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
            converted.Freeze();

            var pixel = new byte[4];
            converted.CopyPixels(pixel, 4, 0);
            return Color.FromRgb(pixel[2], pixel[1], pixel[0]);
        }
        catch
        {
            return Color.FromRgb(0x10, 0x10, 0x14);
        }
    }
}
