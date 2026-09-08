using System.Globalization;
using System.Text;
using System.Text.Json;
using Driftwall.Core;

namespace Driftwall.Sources;

/// <summary>
/// Flickr's REST API. Top mode is Flickr Explore (flickr.interestingness.getList); search and
/// category modes use flickr.photos.search sorted by interestingness. Results default to the
/// licence ids that permit reuse, so a wallpaper is never an all-rights-reserved photo.
/// </summary>
public sealed class FlickrSource : PhotoSourceBase
{
    private const string Endpoint = "https://api.flickr.com/services/rest/";

    /// <summary>Documented per_page ceiling for every method used here.</summary>
    private const int MaxPerPage = 500;

    /// <summary>Roughly how many photos one day of Explore holds.</summary>
    private const int ExploreDaySize = 500;

    /// <summary>How far back "all" may reach; older Explore lists predate high-resolution uploads.</summary>
    private const int ExploreArchiveDays = 1825;

    /// <summary>A group pool's length is unknown up front, so Random guesses inside this many pages.</summary>
    private const int RandomGroupPages = 20;

    private const int ThumbnailWidth = 400;
    private const int PreviewWidth = 1080;
    private const int MinFullWidth = 1920;
    private const int MaxTags = 20;

    /// <summary>Licence ids that allow reuse. Options["license"] overrides; "all" disables the filter.</summary>
    private const string ReusableLicenses = "1,2,3,4,5,6,7,8,9,10";

    // url_h, url_k and url_3k..url_6k are absent from Flickr's published extras list but are honoured
    // by the live API. Unknown extras are ignored rather than rejected, so this degrades safely to the
    // documented sizes if that ever changes.
    private const string Extras =
        "license,owner_name,path_alias,tags,o_dims," +
        "url_q,url_n,url_m,url_z,url_c,url_l,url_h,url_k,url_3k,url_4k,url_5k,url_6k,url_o";

    /// <summary>Size suffixes probed on each photo. Real pixel counts come from the width_/height_ pairs.</summary>
    private static readonly string[] _sizeSuffixes = { "q", "n", "m", "z", "c", "l", "h", "k", "3k", "4k", "5k", "6k", "o" };

    private static readonly string[] _timeRanges = { "day", "week", "month", "year", "all" };

    private static readonly (string Id, string Name, string Tags)[] _categoryTable =
    {
        ("landscape",        "Landscape",        "landscape,mountains,valley"),
        ("nature",           "Nature",           "nature,outdoors,wilderness"),
        ("cityscape",        "Cityscape",        "cityscape,skyline,urban"),
        ("astrophotography", "Astrophotography", "astrophotography,milkyway,nightsky"),
        ("macro",            "Macro",            "macro,closeup"),
        ("wildlife",         "Wildlife",         "wildlife,animals,birds"),
        ("minimal",          "Minimal",          "minimalism,minimal,simplicity"),
        ("architecture",     "Architecture",     "architecture,building,facade"),
        ("night",            "Night",            "night,longexposure,citylights"),
        ("aerial",           "Aerial",           "aerial,drone,fromabove"),
        ("black-and-white",  "Black and White",  "blackandwhite,monochrome,bw"),
        ("autumn",           "Autumn",           "autumn,fall,foliage"),
        ("winter",           "Winter",           "winter,snow,frost"),
        ("ocean",            "Ocean",            "ocean,sea,seascape"),
        ("desert",           "Desert",           "desert,dunes,arid"),
        ("forest",           "Forest",           "forest,woodland,trees"),
    };

    private static readonly IReadOnlyList<SourceCategory> _categories =
        Array.ConvertAll(_categoryTable, c => new SourceCategory(c.Id, c.Name));

    private static readonly Dictionary<string, string> _categoryTags =
        _categoryTable.ToDictionary(c => c.Id, c => c.Tags, StringComparer.OrdinalIgnoreCase);

    /// <summary>Flickr's documented licence table (flickr.photos.licenses.getInfo).</summary>
    private static readonly Dictionary<int, string> _licenseNames = new()
    {
        [0] = "All Rights Reserved",
        [1] = "CC BY-NC-SA 2.0",
        [2] = "CC BY-NC 2.0",
        [3] = "CC BY-NC-ND 2.0",
        [4] = "CC BY 2.0",
        [5] = "CC BY-SA 2.0",
        [6] = "CC BY-ND 2.0",
        [7] = "No known copyright restrictions",
        [8] = "United States Government Work",
        [9] = "Public Domain Dedication (CC0)",
        [10] = "Public Domain Mark",
    };

