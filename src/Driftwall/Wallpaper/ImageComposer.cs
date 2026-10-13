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
/// <para>
/// The photo is resampled once, with <see cref="Resampler"/>, straight to the pixels it will occupy,
/// and the result is saved as PNG. Windows re-encodes a JPEG wallpaper at quality 85 before showing
/// it (the JPEGImportQuality default) but displays a PNG's pixels untouched, so PNG is the only
/// format that reaches the screen exactly as rendered.
/// </para>
/// </summary>
public static class ImageComposer
{
    /// <summary>File extension of rendered wallpapers.</summary>
    public const string OutputExtension = ".png";

    /// <summary>Width the blur backdrop is downsampled to before being stretched back up.</summary>
    private const int BlurSeedWidth = 48;

    /// <summary>How much the blurred backdrop is darkened so the photo stays the focus.</summary>
    private const double BlurDarkenOpacity = 0.38;

    /// <summary>Metadata query for the EXIF orientation tag.</summary>
    private const string OrientationQuery = "/app1/ifd/{ushort=274}";

    /// <summary>Reads pixel dimensions from the file header without decoding pixel data.</summary>
    public static (int Width, int Height) ReadDimensions(string path)
    {
        var (width, height, _) = ReadHeader(path);
        return (width, height);
    }

    /// <summary>
    /// Dimensions as the photo will appear on screen: width and height swap for camera photos whose
    /// EXIF orientation turns them sideways. Runs on <see cref="RenderThread"/>.
    /// </summary>
    public static Task<(int Width, int Height)> ReadDisplayDimensionsAsync(string path, CancellationToken ct = default) =>
        RenderThread.InvokeAsync(() =>
        {
            var (width, height, orientation) = ReadHeader(path);
            return IsSideways(orientation) ? (height, width) : (width, height);
        }, ct);

    /// <summary>
    /// How many times a photo is enlarged to reach a canvas in the given mode. Above 1 the photo is
    /// stretched past its real pixels. Center and Tile never scale, so they report 1.
    /// </summary>
    public static double EnlargementFactor(int photoWidth, int photoHeight, int canvasWidth, int canvasHeight, ScalingMode mode)
    {
        if (photoWidth <= 0 || photoHeight <= 0 || canvasWidth <= 0 || canvasHeight <= 0) return 1.0;

        double x = (double)canvasWidth / photoWidth;
        double y = (double)canvasHeight / photoHeight;

        return mode switch
        {
            ScalingMode.Fit => Math.Min(x, y),
            ScalingMode.Center or ScalingMode.Tile => 1.0,
            _ => Math.Max(x, y), // Fill and Stretch have to reach both edges.
        };
    }

