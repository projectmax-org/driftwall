using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Driftwall.Core;

namespace Driftwall.Sources;

/// <summary>
/// Pixabay photos (https://pixabay.com/api/docs/). The free key travels in the "key" query
/// parameter. Terms: 100 requests per 60 seconds, responses must be cached for 24 hours,
/// image URLs may not be hot-linked permanently (Driftwall downloads before use), and the
/// provider must be credited wherever results are shown.
/// </summary>
public sealed class PixabaySource : PhotoSourceBase
{
    private const string ApiBase = "https://pixabay.com/api/";

    /// <summary>The API exposes at most 500 hits per query; asking past that answers HTTP 400.</summary>
    private const int MaxReachableResults = 500;

    private const int MinPageSize = 3;
    private const int MaxPageSize = 200;

    /// <summary>fullHDURL is capped at 1920px, so only a wider desktop justifies the original file.</summary>
    private const int FullHdWidth = 1920;

    private static readonly SourceCategory[] _categories =
    {
        new("backgrounds", "Backgrounds"),
        new("fashion", "Fashion"),
        new("nature", "Nature"),
        new("science", "Science"),
        new("education", "Education"),
        new("feelings", "Feelings"),
        new("health", "Health"),
        new("people", "People"),
        new("religion", "Religion"),
        new("places", "Places"),
        new("animals", "Animals"),
        new("industry", "Industry"),
        new("computer", "Computer"),
        new("food", "Food"),
        new("sports", "Sports"),
        new("transportation", "Transportation"),
        new("travel", "Travel"),
        new("buildings", "Buildings"),
        new("business", "Business"),
        new("music", "Music"),
    };

    private static readonly HashSet<string> _categoryIds =
        new(_categories.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);

    public override string Id => "pixabay";
    public override string DisplayName => "Pixabay";
    public override string Description => "Community photos under the Pixabay Content License. Needs a free API key.";

    public override bool RequiresApiKey => true;
    public override string? ApiKeyHelpUrl => "https://pixabay.com/api/docs/";
    public override string? LicenseUrl => "https://pixabay.com/service/license-summary/";

    public override IReadOnlyList<SourceCategory> Categories => _categories;

    /// <summary>
    /// Empty on purpose: Pixabay cannot filter by upload date. <see cref="SourceQuery.TimeRange"/> is
    /// still honoured as far as the API allows, by switching between the popular and latest orderings.
    /// </summary>
    public override IReadOnlyList<string> SupportedTimeRanges => Array.Empty<string>();

    public override string? Validate(SourceContext context)
    {
        var problem = base.Validate(context);
        if (problem is not null) return problem;

        var selection = context.Selection;
        if (selection.Mode == QueryMode.Category && !_categoryIds.Contains(selection.Category ?? string.Empty))
            return "Pick one of Pixabay's built-in categories.";

        return null;
    }

    public override async Task<PhotoPage> FetchAsync(SourceQuery query, SourceContext context, CancellationToken ct)
    {
        var key = context.ApiKey;
        if (string.IsNullOrWhiteSpace(key))
            throw new SourceException("Pixabay needs a free API key. Add one in Settings → API keys.", isAuth: true);

        var pageSize = Math.Clamp(query.PageSize, MinPageSize, MaxPageSize);
        var page = query.Mode == QueryMode.Random ? RandomPage(pageSize) : Math.Max(1, query.Page);
        if ((long)(page - 1) * pageSize >= MaxReachableResults) return PhotoPage.Empty;

        using var doc = await SendAsync(context.Http, BuildUrl(key, query, page, pageSize), ct).ConfigureAwait(false);
        if (doc is null) return PhotoPage.Empty;

        try
        {
            return ReadPage(doc.RootElement, query, context, page, pageSize);
        }
        catch (Exception ex) when (ex is not SourceException and not OperationCanceledException)
        {
            throw new SourceException("Pixabay returned a response Driftwall could not read.", inner: ex);
        }
    }

    private PhotoPage ReadPage(JsonElement root, SourceQuery query, SourceContext context, int page, int pageSize)
    {
        // TryGetProperty throws InvalidOperationException on anything that is not an object, so the
        // root's kind is settled before it (or "totalHits" below) is read.
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array)
            throw new SourceException("Pixabay returned an unexpected response shape.");

