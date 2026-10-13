using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Driftwall.Core;

/// <summary>
/// Content-addressed download cache for photos and thumbnails.
/// <para>
/// Two things matter here. Concurrent requests for the same URL must share one download, or scrolling
/// the browse grid would fire the same request a dozen times. And the cache must stay bounded without
/// a background sweeper thread, so eviction is amortised onto writes.
/// </para>
/// </summary>
public sealed class ImageCache
{
    private static readonly string[] KnownExtensions =
        [".jpg", ".jpeg", ".png", ".bmp", ".webp", ".gif", ".tif", ".tiff", ".avif", ".heic", ".jfif"];

    /// <summary>
    /// How long a download may go without receiving a byte. The client's own timeout stops once the
    /// headers arrive, so without this a connection that dies mid-body (sleep, a Wi-Fi drop) would
    /// leave the download, and everyone waiting on it, hanging for good.
    /// </summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, Task<string?>> _inFlight = new(StringComparer.Ordinal);
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _downloadLimit;

    private long _bytesWrittenSinceSweep;
    private int _sweeping;

    public ImageCache(HttpClient http, int maxConcurrentDownloads = 4)
    {
        _http = http;
        _downloadLimit = new SemaphoreSlim(maxConcurrentDownloads, maxConcurrentDownloads);
    }

    /// <summary>Upper bound for each cache directory, in bytes. Set from settings.</summary>
    public long MaxBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>Resolves a photo to a local file, downloading it if necessary. Null when unavailable.</summary>
    public Task<string?> GetFullAsync(PhotoItem photo, CancellationToken ct) =>
        ResolveAsync(photo, photo.FullUrl ?? photo.PreviewUrl ?? photo.ThumbnailUrl, Paths.ImageCacheDirectory, ct);

    /// <summary>Resolves a photo's grid thumbnail to a local file.</summary>
    public Task<string?> GetThumbnailAsync(PhotoItem photo, CancellationToken ct) =>
        ResolveAsync(photo, photo.ThumbnailUrl ?? photo.PreviewUrl ?? photo.FullUrl, Paths.ThumbnailCacheDirectory, ct);

    private Task<string?> ResolveAsync(PhotoItem photo, string? url, string directory, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(photo.LocalPath)) return GetAsync(url, directory, photo, ct);

