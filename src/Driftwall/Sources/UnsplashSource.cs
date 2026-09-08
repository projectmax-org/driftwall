using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Driftwall.Core;

namespace Driftwall.Sources;

/// <summary>
/// Unsplash via the official REST API (https://api.unsplash.com). Needs a free Access Key, sent as
/// "Authorization: Client-ID {key}". Demo applications are capped at 50 requests/hour, production
/// applications at 5000. The API guidelines make two things contractual: image bytes must come from
/// the urls the API hands back, and <see cref="PhotoItem.DownloadTrackUrl"/> must be pinged whenever
/// a photo is actually used as a wallpaper.
/// </summary>
public sealed class UnsplashSource : PhotoSourceBase
{
    private const string Api = "https://api.unsplash.com";

    /// <summary>Attribution links back to Unsplash must carry these, per the API guidelines.</summary>
    private const string Utm = "utm_source=driftwall&utm_medium=referral";

    /// <summary>Hard API cap on both per_page and the random endpoint's count.</summary>
    private const int MaxPerPage = 30;

    private static readonly SourceCategory[] _topics =
    [
        new("wallpapers", "Wallpapers"),
        new("nature", "Nature"),
        new("3d-renders", "3D Renders"),
        new("textures-patterns", "Textures"),
        new("architecture-interior", "Architecture"),
        new("travel", "Travel"),
        new("film", "Film"),
        new("street-photography", "Street Photography"),
        new("animals", "Animals"),
        new("experimental", "Experimental"),
        new("fashion-beauty", "Fashion & Beauty"),
        new("people", "People"),
        new("food-drink", "Food & Drink"),
        new("spirituality", "Spirituality"),
        new("business-work", "Business & Work"),
        new("health", "Health & Wellness"),
        new("sports", "Sports"),
        new("current-events", "Current Events"),
    ];

    public override string Id => "unsplash";
    public override string DisplayName => "Unsplash";
    public override string Description => "Editor-curated high-resolution photography, free under the Unsplash License.";

    public override bool RequiresApiKey => true;
    public override string? ApiKeyHelpUrl => "https://unsplash.com/oauth/applications";
    public override string? LicenseUrl => "https://unsplash.com/license";

    public override IReadOnlyList<SourceCategory> Categories => _topics;

    /// <summary>Empty: no Unsplash listing endpoint accepts a date window, so TimeRange has no meaning here.</summary>
    public override IReadOnlyList<string> SupportedTimeRanges => Array.Empty<string>();

    public override async Task<PhotoPage> FetchAsync(SourceQuery query, SourceContext context, CancellationToken ct)
    {
        var key = context.GetApiKey(Id);
        if (string.IsNullOrWhiteSpace(key))
            throw new SourceException("Unsplash needs a free Access Key. Add one in Settings → API keys.", isAuth: true);

        var page = Math.Max(1, query.Page);
        var perPage = Math.Clamp(query.PageSize, 1, MaxPerPage);

        try
        {
            return query.Mode switch
            {
                QueryMode.Search => await SearchAsync(query, context, key, page, perPage, ct).ConfigureAwait(false),
                QueryMode.Category => await TopicAsync(query, context, key, page, perPage, ct).ConfigureAwait(false),
                QueryMode.Random => await RandomAsync(query, context, key, perPage, ct).ConfigureAwait(false),
                _ => await EditorialAsync(query, context, key, page, perPage, ct).ConfigureAwait(false),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SourceException("Unsplash did not respond in time. Try again.");
        }
        catch (HttpRequestException ex)
        {
            throw new SourceException($"Could not reach Unsplash ({ex.Message}).", inner: ex);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new SourceException("Unsplash returned a response Driftwall could not read.", inner: ex);
        }
    }

    // ---------- endpoints ----------

    /// <summary>
    /// GET /photos - the hand-picked editorial feed, the closest thing the API still offers to "top":
    /// order_by=popular was deprecated in 2020 and is no longer a documented value on this endpoint.
    /// It also takes no orientation filter, so that narrowing happens client-side in <see cref="Map"/>.
    /// </summary>
    private async Task<PhotoPage> EditorialAsync(
        SourceQuery query, SourceContext context, string key, int page, int perPage, CancellationToken ct)
    {
        var url = $"{Api}/photos?page={page}&per_page={perPage}";
        using var doc = await GetAsync(url, key, context, ct).ConfigureAwait(false);
        return FromArray(doc.RootElement, query, context, key, perPage);
    }

    /// <summary>GET /search/photos - the only endpoint that reports a page count, so paging is exact.</summary>
    private async Task<PhotoPage> SearchAsync(
        SourceQuery query, SourceContext context, string key, int page, int perPage, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query.Keyword))
            return PhotoPage.Message("Enter a keyword to search Unsplash.");

