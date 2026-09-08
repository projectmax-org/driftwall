using System.Globalization;
using System.Text.Json;
using Driftwall.Core;

namespace Driftwall.Sources;

/// <summary>
/// NASA's Astronomy Picture of the Day (api.nasa.gov/planetary/apod). Top walks the archive
/// backwards one window of days per page, Random uses the service's own <c>count=</c> sampler, and
/// Search filters titles and explanations client-side because APOD has no search endpoint.
/// </summary>
/// <remarks>
/// The service reports no pixel dimensions, author page or tags, so <see cref="PhotoItem.Width"/>
/// and <see cref="PhotoItem.Height"/> stay 0 and <see cref="SourceQuery.Orientation"/>, MinWidth and
/// MinHeight cannot be honoured server- or client-side — there is nothing to filter on. SafeSearch is
/// moot: APOD is hand-curated astronomy imagery.
/// </remarks>
public sealed class NasaApodSource : PhotoSourceBase
{
    private const string Endpoint = "https://api.nasa.gov/planetary/apod";

    /// <summary>
    /// Shared key api.nasa.gov accepts without registration. Documented at 30 requests/hour and
    /// 50/day per IP, though the live X-RateLimit-Limit header has been seen as low as 10 — treat
    /// the documented figures as a ceiling, not a promise.
    /// </summary>
    private const string DemoKey = "DEMO_KEY";

    /// <summary>Documented ceiling for the <c>count</c> parameter.</summary>
    private const int MaxCount = 100;

    /// <summary>Upper bound on the days one Search page scans, so a page stays a single request.</summary>
    private const int MaxSearchWindowDays = 180;

    /// <summary>The first APOD. Any date outside [this, today UTC] makes the service answer HTTP 400.</summary>
    private static readonly DateOnly FirstApod = new(1995, 6, 16);

    private static readonly string[] TimeRanges = { "day", "week", "month", "year", "all" };

    private static readonly char[] Whitespace = { ' ', '\t', '\r', '\n', '\f', '\v' };

    public override string Id => "nasa";

    public override string DisplayName => "NASA APOD";

    public override string Description =>
        "Astronomy Picture of the Day, one image a day since 1995. Works with no key — the shared DEMO_KEY " +
        "allows 30 requests an hour and 50 a day per IP address; a free key from api.nasa.gov raises that to 1,000 an hour.";

    public override string? ApiKeyHelpUrl => "https://api.nasa.gov/";

    public override string? LicenseUrl => "https://apod.nasa.gov/apod/lib/about_apod.html";

    /// <summary>APOD has no popularity ordering, so the time range caps how far back Top may page.</summary>
    public override IReadOnlyList<string> SupportedTimeRanges => TimeRanges;

    public override async Task<PhotoPage> FetchAsync(SourceQuery query, SourceContext context, CancellationToken ct)
    {
        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, MaxCount);
        var keyword = query.Keyword?.Trim();

        try
        {
            return query.Mode switch
            {
                QueryMode.Random => await FetchRandomAsync(size, context, ct).ConfigureAwait(false),
                QueryMode.Search when !string.IsNullOrEmpty(keyword) =>
                    await FetchSearchAsync(keyword, page, size, context, ct).ConfigureAwait(false),
                // Category is not offered and a keyword-less search has nothing to match on:
                // both fall back to the most recent pictures.
                _ => await FetchRecentAsync(query.TimeRange, page, size, context, ct).ConfigureAwait(false),
            };
        }
        catch (SourceException ex) when (ex.IsRateLimit && UsingDemoKey(context))
        {
            throw new SourceException(
                "NASA's shared DEMO_KEY is out of requests (30 an hour, 50 a day, counted per IP address). " +
                "Add a free key from api.nasa.gov in Settings → API keys.",
                isRateLimit: true, inner: ex);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new SourceException($"{DisplayName} did not respond in time.", inner: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new SourceException($"{DisplayName} could not be reached ({ex.Message})", inner: ex);
        }
        catch (JsonException ex)
        {
            throw new SourceException($"{DisplayName} sent a response Driftwall could not read.", inner: ex);
        }
    }

    /// <summary>Newest pictures first, one <paramref name="size"/>-day window per page.</summary>
    private async Task<PhotoPage> FetchRecentAsync(string timeRange, int page, int size, SourceContext context, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var oldest = EarliestFor(timeRange, today);

        if (!TryWindow(today, oldest, page, size, out var start, out var end)) return PhotoPage.Empty;

        var items = await FetchRangeAsync(start, end, context, ct).ConfigureAwait(false);
        return new PhotoPage(items, HasMore: start > oldest);
    }

    /// <summary>
    /// There is no search endpoint, so each page pulls a fixed slice of the archive and matches
    /// locally. Every hit in the slice is returned — dropping the overflow would make those pictures
    /// unreachable, since the next page moves the whole window further back.
    /// </summary>
    private async Task<PhotoPage> FetchSearchAsync(string keyword, int page, int size, SourceContext context, CancellationToken ct)
    {
        var window = Math.Clamp(size * 6, 30, MaxSearchWindowDays);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        if (!TryWindow(today, FirstApod, page, window, out var start, out var end)) return PhotoPage.Empty;

        var terms = keyword.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries);
        var items = await FetchRangeAsync(start, end, context, ct, el => Matches(el, terms)).ConfigureAwait(false);
        var hasMore = start > FirstApod;