        var items = new List<PhotoItem>(hits.GetArrayLength());
        var sawFullResolution = false;

        foreach (var hit in hits.EnumerateArray())
        {
            if (hit.ValueKind != JsonValueKind.Object) continue;
            sawFullResolution |= GetString(hit, "fullHDURL") is not null || GetString(hit, "imageURL") is not null;

            var item = MapHit(hit, context.TargetWidth);
            if (item is not null && Matches(item, query)) items.Add(item);
        }

        if (query.Mode == QueryMode.Random) Random.Shared.Shuffle(CollectionsMarshal.AsSpan(items));

        var reachable = Math.Min(GetInt(root, "totalHits"), MaxReachableResults);
        var hasMore = query.Mode == QueryMode.Random
            ? reachable > pageSize
            : (long)page * pageSize < reachable;

        if (items.Count == 0 && !hasMore) return PhotoPage.Empty;

        // fullHDURL/imageURL ship only to accounts approved for full API access; without them the
        // best available file is largeImageURL at 1280px, which under-fills a 1080p desktop.
        var notice = items.Count > 0 && !sawFullResolution
            ? "Pixabay is only serving 1280px images. Request full API access on pixabay.com for sharper wallpapers."
            : null;

        return new PhotoPage(items, hasMore, notice);
    }

    private PhotoItem? MapHit(JsonElement hit, int targetWidth)
    {
        var id = ReadId(hit);
        if (id is null) return null;

        var webformat = GetString(hit, "webformatURL");
        var large = GetString(hit, "largeImageURL");
        var full = PickFullUrl(hit, large, webformat, targetWidth);
        if (full is null) return null;

        var tags = SplitTags(GetString(hit, "tags"));
        var user = GetString(hit, "user");
        var userId = GetInt(hit, "user_id");

        return new PhotoItem
        {
            Id = id,
            ProviderId = Id,
            ProviderName = DisplayName,
            // Documented webformatURL variants are _180, _340 and _960 only — there is no _1280 form.
            ThumbnailUrl = WebformatVariant(webformat, "_340") ?? GetString(hit, "previewURL") ?? webformat,
            PreviewUrl = large ?? WebformatVariant(webformat, "_960") ?? webformat,
            FullUrl = full,
            // imageWidth/imageHeight describe the ORIGINAL upload, which is usually larger than the
            // file FullUrl actually serves (1920px for fullHDURL, 1280px for largeImageURL).
            Width = GetInt(hit, "imageWidth"),
            Height = GetInt(hit, "imageHeight"),
            Title = TitleFrom(tags),
            AuthorName = user,
            AuthorUrl = user is null || userId <= 0 ? null : $"https://pixabay.com/users/{Net.Esc(user)}-{userId}/",
            SourcePageUrl = GetString(hit, "pageURL"),
            License = "Pixabay Content License",
            Tags = tags,
        };
    }

    private static string BuildUrl(string key, SourceQuery query, int page, int pageSize)
    {
        var url = new StringBuilder(ApiBase)
            .Append("?key=").Append(Net.Esc(key))
            .Append("&image_type=photo")
            .Append("&page=").Append(page)
            .Append("&per_page=").Append(pageSize)
            .Append("&safesearch=").Append(query.SafeSearch ? "true" : "false")
            .Append("&order=").Append(OrderFor(query.TimeRange));

        if (OrientationFor(query.Orientation) is { } orientation) url.Append("&orientation=").Append(orientation);
        if (query.MinWidth > 0) url.Append("&min_width=").Append(query.MinWidth);
        if (query.MinHeight > 0) url.Append("&min_height=").Append(query.MinHeight);

        switch (query.Mode)
        {
            case QueryMode.Search when !string.IsNullOrWhiteSpace(query.Keyword):
                var keyword = query.Keyword.Trim();
                url.Append("&q=").Append(Net.Esc(keyword.Length > 100 ? keyword[..100] : keyword));
                break;

            // An unknown category is a hard 400, so send it only when it is one of the documented ids.
            case QueryMode.Category when query.Category is { } category && _categoryIds.Contains(category.Trim()):
                url.Append("&category=").Append(category.Trim().ToLowerInvariant());
                break;

            case QueryMode.Top:
                // Editor's Choice is Pixabay's only curation signal and it suits full-screen use.
                // Random deliberately skips it so the shuffle draws from the whole popular pool.
                url.Append("&editors_choice=true");
                break;
        }

        return url.ToString();
    }

    /// <summary>Pixabay has no date filter; popular vs latest is the closest control it offers.</summary>
    private static string OrderFor(string? timeRange) =>
        timeRange?.Trim().ToLowerInvariant() is "day" or "week" ? "latest" : "popular";

    /// <summary>Null asks for the API default ("all"); Pixabay has no square filter, so that is post-filtered.</summary>
    private static string? OrientationFor(PhotoOrientation orientation) => orientation switch
    {
        PhotoOrientation.Landscape => "horizontal",
        PhotoOrientation.Portrait => "vertical",
        _ => null,
    };

    /// <summary>No random endpoint exists, so Random draws a page from inside the reachable window.</summary>
    private static int RandomPage(int pageSize) =>
        Random.Shared.Next(1, Math.Max(1, MaxReachableResults / pageSize) + 1);

    private static bool Matches(PhotoItem item, SourceQuery query)
    {
        if (query.Orientation == PhotoOrientation.Square && item.Orientation != PhotoOrientation.Square) return false;
        return item.Width >= query.MinWidth && item.Height >= query.MinHeight;
    }

    private static string? PickFullUrl(JsonElement hit, string? large, string? webformat, int targetWidth)
    {
        var fullHd = GetString(hit, "fullHDURL");
        var original = GetString(hit, "imageURL");

        return targetWidth > FullHdWidth
            ? original ?? fullHd ?? large ?? webformat
            : fullHd ?? original ?? large ?? webformat;
    }

    /// <summary>Swaps the size token in a webformatURL ("..._640.jpg" to "..._340.jpg").</summary>
    private static string? WebformatVariant(string? webformatUrl, string sizeToken)
    {
        if (webformatUrl is null) return null;

        var at = webformatUrl.LastIndexOf("_640", StringComparison.Ordinal);
        return at < 0 ? null : string.Concat(webformatUrl.AsSpan(0, at), sizeToken, webformatUrl.AsSpan(at + 4));
    }

    /// <summary>Hits carry no title, so the leading tags stand in for one.</summary>
    private static string? TitleFrom(IReadOnlyList<string> tags)
    {
        if (tags.Count == 0) return null;

        var text = string.Join(", ", tags.Take(3));
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static IReadOnlyList<string> SplitTags(string? tags) =>
        tags is null
            ? Array.Empty<string>()
            : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? ReadId(JsonElement hit)
    {
        if (!hit.TryGetProperty("id", out var id)) return null;

        return id.ValueKind switch
        {
            JsonValueKind.Number => id.GetRawText(),
            JsonValueKind.String => id.GetString(),
            _ => null,
        };
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static int GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : 0;

    /// <summary>
    /// Sent by hand rather than through <see cref="Net.GetJsonDocumentAsync"/>: Pixabay answers a bad
    /// key with HTTP 400 and a plain-text body, which would otherwise surface as a generic failure
    /// instead of an auth problem. Returns null when the request ran off the end of the results.
    /// </summary>
    private async Task<JsonDocument?> SendAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (res.StatusCode == HttpStatusCode.BadRequest)
        {
            var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // The docs only promise "a description of the issue in plain text", so match loosely.
            if (body.Contains("out of valid range", StringComparison.OrdinalIgnoreCase)) return null;
            if (body.Contains("API key", StringComparison.OrdinalIgnoreCase))
                throw new SourceException("Pixabay rejected the API key. Check it in Settings → API keys.", isAuth: true);

            var detail = body.Trim();
            if (detail.Length > 200) detail = detail[..200];
            throw new SourceException(detail.Length == 0
                ? "Pixabay rejected the request (HTTP 400)."
                : $"Pixabay rejected the request: {detail}");
        }

        Net.EnsureOk(res, DisplayName);

        await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        try
        {
            return await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            // A captive portal or proxy can answer 200 with HTML; that must not reach the UI as a crash.
            throw new SourceException("Pixabay returned a response that was not valid JSON.", inner: ex);
        }
    }
}