        var url = new StringBuilder(Api)
            .Append("/search/photos?query=").Append(Net.Esc(query.Keyword))
            .Append("&page=").Append(page)
            .Append("&per_page=").Append(perPage)
            .Append("&content_filter=").Append(query.SafeSearch ? "high" : "low");
        AppendOrientation(url, query.Orientation);

        using var doc = await GetAsync(url.ToString(), key, context, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
        {
            return PhotoPage.Empty;
        }

        return new PhotoPage(Map(results, query, context, key), page < ReadInt(root, "total_pages"));
    }

    /// <summary>GET /topics/{slug}/photos - topics are Unsplash's curated categories.</summary>
    private async Task<PhotoPage> TopicAsync(
        SourceQuery query, SourceContext context, string key, int page, int perPage, CancellationToken ct)
    {
        // Unknown slugs are passed through so a topic added after this build still works.
        var slug = string.IsNullOrWhiteSpace(query.Category) ? "wallpapers" : query.Category.Trim();

        var url = new StringBuilder(Api)
            .Append("/topics/").Append(Net.Esc(slug))
            .Append("/photos?page=").Append(page)
            .Append("&per_page=").Append(perPage)
            .Append("&order_by=popular");
        AppendOrientation(url, query.Orientation);

        using var doc = await GetAsync(url.ToString(), key, context, ct).ConfigureAwait(false);
        return FromArray(doc.RootElement, query, context, key, perPage);
    }

