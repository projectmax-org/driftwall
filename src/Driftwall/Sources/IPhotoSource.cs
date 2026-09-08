using Driftwall.Core;

namespace Driftwall.Sources;

/// <summary>Everything a source needs to do its work, without reaching into app globals.</summary>
public sealed class SourceContext
{
    public required HttpClient Http { get; init; }

    /// <summary>Returns the user-entered API key for a provider id, or null/empty if unset.</summary>
    public required Func<string, string?> GetApiKey { get; init; }

    /// <summary>The selection the user configured for this source (options, folder paths, ...).</summary>
    public required SourceSelection Selection { get; init; }

    public required AppSettings Settings { get; init; }

    /// <summary>Widest monitor in pixels — sources use it to request an appropriately sized image.</summary>
    public int TargetWidth { get; init; } = 1920;

    public int TargetHeight { get; init; } = 1080;

    public string? ApiKey => GetApiKey(SelectionProviderId);

    private string SelectionProviderId => Selection.ProviderId;
}

/// <summary>
/// A place photos come from. Implementations must be stateless and thread-safe:
/// one instance is shared by the whole app.
/// </summary>
public interface IPhotoSource
{
    /// <summary>Stable lower-case id used in settings and <see cref="PhotoItem.ProviderId"/>.</summary>
    string Id { get; }

    /// <summary>Name shown in the UI.</summary>
    string DisplayName { get; }

    /// <summary>One-line description shown under the name in the source picker.</summary>
    string Description { get; }

    /// <summary>True when the user must paste an API key before this source works.</summary>
    bool RequiresApiKey { get; }

    /// <summary>Page where a user can obtain a free API key.</summary>
    string? ApiKeyHelpUrl { get; }

    /// <summary>Page describing the licence terms of the photos.</summary>
    string? LicenseUrl { get; }

    bool SupportsTop { get; }
    bool SupportsSearch { get; }
    bool SupportsCategories { get; }

    /// <summary>Time ranges this source honours for Top mode; empty when it has no concept of one.</summary>
    IReadOnlyList<string> SupportedTimeRanges { get; }

    /// <summary>Categories offered in Category mode. Empty when <see cref="SupportsCategories"/> is false.</summary>
    IReadOnlyList<SourceCategory> Categories { get; }

    /// <summary>Human-readable reason the source cannot run yet (missing key/folder), or null when ready.</summary>
    string? Validate(SourceContext context);

    /// <summary>
    /// Fetch one page. Must honour <paramref name="ct"/>, must not throw for empty results
    /// (return <see cref="PhotoPage.Empty"/>), and should throw <see cref="SourceException"/>
    /// for auth/rate-limit problems so the UI can explain them.
    /// </summary>
    Task<PhotoPage> FetchAsync(SourceQuery query, SourceContext context, CancellationToken ct);
}

/// <summary>Convenience base with sensible defaults so implementations stay short.</summary>
public abstract class PhotoSourceBase : IPhotoSource
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string Description { get; }

    public virtual bool RequiresApiKey => false;
    public virtual string? ApiKeyHelpUrl => null;
    public virtual string? LicenseUrl => null;

    public virtual bool SupportsTop => true;
    public virtual bool SupportsSearch => true;
    public virtual bool SupportsCategories => Categories.Count > 0;

    public virtual IReadOnlyList<string> SupportedTimeRanges => Array.Empty<string>();
    public virtual IReadOnlyList<SourceCategory> Categories => Array.Empty<SourceCategory>();

    public virtual string? Validate(SourceContext context)
    {
        if (RequiresApiKey && string.IsNullOrWhiteSpace(context.GetApiKey(Id)))
            return $"{DisplayName} needs a free API key. Add one in Settings → API keys.";
        return null;
    }

    public abstract Task<PhotoPage> FetchAsync(SourceQuery query, SourceContext context, CancellationToken ct);
}
