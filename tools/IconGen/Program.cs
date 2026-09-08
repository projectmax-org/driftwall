using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Driftwall.IconGen;

/// <summary>
/// Draws the Driftwall mark and packs it into a multi-resolution .ico, plus a PNG the app and the
/// store page use.
/// <para>
/// The mark is the one in the window's title bar: a rounded tile in the accent gradient with a
/// single white peak. Two shapes and no strokes, so it is still legible at 16 pixels in the
/// notification area. Everything that shows an icon — the executable, the taskbar, the tray, the
/// shortcuts, the installer, the title bar — is generated from this one drawing so they cannot drift.
/// </para>
/// Run with: dotnet run --project tools/IconGen -- [outputDirectory]   (or .\build.ps1 -Icon)
/// </summary>
internal static class Program
{
    private static readonly int[] IconSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    // Same stops as Brush.AccentGradient in the palette, so the icon matches the UI exactly.
    private static readonly Color GradientStart = Color.FromRgb(0x6D, 0x5E, 0xF6);
    private static readonly Color GradientEnd = Color.FromRgb(0xB8, 0x4B, 0xF0);

    [STAThread]
    private static int Main(string[] args)
    {
        // --wizard [dir]: the installer's banner and header images instead of the icon.
        if (args.Length > 0 && args[0].Equals("--wizard", StringComparison.OrdinalIgnoreCase))
        {
            var artDirectory = args.Length > 1
                ? args[1]
                : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "installer", "art");

            WizardArt.Generate(Path.GetFullPath(artDirectory));
            return 0;
        }

        var outputDirectory = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Driftwall", "Assets");

        outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        var frames = IconSizes.ToDictionary(size => size, size => EncodePng(Render(size)));
        WriteIco(Path.Combine(outputDirectory, "app.ico"), frames);

        File.WriteAllBytes(Path.Combine(outputDirectory, "logo-512.png"), EncodePng(Render(512)));

        Console.WriteLine("Wrote app.ico and logo-512.png to " + outputDirectory);
        return 0;
    }

    private static BitmapSource Render(int size)
    {
        double s = size;
        var visual = new DrawingVisual();
        RenderOptions.SetEdgeMode(visual, EdgeMode.Unspecified);

        using (var dc = visual.RenderOpen())
        {
            DrawMark(dc, new Rect(0, 0, s, s));
        }

        return Rasterize(visual, size);
    }

    /// <summary>
    /// The mark at any size and position: the gradient tile with the white peak. Shared with the
    /// installer artwork so there is exactly one drawing of it.
    /// </summary>
    internal static void DrawMark(DrawingContext dc, Rect tile)
    {
        // Corner radius is a quarter of the tile, matching the 20px/5px title-bar mark.
        double radius = tile.Width * 0.25;

        var background = new LinearGradientBrush(GradientStart, GradientEnd, new Point(0, 0), new Point(1, 1));
        background.Freeze();

        dc.PushClip(new RectangleGeometry(tile, radius, radius));
        dc.DrawRectangle(background, null, tile);
        DrawPeak(dc, tile);
        dc.Pop();
    }

    /// <summary>
    /// The white peak, placed exactly as the title bar places it: apex at the vertical quarter
    /// line, base across the middle half of the tile at 80% height.
    /// </summary>
    private static void DrawPeak(DrawingContext dc, Rect tile)
    {
        double s = tile.Width;
        double x = tile.X, y = tile.Y;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(x + s * 0.25, y + s * 0.80), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(x + s * 0.50, y + s * 0.25), true, true);
            ctx.LineTo(new Point(x + s * 0.75, y + s * 0.80), true, true);
        }
        geometry.Freeze();

        dc.DrawGeometry(Brushes.White, null, geometry);
    }

    private static BitmapSource Rasterize(Visual visual, int size)
    {
        var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    internal static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Writes an ICO whose frames are PNG-compressed. Windows has accepted PNG frames at every size
    /// since Vista, and it keeps the file small enough to embed without thought.
    /// </summary>
    private static void WriteIco(string path, Dictionary<int, byte[]> frames)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);

        var ordered = frames.OrderBy(f => f.Key).ToList();

        writer.Write((ushort)0);               // reserved
        writer.Write((ushort)1);               // type: icon
        writer.Write((ushort)ordered.Count);

        int offset = 6 + ordered.Count * 16;
        foreach (var (size, data) in ordered)
        {
            writer.Write((byte)(size >= 256 ? 0 : size)); // 0 means 256
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);             // palette size
            writer.Write((byte)0);             // reserved
            writer.Write((ushort)1);           // colour planes
            writer.Write((ushort)32);          // bits per pixel
            writer.Write(data.Length);
            writer.Write(offset);
            offset += data.Length;
        }

        foreach (var (_, data) in ordered) writer.Write(data);
    }
}
