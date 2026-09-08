using System.Text;
using System.Text.Json;
using Driftwall.Core;

namespace Driftwall.Sources;

/// <summary>
/// Wallhaven (https://wallhaven.cc/api/v1) — user-curated wallpapers, every one already shot or
/// rendered at desktop resolution. Anonymous access covers the SFW catalogue; an optional API key
/// raises the rate limit, applies the account's own browsing filters and is the only way to reach
/// NSFW results. Top and Category read the toplist, Search sorts by relevance, Random uses the
/// random listing.
/// </summary>
/// <remarks>
/// Wallhaven hosts uploads from thousands of people under no single licence, so
/// <see cref="PhotoItem.License"/> is deliberately vague and <see cref="PhotoItem.SourcePageUrl"/>
/// always points at the wallpaper page, where the real terms and the uploader's stated source live.
/// The API allows 45 requests per minute (X-RateLimit-Limit) and answers 429 past that.
/// </remarks>
public sealed class WallhavenSource : PhotoSourceBase
{
    private const string SearchUrl = "https://wallhaven.cc/api/v1/search";

    /// <summary>Listings are a fixed 24 items; the API exposes no per-page parameter.</summary>
    private const int ApiPageSize = 24;

    /// <summary>Ceiling on requests per fetch, so one page cannot eat the 45/minute budget.</summary>
    private const int MaxRequestsPerFetch = 3;

    /// <summary>general/anime/people bitmask. General-only unless the selection overrides it.</summary>
    private const string DefaultCategoryMask = "100";

    /// <summary>Category ids double as the tag query, because Wallhaven has no category endpoint.</summary>
    private static readonly SourceCategory[] _categories =
    {
        new("nature", "Nature"),
        new("landscape", "Landscape"),
        new("space", "Space"),
        new("minimal", "Minimal"),
        new("abstract", "Abstract"),
        new("city", "City"),
        new("cars", "Cars"),
        new("technology", "Technology"),
        new("animals", "Animals"),
        new("fantasy", "Fantasy"),
        new("dark", "Dark"),
        new("sci-fi", "Sci-Fi"),
        new("mountains", "Mountains"),
        new("ocean", "Ocean"),
        new("forest", "Forest"),
        new("sunset", "Sunset"),
        new("architecture", "Architecture"),
        new("texture", "Texture"),
    };

    /// <summary>The toplist stops at one year, so "all" is not offered (it still maps to 1y).</summary>
    private static readonly string[] _timeRanges = { "day", "week", "month", "year" };

    public override string Id => "wallhaven";
    public override string DisplayName => "Wallhaven";
    public override string Description => "Community-curated wallpapers at desktop resolutions. Works without an account.";

    public override bool RequiresApiKey => false;
    public override string? ApiKeyHelpUrl => "https://wallhaven.cc/settings/account";
    public override string? LicenseUrl => "https://wallhaven.cc/faq";

    public override IReadOnlyList<string> SupportedTimeRanges => _timeRanges;
    public override IReadOnlyList<SourceCategory> Categories => _categories;

