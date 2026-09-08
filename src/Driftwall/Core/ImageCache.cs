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
    public Task<string?> GetFullAsync(PhotoItem photo, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(photo.LocalPath))
            return Task.FromResult<string?>(File.Exists(photo.LocalPath) ? photo.LocalPath : null);

        var url = photo.FullUrl ?? photo.PreviewUrl ?? photo.ThumbnailUrl;
        return GetAsync(url, Paths.ImageCacheDirectory, photo, ct);
    }

    /// <summary>Resolves a photo's grid thumbnail to a local file.</summary>
    public Task<string?> GetThumbnailAsync(PhotoItem photo, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(photo.LocalPath))
            return Task.FromResult<string?>(File.Exists(photo.LocalPath) ? photo.LocalPath : null);

        var url = photo.ThumbnailUrl ?? photo.PreviewUrl ?? photo.FullUrl;
        return GetAsync(url, Paths.ThumbnailCacheDirectory, photo, ct);
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

        return task;
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

            var temp = path + ".part";
            long written;
            await using (var networkStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var fileStream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                await networkStream.CopyToAsync(fileStream, 64 * 1024, ct).ConfigureAwait(false);
                written = fileStream.Length;
            }

            if (written < 1024)
            {
                // Providers occasionally serve a tiny HTML error page with a 200 status.
                TryDelete(temp);
                Log.Warn($"Discarded implausibly small download ({written} bytes) for {url}");
                return null;
            }

            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);

            NoteWrite(written, Path.GetDirectoryName(path)!);
            return path;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn("Download failed for " + url, ex);
            return null;
        }
        finally
        {
            _downloadLimit.Release();
        }
    }

    /// <summary>Copies a cached file into permanent storage so a saved photo survives cache eviction.</summary>
    public async Task<string?> SaveCopyAsync(PhotoItem photo, CancellationToken ct)
    {
        var source = await GetFullAsync(photo, ct).ConfigureAwait(false);
        if (source is null) return null;

        var name = Paths.SafeFileName($"{photo.ProviderId}-{photo.Id}") + Path.GetExtension(source);
        var destination = Path.Combine(Paths.SavedDirectory, name);

        try
        {
            if (!File.Exists(destination)) File.Copy(source, destination);
            return destination;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not save a permanent copy of " + photo.Key, ex);
            return source;
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
