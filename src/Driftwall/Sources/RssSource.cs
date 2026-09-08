using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Driftwall.Core;

namespace Driftwall.Sources;

/// <summary>
/// Generic photo feed reader covering RSS 2.0, RSS 1.0 and Atom. Images are taken from Media RSS
/// (<c>media:content</c> / <c>media:thumbnail</c>), from enclosures, or from the first
/// <c>&lt;img&gt;</c> in the item's HTML. Feed URLs come from <c>SourceSelection.Options["feeds"]</c>
/// (newline, whitespace or comma separated); with none configured the built-in <see cref="Categories"/>
/// feeds are used so the source produces photos the moment it is added. A feed has no query API, so
/// keyword search, orientation, size, safe-search, time window and paging are all applied client-side
/// to a single fetch of each feed.
/// </summary>
public sealed partial class RssSource : PhotoSourceBase
{
    private const string ProviderKey = "rss";
    private const string ProviderLabel = "RSS / Atom feed";
    private const string FeedsOption = "feeds";

    /// <summary>Feeds are small and cannot be paged server-side, so the whole pool is capped.</summary>
    private const int MaxItems = 200;

    /// <summary>Upper bound on feeds fetched per call, so a long Options list can't stall a rotation.</summary>
    private const int MaxFeeds = 6;

    private const int MaxTags = 12;

    /// <summary>Smallest width we will accept as a grid thumbnail; below it the full image is used.</summary>
    private const int MinThumbnailWidth = 200;

