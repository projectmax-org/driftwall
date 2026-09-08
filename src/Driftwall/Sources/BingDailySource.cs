using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Driftwall.Core;

namespace Driftwall.Sources;

/// <summary>
/// Bing's home-page image of the day. No API key, no quota headers.
/// The archive endpoint only serves the last 8 days for a single market, so a browsable gallery is
/// assembled by querying several markets in parallel and merging them. Bing syndicates one photo to
/// many markets under a different id and a localised caption each time, so entries are deduplicated
/// by the image's base name and the first market that supplies one wins its metadata — which is why
/// the default market list leads with English.
/// </summary>
public sealed partial class BingDailySource : PhotoSourceBase
{
    private const string Host = "https://www.bing.com";
    private const string ProviderKey = "bing";
    private const string Name = "Bing Daily";

    /// <summary>The public endpoint clamps idx to 7 and n to 8, so one call returns the entire window.</summary>
    private const int ImagesPerMarket = 8;

    /// <summary>Upper bound on markets, and therefore on requests issued per fetch.</summary>
    private const int MaxMarkets = 16;

    /// <summary>
    /// Microsoft licenses these images for use as desktop wallpaper only — exactly what this app does
    /// with them. They must not be redistributed, resold or used commercially, so the app may cache a
    /// copy for the current wallpaper but must never republish one.
    /// </summary>
    private const string LicenseText = "Bing wallpaper — personal use only";

    private static readonly string[] DefaultMarkets =
        ["en-US", "en-GB", "en-AU", "en-CA", "en-IN", "de-DE", "fr-FR", "ja-JP", "zh-CN", "pt-BR"];

    /// <summary>Renditions Bing publishes for every daily image, verified against the live CDN.</summary>
    private static readonly Rendition Landscape = new("_UHD.jpg", 3840, 2160);
    private static readonly Rendition Portrait = new("_1080x1920.jpg", 1080, 1920);
    private static readonly Rendition Square = new("_UHD.jpg", 2160, 2160);

    public override string Id => ProviderKey;
    public override string DisplayName => Name;
    public override string Description => "Microsoft's daily home-page photo, merged across world markets.";

    public override string? LicenseUrl => "https://www.microsoft.com/en-us/bing/bing-wallpaper";

    public override bool SupportsSearch => false;

    /// <summary>The feed is only ever 8 days deep, so anything longer than a week means "everything".</summary>
    public override IReadOnlyList<string> SupportedTimeRanges => ["day", "week", "all"];

    public override async Task<PhotoPage> FetchAsync(SourceQuery query, SourceContext context, CancellationToken ct)
    {
        var entries = await GatherAsync(ResolveMarkets(context.Selection), context.Http, ct).ConfigureAwait(false);
        if (entries.Length == 0) return PhotoPage.Empty;

        var rendition = RenditionFor(query.Orientation);
        if (query.MinWidth > rendition.Width || query.MinHeight > rendition.Height)
            return PhotoPage.Message($"Bing daily images top out at {rendition.Width}×{rendition.Height}.");

        entries = ApplyTimeRange(entries, query.TimeRange);
        if (entries.Length == 0) return PhotoPage.Empty;

        // Reshuffling more than once a day would be noise: the feed itself only changes daily, and a
        // seed that is stable for the day keeps successive pages from repeating or skipping photos.
        if (query.Mode == QueryMode.Random)
            new Random(DateOnly.FromDateTime(DateTime.UtcNow).DayNumber).Shuffle(entries);

        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var skip = (Math.Max(query.Page, 1) - 1L) * pageSize;
        if (skip >= entries.Length) return PhotoPage.Empty;

        var take = (int)Math.Min(pageSize, entries.Length - skip);
        var items = new List<PhotoItem>(take);
        for (var i = 0; i < take; i++)
            items.Add(ToPhotoItem(entries[(int)skip + i], rendition));

        var notice = query.Mode is QueryMode.Search or QueryMode.Category
            ? "Bing Daily is a curated feed — keywords and categories don't apply."
            : null;

        return new PhotoPage(items, skip + take < entries.Length, notice);
    }