        return items.Count > 0
            ? new PhotoPage(items, hasMore)
            : new PhotoPage(Array.Empty<PhotoItem>(), hasMore,
                $"No picture between {Iso(start)} and {Iso(end)} mentions \"{keyword}\". Load more to keep looking further back.");
    }

    /// <summary>Random uses <c>count</c>, which samples the whole archive, so every call is a fresh page.</summary>
    private async Task<PhotoPage> FetchRandomAsync(int size, SourceContext context, CancellationToken ct)
    {
        var url = $"{Endpoint}?count={size}";
        var items = await GetItemsAsync(url, context, ct).ConfigureAwait(false);
        return items.Count > 0 ? new PhotoPage(items, HasMore: true) : PhotoPage.Empty;
    }

    private async Task<List<PhotoItem>> FetchRangeAsync(
        DateOnly start, DateOnly end, SourceContext context, CancellationToken ct, Func<JsonElement, bool>? include = null)
    {
        var url = $"{Endpoint}?start_date={Iso(start)}&end_date={Iso(end)}";
        var items = await GetItemsAsync(url, context, ct, include).ConfigureAwait(false);

        // The service answers a range in ascending date order; the app wants newest first.
        items.Sort(static (a, b) => string.CompareOrdinal(b.Id, a.Id));
        return items;
    }

    private async Task<List<PhotoItem>> GetItemsAsync(
        string url, SourceContext context, CancellationToken ct, Func<JsonElement, bool>? include = null)
    {
        var key = ResolveKey(context);
        using var doc = await Net.GetJsonDocumentAsync(
            context.Http, url, ct,
            req => req.Headers.TryAddWithoutValidation("X-Api-Key", key),
            DisplayName).ConfigureAwait(false);

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
            throw new SourceException($"{DisplayName} sent a response Driftwall could not read.");

        var items = new List<PhotoItem>(root.GetArrayLength());
        foreach (var element in root.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            if (include is not null && !include(element)) continue;

            var item = Map(element);
            if (item is not null) items.Add(item);
        }

        return items;
    }

    private PhotoItem? Map(JsonElement element)
    {
        // Roughly one entry a week is a video or an interactive page with no still image behind it.
        if (!string.Equals(Str(element, "media_type"), "image", StringComparison.OrdinalIgnoreCase)) return null;

        var date = Str(element, "date");
        if (string.IsNullOrEmpty(date)) return null;

        // `url` is the ~1000px web copy; `hdurl` is the full-resolution original and is occasionally absent.
        var preview = Str(element, "url");
        var full = Str(element, "hdurl") ?? preview;
        if (full is null) return null;

        var copyright = Clean(Str(element, "copyright"));

        return new PhotoItem
        {
            Id = date,
            ProviderId = Id,
            ProviderName = DisplayName,
            // APOD publishes no smaller rendition, so the web copy doubles as the grid thumbnail.
            ThumbnailUrl = preview ?? full,
            PreviewUrl = preview ?? full,
            FullUrl = full,
            Title = Clean(Str(element, "title")),
            AuthorName = copyright,
            SourcePageUrl = ArchiveUrl(date),
            License = copyright is null ? "Public domain (NASA)" : "Copyright " + copyright,
        };
    }

    /// <summary>
    /// The page-th window of <paramref name="days"/> days counting back from <paramref name="today"/>,
    /// clipped to <paramref name="oldest"/>. False once the archive is exhausted.
    /// </summary>
    private static bool TryWindow(DateOnly today, DateOnly oldest, int page, int days, out DateOnly start, out DateOnly end)
    {
        start = end = today;

        var offset = (long)(page - 1) * days;
        if (offset > today.DayNumber - oldest.DayNumber) return false;

        end = DateOnly.FromDayNumber(today.DayNumber - (int)offset);
        start = DateOnly.FromDayNumber(Math.Max(oldest.DayNumber, end.DayNumber - days + 1));
        return true;
    }

    private static DateOnly EarliestFor(string? timeRange, DateOnly today)
    {
        var earliest = timeRange?.Trim().ToLowerInvariant() switch
        {
            "day" => today,
            "week" => today.AddDays(-6),
            "month" => today.AddDays(-30),
            "year" => today.AddDays(-364),
            _ => FirstApod,
        };
        return earliest > FirstApod ? earliest : FirstApod;
    }

    private static bool Matches(JsonElement element, string[] terms)
    {
        var haystack = Str(element, "title") + " " + Str(element, "explanation");
        foreach (var term in terms)
        {
            if (!haystack.Contains(term, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>
    /// APOD's own page for a date, e.g. 2026-08-25 -> ap260825.html. NASA has announced a move to
    /// science.nasa.gov/apod, but the dated pages still resolve and remain the canonical permalink.
    /// </summary>
    private static string? ArchiveUrl(string date) =>
        DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? "https://apod.nasa.gov/apod/ap" + parsed.ToString("yyMMdd", CultureInfo.InvariantCulture) + ".html"
            : null;

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Credits and titles arrive with embedded newlines and padding; flatten them to one line.</summary>
    private static string? Clean(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : string.Join(' ', text.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries));

    private string ResolveKey(SourceContext context)
    {
        var key = context.GetApiKey(Id);
        return string.IsNullOrWhiteSpace(key) ? DemoKey : key.Trim();
    }

    private bool UsingDemoKey(SourceContext context) => ResolveKey(context) == DemoKey;
}
