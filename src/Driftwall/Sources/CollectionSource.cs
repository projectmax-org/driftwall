using Driftwall.Core;

namespace Driftwall.Sources;

/// <summary>
/// Serves photos the user saved into one of their own collections.
/// <para>
/// This is what closes the loop on browsing: a photo found through any other source can be kept and
/// then used as a wallpaper source in its own right, with no network access at all.
/// </para>
/// The collection to read is the selection's <c>Category</c>, which is what the pickers in Browse and
/// Settings change. <c>Options["collectionId"]</c> is only a fallback: Browse copies the options of a
/// configured source, so a stale id there must never win over what the user picked.
/// </summary>
public sealed class CollectionSource : PhotoSourceBase
{
    public const string CollectionIdOption = "collectionId";

    private readonly CollectionsStore _collections;

    public CollectionSource(CollectionsStore collections) => _collections = collections;

    public override string Id => "collection";

    public override string DisplayName => "My collections";

    public override string Description => "Photos you saved inside Driftwall. Works offline.";

    public override bool SupportsTop => true;

    public override bool SupportsSearch => true;

    public override bool SupportsCategories => true;

    /// <summary>The user's collections are the categories, so this list changes as they add them.</summary>
    public override IReadOnlyList<SourceCategory> Categories =>
        _collections.Collections.Select(c => new SourceCategory(c.Id, $"{c.Name} ({c.Count})")).ToList();

    public override string? Validate(SourceContext context)
    {
        var id = ResolveCollectionId(context);
        if (id is null) return "Choose which collection to use.";

        var collection = _collections.Find(id);
        if (collection is null) return "That collection no longer exists.";
        if (collection.Count == 0) return $"\"{collection.Name}\" is empty. Save some photos to it first.";

        return null;
    }

    public override Task<PhotoPage> FetchAsync(SourceQuery query, SourceContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var id = ResolveCollectionId(context);
        if (id is null) return Task.FromResult(PhotoPage.Message("No collection selected."));

        IEnumerable<PhotoItem> photos = _collections.PhotosIn(id);

        if (query.Mode == QueryMode.Search && !string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            photos = photos.Where(p =>
                Contains(p.Title, keyword) ||
                Contains(p.AuthorName, keyword) ||
                Contains(p.ProviderName, keyword) ||
                p.Tags.Any(t => Contains(t, keyword)));
        }

        var all = photos.ToList();

        if (query.Mode == QueryMode.Random)
        {
            // One permutation of the whole collection, sliced by page. Seeding on the page instead
            // gave every page its own shuffle, so pages overlapped and some photos were never served.
            // This stays stable across restarts and reshuffles only when the collection changes size.
            var random = new Random(SeedFor(id, all.Count));
            for (int i = all.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (all[i], all[j]) = (all[j], all[i]);
            }
        }

        int skip = Math.Max(0, (query.Page - 1) * query.PageSize);
        var page = all.Skip(skip).Take(query.PageSize).ToList();

        return Task.FromResult(new PhotoPage(page, skip + page.Count < all.Count));
    }

    private string? ResolveCollectionId(SourceContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.Selection.Category))
            return context.Selection.Category;

        if (context.Selection.Options.TryGetValue(CollectionIdOption, out var fromOptions) && !string.IsNullOrWhiteSpace(fromOptions))
            return fromOptions;

        // Fall back to the first collection that actually has something in it.
        return _collections.FirstWithPhotos()?.Id;
    }

    private static int SeedFor(string collectionId, int count)
    {
        // Hand-rolled because string.GetHashCode is randomised per process and the seed has to
        // survive a restart.
        unchecked
        {
            int seed = 17;
            foreach (char c in collectionId) seed = (seed * 31) + c;
            return (seed * 31) + count;
        }
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