    /// <summary>Queries every market at once, then merges them newest-first, dropping repeats.</summary>
    private static async Task<Entry[]> GatherAsync(string[] markets, HttpClient http, CancellationToken ct)
    {
        var tasks = new Task<List<Entry>>[markets.Length];
        for (var i = 0; i < markets.Length; i++)
            tasks[i] = FetchMarketAsync(http, markets[i], ct);

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // A market that is unreachable or blocked must not cost us the other nine.
        }
        ct.ThrowIfCancellationRequested();

        var merged = new List<Entry>(markets.Length * ImagesPerMarket);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SourceException? failure = null;

        foreach (var task in tasks)
        {
            if (!task.IsCompletedSuccessfully)
            {
                failure ??= AsSourceException(task.Exception);
                continue;
            }
            foreach (var entry in task.Result)
                if (seen.Add(entry.Slug)) merged.Add(entry);
        }

        // Only surface a failure when it cost us the whole gallery; a partial result is still useful.
        if (merged.Count == 0 && failure is not null) throw failure;

        // OrderByDescending is a stable sort, so same-day photos keep their market order and paging
        // stays consistent between calls.
        return merged.OrderByDescending(e => e.Date).ToArray();
    }

    private static async Task<List<Entry>> FetchMarketAsync(HttpClient http, string market, CancellationToken ct)
    {
        var url = $"{Host}/HPImageArchive.aspx?format=js&idx=0&n={ImagesPerMarket}&mkt={Net.Esc(market)}";

        try
        {
            using var doc = await Net.GetJsonDocumentAsync(http, url, ct, providerName: Name).ConfigureAwait(false);

            var entries = new List<Entry>(ImagesPerMarket);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
                return entries;

            foreach (var image in images.EnumerateArray())
                if (ReadEntry(image) is { } entry) entries.Add(entry);

            return entries;
        }
        catch (JsonException ex)
        {
            throw new SourceException($"{Name} returned a response that could not be parsed.", inner: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new SourceException($"{Name} could not be reached ({market}).", inner: ex);
        }
    }

    private static Entry? ReadEntry(JsonElement image)
    {
        var urlBase = ReadString(image, "urlbase");
        if (urlBase is null) return null;

        if (!DateOnly.TryParseExact(ReadString(image, "startdate"), "yyyyMMdd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            date = DateOnly.FromDateTime(DateTime.UtcNow);

        return new Entry(
            SlugOf(urlBase),
            date,
            urlBase,
            ReadString(image, "copyright"),
            ReadString(image, "copyrightlink"),
            ReadString(image, "title"));
    }

    private static PhotoItem ToPhotoItem(Entry entry, Rendition rendition)
    {
        var full = Host + entry.UrlBase + rendition.Suffix;
        var (title, author) = SplitCopyright(entry.Copyright, entry.Headline);

        return new PhotoItem
        {
            Id = entry.Slug,
            ProviderId = ProviderKey,
            ProviderName = Name,
            ThumbnailUrl = Scaled(full, rendition, 400),
            PreviewUrl = Scaled(full, rendition, 1080),
            FullUrl = full,
            Width = rendition.Width,
            Height = rendition.Height,
            Title = title,
            AuthorName = author,
            AuthorUrl = null, // Bing credits the photographer and agency but publishes no profile link.
            SourcePageUrl = Absolute(entry.CopyrightLink),
            License = LicenseText,
            Tags = KeywordsOf(entry.CopyrightLink),
        };
    }

    /// <summary>
    /// Bing has no square rendition, so one is cut from the UHD master by its own resizer
    /// (rs=1&amp;c=4 scales and centre-crops).
    /// </summary>
    private static Rendition RenditionFor(PhotoOrientation orientation) => orientation switch
    {
        PhotoOrientation.Portrait => Portrait,
        PhotoOrientation.Square => Square,
        _ => Landscape,
    };

    /// <summary>Asks Bing's resizer for a width, preserving the rendition's aspect ratio.</summary>
    private static string Scaled(string full, Rendition rendition, int width)
    {
        if (width >= rendition.Width) return full;
        var height = (int)Math.Round(width * (double)rendition.Height / rendition.Width);
        return $"{full}&w={width}&h={height}&rs=1&c=4";
    }

    private static Entry[] ApplyTimeRange(Entry[] entries, string? timeRange)
    {
        if (entries.Length == 0) return entries;

        var newest = entries[0].Date;
        var cutoff = timeRange?.ToLowerInvariant() switch
        {
            "day" => newest,
            "week" => newest.AddDays(-6),
            _ => DateOnly.MinValue,
        };
        return cutoff == DateOnly.MinValue ? entries : entries.Where(e => e.Date >= cutoff).ToArray();
    }

    private static string[] ResolveMarkets(SourceSelection selection)
    {
        if (!selection.Options.TryGetValue("markets", out var raw) || string.IsNullOrWhiteSpace(raw))
            return DefaultMarkets;

        var custom = raw
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxMarkets)
            .ToArray();

        return custom.Length > 0 ? custom : DefaultMarkets;
    }

    /// <summary>
    /// Reduces "/th?id=OHR.SamarkandCeiling_EN-US3761829748" to "SamarkandCeiling" — the only part that
    /// is identical when the same photo is republished in another market, so it is both the dedupe key
    /// and a stable id for favourites and history.
    /// </summary>
    private static string SlugOf(string urlBase)
    {
        var token = urlBase;

        var id = token.IndexOf("id=", StringComparison.OrdinalIgnoreCase);
        if (id >= 0) token = token[(id + 3)..];

        var amp = token.IndexOf('&');
        if (amp >= 0) token = token[..amp];

        var slash = token.LastIndexOf('/');
        if (slash >= 0) token = token[(slash + 1)..];

        if (token.StartsWith("OHR.", StringComparison.OrdinalIgnoreCase)) token = token[4..];

        var stripped = MarketSuffix().Replace(token, string.Empty);
        return stripped.Length > 0 ? stripped : urlBase;
    }

    /// <summary>Splits "Caption text (© Photographer/Agency)" into its caption and its credit.</summary>
    private static (string? Title, string? Author) SplitCopyright(string? copyright, string? headline)
    {
        if (copyright is null) return (headline, null);

        var open = copyright.LastIndexOf('(');
        var close = copyright.LastIndexOf(')');
        if (open < 0 || close < open) return (copyright, null);

        var title = NullIfBlank(copyright[..open]) ?? headline;
        var author = NullIfBlank(copyright[(open + 1)..close].TrimStart('©', ' '));
        return (title, author);
    }

    /// <summary>
    /// The caption link carries the term Bing itself associates with the photo
    /// (…/search?q=Samarkand&amp;form=hpcapt), the closest thing the feed has to a keyword.
    /// </summary>
    private static IReadOnlyList<string> KeywordsOf(string? copyrightLink)
    {
        var start = copyrightLink?.IndexOf('?') ?? -1;
        if (start < 0) return [];

        foreach (var pair in copyrightLink![(start + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!pair.StartsWith("q=", StringComparison.OrdinalIgnoreCase)) continue;

            var keyword = NullIfBlank(Uri.UnescapeDataString(pair[2..].Replace('+', ' ')));
            return keyword is null ? [] : [keyword];
        }
        return [];
    }

    private static string? Absolute(string? link) => link switch
    {
        null => null,
        _ when link.StartsWith("http", StringComparison.OrdinalIgnoreCase) => link,
        _ when link.StartsWith('/') => Host + link,
        _ => null,
    };

    private static SourceException AsSourceException(AggregateException? error) =>
        error?.Flatten().InnerExceptions.OfType<SourceException>().FirstOrDefault()
        ?? new SourceException($"{Name} could not be reached.", inner: error);

    // TryGetProperty throws InvalidOperationException unless the receiver is an object, and a market
    // feed may carry a null in its images array, so the kind is confirmed before reaching in.
    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? NullIfBlank(value.GetString())
            : null;

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Matches the "_EN-US1234", "_ROW1234" or "_XX1234" market tag Bing appends to every id.</summary>
    [GeneratedRegex(@"_(?:ROW|[A-Z]{2}(?:-[A-Z]{2})?)\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex MarketSuffix();

    private sealed record Entry(
        string Slug,
        DateOnly Date,
        string UrlBase,
        string? Copyright,
        string? CopyrightLink,
        string? Headline);

    private readonly record struct Rendition(string Suffix, int Width, int Height);
}
