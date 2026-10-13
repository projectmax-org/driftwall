using Driftwall.Core;
using Driftwall.Wallpaper;

namespace Driftwall.Sources;

/// <summary>
/// The catalogue of available photo sources. Built once at start-up; every source instance is shared,
/// which is why implementations are required to be stateless.
/// </summary>
public sealed class SourceRegistry
{
    private readonly Dictionary<string, IPhotoSource> _byId;

    public SourceRegistry(CollectionsStore collections)
    {
        All =
        [
            // No key required — these make the app useful the moment it is installed.
            new WallhavenSource(),
            new BingDailySource(),
            new RedditSource(),
            new NasaApodSource(),
            new RssSource(),
            new LocalFolderSource(),
            new CollectionSource(collections),

            // Free key required.
            new UnsplashSource(),
            new PexelsSource(),
            new PixabaySource(),
            new FlickrSource(),
        ];

        _byId = All.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<IPhotoSource> All { get; }

    /// <summary>Sources that work without the user configuring anything.</summary>
    public IEnumerable<IPhotoSource> ReadyToUse => All.Where(s => !s.RequiresApiKey);

    public IPhotoSource? Find(string? id) =>
        id is not null && _byId.TryGetValue(id, out var source) ? source : null;

    /// <summary>Resolves the sources for the selections the user has switched on.</summary>
    public IEnumerable<(IPhotoSource Source, SourceSelection Selection)> Resolve(AppSettings settings)
    {
        foreach (var selection in settings.Sources)
        {
            if (!selection.Enabled) continue;
            var source = Find(selection.ProviderId);
            if (source is not null) yield return (source, selection);
        }
    }

    /// <summary>
    /// The size and shape photos are fetched for. A photo spanning the desktop has to cover the whole
    /// span, so it is sized for that; otherwise for the largest display. Displays of mixed shapes that
    /// each get their own photo ask for either orientation, so a portrait display can be given a
    /// portrait photo and a landscape one a landscape photo.
    /// </summary>
    public static (int Width, int Height, PhotoOrientation Orientation) TargetFor(IReadOnlyList<MonitorInfo> monitors, AppSettings settings)
    {
        int width, height;
        if (settings.MultiMonitor == MultiMonitorMode.SpanAcrossMonitors)
        {
            width = monitors.Max(m => m.X + m.Width) - monitors.Min(m => m.X);
            height = monitors.Max(m => m.Y + m.Height) - monitors.Min(m => m.Y);
        }
        else
        {
            width = monitors.Max(m => m.Width);
            height = monitors.Max(m => m.Height);

            if (settings.MultiMonitor == MultiMonitorMode.DifferentPerMonitor
                && monitors.Any(m => m.Width >= m.Height) && monitors.Any(m => m.Width < m.Height))
                return (width, height, PhotoOrientation.Any);
        }

        return (width, height, width >= height ? PhotoOrientation.Landscape : PhotoOrientation.Portrait);
    }

    /// <summary>
    /// The size a photo must reach to be worth fetching. Usually the target itself; where landscape and
    /// portrait displays each get their own photo, the widest by tallest of all of them would turn
    /// away the photos for both shapes, so each shape only has to fit the smaller of the two.
    /// Whether a photo is too small for the display it actually goes to is checked once it is downloaded.
    /// </summary>
    public static (int Width, int Height) FloorFor(IReadOnlyList<MonitorInfo> monitors, AppSettings settings)
    {
        var (width, height, orientation) = TargetFor(monitors, settings);
        if (orientation != PhotoOrientation.Any || settings.MultiMonitor != MultiMonitorMode.DifferentPerMonitor)
            return (width, height);

        var landscape = monitors.Where(m => m.Width >= m.Height).ToList();
        var portrait = monitors.Where(m => m.Width < m.Height).ToList();
        return (Math.Min(landscape.Max(m => m.Width), portrait.Max(m => m.Width)),
                Math.Min(landscape.Max(m => m.Height), portrait.Max(m => m.Height)));
    }

    /// <summary>
    /// Builds the query a selection describes, sized for the target given. Without an
    /// <paramref name="orientation"/> it follows the target's shape; without a floor, the smallest
    /// size it asks for follows the target too.
    /// </summary>
    public static SourceQuery BuildQuery(
        SourceSelection selection, AppSettings settings, int page, int pageSize, int targetWidth, int targetHeight,
        PhotoOrientation? orientation = null, (int Width, int Height)? floor = null)
    {
        int minWidth = 0, minHeight = 0;
        if (settings.RejectLowResolution)
        {
            minWidth = (int)((floor?.Width ?? targetWidth) * settings.MinResolutionRatio);
            minHeight = (int)((floor?.Height ?? targetHeight) * settings.MinResolutionRatio);
        }

        return new SourceQuery
        {
            Mode = selection.Mode,
            Keyword = selection.Keyword,
            Category = selection.Category,
            TimeRange = selection.TimeRange,
            Orientation = orientation ?? (targetWidth >= targetHeight ? PhotoOrientation.Landscape : PhotoOrientation.Portrait),
            MinWidth = minWidth,
            MinHeight = minHeight,
            SafeSearch = true,
            Page = page,
            PageSize = pageSize,
        };
    }

    public SourceContext BuildContext(SourceSelection selection, AppSettings settings, Func<string, string?> getApiKey, int targetWidth, int targetHeight) =>
        new()
        {
            Http = Net.Client,
            GetApiKey = getApiKey,
            Selection = selection,
            Settings = settings,
            TargetWidth = targetWidth,
            TargetHeight = targetHeight,
        };
}
