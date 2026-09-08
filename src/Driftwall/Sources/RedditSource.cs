using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Driftwall.Core;

namespace Driftwall.Sources;

/// <summary>
/// Image posts from wallpaper subreddits, read through Reddit's listing API.
/// <para>
/// Reddit deprecated anonymous <c>*.json</c> listings in May 2026; verified 2026-09-01, a request carrying a
/// compliant User-Agent now comes back as "403 Blocked". This source therefore authenticates with an
/// app-only OAuth token and falls back to the public path only when no credentials are configured, so an
/// older install fails with an explanation instead of silently returning nothing.
/// </para>
/// <para>
/// The API key field holds <c>clientId:clientSecret</c> from https://www.reddit.com/prefs/apps ("script" or
/// "web app"); an installed app has no secret, so passing just the client id also works. The free budget is
/// 100 queries/minute per client id averaged over 10 minutes, which is why a fetch is capped at a few requests.
/// </para>
/// </summary>
public sealed class RedditSource : PhotoSourceBase
{
    private const string Web = "https://www.reddit.com";
    private const string OAuthApi = "https://oauth.reddit.com";
    private const string TokenEndpoint = "https://www.reddit.com/api/v1/access_token";

    /// <summary>Reddit documents "platform:app id:version"; its filters drop the generic <see cref="Net.UserAgent"/>.</summary>
    private const string RedditUserAgent = "windows:org.projectmax.driftwall:v1.0 (+https://projectmax-org.github.io/driftwall/)";

    /// <summary>Reddit caps a listing's <c>limit</c> at 100.</summary>
    private const int ListingLimit = 100;

    /// <summary>Ceiling on cursor follow-ups per fetch, so a deep page cannot burn the rate limit.</summary>
    private const int MaxListingRequests = 4;

    private static readonly string[] DefaultSubreddits =
    {
        "wallpapers", "wallpaper", "EarthPorn", "SkyPorn", "CityPorn", "spaceporn",
        "ExposurePorn", "WQHD_Wallpaper", "Amoledbackgrounds", "minimalwallpaper", "widescreenwallpaper",
    };

    private static readonly string[] TimeWindows = { "day", "week", "month", "year", "all" };