    // Extraction rules, best first. The order is part of the contract: it encodes how trustworthy a
    // rendition is, so a 1280px enclosure beats a 180px teaser <img> even though sizes are unknown.
    private const int RuleMediaContent = 1;
    private const int RuleMediaThumbnail = 2;
    private const int RuleEnclosure = 3;
    private const int RuleAtomEnclosure = 4;
    private const int RuleHtmlImage = 5;

    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Media = "http://search.yahoo.com/mrss/";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace ContentModule = "http://purl.org/rss/1.0/modules/content/";

    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".avif", ".gif", ".bmp"];
    private static readonly string[] DateElementNames = ["pubDate", "published", "date", "updated"];
    private static readonly string[] TimeRanges = ["day", "week", "month", "year", "all"];

    /// <summary>
    /// Built-ins, each verified to return HTTP 200 with real photo payloads. NASA's older
    /// /rss/dyn/lg_image_of_the_day.rss now 301s to /feeds/iotd-feed, so the target is used directly.
    /// </summary>
    private static readonly (string Id, string Name, Uri Url)[] BuiltInFeeds =
    [
        ("nasa-iotd", "NASA Image of the Day", new Uri("https://www.nasa.gov/feeds/iotd-feed")),
        ("esa-hubble", "ESA/Hubble Images", new Uri("https://esahubble.org/images/feed/")),
        ("esa-webb", "James Webb Space Telescope", new Uri("https://esawebb.org/images/feed/")),
        ("eso", "ESO Images", new Uri("https://www.eso.org/public/images/feed/")),
        ("flickr-landscape", "Flickr — Landscapes", new Uri("https://www.flickr.com/services/feeds/photos_public.gne?tags=landscape&format=rss2")),
        ("flickr-nature", "Flickr — Nature", new Uri("https://www.flickr.com/services/feeds/photos_public.gne?tags=nature&format=rss2")),
    ];

    private static readonly SourceCategory[] BuiltInCategories =
        BuiltInFeeds.Select(f => new SourceCategory(f.Id, f.Name)).ToArray();

    private static readonly Uri[] BuiltInFeedUrls = BuiltInFeeds.Select(f => f.Url).ToArray();

    public override string Id => ProviderKey;

    public override string DisplayName => ProviderLabel;

    public override string Description => "Any photo feed — NASA, ESA/Hubble, Webb, ESO and Flickr built in, or paste your own URLs.";

    public override IReadOnlyList<string> SupportedTimeRanges => TimeRanges;

    public override IReadOnlyList<SourceCategory> Categories => BuiltInCategories;

    /// <summary>Only complains when the user typed feed URLs and none of them are usable.</summary>
    public override string? Validate(SourceContext context)
    {
        var raw = FeedOption(context.Selection);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        return ParseFeedUrls(raw).Count == 0
            ? "None of the feed URLs are valid http(s) addresses. Put one feed URL per line."
            : null;
    }

    public override async Task<PhotoPage> FetchAsync(SourceQuery query, SourceContext context, CancellationToken ct)
    {
        var feeds = ResolveFeeds(query, context.Selection);
        if (feeds.Count == 0)
            return PhotoPage.Message("No usable feed URL. Add one in this source's options, or pick a built-in feed.");

        var loads = new Task<FeedResult>[feeds.Count];
        for (var i = 0; i < feeds.Count; i++) loads[i] = LoadAsync(feeds[i], context.Http, ct);
        var results = await Task.WhenAll(loads).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var pool = new List<FeedEntry>();
        SourceException? blocked = null;
        var failed = 0;

        foreach (var result in results)
        {
            if (result.Document is null)
            {
                failed++;
                if (result.Error is { } error && (error.IsAuth || error.IsRateLimit)) blocked ??= error;
                continue;
            }

            try
            {
                pool.AddRange(ReadEntries(result.Document, result.Url, query));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One malformed feed must not take the rotation down with it.
                failed++;
            }
        }

        if (pool.Count == 0)
        {
            if (blocked is not null) throw blocked;
            if (failed == results.Length)
                return PhotoPage.Message(FirstError(results) ?? "The feed could not be read.");
            return PhotoPage.Empty;
        }

        var matched = pool
            .DistinctBy(e => e.Item.FullUrl!, StringComparer.OrdinalIgnoreCase)
            .Take(MaxItems)
            .Where(e => MatchesKeyword(e, query) && MatchesConstraints(e.Item, query))
            .ToList();

        var windowed = ApplyTimeWindow(matched, query);
        var ordered = query.Mode == QueryMode.Random ? ShuffleCopy(windowed) : SortNewestFirst(windowed);

        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var skip = (Math.Clamp(query.Page, 1, 10_000) - 1) * pageSize;
        var items = ordered.Skip(skip).Take(pageSize).Select(e => e.Item).ToList();
        var notice = failed > 0 ? $"{failed} of {results.Length} feeds could not be read." : null;

        if (items.Count == 0)
        {
            if (notice is not null) return PhotoPage.Message(notice);
            if (skip == 0)
                return PhotoPage.Message("The feed loaded, but nothing matched the current keyword, size or orientation filters.");
            return PhotoPage.Empty;
        }

        return new PhotoPage(items, ordered.Count > skip + items.Count, notice);
    }

    // ---------- feed selection ----------

    private static IReadOnlyList<Uri> ResolveFeeds(SourceQuery query, SourceSelection selection)
    {
        if (query.Mode == QueryMode.Category)
        {
            var picked = LookupCategory(query.Category);
            if (picked is not null) return [picked];
        }

        var configured = ParseFeedUrls(FeedOption(selection));
        return configured.Count > 0 ? configured : BuiltInFeedUrls;
    }

    private static string? FeedOption(SourceSelection selection) =>
        selection.Options.TryGetValue(FeedsOption, out var raw) ? raw : null;

    private static Uri? LookupCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return null;

        foreach (var feed in BuiltInFeeds)
            if (feed.Id.Equals(category, StringComparison.OrdinalIgnoreCase)) return feed.Url;

        // A raw feed URL is also accepted, so hand-edited settings keep working.
        return Uri.TryCreate(category.Trim(), UriKind.Absolute, out var direct) && IsWeb(direct) ? direct : null;
    }

    private static List<Uri> ParseFeedUrls(string? raw)
    {
        var feeds = new List<Uri>();
        if (string.IsNullOrWhiteSpace(raw)) return feeds;

        foreach (var token in FeedSeparatorRegex().Split(raw))
        {
            var candidate = token.Trim().TrimEnd(',');
            if (candidate.Length == 0 || candidate.StartsWith('#')) continue;
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || !IsWeb(uri)) continue;
            if (feeds.Contains(uri)) continue;

            feeds.Add(uri);
            if (feeds.Count == MaxFeeds) break;
        }

        return feeds;
    }

    // ---------- fetching ----------

    private static async Task<FeedResult> LoadAsync(Uri url, HttpClient http, CancellationToken ct)
    {
        try
        {
            var xml = await Net.GetStringAsync(
                http, url.AbsoluteUri, ct,
                req => req.Headers.Accept.ParseAdd("application/rss+xml, application/atom+xml, application/xml;q=0.9, text/xml;q=0.8, */*;q=0.5"),
                ProviderLabel).ConfigureAwait(false);

            // XDocument.Parse rejects DTDs by default, which is what we want for third-party XML.
            return new FeedResult(url, XDocument.Parse(xml.TrimStart('﻿', ' ', '\t', '\r', '\n')), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SourceException ex)
        {
            return new FeedResult(url, null, ex);
        }
        catch (Exception ex)
        {
            return new FeedResult(url, null, new SourceException($"{url.Host} is not a readable RSS or Atom feed ({ex.Message}).", inner: ex));
        }
    }

    private static string? FirstError(IEnumerable<FeedResult> results) =>
        results.Select(r => r.Error?.Message).FirstOrDefault(m => m is not null);

    // ---------- parsing ----------

    private static IEnumerable<FeedEntry> ReadEntries(XDocument doc, Uri feedUrl, SourceQuery query)
    {
        var root = doc.Root;
        if (root is null) yield break;

        var channel = root.Elements().FirstOrDefault(e => e.Name.LocalName == "channel") ?? root;
        var feedLicense = Clean(channel.Elements().FirstOrDefault(e => e.Name.LocalName is "copyright" or "rights")?.Value);

        // "item" covers RSS 2.0 and RSS 1.0, "entry" covers Atom.
        foreach (var node in root.Descendants().Where(e => e.Name.LocalName is "item" or "entry"))
        {
            if (query.SafeSearch && IsAdultRated(node)) continue;

            var entry = BuildEntry(node, feedUrl, feedLicense);
            if (entry is not null) yield return entry;
        }
    }

    private static FeedEntry? BuildEntry(XElement node, Uri feedUrl, string? feedLicense)
    {
        var images = CollectImages(node, feedUrl);
        if (images.Count == 0) return null;

        var full = images[0];
        var thumbnail = images.FirstOrDefault(i =>
            i.Rule == RuleMediaThumbnail && (i.Width == 0 || i.Width >= MinThumbnailWidth));

        var title = Text(node, "title") ?? Clean(node.Element(Media + "title")?.Value);
        var summary = HtmlHolders(node).FirstOrDefault()?.Value ?? string.Empty;
        var tags = ReadTags(node);

        var item = new PhotoItem
        {
            Id = Text(node, "guid") ?? Text(node, "id") ?? StableId(full.Url),
            ProviderId = ProviderKey,
            ProviderName = ProviderLabel,
            ThumbnailUrl = thumbnail?.Url ?? full.Url,
            // A feed publishes one usable rendition per item, so preview and wallpaper share a URL.
            PreviewUrl = full.Url,
            FullUrl = full.Url,
            Width = full.Width,
            Height = full.Height,
            Title = title,
            AuthorName = ReadAuthor(node),
            AuthorUrl = Clean(node.Element(Atom + "author")?.Element(Atom + "uri")?.Value),
            SourcePageUrl = ReadLink(node, feedUrl),
            License = ReadLicense(node) ?? feedLicense,
            Tags = tags,
        };

        return new FeedEntry(item, ReadPublished(node), $"{title} {summary} {string.Join(' ', tags)}");
    }

    private static List<ImageRef> CollectImages(XElement node, Uri feedUrl)
    {
        var found = new List<ImageRef>();

        // media:content may sit directly under the item or inside a media:group.
        foreach (var media in node.Descendants(Media + "content"))
            if (IsImageMedia(media))
                Add(found, RuleMediaContent, media.Attribute("url")?.Value, Size(media), feedUrl);

        foreach (var thumb in node.Descendants(Media + "thumbnail"))
            Add(found, RuleMediaThumbnail, thumb.Attribute("url")?.Value, Size(thumb), feedUrl);

        foreach (var enclosure in node.Elements().Where(e => e.Name.LocalName == "enclosure"))
            if (IsImageType(enclosure.Attribute("type")?.Value, enclosure.Attribute("url")?.Value))
                Add(found, RuleEnclosure, enclosure.Attribute("url")?.Value, default, feedUrl);

        foreach (var link in node.Elements(Atom + "link"))
            if (string.Equals(link.Attribute("rel")?.Value, "enclosure", StringComparison.OrdinalIgnoreCase)
                && IsImageType(link.Attribute("type")?.Value, link.Attribute("href")?.Value))
                Add(found, RuleAtomEnclosure, link.Attribute("href")?.Value, default, feedUrl);

        foreach (var holder in HtmlHolders(node))
        {
            var src = FirstImageSource(holder);
            if (src is null) continue;

            Add(found, RuleHtmlImage, src, default, feedUrl);
            break;
        }

        found.Sort(static (a, b) => a.Rule != b.Rule
            ? a.Rule.CompareTo(b.Rule)
            : ((long)b.Width * b.Height).CompareTo((long)a.Width * a.Height));

        return found;
    }

    private static void Add(List<ImageRef> found, int rule, string? raw, (int Width, int Height) size, Uri feedUrl)
    {
        var url = Absolute(raw, feedUrl);
        if (url is null) return;
        if (found.Exists(i => string.Equals(i.Url, url, StringComparison.OrdinalIgnoreCase))) return;

        found.Add(new ImageRef(url, rule, size.Width, size.Height));
    }

    /// <summary>HTML payloads, best first: content:encoded, RSS description, Atom content, Atom summary.</summary>
    private static IEnumerable<XElement> HtmlHolders(XElement node)
    {
        var encoded = node.Element(ContentModule + "encoded");
        if (encoded is not null) yield return encoded;

        var description = node.Elements().FirstOrDefault(e => e.Name.LocalName == "description");
        if (description is not null) yield return description;

        var content = node.Element(Atom + "content");
        if (content is not null) yield return content;

        var summary = node.Element(Atom + "summary");
        if (summary is not null) yield return summary;
    }

    private static string? FirstImageSource(XElement holder)
    {
        // Atom type="xhtml" keeps real child elements; every other flavour arrives as escaped markup.
        var inline = holder.Descendants().FirstOrDefault(e => e.Name.LocalName == "img")?.Attribute("src")?.Value;
        if (!string.IsNullOrWhiteSpace(inline)) return inline;

        var match = ImageTagRegex().Match(holder.Value);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ReadAuthor(XElement node) =>
        Clean(node.Element(Dc + "creator")?.Value)
        ?? Clean(node.Element(Atom + "author")?.Element(Atom + "name")?.Value)
        ?? RssAuthorName(node)
        ?? Clean(node.Descendants(Media + "credit").FirstOrDefault()?.Value);

    /// <summary>RSS &lt;author&gt; is an email address, optionally followed by "(Display Name)".</summary>
    private static string? RssAuthorName(XElement node)
    {
        var raw = Text(node, "author");
        if (raw is null) return null;

        var open = raw.IndexOf('(');
        var close = raw.LastIndexOf(')');
        if (open >= 0 && close > open) return Clean(raw[(open + 1)..close]);

        return raw.Contains('@') ? null : raw;
    }

    private static string? ReadLink(XElement node, Uri feedUrl)
    {
        var rss = node.Elements().FirstOrDefault(e => e.Name.LocalName == "link" && !e.HasElements && e.Value.Length > 0);
        if (rss is not null) return Absolute(rss.Value, feedUrl);

        var links = node.Elements(Atom + "link").ToList();
        var alternate = links.FirstOrDefault(l => string.Equals(l.Attribute("rel")?.Value, "alternate", StringComparison.OrdinalIgnoreCase))
            ?? links.FirstOrDefault(l => l.Attribute("rel") is null);

        return Absolute(alternate?.Attribute("href")?.Value, feedUrl);
    }

    private static string? ReadLicense(XElement node)
    {
        var license = node.Descendants(Media + "license").FirstOrDefault();
        if (license is not null) return Clean(license.Value) ?? Clean(license.Attribute("href")?.Value);

        return Clean(node.Descendants(Media + "copyright").FirstOrDefault()?.Value)
            ?? Clean(node.Element(Dc + "rights")?.Value);
    }

    private static IReadOnlyList<string> ReadTags(XElement node)
    {
        var tags = new List<string>();

        foreach (var category in node.Elements().Where(e => e.Name.LocalName == "category"))
        {
            AddTag(tags, category.Attribute("term")?.Value ?? category.Value);
            if (tags.Count == MaxTags) return tags;
        }

        foreach (var keywords in node.Descendants(Media + "keywords"))
            foreach (var part in keywords.Value.Split(','))
            {
                AddTag(tags, part);
                if (tags.Count == MaxTags) return tags;
            }

        return tags.Count == 0 ? Array.Empty<string>() : tags;
    }

    private static void AddTag(List<string> tags, string? raw)
    {
        var tag = Clean(raw);
        if (tag is not null && !tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) tags.Add(tag);
    }

    private static DateTimeOffset? ReadPublished(XElement node)
    {
        foreach (var name in DateElementNames)
        {
            var raw = node.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
            if (raw is not null
                && DateTimeOffset.TryParse(raw.Trim(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var when))
                return when;
        }

        return null;
    }

    /// <summary>
    /// A plain feed carries no adult signal, so safe search can only act on Media RSS ratings
    /// (media:rating scheme="urn:simple" or the deprecated media:adult).
    /// </summary>
    private static bool IsAdultRated(XElement node)
    {
        foreach (var rating in node.Descendants(Media + "rating"))
        {
            var scheme = rating.Attribute("scheme")?.Value;
            var simple = scheme is null || scheme.Equals("urn:simple", StringComparison.OrdinalIgnoreCase);
            if (simple && rating.Value.Trim().Equals("adult", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return string.Equals(node.Element(Media + "adult")?.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    // ---------- filtering / ordering ----------

    private static bool MatchesKeyword(FeedEntry entry, SourceQuery query)
    {
        if (query.Mode != QueryMode.Search || string.IsNullOrWhiteSpace(query.Keyword)) return true;

        foreach (var term in query.Keyword.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (!entry.SearchText.Contains(term, StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }

    private static bool MatchesConstraints(PhotoItem item, SourceQuery query)
    {
        // Most feeds omit pixel sizes; filter only on dimensions the feed actually declared.
        if (item.Width <= 0 || item.Height <= 0) return true;
        if (query.MinWidth > 0 && item.Width < query.MinWidth) return false;
        if (query.MinHeight > 0 && item.Height < query.MinHeight) return false;

        return query.Orientation == PhotoOrientation.Any || item.Orientation == query.Orientation;
    }

    /// <summary>
    /// Applied softly: a picture-of-the-week feed can hold nothing inside a "day" window, and an
    /// empty page reads as a broken source, so the window is dropped when it would exclude everything.
    /// </summary>
    private static List<FeedEntry> ApplyTimeWindow(List<FeedEntry> entries, SourceQuery query)
    {
        if (query.Mode == QueryMode.Search) return entries;

        var since = WindowStart(query.TimeRange);
        if (since is null) return entries;

        var windowed = entries.Where(e => e.Published is null || e.Published >= since).ToList();
        return windowed.Count > 0 ? windowed : entries;
    }

    private static DateTimeOffset? WindowStart(string? range) => range?.Trim().ToLowerInvariant() switch
    {
        "day" => DateTimeOffset.UtcNow.AddDays(-1),
        "week" => DateTimeOffset.UtcNow.AddDays(-7),
        "month" => DateTimeOffset.UtcNow.AddMonths(-1),
        "year" => DateTimeOffset.UtcNow.AddYears(-1),
        _ => null,
    };

    private static List<FeedEntry> SortNewestFirst(List<FeedEntry> entries) =>
        entries.OrderByDescending(e => e.Published ?? DateTimeOffset.MinValue).ToList();

    /// <summary>
    /// Random mode reshuffles on every call, so an item can repeat across pages; the app dedupes on
    /// <see cref="PhotoItem.Key"/>, and a stable order would make "random" identical every run.
    /// </summary>
    private static List<FeedEntry> ShuffleCopy(List<FeedEntry> entries)
    {
        var shuffled = entries.ToArray();
        Random.Shared.Shuffle(shuffled);
        return [.. shuffled];
    }

    // ---------- small helpers ----------

    private static bool IsImageMedia(XElement media)
    {
        var medium = media.Attribute("medium")?.Value;
        return string.IsNullOrEmpty(medium)
            ? IsImageType(media.Attribute("type")?.Value, media.Attribute("url")?.Value)
            : medium.Equals("image", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Trusts the declared MIME type, falling back to the file extension when a feed omits it.</summary>
    private static bool IsImageType(string? type, string? url) =>
        string.IsNullOrEmpty(type)
            ? url is not null && LooksLikeImagePath(url)
            : type.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeImagePath(string url)
    {
        var path = url.AsSpan();
        var cut = path.IndexOfAny('?', '#');
        if (cut >= 0) path = path[..cut];

        foreach (var extension in ImageExtensions)
            if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    private static (int Width, int Height) Size(XElement element) =>
        (Dimension(element, "width"), Dimension(element, "height"));

    private static int Dimension(XElement element, string attribute) =>
        int.TryParse(element.Attribute(attribute)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        && value > 0 ? value : 0;

    private static string? Absolute(string? raw, Uri feedUrl)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var decoded = WebUtility.HtmlDecode(raw).Trim();
        return decoded.Length > 0 && Uri.TryCreate(feedUrl, decoded, out var absolute) && IsWeb(absolute)
            ? absolute.AbsoluteUri
            : null;
    }

    private static bool IsWeb(Uri uri) => uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp;

    private static string? Text(XElement node, string localName) =>
        Clean(node.Elements().FirstOrDefault(e => e.Name.LocalName == localName && !e.HasElements)?.Value);

    private static string? Clean(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>
    /// FNV-1a over the image URL. string.GetHashCode is randomised per process, and this id ends up in
    /// favourites and the no-repeat history, so it has to survive a restart.
    /// </summary>
    private static string StableId(string url)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(url))
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    [GeneratedRegex("""<img\b[^>]*?\bsrc\s*=\s*["']([^"']+)["']""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImageTagRegex();

    /// <summary>Whitespace always separates; a comma only does when another URL follows, because
    /// feed query strings use commas too (Flickr's <c>tags=</c>, for one).</summary>
    [GeneratedRegex(@"\s+|,(?=\s*https?://)", RegexOptions.CultureInvariant)]
    private static partial Regex FeedSeparatorRegex();

    private sealed record ImageRef(string Url, int Rule, int Width, int Height);

    private sealed record FeedEntry(PhotoItem Item, DateTimeOffset? Published, string SearchText);

    private sealed record FeedResult(Uri Url, XDocument? Document, SourceException? Error);
}