    public override async Task<PhotoPage> FetchAsync(SourceQuery query, SourceContext context, CancellationToken ct)
    {
        var apiKey = NullIfBlank(context.GetApiKey(Id));
        var wanted = Math.Max(1, query.PageSize);

        // Caller pages are any size, API pages are always 24. Walking the listing by item offset
        // keeps a page boundary that lands mid-response from skipping or repeating wallpapers.
        var offset = (Math.Max(1, query.Page) - 1) * wanted;
        var firstPage = offset / ApiPageSize + 1;
        var skip = offset % ApiPageSize;
        var requests = Math.Clamp((skip + wanted + ApiPageSize - 1) / ApiPageSize, 1, MaxRequestsPerFetch);

        var items = new List<PhotoItem>(wanted);
        string? seed = null;
        var hasMore = false;

        try
        {
            for (var i = 0; i < requests && items.Count < wanted; i++)
            {
                var page = firstPage + i;
                var url = BuildUrl(query, context, apiKey is not null, page, seed);

                using var doc = await Net.GetJsonDocumentAsync(
                    context.Http, url, ct, request => AddApiKey(request, apiKey), DisplayName).ConfigureAwait(false);

                var root = doc.RootElement;
                Collect(root, items, query, i == 0 ? skip : 0);

                // A random listing returns the seed it used; replaying it keeps the remaining
                // requests of this fetch free of repeats. The next fetch reshuffles, which is
                // exactly what a wallpaper shuffler wants.
                seed ??= ReadSeed(root);

                hasMore = page < ReadLastPage(root);
                if (!hasMore) break;
            }
        }
        catch (JsonException ex)
        {
            throw new SourceException("Wallhaven returned a response Driftwall could not read.", inner: ex);
        }

        if (items.Count > wanted)
            items.RemoveRange(wanted, items.Count - wanted);

        return items.Count == 0 && !hasMore ? PhotoPage.Empty : new PhotoPage(items, hasMore);
    }

    private static string BuildUrl(SourceQuery query, SourceContext context, bool authenticated, int page, string? seed)
    {
        var url = new StringBuilder(SearchUrl)
            .Append("?page=").Append(page)
            .Append("&order=desc")
            .Append("&categories=").Append(ResolveCategoryMask(context))
            .Append("&purity=").Append(ResolvePurity(query, authenticated));

        switch (query.Mode)
        {
            case QueryMode.Search when !string.IsNullOrWhiteSpace(query.Keyword):
                url.Append("&sorting=relevance&q=").Append(Net.Esc(query.Keyword.Trim()));
                break;

            case QueryMode.Category when !string.IsNullOrWhiteSpace(query.Category):
                AppendToplist(url, query).Append("&q=").Append(Net.Esc(query.Category.Trim()));
                break;

            case QueryMode.Random:
                url.Append("&sorting=random");
                break;

            // Top, plus Search/Category with nothing to search for.
            default:
                AppendToplist(url, query);
                break;
        }

        if (MapRatios(query.Orientation) is { } ratios)
            url.Append("&ratios=").Append(ratios);

        if (MapAtLeast(query) is { } atleast)
            url.Append("&atleast=").Append(atleast);

        if (seed is not null)
            url.Append("&seed=").Append(seed);

        return url.ToString();
    }

    private static StringBuilder AppendToplist(StringBuilder url, SourceQuery query) =>
        url.Append("&sorting=toplist&topRange=").Append(MapTopRange(query.TimeRange));

    /// <summary>
    /// The docs pass the key as ?apikey=; the X-API-Key header is the equivalent form and keeps the
    /// secret out of URLs, proxy logs and crash reports.
    /// </summary>
    private static void AddApiKey(HttpRequestMessage request, string? apiKey)
    {
        if (apiKey is not null)
            request.Headers.TryAddWithoutValidation("X-API-Key", apiKey);
    }

    private static string ResolveCategoryMask(SourceContext context) =>
        context.Selection.Options.TryGetValue("categories", out var raw) && IsCategoryMask(raw?.Trim())
            ? raw!.Trim()
            : DefaultCategoryMask;

    private static bool IsCategoryMask(string? value) =>
        value is { Length: 3 } and not "000"
        && value[0] is '0' or '1' && value[1] is '0' or '1' && value[2] is '0' or '1';

    /// <summary>sfw / sketchy / nsfw bitmask. NSFW is served to authenticated accounts only.</summary>
    private static string ResolvePurity(SourceQuery query, bool authenticated) =>
        query.SafeSearch ? "100" : authenticated ? "111" : "110";

    private static string MapTopRange(string? timeRange) => timeRange?.Trim().ToLowerInvariant() switch
    {
        "day" => "1d",
        "week" => "1w",
        // One year is the widest window the toplist offers, so "all" lands there too.
        "year" or "all" => "1y",
        _ => "1M",
    };