    private static readonly SourceCategory[] SubredditCategories =
        Array.ConvertAll(DefaultSubreddits, s => new SourceCategory(s, "r/" + s));

    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".webp" };

    /// <summary>
    /// App-only tokens live 24h. Cached statically and keyed by client id so the instance stays stateless;
    /// a racing duplicate fetch is harmless because Reddit issues an independent token per request.
    /// </summary>
    private static readonly ConcurrentDictionary<string, BearerToken> TokenCache = new(StringComparer.Ordinal);

    private sealed record BearerToken(string Value, DateTimeOffset ExpiresUtc);

    public override string Id => "reddit";
    public override string DisplayName => "Reddit";
    public override string Description => "Top image posts from wallpaper subreddits. Needs a free Reddit app id and secret.";

    public override bool RequiresApiKey => true;
    public override string? ApiKeyHelpUrl => "https://www.reddit.com/prefs/apps";
    public override string? LicenseUrl => "https://redditinc.com/policies/data-api-terms";

    public override IReadOnlyList<string> SupportedTimeRanges => TimeWindows;
    public override IReadOnlyList<SourceCategory> Categories => SubredditCategories;

    public override string? Validate(SourceContext context) =>
        string.IsNullOrWhiteSpace(context.GetApiKey(Id))
            ? "Reddit stopped serving anonymous listings in May 2026. Create a free app at reddit.com/prefs/apps "
              + "and paste \"clientId:clientSecret\" in Settings → API keys."
            : null;

    public override async Task<PhotoPage> FetchAsync(SourceQuery query, SourceContext context, CancellationToken ct)
    {
        var subreddits = ResolveSubreddits(query, context);
        if (subreddits.Length == 0) return PhotoPage.Empty;

        var shuffle = query.Mode == QueryMode.Random;
        var pageSize = Math.Clamp(query.PageSize, 1, ListingLimit);
        var skip = Math.Max(0, query.Page - 1) * pageSize;

        // Reddit's /random endpoint yields a single post and is unreliable, so Random becomes Top over a
        // randomised time window with the results shuffled — that varies the pool without extra requests.
        var time = shuffle ? TimeWindows[Random.Shared.Next(TimeWindows.Length)] : NormalizeTimeRange(query.TimeRange);

        var bearer = await GetBearerTokenAsync(context, ct).ConfigureAwait(false);
        var url = BuildListingUrl(query, string.Join('+', subreddits), time, RestrictToSubreddits(context), bearer is not null);

        var found = new List<PhotoItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var target = shuffle ? pageSize : skip + pageSize;
        var requests = shuffle ? 1 : MaxListingRequests;
        string? after = null;
        var seenCount = 0;

        try
        {
            for (var i = 0; i < requests; i++)
            {
                ct.ThrowIfCancellationRequested();

                // Listings are cursor paged, so a deep page is reached by walking `after` within this call.
                var pageUrl = after is null ? url : $"{url}&after={Net.Esc(after)}&count={seenCount}";
                using var doc = await Net
                    .GetJsonDocumentAsync(context.Http, pageUrl, ct, r => ApplyHeaders(r, bearer), DisplayName)
                    .ConfigureAwait(false);

                var posts = ReadListing(doc.RootElement, out after);
                if (posts.Count == 0) break;
                seenCount += posts.Count;

                foreach (var post in posts)
                {
                    var item = TryReadPost(post, query);
                    if (item is not null && Matches(item, query) && seen.Add(item.Id)) found.Add(item);
                }

                if (after is null || found.Count >= target) break;
            }
        }
        catch (SourceException ex) when (ex.IsAuth && bearer is null)
        {
            throw new SourceException(
                "Reddit no longer answers anonymous listing requests. Add a free app id and secret from "
                + "reddit.com/prefs/apps in Settings → API keys.", isAuth: true, inner: ex);
        }
        catch (SourceException ex) when (ex.IsRateLimit)
        {
            // Reddit throttles hard, and a 429 must not fail a whole rotation: hand back whatever was
            // already parsed, or a notice, instead of an error the user has to dismiss.
            return found.Count > 0
                ? new PhotoPage(TakePage(found, skip, pageSize, shuffle), false, "Reddit is throttling requests — this page is partial.")
                : PhotoPage.Message("Reddit is throttling requests. Driftwall will try again on the next rotation.");
        }
        catch (JsonException ex)
        {
            throw new SourceException("Reddit returned a listing Driftwall could not read.", inner: ex);
        }

        if (found.Count == 0) return PhotoPage.Empty;

        var items = TakePage(found, skip, pageSize, shuffle);
        var hasMore = items.Count > 0 && (after is not null || found.Count > skip + pageSize);
        return new PhotoPage(items, hasMore);
    }

    // ---------- request building ----------

    private static string BuildListingUrl(SourceQuery query, string subredditPath, string time, bool restrict, bool authenticated)
    {
        // oauth.reddit.com serves JSON for the bare path; only the www host needs the .json extension.
        var host = authenticated ? OAuthApi : Web;
        var ext = authenticated ? string.Empty : ".json";

        if (query.Mode == QueryMode.Search && !string.IsNullOrWhiteSpace(query.Keyword))
        {
            var q = Net.Esc(query.Keyword);
            return restrict
                ? $"{host}/r/{subredditPath}/search{ext}?q={q}&restrict_sr=1&sort=top&t={time}&limit={ListingLimit}&raw_json=1"
                : $"{host}/search{ext}?q={q}&sort=top&t={time}&limit={ListingLimit}&raw_json=1";
        }

        return $"{host}/r/{subredditPath}/top{ext}?t={time}&limit={ListingLimit}&raw_json=1";
    }

    /// <summary>Category mode names one subreddit; otherwise Options["subreddits"] overrides the defaults.</summary>
    private static string[] ResolveSubreddits(SourceQuery query, SourceContext context)
    {
        if (query.Mode == QueryMode.Category && NormalizeSubreddit(query.Category) is { } single)
            return new[] { single };

        if (context.Selection.Options.TryGetValue("subreddits", out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            var custom = raw
                .Split(new[] { ',', ';', '+', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(NormalizeSubreddit)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (custom.Length > 0) return custom;
        }

        return DefaultSubreddits;
    }

    /// <summary>Accepts "name", "r/name" or "/r/name"; null when it is not a legal subreddit name.</summary>
    private static string? NormalizeSubreddit(string? value)
    {
        var name = value?.Trim().Trim('/') ?? string.Empty;
        if (name.StartsWith("r/", StringComparison.OrdinalIgnoreCase)) name = name[2..];
        if (name.Length is 0 or > 21) return null;

        foreach (var c in name)
            if (!char.IsAsciiLetterOrDigit(c) && c != '_') return null;

        return name;
    }

    /// <summary>Options["restrict"] = "false" widens Search mode to all of Reddit.</summary>
    private static bool RestrictToSubreddits(SourceContext context) =>
        !context.Selection.Options.TryGetValue("restrict", out var value)
        || !bool.TryParse(value, out var restrict)
        || restrict;

    private static string NormalizeTimeRange(string? range)
    {
        var t = range?.Trim().ToLowerInvariant();
        return t is "hour" or "day" or "week" or "month" or "year" or "all" ? t : "month";
    }

    // ---------- auth ----------

    /// <summary>Bearer token for app-only OAuth, or null when the user has configured no credentials.</summary>
    private async Task<string?> GetBearerTokenAsync(SourceContext context, CancellationToken ct)
    {
        var credential = context.GetApiKey(Id)?.Trim();
        if (string.IsNullOrEmpty(credential)) return null;

        var separator = credential.IndexOf(':');
        var clientId = (separator < 0 ? credential : credential[..separator]).Trim();
        var clientSecret = separator < 0 ? string.Empty : credential[(separator + 1)..].Trim();
        if (clientId.Length == 0) return null;

        if (TokenCache.TryGetValue(clientId, out var cached) && cached.ExpiresUtc > DateTimeOffset.UtcNow)
            return cached.Value;

        var token = await RequestTokenAsync(context.Http, clientId, clientSecret, ct).ConfigureAwait(false);
        TokenCache[clientId] = token;
        return token.Value;
    }

    private async Task<BearerToken> RequestTokenAsync(HttpClient http, string clientId, string clientSecret, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint);
        ApplyHeaders(req);
        req.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")));

        // Confidential apps use client_credentials; an installed app has no secret and must use the
        // installed_client grant with a device id — the documented opt-out sentinel is used here.
        req.Content = new FormUrlEncodedContent(clientSecret.Length > 0
            ? new Dictionary<string, string> { ["grant_type"] = "client_credentials" }
            : new Dictionary<string, string>
            {
                ["grant_type"] = "https://oauth.reddit.com/grants/installed_client",
                ["device_id"] = "DO_NOT_TRACK_THIS_DEVICE",
            });

        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new SourceException("Reddit rejected the app id/secret. Re-copy them from reddit.com/prefs/apps.", isAuth: true);
        Net.EnsureOk(res, DisplayName);

        await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);

        var access = GetString(doc.RootElement, "access_token");
        if (string.IsNullOrEmpty(access))
            throw new SourceException("Reddit issued no access token for those credentials.", isAuth: true);

        var lifetime = GetInt(doc.RootElement, "expires_in");
        if (lifetime <= 0) lifetime = 3600;

        // Retire the token a minute early so it cannot expire mid-request.
        return new BearerToken(access, DateTimeOffset.UtcNow.AddSeconds(lifetime - 60));
    }

    /// <summary>Headers set on the request win over <see cref="Net.Client"/>'s defaults, which is the point here.</summary>
    /// <remarks>
    /// The User-Agent is set as a raw header on purpose. Reddit's documented "platform:app id:version"
    /// form contains colons, which are not legal in an RFC 7230 product token, so the typed UserAgent
    /// collection's parser rejects it with a FormatException — sending it unvalidated is the only way to
    /// transmit the exact string Reddit's filters expect.
    /// </remarks>
    private static void ApplyHeaders(HttpRequestMessage req, string? bearer = null)
    {
        req.Headers.Remove("User-Agent");
        req.Headers.TryAddWithoutValidation("User-Agent", RedditUserAgent);
        if (!string.IsNullOrEmpty(bearer))
            req.Headers.Authorization = new AuthenticationHeaderValue("bearer", bearer);
    }

    // ---------- parsing ----------

    /// <summary>Unwraps a Listing envelope into its t3 (link) children and reports data.after for paging.</summary>
    private static List<JsonElement> ReadListing(JsonElement root, out string? after)
    {
        after = null;
        var posts = new List<JsonElement>();

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return posts;

        if (data.TryGetProperty("after", out var cursor) && cursor.ValueKind == JsonValueKind.String)
            after = cursor.GetString();

        if (!data.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array)
            return posts;

        foreach (var child in children.EnumerateArray())
        {
            if (child.ValueKind == JsonValueKind.Object
                && child.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.ValueEquals("t3")
                && child.TryGetProperty("data", out var post) && post.ValueKind == JsonValueKind.Object)
                posts.Add(post);
        }

        return posts;
    }

    private PhotoItem? TryReadPost(JsonElement post, SourceQuery query)
    {
        var id = GetString(post, "id");
        if (string.IsNullOrEmpty(id)) return null;

        if (GetBool(post, "is_video") || GetBool(post, "is_gallery") || GetBool(post, "is_self") || GetBool(post, "stickied"))
            return null;

        // removed_by_category is a string ("moderator", "deleted", ...) when the post is gone, absent otherwise.
        if (GetString(post, "removed_by_category") is not null) return null;

        if (query.SafeSearch && GetBool(post, "over_18")) return null;

        var link = Unescape(GetString(post, "url_overridden_by_dest") ?? GetString(post, "url"));
        var directImage = IsDirectImage(link);
        if (!directImage && GetString(post, "post_hint") != "image") return null;

        // Without a preview block there are no reliable dimensions or derived sizes, which a wallpaper needs.
        if (!TryGetPreview(post, out var preview)
            || !preview.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object)
            return null;

        var width = GetInt(source, "width");
        var height = GetInt(source, "height");
        if (width <= 0 || height <= 0) return null;

        var sourceUrl = Unescape(GetString(source, "url"));
        var author = GetString(post, "author");
        var hasAuthor = !string.IsNullOrEmpty(author) && author != "[deleted]";
        var permalink = GetString(post, "permalink");

        return new PhotoItem
        {
            Id = id,
            ProviderId = Id,
            ProviderName = DisplayName,
            ThumbnailUrl = PickResolution(preview, 400) ?? sourceUrl,
            PreviewUrl = PickResolution(preview, 1080) ?? sourceUrl,
            // The original upload beats Reddit's re-encoded preview whenever the post links straight to it.
            FullUrl = directImage ? link : sourceUrl,
            Width = width,
            Height = height,
            Title = GetString(post, "title"),
            AuthorName = hasAuthor ? "u/" + author : null,
            AuthorUrl = hasAuthor ? $"{Web}/user/{author}" : null,
            SourcePageUrl = string.IsNullOrEmpty(permalink) ? null : Web + permalink,
            License = "User submitted — check the post",
            Tags = ReadTags(post),
            // Reddit's terms ask for attribution and a link back to the post rather than a usage ping,
            // so SourcePageUrl carries that obligation and DownloadTrackUrl stays null.
        };
    }

    /// <summary>Reddit cannot filter listings by shape or size, so it happens once the real dimensions are known.</summary>
    private static bool Matches(PhotoItem item, SourceQuery query) =>
        item.Width >= query.MinWidth
        && item.Height >= query.MinHeight
        && (query.Orientation == PhotoOrientation.Any || item.Orientation == query.Orientation);

    private static bool TryGetPreview(JsonElement post, out JsonElement image)
    {
        image = default;
        if (post.ValueKind != JsonValueKind.Object) return false;
        if (!post.TryGetProperty("preview", out var preview) || preview.ValueKind != JsonValueKind.Object) return false;
        if (!preview.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array) return false;
        if (images.GetArrayLength() == 0) return false;

        image = images[0];
        return image.ValueKind == JsonValueKind.Object;
    }

    /// <summary>Smallest generated resolution that still reaches <paramref name="wanted"/>px wide, else the largest.</summary>
    private static string? PickResolution(JsonElement image, int wanted)
    {
        if (image.ValueKind != JsonValueKind.Object
            || !image.TryGetProperty("resolutions", out var list) || list.ValueKind != JsonValueKind.Array) return null;

        string? best = null;
        var bestWidth = 0;

        foreach (var res in list.EnumerateArray())
        {
            var width = GetInt(res, "width");
            var url = Unescape(GetString(res, "url"));
            if (width <= 0 || url is null) continue;

            var better = bestWidth < wanted ? width > bestWidth : width >= wanted && width < bestWidth;
            if (best is null || better)
            {
                best = url;
                bestWidth = width;
            }
        }

        return best;
    }

    private static IReadOnlyList<string> ReadTags(JsonElement post)
    {
        var tags = new List<string>(2);

        if (GetString(post, "subreddit") is { Length: > 0 } subreddit) tags.Add("r/" + subreddit);
        if (GetString(post, "link_flair_text")?.Trim() is { Length: > 0 } flair) tags.Add(flair);

        return tags.Count == 0 ? Array.Empty<string>() : tags;
    }

    private static bool IsDirectImage(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        if (uri.Host.Equals("i.redd.it", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("i.imgur.com", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var extension in ImageExtensions)
            if (uri.AbsolutePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    /// <summary>
    /// raw_json=1 is meant to stop Reddit HTML-escaping URLs, but preview links still arrive with encoded
    /// ampersands in some responses, and preview.redd.it rejects the request if its signature stays encoded.
    /// </summary>
    private static string? Unescape(string? url) => url?.Replace("&amp;", "&", StringComparison.Ordinal);

    // ---------- small helpers ----------

    private static IReadOnlyList<PhotoItem> TakePage(List<PhotoItem> found, int skip, int pageSize, bool shuffle)
    {
        if (shuffle)
        {
            for (var i = found.Count - 1; i > 0; i--)
            {
                var j = Random.Shared.Next(i + 1);
                (found[i], found[j]) = (found[j], found[i]);
            }
            skip = 0;
        }

        return skip >= found.Count ? Array.Empty<PhotoItem>() : found.GetRange(skip, Math.Min(pageSize, found.Count - skip));
    }

    // TryGetProperty throws InvalidOperationException on any receiver that is not an object — a JSON
    // null in a listing array is enough — so every helper confirms the kind before reaching in.
    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static int GetInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : 0;
}
