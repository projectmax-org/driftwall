using Driftwall.Core;

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

    /// <summary>Builds the query a selection describes, sized for the largest monitor in play.</summary>
    public static SourceQuery BuildQuery(SourceSelection selection, AppSettings settings, int page, int pageSize, int targetWidth, int targetHeight)
    {
        int minWidth = 0, minHeight = 0;
        if (settings.RejectLowResolution)
        {
            minWidth = (int)(targetWidth * settings.MinResolutionRatio);
            minHeight = (int)(targetHeight * settings.MinResolutionRatio);
        }

        return new SourceQuery
        {
            Mode = selection.Mode,
            Keyword = selection.Keyword,
            Category = selection.Category,
            TimeRange = selection.TimeRange,
            Orientation = targetWidth >= targetHeight ? PhotoOrientation.Landscape : PhotoOrientation.Portrait,
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
