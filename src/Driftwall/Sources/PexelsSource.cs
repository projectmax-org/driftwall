using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Driftwall.Core;

namespace Driftwall.Sources;

/// <summary>
/// Pexels stock photography (https://api.pexels.com/v1). Top and Random read the curated feed;
/// Search and Category run keyword searches, because Pexels has no category endpoint and the
/// categories below are canned search terms. Every Pexels photo is human-moderated and the API
/// exposes neither a safe-search switch nor a time window, so <see cref="SourceQuery.SafeSearch"/>
/// and <see cref="SourceQuery.TimeRange"/> have nothing to map to.
/// </summary>
/// <remarks>
/// The Pexels API guidelines forbid "making Pexels content available as a wallpaper app", so this
/// source needs written permission from Pexels before it ships. Attribution is mandatory: show the
/// photographer plus a link back to Pexels wherever a photo is displayed.
/// </remarks>
public sealed class PexelsSource : PhotoSourceBase
{
    private const string ApiBase = "https://api.pexels.com/v1";

    /// <summary>Documented per_page ceiling.</summary>
    private const int MaxPerPage = 80;

    /// <summary>How many photos deep into the curated feed a Random fetch may start.</summary>
    private const int RandomDepth = 1000;

    private static readonly SourceCategory[] _categories =
    {
        new("nature", "Nature"),
        new("landscape", "Landscape"),
        new("city", "City"),
        new("abstract", "Abstract"),
        new("space", "Space"),
        new("animals", "Animals"),
        new("ocean", "Ocean"),
        new("mountains", "Mountains"),
        new("forest", "Forest"),
        new("minimal", "Minimal"),
        new("technology", "Technology"),
        new("architecture", "Architecture"),
        new("flowers", "Flowers"),
        new("sky", "Sky"),
        new("texture", "Texture"),
        new("dark", "Dark"),
        new("neon", "Neon"),
        new("travel", "Travel"),
    };

