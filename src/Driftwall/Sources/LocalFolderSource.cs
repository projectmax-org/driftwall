using System.Buffers.Binary;
using System.IO;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using Driftwall.Core;

namespace Driftwall.Sources;

/// <summary>
/// Photos already on this machine. No key, no network: the user points Driftwall at one or more
/// folders, stored in <see cref="SourceSelection.Options"/> under "folders" and separated by '|'
/// (paths contain ',' and ';' legitimately). "recursive" toggles subfolder traversal.
/// <para>
/// <see cref="SourceQuery.SafeSearch"/> is ignored: there is nothing to moderate in files the user
/// already owns, and no metadata to moderate them by.
/// </para>
/// </summary>
public sealed class LocalFolderSource : PhotoSourceBase
{
    private const string FoldersOption = "folders";
    private const string RecursiveOption = "recursive";
    private const char FolderSeparator = '|';

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".gif", ".tif", ".tiff", ".jfif", ".avif", ".heic",
    };

    private static readonly string[] TimeRanges = ["day", "week", "month", "year", "all"];

    public override string Id => "local";
    public override string DisplayName => "Local folders";
    public override string Description => "Photos from folders on this PC. Works offline, no account needed.";

    public override bool SupportsCategories => false;
    public override IReadOnlyList<string> SupportedTimeRanges => TimeRanges;

    public override string? Validate(SourceContext context)
    {
        var folders = ReadFolders(context.Selection);
        if (folders.Count == 0)
            return "No folder picked yet. Choose one or more folders in Settings → Sources.";

        var missing = folders.FindAll(static folder => !Directory.Exists(folder));
        if (missing.Count < folders.Count) return null;

        return missing.Count == 1
            ? $"The folder \"{missing[0]}\" no longer exists."
            : "None of these folders exist any more: " + string.Join(", ", missing);
    }

    public override async Task<PhotoPage> FetchAsync(SourceQuery query, SourceContext context, CancellationToken ct)
    {
        var folders = ReadFolders(context.Selection);
        if (folders.Count == 0)
            return PhotoPage.Message("No folder picked yet. Choose one or more folders in Settings → Sources.");

        bool recursive = ReadRecursive(context.Selection);

        // Every step below is blocking file I/O, so keep all of it off the calling thread.
        return await Task.Run(() => Scan(query, folders, recursive, ct), ct).ConfigureAwait(false);
    }

    private PhotoPage Scan(SourceQuery query, List<string> folders, bool recursive, CancellationToken ct)
    {
        var candidates = Collect(folders, recursive, KeywordFor(query), ct);
        if (candidates.Count == 0)
            return PhotoPage.Message(query.Mode == QueryMode.Search
                ? $"No file or folder name in your local folders contains \"{query.Keyword}\"."
                : "No supported image files found in the selected folders.");

        string? notice = null;
        if (query.Mode == QueryMode.Top && CutoffFor(query.TimeRange) is { } since)
        {
            var recent = candidates.FindAll(candidate => candidate.LastWriteUtc >= since);

            // A local library is usually older than the window the UI defaults to, and an empty
            // grid would look like a broken source rather than a filter doing its job.
            if (recent.Count > 0) candidates = recent;
            else notice = $"Nothing in your folders changed in the last {query.TimeRange} — showing everything instead.";
        }

        return BuildPage(Order(candidates, query, folders), query, folders, notice, ct);
    }

    /// <summary>Walks every configured root once, keeping only supported image files.</summary>
    private static List<Candidate> Collect(List<string> folders, bool recursive, string? keyword, CancellationToken ct)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<Candidate>();

        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;

            // FileSystemEnumerable hands back the timestamp the directory walk already read, so
            // Top ordering costs no extra stat call per file. It enumerates lazily, exactly like
            // Directory.EnumerateFiles, and takes the same EnumerationOptions.
            var walk = new FileSystemEnumerable<Candidate>(
                folder,
                static (ref FileSystemEntry entry) => new Candidate(entry.ToFullPath(), entry.LastWriteTimeUtc),
                options)
            {
                ShouldIncludePredicate = static (ref FileSystemEntry entry) =>
                    !entry.IsDirectory && IsSupportedImage(entry.FileName),
            };

            try
            {
                foreach (var candidate in walk)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!seen.Add(candidate.Path)) continue;   // nested roots would yield the same file twice
                    if (keyword is not null && !MatchesKeyword(candidate.Path, folder, keyword)) continue;
                    candidates.Add(candidate);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A root that was unplugged or locked mid-walk must not lose the other roots.
            }
        }

        return candidates;
    }

    private static List<Candidate> Order(List<Candidate> candidates, SourceQuery query, List<string> folders)
    {
        // Path order is the stable baseline, so paging repeats regardless of what order the file
        // system handed the entries back in. The mode-specific sorts below are all stable too.
        candidates.Sort(static (a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));

        return query.Mode switch
        {
            QueryMode.Top => candidates.OrderByDescending(static c => c.LastWriteUtc).ToList(),
            QueryMode.Random => Shuffle(candidates, SeedFor(folders, candidates.Count)),
            _ => candidates,
        };
    }

    private PhotoPage BuildPage(
        List<Candidate> ordered, SourceQuery query, List<string> folders, string? notice, CancellationToken ct)
    {
        int pageSize = Math.Clamp(query.PageSize, 1, 200);
        int skip = (Math.Max(query.Page, 1) - 1) * pageSize;
        bool filtering = query.Orientation != PhotoOrientation.Any || query.MinWidth > 0 || query.MinHeight > 0;

        var items = new List<PhotoItem>(pageSize);
        bool hasMore = false;
        int matched = 0;

        foreach (var candidate in ordered)
        {
            ct.ThrowIfCancellationRequested();

            // Reading a header costs a small disk read, so pay it up front only when a filter
            // might reject the file; otherwise defer it to the files that actually make the page.
            var size = filtering ? ReadImageSize(candidate.Path) : default;
            if (filtering && !Matches(size, query)) continue;

            if (matched++ < skip) continue;
            if (items.Count == pageSize)
            {
                hasMore = true;
                break;
            }

            items.Add(ToPhotoItem(candidate, filtering ? size : ReadImageSize(candidate.Path), folders));
        }

        if (items.Count > 0) return new PhotoPage(items, hasMore, notice);

        return skip > 0
            ? PhotoPage.Empty   // paged past the end, which is not worth explaining to the user
            : PhotoPage.Message("No local images match the requested orientation or minimum size.");
    }

    private PhotoItem ToPhotoItem(in Candidate candidate, (int Width, int Height) size, List<string> folders)
    {
        string directory = Path.GetDirectoryName(candidate.Path) ?? string.Empty;

        return new PhotoItem
        {
            Id = StableId(candidate.Path),
            ProviderId = Id,
            ProviderName = DisplayName,

            // Thumbnail/preview/full URLs stay null: the app reads pixels straight off disk and
            // PhotoItem.BestSource already prefers LocalPath over every URL.
            LocalPath = candidate.Path,

            Width = size.Width,
            Height = size.Height,
            Title = Path.GetFileNameWithoutExtension(candidate.Path),
            SourcePageUrl = directory,
            License = "Local file",
            Tags = FolderTags(directory, folders),
        };
    }

    // ---------- options ----------

    private static List<string> ReadFolders(SourceSelection selection)
    {
        if (!selection.Options.TryGetValue(FoldersOption, out var raw) || string.IsNullOrWhiteSpace(raw))
            return [];

        var folders = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in raw.Split(FolderSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string full;
            try
            {
                // Normalising here is what lets FolderTags match a file back to its root.
                full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(part));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (seen.Add(full)) folders.Add(full);
        }

        return folders;
    }

    private static bool ReadRecursive(SourceSelection selection) =>
        !selection.Options.TryGetValue(RecursiveOption, out var raw)
        || !bool.TryParse(raw, out bool recursive)
        || recursive;

    private static string? KeywordFor(SourceQuery query)
    {
        if (query.Mode != QueryMode.Search) return null;
        string keyword = query.Keyword?.Trim() ?? string.Empty;
        return keyword.Length > 0 ? keyword : null;
    }

    private static DateTimeOffset? CutoffFor(string? timeRange) => timeRange?.Trim().ToLowerInvariant() switch
    {
        "day" => DateTimeOffset.UtcNow.AddDays(-1),
        "week" => DateTimeOffset.UtcNow.AddDays(-7),
        "month" => DateTimeOffset.UtcNow.AddMonths(-1),
        "year" => DateTimeOffset.UtcNow.AddYears(-1),
        _ => null,
    };

    // ---------- selection helpers ----------

    private static bool IsSupportedImage(ReadOnlySpan<char> fileName)
    {
        var extension = Path.GetExtension(fileName);
        return !extension.IsEmpty && SupportedExtensions.GetAlternateLookup<ReadOnlySpan<char>>().Contains(extension);
    }

    /// <summary>
    /// Matches the file name or any folder under the configured root. The root itself is excluded:
    /// its own path would otherwise match every file beneath it whenever the keyword happened to
    /// appear in it.
    /// </summary>
    private static bool MatchesKeyword(string path, string root, string keyword) =>
        path.Length > root.Length
        && path.AsSpan(root.Length).Contains(keyword, StringComparison.OrdinalIgnoreCase);

    private static bool Matches((int Width, int Height) size, SourceQuery query)
    {
        // Unknown dimensions pass: the format may simply have no codec installed, and the wallpaper
        // renderer re-checks resolution once it holds the real bitmap.
        if (size.Width <= 0 || size.Height <= 0) return true;
        if (size.Width < query.MinWidth || size.Height < query.MinHeight) return false;

        return query.Orientation == PhotoOrientation.Any
            || OrientationOf(size.Width, size.Height) == query.Orientation;
    }

    /// <summary>Mirrors <see cref="PhotoItem.Orientation"/> so filtering and the item agree.</summary>
    private static PhotoOrientation OrientationOf(int width, int height) =>
        width > height * 1.05 ? PhotoOrientation.Landscape
        : height > width * 1.05 ? PhotoOrientation.Portrait
        : PhotoOrientation.Square;

    /// <summary>
    /// One permutation of the whole list, sliced by page. Seeding per page instead would reshuffle
    /// the same files into every page and show duplicates as the user pages; this stays stable
    /// across restarts and reshuffles only when the folder set or file count changes.
    /// </summary>
    private static List<Candidate> Shuffle(List<Candidate> candidates, int seed)
    {
        var shuffled = new List<Candidate>(candidates);
        var random = new Random(seed);

        for (int i = shuffled.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        return shuffled;
    }

    private static int SeedFor(List<string> folders, int count)
    {
        // Hand-rolled because string.GetHashCode is randomised per process and the seed has to
        // survive a restart.
        unchecked
        {
            int seed = 17;
            foreach (var folder in folders)
                foreach (char c in folder)
                    seed = (seed * 31) + char.ToLowerInvariant(c);

            return (seed * 31) + count;
        }
    }

    /// <summary>Stable across runs and independent of how the path happened to be cased.</summary>
    private static string StableId(string fullPath)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToLowerInvariant()), digest);
        return Convert.ToHexStringLower(digest[..16]);
    }

    /// <summary>Folder names below the configured root make useful, free tags ("Trips", "2024").</summary>
    private static IReadOnlyList<string> FolderTags(string directory, List<string> folders)
    {
        var root = folders.Find(folder => directory.StartsWith(folder, StringComparison.OrdinalIgnoreCase));
        if (root is null || directory.Length <= root.Length) return [];

        return directory[root.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .TakeLast(4)   // the nearest folders describe the photo; the rest is just tree depth
            .ToArray();
    }

    // ---------- dimensions ----------

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static ReadOnlySpan<byte> JpegSignature => [0xFF, 0xD8, 0xFF];

    /// <summary>
    /// Reads pixel dimensions from the file header alone. A folder can hold thousands of 40MP
    /// files and this runs in the background while the user is doing something else, so nothing
    /// here decodes pixel data. Returns (0, 0) when the size cannot be established.
    /// </summary>
    private static (int Width, int Height) ReadImageSize(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096, FileOptions.SequentialScan);

            Span<byte> header = stackalloc byte[32];
            int read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);

            var size = ReadFromHeader(header[..read], stream);
            if (size.Width > 0 && size.Height > 0) return size;

            stream.Position = 0;
            return ReadWithImagingDecoder(stream);
        }
        catch
        {
            return default;   // locked, truncated or in a format Windows has no codec for
        }
    }

    private static (int Width, int Height) ReadFromHeader(ReadOnlySpan<byte> header, Stream stream)
    {
        if (header.Length >= 24 && header.StartsWith(PngSignature))
        {
            uint width = BinaryPrimitives.ReadUInt32BigEndian(header[16..]);
            uint height = BinaryPrimitives.ReadUInt32BigEndian(header[20..]);
            return width <= int.MaxValue && height <= int.MaxValue ? ((int)width, (int)height) : default;
        }

        if (header.Length >= 10 && (header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8)))
            return (BinaryPrimitives.ReadUInt16LittleEndian(header[6..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(header[8..]));

        if (header.Length >= 26 && header.StartsWith("BM"u8))
            return ReadBmpSize(header);

        if (header.Length >= 16 && header.StartsWith("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
            return ReadWebpSize(header);

        if (header.StartsWith(JpegSignature))
            return ReadJpegSize(stream);

        return default;   // TIFF, AVIF, HEIC and anything unusual fall through to the WIC decoder
    }

    private static (int Width, int Height) ReadBmpSize(ReadOnlySpan<byte> header)
    {
        // BITMAPCOREHEADER stores 16-bit dimensions; every later header uses signed 32-bit, where
        // a negative height only means the rows are stored top-down.
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[14..]) == 12)
            return (BinaryPrimitives.ReadInt16LittleEndian(header[18..]),
                    Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(header[20..])));

        int width = BinaryPrimitives.ReadInt32LittleEndian(header[18..]);
        int height = BinaryPrimitives.ReadInt32LittleEndian(header[22..]);
        return (width, height == int.MinValue ? 0 : Math.Abs(height));
    }

    private static (int Width, int Height) ReadWebpSize(ReadOnlySpan<byte> header)
    {
        var chunk = header[12..16];

        // Extended format: 24-bit canvas width/height minus one, after three bytes of flags.
        if (chunk.SequenceEqual("VP8X"u8) && header.Length >= 29)
            return ((int)ReadUInt24LittleEndian(header[23..]) + 1, (int)ReadUInt24LittleEndian(header[26..]) + 1);

        // Lossy: the VP8 key frame carries 14-bit dimensions after the 9D 01 2A start code.
        if (chunk.SequenceEqual("VP8 "u8) && header.Length >= 30
            && header[23] == 0x9D && header[24] == 0x01 && header[25] == 0x2A)
            return (BinaryPrimitives.ReadUInt16LittleEndian(header[26..]) & 0x3FFF,
                    BinaryPrimitives.ReadUInt16LittleEndian(header[28..]) & 0x3FFF);

        // Lossless: a 0x2F signature then 14-bit width-1 and height-1, least significant bit first.
        if (chunk.SequenceEqual("VP8L"u8) && header.Length >= 25 && header[20] == 0x2F)
        {
            uint bits = BinaryPrimitives.ReadUInt32LittleEndian(header[21..]);
            return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
        }

        return default;
    }

    private static uint ReadUInt24LittleEndian(ReadOnlySpan<byte> source) =>
        (uint)(source[0] | (source[1] << 8) | (source[2] << 16));

    /// <summary>Walks JPEG segments, seeking over each payload, until it reaches a frame header.</summary>
    private static (int Width, int Height) ReadJpegSize(Stream stream)
    {
        stream.Position = 2;   // past the SOI marker
        Span<byte> segment = stackalloc byte[5];

        while (true)
        {
            int marker = stream.ReadByte();
            if (marker < 0) return default;
            if (marker != 0xFF) continue;

            do
            {
                marker = stream.ReadByte();
            }
            while (marker == 0xFF);   // markers may be preceded by any number of 0xFF fill bytes

            if (marker < 0 || marker == 0xD9 || marker == 0xDA) return default;   // EOI, or entropy data starts
            if (marker == 0x00 || marker == 0x01 || marker is >= 0xD0 and <= 0xD8) continue;   // no payload

            if (stream.ReadAtLeast(segment[..2], 2, throwOnEndOfStream: false) < 2) return default;
            int length = BinaryPrimitives.ReadUInt16BigEndian(segment[..2]);
            if (length < 2) return default;

            // SOF0-SOF15 hold the frame size; DHT, JPG and DAC share that marker range.
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                if (stream.ReadAtLeast(segment, segment.Length, throwOnEndOfStream: false) < segment.Length)
                    return default;

                return (BinaryPrimitives.ReadUInt16BigEndian(segment[3..]),   // after precision and height
                        BinaryPrimitives.ReadUInt16BigEndian(segment[1..3]));
            }

            stream.Position += length - 2;
        }
    }

    /// <summary>
    /// Fallback for TIFF, AVIF, HEIC and unusual headers. WIC takes the frame size from decoder
    /// metadata, so no pixels are decoded, but <see cref="BitmapCacheOption.None"/> means the frame
    /// reads through the stream on demand — the size must be taken before the caller closes it.
    /// </summary>
    private static (int Width, int Height) ReadWithImagingDecoder(Stream stream)
    {
        var frame = BitmapFrame.Create(
            stream,
            BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.None);

        return (frame.PixelWidth, frame.PixelHeight);
    }

    private readonly record struct Candidate(string Path, DateTimeOffset LastWriteUtc);
}
