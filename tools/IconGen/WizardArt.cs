using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Driftwall.IconGen;

/// <summary>
/// Draws the artwork the setup wizard shows: the tall banner on the Welcome and Finished pages and
/// the small mark in the header of every other page, at every size Inno Setup picks from as the
/// display DPI changes.
/// <para>
/// Both are the brand mark alone on a transparent ground, the same drawing as the app icon and the
/// title bar. The wizard page colour shows through, so one set of files serves the light and dark
/// appearances and nothing has to be colour-matched. That is how the Windows App Installer sheet
/// treats an app's icon, and it keeps every word in the wizard native text rather than pixels.
/// </para>
/// </summary>
internal static class WizardArt
{
    // Image areas Inno Setup 6.6+ reserves at each DPI with its default font and WizardSizePercent.
    // Rendering at exactly these sizes means Setup never rescales the artwork.
    private static readonly (int Scale, int Width, int Height)[] BannerSizes =
    [
        (100, 202, 386), (125, 269, 515), (150, 336, 643), (175, 403, 772),
        (200, 430, 824), (225, 498, 953), (250, 534, 1022),
    ];

    private static readonly (int Scale, int Size)[] SmallSizes =
    [
        (100, 58), (125, 77), (150, 97), (175, 116), (200, 124), (225, 143), (250, 159),
    ];

    public static void Generate(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        foreach (var (scale, width, height) in BannerSizes)
        {
            var path = Path.Combine(outputDirectory, $"wizard-mark-{scale}.png");
            File.WriteAllBytes(path, Program.EncodePng(RenderBanner(width, height)));
        }

        foreach (var (scale, size) in SmallSizes)
        {
            var path = Path.Combine(outputDirectory, $"wizard-small-{scale}.png");
            File.WriteAllBytes(path, Program.EncodePng(RenderSmall(size)));
        }

        Console.WriteLine($"Wrote wizard artwork ({BannerSizes.Length} banners, {SmallSizes.Length} header marks) to {outputDirectory}");
    }

    /// <summary>
    /// The Welcome/Finished banner: the mark at half the column's width, centred a third of the way
    /// down so it sits beside the page's title and first lines like an icon next to its text.
    /// </summary>
    private static BitmapSource RenderBanner(int width, int height)
    {
        int side = Round(width * 0.50);
        int left = Round((width - side) / 2.0);
        int top = Round(height * 0.32 - side / 2.0);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            Program.DrawMark(dc, new Rect(left, top, side, side));
        }

        return Rasterize(visual, width, height);
    }

    /// <summary>The header mark: the tile at 86% of its box, so it sits off the window edge.</summary>
    private static BitmapSource RenderSmall(int size)
    {
        int side = Round(size * 0.86);
        int inset = (size - side) / 2;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            Program.DrawMark(dc, new Rect(inset, inset, side, side));
        }

        return Rasterize(visual, size, size);
    }

    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    private static BitmapSource Rasterize(Visual visual, int width, int height)
    {
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }
}