    /// <summary>"landscape" and "portrait" are the site's All Wide / All Portrait ratio groups.</summary>
    private static string? MapRatios(PhotoOrientation orientation) => orientation switch
    {
        PhotoOrientation.Landscape => "landscape",
        PhotoOrientation.Portrait => "portrait",
        PhotoOrientation.Square => "1x1",
        _ => null,
    };

    private static string? MapAtLeast(SourceQuery query)
    {
        var width = Math.Max(0, query.MinWidth);
        var height = Math.Max(0, query.MinHeight);
        return width == 0 && height == 0 ? null : $"{width}x{height}";
    }

    /// <summary>Reads one listing, dropping <paramref name="skip"/> leading entries the previous caller page already used.</summary>
    private void Collect(JsonElement root, List<PhotoItem> into, SourceQuery query, int skip)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return;

        foreach (var entry in data.EnumerateArray())
        {
            if (skip > 0)
            {
                skip--;
                continue;
            }

            if (MapWallpaper(entry) is { } item && Accepts(item, query))
                into.Add(item);
        }
    }

    private PhotoItem? MapWallpaper(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object) return null;
        if (Text(entry, "id") is not { } id) return null;
        if (Text(entry, "path") is not { } path) return null;

        entry.TryGetProperty("thumbs", out var thumbs);
        var category = Text(entry, "category");

        return new PhotoItem
        {
            Id = id,
            ProviderId = Id,
            ProviderName = DisplayName,
            // "large" is ~432px wide; "small" and "original" are both 300px.
            ThumbnailUrl = Text(thumbs, "large") ?? Text(thumbs, "original") ?? Text(thumbs, "small"),
            // Wallhaven publishes no mid-size derivative, so the preview is the wallpaper file
            // itself. The download cache turns that around: opening a photo pre-fetches its bytes.
            PreviewUrl = path,
            FullUrl = path,
            Width = Number(entry, "dimension_x"),
            Height = Number(entry, "dimension_y"),
            Title = BuildTitle(category, Text(entry, "resolution")),
            // Listings carry no uploader; "source" is where the uploader says the art came from.
            AuthorUrl = HttpUrl(Text(entry, "source")),
            SourcePageUrl = Text(entry, "url"),
            License = "Varies — check the source page",
            // Only /w/{id} returns tags[], which would cost one request per photo; the listing's
            // category is the single classification it does hand back.
            Tags = category is null ? Array.Empty<string>() : new[] { category },
            // DownloadTrackUrl stays null: Wallhaven asks for no usage ping.
        };
    }

    /// <summary>Wallhaven titles nothing, so the category and native resolution stand in.</summary>
    private static string BuildTitle(string? category, string? resolution)
    {
        var subject = category is null
            ? "Wallpaper"
            : char.ToUpperInvariant(category[0]) + category[1..] + " wallpaper";

        return resolution is null ? subject : $"{subject} ({resolution})";
    }

    /// <summary>Backstop for what the query parameters only approximate.</summary>
    private static bool Accepts(PhotoItem photo, SourceQuery query) =>
        (query.Orientation == PhotoOrientation.Any || photo.Orientation == query.Orientation)
        && (query.MinWidth <= 0 || photo.Width >= query.MinWidth)
        && (query.MinHeight <= 0 || photo.Height >= query.MinHeight);

    /// <summary>Documented as [a-zA-Z0-9]{6}; anything else is not echoed back into a URL.</summary>
    private static string? ReadSeed(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("meta", out var meta)) return null;
        if (Text(meta, "seed") is not { Length: 6 } seed) return null;

        foreach (var c in seed)
        {
            if (!char.IsAsciiLetterOrDigit(c)) return null;
        }

        return seed;
    }

    private static int ReadLastPage(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("meta", out var meta)
            ? Number(meta, "last_page")
            : 0;

    private static string? Text(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? NullIfBlank(value.GetString())
            : null;

    private static int Number(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : 0;

    private static string? HttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? value
            : null;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
