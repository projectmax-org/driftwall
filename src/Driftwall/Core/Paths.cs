namespace Driftwall.Core;

/// <summary>Every on-disk location the app uses. Created lazily on first access.</summary>
public static class Paths
{
    private static string? _data;

    /// <summary>%LOCALAPPDATA%\Driftwall — settings, collections, logs, caches.</summary>
    public static string DataDirectory => _data ??= EnsureDirectory(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Driftwall"));

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string CollectionsFile => Path.Combine(DataDirectory, "collections.json");

    public static string StateFile => Path.Combine(DataDirectory, "state.json");

    /// <summary>Downloaded originals, keyed by content hash.</summary>
    public static string ImageCacheDirectory => EnsureDirectory(Path.Combine(DataDirectory, "cache", "images"));

    /// <summary>Small grid thumbnails.</summary>
    public static string ThumbnailCacheDirectory => EnsureDirectory(Path.Combine(DataDirectory, "cache", "thumbs"));

    /// <summary>Composed per-monitor wallpaper files handed to Windows.</summary>
    public static string RenderDirectory => EnsureDirectory(Path.Combine(DataDirectory, "rendered"));

    /// <summary>Full-size copies of photos the user saved to a collection, kept independent of the cache.</summary>
    public static string SavedDirectory => EnsureDirectory(Path.Combine(DataDirectory, "saved"));

    public static string EnsureDirectory(string path)
    {
        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Turns arbitrary text into something safe to use as a file name.</summary>
    public static string SafeFileName(string value, int maxLength = 60)
    {
        Span<char> buffer = stackalloc char[Math.Min(value.Length, maxLength)];
        var invalid = Path.GetInvalidFileNameChars();
        int n = 0;
        foreach (var ch in value)
        {
            if (n == buffer.Length) break;
            buffer[n++] = Array.IndexOf(invalid, ch) >= 0 ? '_' : ch;
        }
        var result = new string(buffer[..n]).Trim();
        return result.Length == 0 ? "untitled" : result;
    }
}