        // A local file that has gone missing falls back to the photo's URL, when it has one: a saved
        // copy can be deleted, and older versions could record a cache file as the saved copy. The
        // check runs off the caller's thread, often the UI's, because a file on a network share that
        // is not there can keep it waiting for many seconds.
        var local = photo.LocalPath;
        return Task.Run(() => File.Exists(local) ? Task.FromResult<string?>(local) : GetAsync(url, directory, photo, ct), ct);
    }

    private Task<string?> GetAsync(string? url, string directory, PhotoItem photo, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return Task.FromResult<string?>(null);

        var path = PathFor(url, directory);
        if (File.Exists(path))
        {
            TouchQuietly(path);
            return Task.FromResult<string?>(path);
        }

        // One download per URL, however many callers ask for it.
        var task = _inFlight.GetOrAdd(path, _ => DownloadAsync(url, path, photo, ct));

        if (task.IsCompleted) _inFlight.TryRemove(path, out Task<string?>? _);
        else task.ContinueWith(_ => _inFlight.TryRemove(path, out Task<string?>? _), TaskScheduler.Default);

        // The shared download follows the token of whoever started it, so a caller that joined it
        // later would otherwise wait past its own deadline.
        return ct.CanBeCanceled ? task.WaitAsync(ct) : task;
    }

    private async Task<string?> DownloadAsync(string url, string path, PhotoItem photo, CancellationToken ct)
    {
        await _downloadLimit.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("image/*");
            if (!string.IsNullOrEmpty(photo.SourcePageUrl) && Uri.TryCreate(photo.SourcePageUrl, UriKind.Absolute, out var referer))
                request.Headers.Referrer = referer;

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"Download failed ({(int)response.StatusCode}) for {url}");
                return null;
            }

            // Hotlink guards, captive portals and "image removed" pages answer with a 200 and a web
            // page. Cached under the photo's URL, that page would stand in for the photo from then on.
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && IsNeverAnImage(mediaType))
            {
                Log.Warn($"Discarded a non-image response ({mediaType}) for {url}");
                return null;
            }

            var temp = path + ".part";
            long written;
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(StallTimeout);
            await using (var networkStream = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false))
            await using (var fileStream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = await networkStream.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    stall.CancelAfter(StallTimeout);
                }
                written = fileStream.Length;
            }

            if (written < 1024)
            {
                // Providers occasionally serve a tiny HTML error page with a 200 status.
                TryDelete(temp);
                Log.Warn($"Discarded implausibly small download ({written} bytes) for {url}");
                return null;
            }

            if (StartsLikeMarkup(temp))
            {
                // The same pages sometimes claim to be an image; no image format opens with markup.
                TryDelete(temp);
                Log.Warn($"Discarded a web page served in place of an image for {url}");
                return null;
            }

            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);

            NoteWrite(written, Path.GetDirectoryName(path)!);
            return path;
        }
        catch (OperationCanceledException)
        {
            TryDelete(path + ".part");
            // The caller giving up is routine; a server that stopped answering is worth a line.
            if (!ct.IsCancellationRequested) Log.Warn("Download timed out for " + url);
            return null;
        }
        catch (Exception ex)
        {
            TryDelete(path + ".part");
            Log.Warn("Download failed for " + url, ex);
            return null;
        }
        finally
        {
            _downloadLimit.Release();
        }
    }

    /// <summary>
    /// Content types that are never a photo: a web page, an API error or a feed. Anything else is
    /// let through, since CDNs often send images as application/octet-stream or with no type at all.
    /// </summary>
    private static bool IsNeverAnImage(string mediaType) =>
        !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        && (mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase));

    /// <summary>True when a file opens like HTML, XML or JSON, which no image format does.</summary>
    private static bool StartsLikeMarkup(string file)
    {
        Span<byte> head = stackalloc byte[64];
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            head = head[..stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false)];

        ReadOnlySpan<byte> text = head;
        // Skip a UTF-8 byte order mark and leading whitespace, either of which a text page may start with.
        if (text.Length >= 3 && text[0] == 0xEF && text[1] == 0xBB && text[2] == 0xBF) text = text[3..];
        text = text.TrimStart(" \t\r\n"u8);
        return !text.IsEmpty && (text[0] == (byte)'<' || text[0] == (byte)'{');
    }

    /// <summary>Copies a cached file into permanent storage so a saved photo survives cache eviction.</summary>
    public async Task<string?> SaveCopyAsync(PhotoItem photo, CancellationToken ct)
    {
        var source = await GetFullAsync(photo, ct).ConfigureAwait(false);
        if (source is null) return null;

        // A photo that came from a collection may already be a permanent copy.
        if (string.Equals(Path.GetDirectoryName(source), Paths.SavedDirectory, StringComparison.OrdinalIgnoreCase))
            return source;

        // Up to 1.0.4 a copy was named "<provider>-<id>". Where that name was neither cut short nor
        // changed it belongs to this photo alone, so a copy already saved under it is used again rather
        // than saved a second time.
        var legacyText = $"{photo.ProviderId}-{photo.Id}";
        if (Paths.SafeFileName(legacyText) == legacyText)
        {
            var legacy = Path.Combine(Paths.SavedDirectory, legacyText + Path.GetExtension(source));
            if (File.Exists(legacy)) return legacy;
        }

        // The readable part is cut short to fit a file name, and RSS ids are whole permalinks that
        // often share their first sixty characters, so a hash of the full key keeps photos apart.
        var keyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(photo.Key)))[..16];
        var name = Paths.SafeFileName($"{photo.ProviderId}-{photo.Id}", 40) + "-" + keyHash + Path.GetExtension(source);
        var destination = Path.Combine(Paths.SavedDirectory, name);

        try
        {
            if (!File.Exists(destination)) File.Copy(source, destination);
            return destination;
        }
        catch (Exception ex)
        {
            // Handing back the cache file instead would record it as permanent, and the photo would
            // break once the cache is swept. Without a saved copy it keeps resolving through its URL.
            Log.Warn("Could not save a permanent copy of " + photo.Key, ex);
            return null;
        }
    }

    public static string PathFor(string url, string directory)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..32];
        return Path.Combine(directory, hash + ExtensionFor(url));
    }

    private static string ExtensionFor(string url)
    {
        // Strip the query before looking at the extension; most CDN URLs carry sizing parameters.
        var withoutQuery = url.Split('?', '#')[0];
        var extension = Path.GetExtension(withoutQuery);
        if (string.IsNullOrEmpty(extension)) return ".jpg";

        extension = extension.ToLowerInvariant();
        return Array.IndexOf(KnownExtensions, extension) >= 0 ? extension : ".jpg";
    }

    // ---------------- eviction ----------------

    private void NoteWrite(long bytes, string directory)
    {
        // Sweeping on every write would stat thousands of files constantly; sweeping after roughly
        // a tenth of the budget has been written keeps the cost negligible and the cap honoured.
        var accumulated = Interlocked.Add(ref _bytesWrittenSinceSweep, bytes);
        if (accumulated < MaxBytes / 10) return;

        Interlocked.Exchange(ref _bytesWrittenSinceSweep, 0);
        if (Interlocked.Exchange(ref _sweeping, 1) == 1) return;

        _ = Task.Run(() =>
        {
            try { Sweep(directory); }
            finally { Interlocked.Exchange(ref _sweeping, 0); }
        });
    }

    /// <summary>Deletes least-recently-used files until the directory is back under budget.</summary>
    public void Sweep(string directory)
    {
        try
        {
            var files = new DirectoryInfo(directory).GetFiles();
            long total = files.Sum(f => f.Length);
            if (total <= MaxBytes) return;

            foreach (var file in files.OrderBy(f => f.LastAccessTimeUtc))
            {
                if (total <= MaxBytes * 0.8) break;
                long length = file.Length;
                if (TryDelete(file.FullName)) total -= length;
            }

            Log.Info($"Cache swept: {directory} now {total / (1024 * 1024)} MB");
        }
        catch (Exception ex)
        {
            Log.Warn("Cache sweep failed for " + directory, ex);
        }
    }

    public void SweepAll()
    {
        Sweep(Paths.ImageCacheDirectory);
        Sweep(Paths.ThumbnailCacheDirectory);
    }

    public static long DirectorySize(string directory)
    {
        try { return new DirectoryInfo(directory).EnumerateFiles().Sum(f => f.Length); }
        catch { return 0; }
    }

    public void Clear()
    {
        foreach (var directory in new[] { Paths.ImageCacheDirectory, Paths.ThumbnailCacheDirectory })
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory)) TryDelete(file);
            }
            catch (Exception ex)
            {
                Log.Warn("Could not clear " + directory, ex);
            }
        }
    }

    private static void TouchQuietly(string path)
    {
        // Last-access time drives LRU eviction, and Windows does not reliably update it on read.
        try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch { /* not worth reporting */ }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