    /// <summary>
    /// Composes <paramref name="sourcePath"/> onto a <paramref name="targetWidth"/> x
    /// <paramref name="targetHeight"/> canvas and writes it to <paramref name="outputPath"/> as PNG.
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
        CancellationToken ct = default)
        => RenderThread.InvokeAsync(
            () => Compose(sourcePath, outputPath, targetWidth, targetHeight, mode, letterbox, letterboxColor, ct),
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
        CancellationToken ct = default)
        => RenderThread.InvokeAsync(
            () => ComposeSpan(sourcePath, monitors, outputPathFor, mode, letterbox, letterboxColor, ct),
            ct);

    private static bool Compose(
        string sourcePath, string outputPath, int targetWidth, int targetHeight,
        ScalingMode mode, LetterboxStyle letterbox, Color letterboxColor,
        CancellationToken ct)
    {
        if (targetWidth <= 0 || targetHeight <= 0) return false;

        try
        {
            ct.ThrowIfCancellationRequested();

            var source = Decode(sourcePath, targetWidth, targetHeight, mode);
            if (source is null) return false;

            var rendered = Render(source, targetWidth, targetHeight, mode, letterbox, letterboxColor, ct);

            ct.ThrowIfCancellationRequested();
            Encode(rendered, outputPath);
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
            // A 4K canvas is ~33 MB of pixels. Hand it back promptly instead of waiting for a gen-2
            // collection that may not happen for minutes on an otherwise idle app.
            MemoryTrimmer.RequestCollect();
        }
    }

    private static bool ComposeSpan(
        string sourcePath, IReadOnlyList<MonitorInfo> monitors, Func<MonitorInfo, string> outputPathFor,
        ScalingMode mode, LetterboxStyle letterbox, Color letterboxColor,
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

            var spanned = Render(source, spanWidth, spanHeight, mode, letterbox, letterboxColor, ct);

            foreach (var monitor in monitors)
            {
                ct.ThrowIfCancellationRequested();

                // Monitor rects are in virtual-desktop coordinates; shift them into canvas space.
                var slice = new Int32Rect(monitor.X - left, monitor.Y - top, monitor.Width, monitor.Height);

                if (slice.X < 0 || slice.Y < 0 ||
                    slice.X + slice.Width > spanned.PixelWidth ||
                    slice.Y + slice.Height > spanned.PixelHeight)
                {
                    Log.Warn("Span slice out of bounds for " + monitor.Name + "; falling back to full canvas.");
                    Encode(spanned, outputPathFor(monitor));
                    continue;
                }

                var cropped = new CroppedBitmap(spanned, slice);
                cropped.Freeze();
                Encode(cropped, outputPathFor(monitor));
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

        var (naturalWidth, naturalHeight, orientation) = ReadHeader(path);

        // A sideways camera photo is measured the way it will be shown.
        int shownWidth = IsSideways(orientation) ? naturalHeight : naturalWidth;
        int shownHeight = IsSideways(orientation) ? naturalWidth : naturalHeight;

        int decodeWidth = 0;
        if (shownWidth > 0 && shownHeight > 0 && mode is not (ScalingMode.Center or ScalingMode.Tile))
        {
            double scale = mode == ScalingMode.Fit
                ? Math.Min((double)targetWidth / shownWidth, (double)targetHeight / shownHeight)
                : Math.Max((double)targetWidth / shownWidth, (double)targetHeight / shownHeight);

            // Decoding only ever shrinks, and never below the size needed: the resampler takes the
            // final, exact step. DecodePixelWidth applies before rotation, hence the natural width.
            // The tolerance absorbs floating-point noise: 4188 * (3840 / 4188.0) is 3840.0000000000005,
            // and a ceiling of 3841 would turn an exact copy into a soft 3841-to-3840 resample. A real
            // fractional part here is at least 1/65535, far above it.
            if (scale < 1.0) decodeWidth = Math.Max(1, (int)Math.Ceiling(naturalWidth * scale - 1e-7));
        }

        try
        {
            // Read through a stream, not a Uri: a file Uri can unescape "%20" or "%C3%BC" in a file name
            // and open a different file. OnLoad reads every pixel in EndInit, so the stream can close after.
            using var stream = File.OpenRead(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = stream;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
            image.EndInit();
            image.Freeze();

            return ApplyExifOrientation(image, orientation);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not decode " + path, ex);
            return null;
        }
    }

    /// <summary>Pixel size and EXIF orientation (1 when absent), read without decoding pixels.</summary>
    private static (int Width, int Height, ushort Orientation) ReadHeader(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            return (frame.PixelWidth, frame.PixelHeight, OrientationOf(frame));
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read image dimensions: " + path, ex);
            return (0, 0, 1);
        }
    }

    private static ushort OrientationOf(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata &&
                metadata.ContainsQuery(OrientationQuery) &&
                metadata.GetQuery(OrientationQuery) is ushort orientation)
                return orientation;
        }
        catch
        {
            // Missing or malformed EXIF is normal, and PNG metadata does not support the query at all.
        }

        return 1;
    }

    /// <summary>Orientations 5 to 8 are the ones <see cref="ApplyExifOrientation"/> turns a quarter.</summary>
    private static bool IsSideways(ushort orientation) => orientation is 5 or 6 or 7 or 8;

    /// <summary>
    /// WPF's decoders do not rotate by the EXIF orientation tag, so photos straight off a phone in a
    /// local folder would otherwise appear sideways.
    /// <para>
    /// 2, 4, 5 and 7 are the mirrored forms: the same turn as their neighbours, then a horizontal flip.
    /// Each step is its own bitmap, so the result never depends on which of a combined turn and flip
    /// WIC would apply first.
    /// </para>
    /// </summary>
    private static BitmapSource ApplyExifOrientation(BitmapSource image, ushort orientation)
    {
        double angle = orientation switch
        {
            3 or 4 => 180,
            5 or 6 => 90,
            7 or 8 => 270,
            _ => 0,
        };
        bool mirror = orientation is 2 or 4 or 5 or 7;

        BitmapSource result = image;
        if (angle != 0) result = Transformed(result, new RotateTransform(angle));
        if (mirror) result = Transformed(result, new ScaleTransform(-1, 1));
        return result;
    }

    private static BitmapSource Transformed(BitmapSource source, Transform transform)
    {
        transform.Freeze();
        var bitmap = new TransformedBitmap(source, transform);
        bitmap.Freeze();
        return bitmap;
    }

    // ---------------- drawing ----------------

    /// <summary>
    /// Produces the finished canvas. Fill and Stretch cover every pixel, so the resampled photo is the
    /// canvas and no render target is needed. The other modes draw it over a backdrop.
    /// </summary>
    private static BitmapSource Render(
        BitmapSource source, int canvasWidth, int canvasHeight,
        ScalingMode mode, LetterboxStyle letterbox, Color letterboxColor, CancellationToken ct)
    {
        // A photo with an alpha channel still goes over the black backdrop, so transparent areas come
        // out black rather than as whatever colour happens to be stored under them.
        if (mode is ScalingMode.Fill or ScalingMode.Stretch && IsOpaque(source.Format))
            return PlacePhoto(source, canvasWidth, canvasHeight, mode).Image;

        var canvas = new Rect(0, 0, canvasWidth, canvasHeight);
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        RenderOptions.SetEdgeMode(visual, EdgeMode.Unspecified);

        using (var dc = visual.RenderOpen())
        {
            DrawBackdrop(dc, source, canvas, mode, letterbox, letterboxColor);
            DrawPhoto(dc, source, canvas, mode);
        }

        ct.ThrowIfCancellationRequested();
        return Rasterize(visual, canvasWidth, canvasHeight);
    }

    /// <summary>Pixel formats with no alpha channel. Anything else may be transparent somewhere.</summary>
    private static bool IsOpaque(PixelFormat format) =>
        format == PixelFormats.Bgr24 || format == PixelFormats.Bgr32 || format == PixelFormats.Rgb24 ||
        format == PixelFormats.Rgb48 || format == PixelFormats.Cmyk32 || format == PixelFormats.Gray8 ||
        format == PixelFormats.Gray16 || format == PixelFormats.Gray4 || format == PixelFormats.Gray2 ||
        format == PixelFormats.BlackWhite;

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
        if (mode == ScalingMode.Tile)
        {
            var brush = new ImageBrush(source)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, source.PixelWidth, source.PixelHeight),
                ViewportUnits = BrushMappingMode.Absolute,
                // Fill maps the whole photo onto its viewport, which is already its pixel size. None
                // would draw it at its DPI size instead, cropping a 72-dpi photo or shrinking a 144-dpi one.
                Stretch = Stretch.Fill,
            };
            brush.Freeze();
            dc.DrawRectangle(brush, null, canvas);
            return;
        }

        var placement = PlacePhoto(source, (int)canvas.Width, (int)canvas.Height, mode);

        // The photo already has exactly the pixels it will occupy, at whole-pixel positions. Nearest
        // neighbour makes WPF copy them rather than filter them a second time.
        var photo = new DrawingGroup { ClipGeometry = new RectangleGeometry(canvas) };
        RenderOptions.SetBitmapScalingMode(photo, BitmapScalingMode.NearestNeighbor);
        using (var context = photo.Open())
        {
            context.DrawImage(placement.Image, placement.Target);
        }
        photo.Freeze();
        dc.DrawDrawing(photo);
    }

    /// <summary>A photo resampled to the pixels it will occupy, and where those pixels go on the canvas.</summary>
    private readonly record struct Placement(BitmapSource Image, Rect Target);

    private static Placement PlacePhoto(BitmapSource source, int canvasWidth, int canvasHeight, ScalingMode mode)
    {
        int width = source.PixelWidth;
        int height = source.PixelHeight;
        var whole = new Rect(0, 0, width, height);

        switch (mode)
        {
            case ScalingMode.Stretch:
                return new Placement(
                    Resampler.Resize(source, whole, canvasWidth, canvasHeight),
                    new Rect(0, 0, canvasWidth, canvasHeight));

            case ScalingMode.Center:
            case ScalingMode.Tile:
                // Whole-pixel offsets: half a pixel would make WPF blur every pixel of the photo.
                return new Placement(source, new Rect(
                    Math.Floor((canvasWidth - width) / 2.0),
                    Math.Floor((canvasHeight - height) / 2.0),
                    width, height));

            case ScalingMode.Fit:
            {
                double scale = Math.Min((double)canvasWidth / width, (double)canvasHeight / height);
                int fitWidth = Math.Clamp((int)Math.Round(width * scale), 1, canvasWidth);
                int fitHeight = Math.Clamp((int)Math.Round(height * scale), 1, canvasHeight);
                return new Placement(
                    Resampler.Resize(source, whole, fitWidth, fitHeight),
                    new Rect((canvasWidth - fitWidth) / 2, (canvasHeight - fitHeight) / 2, fitWidth, fitHeight));
            }

            default:
            {
                // Fill: only the part of the photo that stays on screen is resampled, straight to the
                // canvas size, so no pixels are spent on the cropped-off edges.
                double scale = Math.Max((double)canvasWidth / width, (double)canvasHeight / height);
                double regionWidth = canvasWidth / scale;
                double regionHeight = canvasHeight / scale;
                double regionX = (width - regionWidth) / 2;
                double regionY = (height - regionHeight) / 2;
                if (scale == 1.0)
                {
                    // Unscaled, the crop must start on a whole pixel. An odd number of spare rows would
                    // put it half a pixel in, and the filter would then blur every row it copies.
                    regionX = Math.Floor(regionX);
                    regionY = Math.Floor(regionY);
                }

                var region = new Rect(regionX, regionY, regionWidth, regionHeight);
                return new Placement(
                    Resampler.Resize(source, region, canvasWidth, canvasHeight),
                    new Rect(0, 0, canvasWidth, canvasHeight));
            }
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

    private static void Encode(BitmapSource image, string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Paths.EnsureDirectory(directory);

        // A wallpaper is opaque, and dropping the alpha channel makes the file a quarter smaller.
        var opaque = new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);
        var encoder = new PngBitmapEncoder { Interlace = PngInterlaceOption.Off };
        encoder.Frames.Add(BitmapFrame.Create(opaque));

        // Write to a temporary file first: Windows sometimes has the previous wallpaper file open,
        // and a half-written file would show as a corrupt desktop.
        var temp = outputPath + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
            {
                encoder.Save(stream);
            }

            if (File.Exists(outputPath)) File.Delete(outputPath);
            File.Move(temp, outputPath);
        }
        catch
        {
            // Nothing ever uses a half-written file again, and a 4K PNG is tens of megabytes.
            try { File.Delete(temp); } catch { /* the render folder sweep gets it */ }
            throw;
        }
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
