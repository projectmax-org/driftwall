using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Driftwall.Core;

/// <summary>Shared HTTP plumbing. One pooled handler for the whole process.</summary>
public static class Net
{
    public const string UserAgent = "Driftwall/1.0 (+https://projectmax.app/driftwall)";

    private static readonly Lazy<HttpClient> _client = new(() =>
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            MaxConnectionsPerServer = 6,
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        c.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate, br");
        return c;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    public static HttpClient Client => _client.Value;

    /// <summary>GET a URL and deserialize the JSON body, mapping HTTP failures to SourceException.</summary>
    public static async Task<T?> GetJsonAsync<T>(
        HttpClient http, string url, CancellationToken ct,
        Action<HttpRequestMessage>? configure = null, string providerName = "Source")
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        configure?.Invoke(req);

        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        EnsureOk(res, providerName);

        await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, DriftwallJson.Network, ct).ConfigureAwait(false);
    }

    /// <summary>GET a URL and return the JSON body as a JsonDocument the caller must dispose.</summary>
    public static async Task<JsonDocument> GetJsonDocumentAsync(
        HttpClient http, string url, CancellationToken ct,
        Action<HttpRequestMessage>? configure = null, string providerName = "Source")
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        configure?.Invoke(req);

        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        EnsureOk(res, providerName);

        await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
    }

    /// <summary>GET a URL as text (for RSS/Atom).</summary>
    public static async Task<string> GetStringAsync(
        HttpClient http, string url, CancellationToken ct,
        Action<HttpRequestMessage>? configure = null, string providerName = "Source")
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        configure?.Invoke(req);

        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        EnsureOk(res, providerName);
        return await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    public static void EnsureOk(HttpResponseMessage res, string providerName)
    {
        if (res.IsSuccessStatusCode) return;

        throw res.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new SourceException($"{providerName} rejected the request ({(int)res.StatusCode}). Check the API key.", isAuth: true),
            HttpStatusCode.TooManyRequests =>
                new SourceException($"{providerName} rate limit reached. Try again later.", isRateLimit: true),
            HttpStatusCode.NotFound =>
                new SourceException($"{providerName} returned 404 — that category or feed may no longer exist."),
            _ => new SourceException($"{providerName} returned HTTP {(int)res.StatusCode} ({res.ReasonPhrase})."),
        };
    }

    /// <summary>Percent-encode a query-string value.</summary>
    public static string Esc(string? value) => Uri.EscapeDataString(value ?? string.Empty);

    /// <summary>Fire-and-forget ping used for provider attribution requirements. Never throws.</summary>
    public static void TrackDownload(HttpClient http, string? url, Action<HttpRequestMessage>? configure = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                configure?.Invoke(req);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var _ = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            }
            catch { /* attribution ping is best-effort by design */ }
        });
    }
}
