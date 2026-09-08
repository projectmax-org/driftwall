using System.Text.Json.Serialization;

namespace Driftwall.Core;

public enum PhotoOrientation
{
    Any = 0,
    Landscape = 1,
    Portrait = 2,
    Square = 3,
}

/// <summary>How a source should pick photos.</summary>
public enum QueryMode
{
    /// <summary>Most popular / trending / editor-picked photos.</summary>
    Top = 0,
    /// <summary>Top photos limited to a provider category.</summary>
    Category = 1,
    /// <summary>Free-text keyword search.</summary>
    Search = 2,
    /// <summary>Shuffled / random selection.</summary>
    Random = 3,
}

/// <summary>A single photo discovered from a source. Immutable and cheap to hold in a list.</summary>
public sealed record PhotoItem
{
    /// <summary>Provider-local stable id (used with ProviderId to form <see cref="Key"/>).</summary>
    public required string Id { get; init; }

    /// <summary>Lower-case provider id, e.g. "unsplash". Must equal IPhotoSource.Id.</summary>
    public required string ProviderId { get; init; }

    /// <summary>Human-readable provider name shown in the UI, e.g. "Unsplash".</summary>
    public required string ProviderName { get; init; }

    /// <summary>Small image (roughly 400px wide) used for the browse grid. May equal PreviewUrl.</summary>
    public string? ThumbnailUrl { get; init; }

    /// <summary>Medium image (roughly 1080px wide) used for the detail pane.</summary>
    public string? PreviewUrl { get; init; }

    /// <summary>Highest practical quality URL, used when the photo becomes the wallpaper.</summary>
    public string? FullUrl { get; init; }

    /// <summary>Set for on-disk sources (local folder, collections cache). Takes priority over FullUrl.</summary>
    public string? LocalPath { get; init; }

    public int Width { get; init; }
    public int Height { get; init; }

    public string? Title { get; init; }
    public string? AuthorName { get; init; }
    public string? AuthorUrl { get; init; }

    /// <summary>Page a user can open to view the photo in context / check its licence.</summary>
    public string? SourcePageUrl { get; init; }

    public string? License { get; init; }

    /// <summary>
    /// Optional endpoint that must be pinged when the photo is actually used, to satisfy
    /// provider API terms (Unsplash requires this). Fired fire-and-forget, failures ignored.
    /// <para>
    /// Deliberately not serialised: providers authenticate this URL by embedding the API key in it,
    /// and persisting it would write that key in clear text into collections.json and state.json,
    /// undoing the DPAPI encryption the settings file uses. It only needs to live as long as the
    /// session that fetched the photo, which is the session that actually reports the download.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public string? DownloadTrackUrl { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>Globally unique key, "provider:id". Used for dedupe, favourites and history.</summary>
    [JsonIgnore]
    public string Key => ProviderId + ":" + Id;

    [JsonIgnore]
    public double AspectRatio => Height > 0 ? (double)Width / Height : 0d;

    [JsonIgnore]
    public PhotoOrientation Orientation =>
        Width <= 0 || Height <= 0 ? PhotoOrientation.Any
        : Width > Height * 1.05 ? PhotoOrientation.Landscape
        : Height > Width * 1.05 ? PhotoOrientation.Portrait
        : PhotoOrientation.Square;

    /// <summary>Best URL/path to fetch full-quality bytes from.</summary>
    [JsonIgnore]
    public string? BestSource => LocalPath ?? FullUrl ?? PreviewUrl ?? ThumbnailUrl;

    /// <summary>Best URL/path for a grid thumbnail.</summary>
    [JsonIgnore]
    public string? BestThumbnail => LocalPath ?? ThumbnailUrl ?? PreviewUrl ?? FullUrl;
}

/// <summary>A selectable category exposed by a provider (Unsplash topic, Pixabay category, ...).</summary>
public sealed record SourceCategory(string Id, string Name);

/// <summary>What the app asks a source for.</summary>
public sealed record SourceQuery
{
    public QueryMode Mode { get; init; } = QueryMode.Top;

    /// <summary>Free-text keyword. Set when <see cref="Mode"/> is <see cref="QueryMode.Search"/>.</summary>
    public string? Keyword { get; init; }

    /// <summary>Provider category id. Set when <see cref="Mode"/> is <see cref="QueryMode.Category"/>.</summary>
    public string? Category { get; init; }

    public PhotoOrientation Orientation { get; init; } = PhotoOrientation.Landscape;

    /// <summary>Minimum acceptable pixel size. 0 means "no constraint".</summary>
    public int MinWidth { get; init; }
    public int MinHeight { get; init; }

    public bool SafeSearch { get; init; } = true;

    /// <summary>1-based page number.</summary>
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 30;

    /// <summary>Time window for "top" style ordering: day | week | month | year | all.</summary>
    public string TimeRange { get; init; } = "month";
}

/// <summary>One page of results from a source.</summary>
public sealed record PhotoPage(IReadOnlyList<PhotoItem> Items, bool HasMore = false, string? Notice = null)
{
    public static readonly PhotoPage Empty = new(Array.Empty<PhotoItem>());

    public static PhotoPage Message(string notice) => new(Array.Empty<PhotoItem>(), false, notice);
}

/// <summary>Raised by a source when the caller did something recoverable (bad key, rate limit).</summary>
public sealed class SourceException : Exception
{
    public SourceException(string message, bool isRateLimit = false, bool isAuth = false, Exception? inner = null)
        : base(message, inner)
    {
        IsRateLimit = isRateLimit;
        IsAuth = isAuth;
    }

    public bool IsRateLimit { get; }
    public bool IsAuth { get; }
}