    /// <summary>Category ids whose own text makes a poor search term ("space" returns interiors).</summary>
    private static readonly Dictionary<string, string> _categorySearchTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["space"] = "outer space",
        ["abstract"] = "abstract background",
        ["dark"] = "dark background",
        ["texture"] = "texture background",
        ["minimal"] = "minimalist",
        ["neon"] = "neon lights",
    };

    public override string Id => "pexels";
    public override string DisplayName => "Pexels";
    public override string Description => "Curated free stock photography. Needs a free API key.";

    public override bool RequiresApiKey => true;
    public override string? ApiKeyHelpUrl => "https://www.pexels.com/api/";
    public override string? LicenseUrl => "https://www.pexels.com/license/";

    public override IReadOnlyList<SourceCategory> Categories => _categories;

    public override async Task<PhotoPage> FetchAsync(SourceQuery query, SourceContext context, CancellationToken ct)
    {
        var apiKey = context.GetApiKey(Id);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new SourceException("Pexels needs a free API key. Add one in Settings → API keys.", isAuth: true);

        var term = ResolveTerm(query);
        var perPage = Math.Clamp(query.PageSize, 1, MaxPerPage);
        var page = Math.Max(1, query.Page);

        // /curated has no orientation filter, so over-fetch to survive the client-side trim.
        if (term is null && query.Orientation != PhotoOrientation.Any)
            perPage = Math.Min(perPage * 2, MaxPerPage);

        // Pexels has no random endpoint: start the feed at an arbitrary page and let the caller's
        // paging walk forward from there.
        if (query.Mode == QueryMode.Random)
            page += Random.Shared.Next(Math.Max(1, RandomDepth / perPage));

        var url = term is null
            ? $"{ApiBase}/curated?page={page}&per_page={perPage}"
            : BuildSearchUrl(term, query, page, perPage);

        try
        {
            using var doc = await Net.GetJsonDocumentAsync(
                context.Http, url, ct,
                // Pexels takes the raw key as the Authorization value, with no scheme prefix.
                req => req.Headers.TryAddWithoutValidation("Authorization", apiKey),
                DisplayName).ConfigureAwait(false);

            return ReadPage(doc.RootElement, query, term);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new SourceException("Pexels returned a response Driftwall could not read.", inner: ex);
        }
    }

    private static string BuildSearchUrl(string term, SourceQuery query, int page, int perPage)
    {
        var url = $"{ApiBase}/search?query={Net.Esc(term)}&page={page}&per_page={perPage}";

        if (MapOrientation(query.Orientation) is { } orientation)
            url += "&orientation=" + orientation;

        if (MapSizeFloor(query.MinWidth, query.MinHeight) is { } size)
            url += "&size=" + size;

        return url;
    }

    private static string? ResolveTerm(SourceQuery query)
    {
        var raw = query.Mode switch
        {
            QueryMode.Search => query.Keyword,
            QueryMode.Category => query.Category,
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(raw)) return null;

        raw = raw.Trim();
        return query.Mode == QueryMode.Category && _categorySearchTerms.TryGetValue(raw, out var mapped) ? mapped : raw;
    }

    private static string? MapOrientation(PhotoOrientation orientation) => orientation switch
    {
        PhotoOrientation.Landscape => "landscape",
        PhotoOrientation.Portrait => "portrait",
        PhotoOrientation.Square => "square",
        _ => null,
    };

    /// <summary>
    /// The search "size" parameter is a minimum-megapixel filter (small 4MP, medium 12MP,
    /// large 24MP), not an image variant. Pick the largest tier that cannot exclude a photo the
    /// caller would have accepted, and let <see cref="Accepts"/> trim the rest.
    /// </summary>
    private static string? MapSizeFloor(int minWidth, int minHeight)
    {
        if (minWidth <= 0 || minHeight <= 0) return null;

        return ((long)minWidth * minHeight / 1_000_000d) switch
        {
            >= 24d => "large",
            >= 12d => "medium",
            >= 4d => "small",
            _ => null,
        };
    }

    private PhotoPage ReadPage(JsonElement root, SourceQuery query, string? term)
    {
        // TryGetProperty throws InvalidOperationException on anything that is not an object, so the
        // root's kind is settled before it is walked — a proxy or error body that answers with an
        // array or a bare string must read as "no photos", not as an exception.
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("photos", out var photos) || photos.ValueKind != JsonValueKind.Array)
            return PhotoPage.Empty;

        // Pexels ships no per-photo tags; the term a photo matched on is the only one available.
        var tags = term is null ? Array.Empty<string>() : new[] { term };
        var items = new List<PhotoItem>(photos.GetArrayLength());

        foreach (var photo in photos.EnumerateArray())
        {
            if (MapPhoto(photo, tags) is { } item && Accepts(item, query))
                items.Add(item);
        }

        if (query.Mode == QueryMode.Random)
            Random.Shared.Shuffle(CollectionsMarshal.AsSpan(items));

        var hasMore = root.TryGetProperty("next_page", out var next) && next.ValueKind == JsonValueKind.String;
        return new PhotoPage(items, hasMore);
    }

    private PhotoItem? MapPhoto(JsonElement photo, IReadOnlyList<string> tags)
    {
        if (photo.ValueKind != JsonValueKind.Object) return null;
        if (ReadId(photo) is not { } id) return null;
        if (!photo.TryGetProperty("src", out var src) || src.ValueKind != JsonValueKind.Object) return null;
        if (Text(src, "original") is not { } original) return null;

        return new PhotoItem
        {
            Id = id,
            ProviderId = Id,
            ProviderName = DisplayName,
            // "medium" is scaled to 350px high; "large2x" is the 940x650 box at DPR 2 (~1880px wide).
            ThumbnailUrl = Text(src, "medium") ?? Text(src, "small") ?? original,
            PreviewUrl = Text(src, "large2x") ?? Text(src, "large") ?? original,
            FullUrl = original,
            Width = Number(photo, "width"),
            Height = Number(photo, "height"),
            Title = Text(photo, "alt"),
            AuthorName = Text(photo, "photographer"),
            AuthorUrl = Text(photo, "photographer_url"),
            SourcePageUrl = Text(photo, "url"),
            License = "Pexels License",
            Tags = tags,
            // DownloadTrackUrl stays null: Pexels requires visible attribution, not a usage ping.
        };
    }

    /// <summary>Applies what the API could not: curated ignores orientation, and "size" only bounds megapixels.</summary>
    private static bool Accepts(PhotoItem photo, SourceQuery query) =>
        (query.Orientation == PhotoOrientation.Any || photo.Orientation == query.Orientation)
        && (query.MinWidth <= 0 || photo.Width >= query.MinWidth)
        && (query.MinHeight <= 0 || photo.Height >= query.MinHeight);

    private static string? ReadId(JsonElement photo) =>
        photo.TryGetProperty("id", out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt64(out var number) => number.ToString(CultureInfo.InvariantCulture),
                JsonValueKind.String => NullIfBlank(value.GetString()),
                _ => null,
            }
            : null;

    private static string? Text(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? NullIfBlank(value.GetString())
            : null;

    private static int Number(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : 0;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