    public override string Id => "flickr";
    public override string DisplayName => "Flickr";
    public override string Description => "Flickr Explore, tag feeds and full-text search across the Flickr community.";

    public override bool RequiresApiKey => true;
    public override string? ApiKeyHelpUrl => "https://www.flickr.com/services/apps/create/apply/";
    public override string? LicenseUrl => "https://www.flickr.com/creativecommons/";

    public override IReadOnlyList<string> SupportedTimeRanges => _timeRanges;
    public override IReadOnlyList<SourceCategory> Categories => _categories;

    public override async Task<PhotoPage> FetchAsync(SourceQuery query, SourceContext context, CancellationToken ct)
    {
        var apiKey = context.GetApiKey(Id);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new SourceException($"{DisplayName} needs a free API key. Add one in Settings → API keys.", isAuth: true);

        var call = BuildCall(query, context, apiKey);
        var fullWidth = Math.Max(context.TargetWidth, MinFullWidth);
        var page = call.Page;
        var retried = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var doc = await Net
                    .GetJsonDocumentAsync(context.Http, call.Url + "&page=" + page, ct, providerName: DisplayName)
                    .ConfigureAwait(false);

                var photos = ReadPhotos(doc.RootElement);
                var pageCount = IntOf(photos, "pages");

                // A randomised page can land past the end of the set; retake it once inside the real range.
                if (!retried && query.Mode == QueryMode.Random && pageCount > 0 && page > pageCount)
                {
                    retried = true;
                    page = Random.Shared.Next(1, pageCount + 1);
                    continue;
                }

                var items = Parse(photos, query, call.Licenses, fullWidth);
                var hasMore = call.HasMore ?? IntOf(photos, "page") < pageCount;
                return items.Count == 0 && !hasMore ? PhotoPage.Empty : new PhotoPage(items, hasMore);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                throw new SourceException($"{DisplayName} sent a response Driftwall could not read.", inner: ex);
            }
        }
    }

    /// <summary>A prepared request plus the behaviour the response handler cannot infer from the body.</summary>
    /// <param name="Url">Everything except the page number, which the caller appends.</param>
    /// <param name="Licenses">Non-null when the method ignores the server-side licence filter.</param>
    /// <param name="HasMore">Overrides the paging Flickr reports, when non-null.</param>
    private readonly record struct Call(string Url, int Page, HashSet<int>? Licenses, bool? HasMore);

    private static Call BuildCall(SourceQuery query, SourceContext context, string apiKey)
    {
        var options = context.Selection.Options;
        var licenseCsv = LicenseCsv(options);
        var groupId = NullIfBlank(options.GetValueOrDefault("group_id"));
        var perPage = Math.Clamp(query.PageSize, 1, MaxPerPage);
        var page = Math.Max(1, query.Page);

        var text = query.Mode == QueryMode.Search ? NullIfBlank(query.Keyword) : null;
        var tags = query.Mode == QueryMode.Category ? CategoryTags(query.Category) : null;

        // flickr.photos.search refuses parameterless queries (error 3), so it only runs when a
        // keyword or a category constrains it; anything else falls through to Explore or a pool.
        if (text is not null || tags is not null)
            return new Call(SearchUrl(query, apiKey, text, tags, groupId, licenseCsv, perPage), page, null, null);

        if (groupId is not null)
        {
            var groupPage = query.Mode == QueryMode.Random ? Random.Shared.Next(1, RandomGroupPages + 1) : page;
            return new Call(GroupUrl(apiKey, groupId, perPage), groupPage, ParseLicenses(licenseCsv),
                query.Mode == QueryMode.Random ? true : null);
        }

        return ExploreCall(query, apiKey, licenseCsv, perPage, page);
    }

    /// <summary>
    /// flickr.interestingness.getList takes neither a licence nor a safe_search argument, so the
    /// licence filter runs after parsing. Explore itself is human-curated and stays safe.
    /// </summary>
    private static Call ExploreCall(SourceQuery query, string apiKey, string? licenseCsv, int perPage, int page)
    {
        var licenses = ParseLicenses(licenseCsv);
        var days = WindowDays(query.TimeRange);

        if (query.Mode == QueryMode.Random)
        {
            // Flickr has no random endpoint. A random day's Explore list is the closest equivalent,
            // capped to recent years so the photos are still large enough for a desktop.
            var window = days == 0 ? ExploreArchiveDays : Math.Min(days, ExploreArchiveDays);
            var url = ExploreUrl(apiKey, perPage, DateBack(Random.Shared.Next(1, window + 1)));
            return new Call(url, Random.Shared.Next(1, Math.Max(1, ExploreDaySize / perPage) + 1), licenses, true);
        }

        if (days == 1)
            return new Call(ExploreUrl(apiKey, perPage, null), page, licenses, null);

        // One Explore list covers a single day, so a wider window walks back a day per page instead
        // of paging deeper into the same day.
        var walk = days == 0 ? ExploreArchiveDays : days;
        var date = page == 1 ? null : DateBack(page - 1);
        return new Call(ExploreUrl(apiKey, perPage, date), 1, licenses, page < walk);
    }

    private static string ExploreUrl(string apiKey, int perPage, string? date)
    {
        var sb = new StringBuilder(Endpoint).Append("?method=flickr.interestingness.getList");
        AppendCommon(sb, apiKey, perPage);
        if (date is not null) sb.Append("&date=").Append(date);
        return sb.ToString();
    }

    private static string GroupUrl(string apiKey, string groupId, int perPage)
    {
        var sb = new StringBuilder(Endpoint).Append("?method=flickr.groups.pools.getPhotos");
        AppendCommon(sb, apiKey, perPage);
        return sb.Append("&group_id=").Append(Net.Esc(groupId)).ToString();
    }

    private static string SearchUrl(
        SourceQuery query, string apiKey, string? text, string? tags, string? groupId, string? licenseCsv, int perPage)
    {
        var sb = new StringBuilder(Endpoint).Append("?method=flickr.photos.search");
        AppendCommon(sb, apiKey, perPage);

        if (text is not null) sb.Append("&text=").Append(Net.Esc(text));
        if (tags is not null) sb.Append("&tags=").Append(Net.Esc(tags)).Append("&tag_mode=any");
        if (groupId is not null) sb.Append("&group_id=").Append(Net.Esc(groupId));
        if (licenseCsv is not null) sb.Append("&license=").Append(Net.Esc(licenseCsv));

        // content_types supersedes the deprecated content_type argument; 0 selects real photos.
        sb.Append("&sort=interestingness-desc&content_types=0&media=photos");
        sb.Append("&safe_search=").Append(query.SafeSearch ? '1' : '3');

        var since = MinUploadDate(query.TimeRange);
        if (since is not null) sb.Append("&min_upload_date=").Append(since.Value);
        return sb.ToString();
    }

    private static void AppendCommon(StringBuilder sb, string apiKey, int perPage) => sb
        .Append("&api_key=").Append(Net.Esc(apiKey))
        .Append("&format=json&nojsoncallback=1&extras=").Append(Extras)
        .Append("&per_page=").Append(perPage);

    private List<PhotoItem> Parse(JsonElement photos, SourceQuery query, HashSet<int>? licenses, int fullWidth)
    {
        var items = new List<PhotoItem>();
        if (!photos.TryGetProperty("photo", out var array) || array.ValueKind != JsonValueKind.Array)
            return items;

        foreach (var photo in array.EnumerateArray())
        {
            if (photo.ValueKind != JsonValueKind.Object) continue;

            var id = StringOf(photo, "id");
            if (id is null) continue;

            var license = IntOf(photo, "license");
            if (licenses is not null && !licenses.Contains(license)) continue;

            var sizes = ReadSizes(photo);
            if (sizes.Count == 0) continue;

            var full = Pick(sizes, fullWidth);
            if (full.Width < query.MinWidth || full.Height < query.MinHeight) continue;

            var owner = StringOf(photo, "owner");
            var alias = StringOf(photo, "pathalias") ?? owner;
            var authorUrl = alias is null ? null : "https://www.flickr.com/photos/" + alias;

            var item = new PhotoItem
            {
                Id = id,
                ProviderId = Id,
                ProviderName = DisplayName,
                ThumbnailUrl = Pick(sizes, ThumbnailWidth).Url,
                PreviewUrl = Pick(sizes, PreviewWidth).Url,
                FullUrl = full.Url,
                Width = full.Width,
                Height = full.Height,
                Title = StringOf(photo, "title"),
                AuthorName = StringOf(photo, "ownername") ?? owner,
                AuthorUrl = authorUrl,
                SourcePageUrl = authorUrl is null ? null : authorUrl + "/" + id,
                License = _licenseNames.GetValueOrDefault(license),
                Tags = ReadTags(photo),
            };

            // Neither photos.search nor interestingness can filter by shape, so it happens here.
            if (query.Orientation != PhotoOrientation.Any && item.Orientation != query.Orientation) continue;

            items.Add(item);
        }

        return items;
    }

    private readonly record struct PhotoSize(string Url, int Width, int Height);

    /// <summary>Every size Flickr returned for one photo, ascending by width.</summary>
    private static List<PhotoSize> ReadSizes(JsonElement photo)
    {
        var sizes = new List<PhotoSize>(_sizeSuffixes.Length);
        foreach (var suffix in _sizeSuffixes)
        {
            var url = StringOf(photo, "url_" + suffix);
            if (url is null) continue;

            var width = IntOf(photo, "width_" + suffix);
            var height = IntOf(photo, "height_" + suffix);
            if (width == 0 && suffix == "o")
            {
                // The o_dims extra reports the original's size under its own field names.
                width = IntOf(photo, "o_width");
                height = IntOf(photo, "o_height");
            }

            if (width > 0 && height > 0) sizes.Add(new PhotoSize(url, width, height));
        }

        sizes.Sort(static (a, b) => a.Width.CompareTo(b.Width));
        return sizes;
    }

    /// <summary>Smallest size at least <paramref name="minWidth"/> across, or the largest on offer.</summary>
    private static PhotoSize Pick(List<PhotoSize> sizes, int minWidth)
    {
        foreach (var size in sizes)
            if (size.Width >= minWidth) return size;
        return sizes[^1];
    }

    private static IReadOnlyList<string> ReadTags(JsonElement photo)
    {
        var raw = StringOf(photo, "tags");
        if (raw is null) return Array.Empty<string>();

        var tags = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tags.Length <= MaxTags ? tags : tags[..MaxTags];
    }

    /// <summary>Flickr answers HTTP 200 even for failures, so the envelope carries the real status.</summary>
    private JsonElement ReadPhotos(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new SourceException($"{DisplayName} returned an unexpected response.");

        if (!string.Equals(StringOf(root, "stat"), "ok", StringComparison.OrdinalIgnoreCase))
            throw Failure(root);

        if (!root.TryGetProperty("photos", out var photos) || photos.ValueKind != JsonValueKind.Object)
            throw new SourceException($"{DisplayName} returned a response without a photo list.");

        return photos;
    }

    private SourceException Failure(JsonElement root)
    {
        var code = IntOf(root, "code");
        var message = StringOf(root, "message") ?? "no detail given";

        return code switch
        {
            98 or 99 or 100 => new SourceException($"{DisplayName} rejected the API key ({message}).", isAuth: true),
            // 105 is what Flickr returns once a key runs past its 3600 calls/hour budget.
            105 => new SourceException($"{DisplayName} is throttling or temporarily unavailable ({message}).", isRateLimit: true),
            _ => new SourceException($"{DisplayName} returned error {code}: {message}."),
        };
    }

    private static string? CategoryTags(string? category)
    {
        category = NullIfBlank(category);
        return category is null ? null : _categoryTags.GetValueOrDefault(category, category);
    }

    private static string? LicenseCsv(IReadOnlyDictionary<string, string> options)
    {
        var configured = NullIfBlank(options.GetValueOrDefault("license"));
        if (configured is null) return ReusableLicenses;

        return configured.Equals("all", StringComparison.OrdinalIgnoreCase)
            || configured.Equals("any", StringComparison.OrdinalIgnoreCase)
            ? null
            : configured;
    }

    private static HashSet<int>? ParseLicenses(string? csv)
    {
        if (csv is null) return null;

        var ids = new HashSet<int>();
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) ids.Add(id);

        return ids.Count == 0 ? null : ids;
    }

    /// <summary>Length of the requested time window in days; 0 means unbounded.</summary>
    private static int WindowDays(string? timeRange) => timeRange?.Trim().ToLowerInvariant() switch
    {
        "day" => 1,
        "week" => 7,
        "year" => 365,
        "all" => 0,
        _ => 30,
    };

    private static long? MinUploadDate(string? timeRange)
    {
        var days = WindowDays(timeRange);
        return days == 0 ? null : DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds();
    }

    private static string DateBack(int days) =>
        DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? StringOf(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? NullIfBlank(value.GetString())
            : null;

    /// <summary>Flickr returns numeric fields as JSON numbers or strings depending on the method.</summary>
    private static int IntOf(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) ? IntOf(value) : 0;

    private static int IntOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.TryGetInt32(out var number) ? number : 0,
        JsonValueKind.String => int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0,
        _ => 0,
    };
}
