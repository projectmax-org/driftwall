using System.Windows.Media.Imaging;
using Driftwall.Core;

namespace Driftwall.UI.ViewModels;

/// <summary>
/// One photo in the browse or collection grid.
/// <para>
/// The thumbnail is loaded when the tile scrolls into view and released when it scrolls out, so the
/// memory held by the grid is proportional to what is on screen rather than to how far the user has
/// scrolled. The virtualising panel recycles the containers; this releases the pixels.
/// </para>
/// </summary>
public sealed class PhotoTileViewModel : ViewModelBase
{
    /// <summary>Thumbnails are decoded to this width; enough for a sharp tile, small in memory.</summary>
    private const int ThumbnailWidth = 360;

    private readonly ImageCache _cache;
    private readonly CollectionsStore _collections;

    private BitmapSource? _thumbnail;
    private bool _isLoading;
    private bool _failed;
    private bool _isSaved;
    private CancellationTokenSource? _loadCts;

    public PhotoTileViewModel(PhotoItem photo, ImageCache cache, CollectionsStore collections)
    {
        Photo = photo;
        _cache = cache;
        _collections = collections;
        _isSaved = collections.Contains(photo);
    }

    public PhotoItem Photo { get; }

    public string Title => string.IsNullOrWhiteSpace(Photo.Title) ? "Untitled" : Photo.Title!;

    public string Credit => Photo.AuthorName is { Length: > 0 } author
        ? $"{author} · {Photo.ProviderName}"
        : Photo.ProviderName;

    public string ResolutionLabel => Photo.Width > 0 && Photo.Height > 0
        ? $"{Photo.Width} × {Photo.Height}"
        : string.Empty;

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        private set => Set(ref _thumbnail, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => Set(ref _isLoading, value);
    }

    public bool Failed
    {
        get => _failed;
        private set => Set(ref _failed, value);
    }

    /// <summary>Drives the heart badge. Refreshed when collections change.</summary>
    public bool IsSaved
    {
        get => _isSaved;
        set => Set(ref _isSaved, value);
    }

    public void RefreshSavedState() => IsSaved = _collections.Contains(Photo);

    /// <summary>Called when the tile becomes visible.</summary>
    public async Task LoadAsync()
    {
        if (Thumbnail is not null || IsLoading) return;

        var cts = new CancellationTokenSource();
        _loadCts = cts;
        IsLoading = true;
        Failed = false;

        try
        {
            var path = await _cache.GetThumbnailAsync(Photo, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested) return;

            if (path is null)
            {
                Failed = true;
                return;
            }

            var image = await RenderThread.InvokeAsync(() => Decode(path), cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested) return;

            if (image is null) Failed = true;
            else Thumbnail = image;
        }
        catch (OperationCanceledException)
        {
            // Scrolled away before the download finished; nothing to report.
        }
        catch (Exception ex)
        {
            Log.Warn("Thumbnail load failed for " + Photo.Key, ex);
            Failed = true;
        }
        finally
        {
            IsLoading = false;
            if (ReferenceEquals(_loadCts, cts)) _loadCts = null;
            cts.Dispose();
        }
    }

    /// <summary>Called when the tile scrolls out of view.</summary>
    public void Release()
    {
        try { _loadCts?.Cancel(); }
        catch (ObjectDisposedException) { }

        // Dropping the reference is what frees the pixels; the bitmap is frozen and unshared.
        Thumbnail = null;
        IsLoading = false;
    }

    private static BitmapSource? Decode(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = ThumbnailWidth;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not decode thumbnail " + path, ex);
            return null;
        }
    }
}