    /// <summary>
    /// GET /photos/random - ignores page entirely and may repeat photos inside one response, so
    /// <see cref="Map"/> de-duplicates and the feed is always reported as having more.
    /// The topics filter is deliberately omitted: it takes public topic ids, not the slugs we expose.
    /// </summary>
    private async Task<PhotoPage> RandomAsync(
        SourceQuery query, SourceContext context, string key, int perPage, CancellationToken ct)
    {
        var url = new StringBuilder(Api)
            .Append("/photos/random?count=").Append(perPage)
            .Append("&content_filter=").Append(query.SafeSearch ? "high" : "low");
        AppendOrientation(url, query.Orientation);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
            url.Append("&query=").Append(Net.Esc(query.Keyword));

        using var doc = await GetAsync(url.ToString(), key, context, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return PhotoPage.Empty;

        return new PhotoPage(Map(root, query, context, key), HasMore: true);
    }

    // ---------- http ----------

    private async Task<JsonDocument> GetAsync(string url, string key, SourceContext context, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.TryAddWithoutValidation("Authorization", "Client-ID " + key);
        req.Headers.TryAddWithoutValidation("Accept-Version", "v1");

        using var res = await context.Http
            .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        // Unsplash answers an exhausted hourly quota with 403, which Net.EnsureOk would report as a bad
        // key, so the auth/rate-limit split is made here before deferring to the shared mapping.
        if (res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw await DescribeRejectionAsync(res, ct).ConfigureAwait(false);
        Net.EnsureOk(res, DisplayName);

        await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
    }

    private static async Task<SourceException> DescribeRejectionAsync(HttpResponseMessage res, CancellationToken ct)
    {
        var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var exhausted = res.Headers.TryGetValues("X-Ratelimit-Remaining", out var remaining)
            && remaining.FirstOrDefault() == "0";

        if (res.StatusCode == HttpStatusCode.Forbidden &&
            (exhausted || body.Contains("Rate Limit", StringComparison.OrdinalIgnoreCase)))
        {
            return new SourceException(
                "Unsplash hourly rate limit reached. Demo applications get 50 requests per hour - "
                + "apply for production access at unsplash.com/oauth/applications to raise it to 5000.",
                isRateLimit: true);
        }

        return new SourceException(
            "Unsplash rejected the Access Key. Check it in Settings → API keys.", isAuth: true);
    }

    // ---------- mapping ----------

    private PhotoPage FromArray(JsonElement root, SourceQuery query, SourceContext context, string key, int requested)
    {
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return PhotoPage.Empty;

        // HasMore keys off the raw count, not the filtered one, so a page the client-side filter
        // empties still lets the UI walk forward.
        return new PhotoPage(Map(root, query, context, key), root.GetArrayLength() >= requested);
    }

    private List<PhotoItem> Map(JsonElement array, SourceQuery query, SourceContext context, string key)
    {
        var fullWidth = Math.Clamp(context.TargetWidth, 1920, 7680);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<PhotoItem>(array.GetArrayLength());

        foreach (var element in array.EnumerateArray())
        {
            var item = ReadPhoto(element, key, fullWidth);
            if (item is null || !seen.Add(item.Id) || !Accepts(item, query)) continue;
            items.Add(item);
        }

        return items;
    }

    private PhotoItem? ReadPhoto(JsonElement photo, string key, int fullWidth)
    {
        var id = ReadString(photo, "id");
        if (string.IsNullOrEmpty(id)) return null;

        var urls = ReadObject(photo, "urls");
        var links = ReadObject(photo, "links");
        var user = ReadObject(photo, "user");
        var raw = ReadString(urls, "raw");

        return new PhotoItem
        {
            Id = id,
            ProviderId = Id,
            ProviderName = DisplayName,
            ThumbnailUrl = Resized(raw, 400, 80) ?? ReadString(urls, "small"),
            PreviewUrl = Resized(raw, 1080, 85) ?? ReadString(urls, "regular"),
            FullUrl = Resized(raw, fullWidth, 85) ?? ReadString(urls, "full"),
            Width = ReadInt(photo, "width"),
            Height = ReadInt(photo, "height"),
            Title = ReadString(photo, "description") ?? ReadString(photo, "alt_description"),
            AuthorName = ReadString(user, "name") ?? ReadString(user, "username"),
            AuthorUrl = WithUtm(ReadString(ReadObject(user, "links"), "html")),
            SourcePageUrl = WithUtm(ReadString(links, "html")),
            License = "Unsplash License",
            DownloadTrackUrl = TrackUrl(ReadString(links, "download_location"), key),
            Tags = ReadTags(photo),
        };
    }

    /// <summary>
    /// urls.raw is an imgix endpoint carrying the ixid the guidelines say to preserve, so sizing is
    /// appended to it rather than swapping in one of the fixed-size urls. fit=max never upscales, so
    /// a photo smaller than the monitor still comes back at its native size.
    /// </summary>
    private static string? Resized(string? raw, int width, int quality) =>
        string.IsNullOrEmpty(raw) ? null : $"{raw}{Joiner(raw)}w={width}&q={quality}&fm=jpg&fit=max";

    private static string? WithUtm(string? url) =>
        string.IsNullOrEmpty(url) ? null : $"{url}{Joiner(url)}{Utm}";

    /// <summary>
    /// The download ping is fired by the app through Net.TrackDownload, which sends no provider
    /// headers, so the Access Key rides along as the documented public client_id parameter.
    /// </summary>
    private static string? TrackUrl(string? location, string key) =>
        string.IsNullOrEmpty(location) ? null : $"{location}{Joiner(location)}client_id={Net.Esc(key)}";

    private static char Joiner(string url) => url.Contains('?') ? '&' : '?';

    private static bool Accepts(PhotoItem item, SourceQuery query)
    {
        if (query.MinWidth > 0 && item.Width < query.MinWidth) return false;
        if (query.MinHeight > 0 && item.Height < query.MinHeight) return false;

        // An unknown orientation means the payload omitted the dimensions; keep the photo rather than
        // silently emptying the page.
        return query.Orientation == PhotoOrientation.Any
            || item.Orientation == PhotoOrientation.Any
            || item.Orientation == query.Orientation;
    }

    private static void AppendOrientation(StringBuilder url, PhotoOrientation orientation)
    {
        var value = orientation switch
        {
            PhotoOrientation.Landscape => "landscape",
            PhotoOrientation.Portrait => "portrait",
            PhotoOrientation.Square => "squarish",
            _ => null,
        };
        if (value is not null) url.Append("&orientation=").Append(value);
    }

    // ---------- json helpers (each one tolerates a missing or wrongly-typed node) ----------

    private static IReadOnlyList<string> ReadTags(JsonElement photo)
    {
        if (photo.ValueKind != JsonValueKind.Object ||
            !photo.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var titles = new List<string>(tags.GetArrayLength());
        foreach (var tag in tags.EnumerateArray())
        {
            var title = ReadString(tag, "title");
            if (!string.IsNullOrWhiteSpace(title)) titles.Add(title);
        }

        return titles.Count == 0 ? Array.Empty<string>() : titles;
    }

    private static JsonElement ReadObject(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? value : default;

    private static string? ReadString(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static int ReadInt(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number : 0;
}
