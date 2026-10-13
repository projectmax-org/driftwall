using System.Collections.ObjectModel;
using System.Windows.Input;
using Driftwall.Core;
using Driftwall.Sources;

namespace Driftwall.UI.ViewModels;

/// <summary>
/// The browse tab: pick a source, look at what it offers, keep what you like.
/// <para>
/// This is where a photo goes from "found" to "saved into a collection" to "used as a wallpaper
/// source", so it deliberately owns all three steps rather than just being a gallery.
/// </para>
/// </summary>
public sealed class BrowseViewModel : ViewModelBase
{
    private const int PageSize = 30;

    private readonly AppServices _services;

    private IPhotoSource _selectedSource;
    private QueryMode _mode = QueryMode.Top;
    private string _keyword = string.Empty;
    private SourceCategory? _selectedCategory;
    private string _timeRange = "month";
    private PhotoCollection? _activeCollection;

    private bool _isBusy;
    private string? _statusMessage;
    private bool _hasError;
    private bool _hasMore;
    private string? _loadMoreError;
    private int _page;
    private bool _hasLoadedOnce;
    private CancellationTokenSource? _fetchCts;

    public BrowseViewModel(AppServices services)
    {
        _services = services;

        AvailableSources = services.Sources.All.ToList();
        _selectedSource = AvailableSources.First();
        // The SelectedSource setter does this for every later source. Without it the first one
        // opens Categories on a blank picker and fetches its plain toplist instead.
        _selectedCategory = _selectedSource.Categories.FirstOrDefault();

        Collections = services.Collections.Collections;
        _activeCollection = Collections.FirstOrDefault();

        // Not an AsyncRelayCommand: that ignores a new search while the last one is still loading,
        // when the new one is meant to replace it. FetchAsync cancels whatever is in flight.
        SearchCommand = new RelayCommand(() => { _ = ReloadAsync(); });
        LoadMoreCommand = new AsyncRelayCommand(LoadMoreAsync, () => HasMore && !IsBusy);
        SetWallpaperCommand = new AsyncRelayCommand(SetWallpaperAsync);
        ToggleSaveCommand = new AsyncRelayCommand(ToggleSaveAsync);
        SaveToCollectionCommand = new AsyncRelayCommand(SaveToCollectionAsync);
        OpenSourcePageCommand = new RelayCommand(OpenSourcePage);
        AddAsWallpaperSourceCommand = new RelayCommand(AddAsWallpaperSource);
        ClearKeywordCommand = new RelayCommand(() => { Keyword = string.Empty; });

        services.Collections.CollectionsChanged += (_, _) =>
        {
            foreach (var tile in Photos) tile.RefreshSavedState();
        };
    }

    /// <summary>True when the currently selected source offers this mode.</summary>
    private bool ModeSupported(QueryMode mode) => mode switch
    {
        QueryMode.Top => _selectedSource.SupportsTop,
        QueryMode.Category => _selectedSource.SupportsCategories,
        QueryMode.Search => _selectedSource.SupportsSearch,
        // Every source can shuffle: worst case it falls back to its own default ordering.
        _ => true,
    };

    public ObservableCollection<PhotoTileViewModel> Photos { get; } = new();

    public IReadOnlyList<IPhotoSource> AvailableSources { get; }

    public ObservableCollection<PhotoCollection> Collections { get; }

    /// <summary>Windows the selected source actually honours, not a shared list.</summary>
    public IReadOnlyList<string> TimeRanges => SelectedSource.SupportedTimeRanges;

    public IPhotoSource SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (!Set(ref _selectedSource, value)) return;

            // Modes and categories differ per source, so reset to something the new source supports.
            if (!ModeSupported(_mode)) _mode = value.SupportsTop ? QueryMode.Top : QueryMode.Search;
            _selectedCategory = StartingCategory(value);
            _page = 0;

            // A range the new source does not offer would leave the ComboBox blank.
            var ranges = value.SupportedTimeRanges;
            if (ranges.Count > 0 && !ranges.Contains(_timeRange, StringComparer.OrdinalIgnoreCase))
                _timeRange = ranges[Math.Min(1, ranges.Count - 1)];

            Raise(nameof(Mode), nameof(IsSearchMode), nameof(IsCategoryMode),
                  nameof(Categories), nameof(SelectedCategory), nameof(SupportsSearch),
                  nameof(SupportsTop), nameof(SupportsCategories), nameof(SupportsTimeRange),
                  nameof(NeedsApiKey), nameof(ApiKeyPrompt), nameof(Terms), nameof(TermsWarning),
                  nameof(TimeRanges), nameof(TimeRange));

            _ = ReloadAsync();
        }
    }

    public QueryMode Mode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value)) return;
            Raise(nameof(IsSearchMode), nameof(IsCategoryMode));
            _page = 0;

            // A search with no keyword yet would just return an error, so wait for the user to type.
            if (value != QueryMode.Search || !string.IsNullOrWhiteSpace(_keyword)) _ = ReloadAsync();
        }
    }

    public string Keyword
    {
        get => _keyword;
        set => Set(ref _keyword, value);
    }

    public SourceCategory? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            // The picker pushes null when a source change swaps its list from under it. Nobody can
            // pick null, and SelectedSource has already chosen the new value, so keep that one.
            if (value is null || !Set(ref _selectedCategory, value)) return;
            if (Mode == QueryMode.Category) _ = ReloadAsync();
        }
    }

    public string TimeRange
    {
        get => _timeRange;
        set
        {
            // Null for the same reason as SelectedCategory.
            if (value is null || !Set(ref _timeRange, value)) return;
            _ = ReloadAsync();
        }
    }

    /// <summary>Collection the heart button adds to.</summary>
    public PhotoCollection? ActiveCollection
    {
        get => _activeCollection;
        set => Set(ref _activeCollection, value);
    }

    public IReadOnlyList<SourceCategory> Categories => SelectedSource.Categories;

    public bool SupportsSearch => SelectedSource.SupportsSearch;
    public bool SupportsTop => SelectedSource.SupportsTop;
    public bool SupportsCategories => SelectedSource.SupportsCategories;
    public bool SupportsTimeRange => SelectedSource.SupportedTimeRanges.Count > 0;

    public bool IsSearchMode => Mode == QueryMode.Search;
    public bool IsCategoryMode => Mode == QueryMode.Category;

    public bool NeedsApiKey =>
        SelectedSource.RequiresApiKey && string.IsNullOrWhiteSpace(_services.Settings.GetApiKey(SelectedSource.Id));

    public string ApiKeyPrompt => $"{SelectedSource.DisplayName} needs a free API key before it can show anything.";

    public ProviderTerms Terms => ProviderTermsTable.For(SelectedSource.Id);

    public string? TermsWarning => Terms.Level == TermsLevel.Restricted ? Terms.ActionRequired : null;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            // The empty state depends on this too, and must not show its hint while a page is on its way.
            OnPropertyChanged(nameof(IsEmpty));
            (LoadMoreCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    public bool HasError
    {
        get => _hasError;
        private set => Set(ref _hasError, value);
    }

    public bool HasMore
    {
        get => _hasMore;
        private set
        {
            if (!Set(ref _hasMore, value)) return;
            (LoadMoreCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Why the last page failed while earlier ones are on screen. A failed first page shows in the
    /// empty state, but that is hidden once the grid has photos, so a later failure needs its own place.
    /// </summary>
    public string? LoadMoreError
    {
        get => _loadMoreError;
        private set => Set(ref _loadMoreError, value);
    }

    public bool IsEmpty => Photos.Count == 0 && !IsBusy;

    public ICommand SearchCommand { get; }
    public ICommand LoadMoreCommand { get; }
    public ICommand SetWallpaperCommand { get; }
    public ICommand ToggleSaveCommand { get; }
    public ICommand SaveToCollectionCommand { get; }
    public ICommand OpenSourcePageCommand { get; }
    public ICommand AddAsWallpaperSourceCommand { get; }
    public ICommand ClearKeywordCommand { get; }

    /// <summary>
    /// Loads the default source the first time the tab is actually shown.
    /// <para>
    /// Deliberately driven by the view becoming visible rather than by the constructor: when the app
    /// starts hidden in the notification area, nobody is looking at the grid, and it should not spend
    /// a network request populating one.
    /// </para>
    /// </summary>
    public Task EnsureLoadedAsync()
    {
        if (_hasLoadedOnce) return Task.CompletedTask;
        _hasLoadedOnce = true;
        return ReloadAsync();
    }

    /// <summary>
    /// Called each time the tab comes into view. A key may have been added or removed under Settings
    /// in the meantime, and nothing else tells this tab, so the banner would go on asking for one.
    /// </summary>
    public void RefreshApiKeyState()
    {
        bool waitingOnKey = HasError && StatusMessage == ApiKeyPrompt;
        OnPropertyChanged(nameof(NeedsApiKey));
        if (_hasLoadedOnce && waitingOnKey && !NeedsApiKey) _ = ReloadAsync();
    }

    /// <summary>Loads the first page for the current source and mode, replacing what is on screen.</summary>
    public async Task ReloadAsync()
    {
        _page = 0;
        // Whether there is more belongs to the old query; Load more waits for the new first page.
        HasMore = false;
        Photos.Clear();
        OnPropertyChanged(nameof(IsEmpty));
        await FetchAsync(replace: true).ConfigureAwait(true);
    }

    private Task LoadMoreAsync() => FetchAsync(replace: false);

    private async Task FetchAsync(bool replace)
    {
        // A source change or a new search supersedes whatever is in flight.
        var previous = Interlocked.Exchange(ref _fetchCts, null);
        try { previous?.Cancel(); previous?.Dispose(); }
        catch (ObjectDisposedException) { }

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        _fetchCts = cts;

        IsBusy = true;
        HasError = false;
        StatusMessage = null;
        LoadMoreError = null;

        try
        {
            if (NeedsApiKey)
            {
                HasError = true;
                StatusMessage = ApiKeyPrompt;
                return;
            }

            var selection = BuildSelection();
            var settings = _services.Settings.Settings;
            var monitors = _services.Engine.ActiveMonitors(settings);

            // Sized and shaped the way rotation asks, so what Browse shows is what rotation would accept.
            var (width, height, orientation) = SourceRegistry.TargetFor(monitors, settings);

            var context = _services.Sources.BuildContext(selection, settings, _services.Settings.GetApiKey, width, height);

            var problem = SelectedSource.Validate(context);
            if (problem is not null)
            {
                HasError = true;
                StatusMessage = problem;
                return;
            }

            // The page number only moves on once that page has arrived, so a page that failed, timed
            // out or was superseded is asked for again next time rather than skipped.
            int nextPage = _page + 1;
            var query = SourceRegistry.BuildQuery(selection, settings, nextPage, PageSize, width, height, orientation,
                SourceRegistry.FloorFor(monitors, settings));
            var result = await SelectedSource.FetchAsync(query, context, cts.Token).ConfigureAwait(true);

            if (cts.IsCancellationRequested) return;
            _page = nextPage;

            if (replace) Photos.Clear();

            var existing = Photos.Select(p => p.Photo.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var photo in result.Items)
            {
                if (!existing.Add(photo.Key)) continue;
                Photos.Add(new PhotoTileViewModel(photo, _services.Cache, _services.Collections));
            }

            HasMore = result.HasMore;

            if (result.Notice is not null)
            {
                StatusMessage = result.Notice;
                HasError = result.Items.Count == 0;
            }
            else if (Photos.Count == 0)
            {
                StatusMessage = "Nothing found. Try a different keyword or source.";
            }
        }
        catch (Exception ex) when (!ReferenceEquals(_fetchCts, cts))
        {
            // Superseded by a newer request, which owns the status now; whatever this one ran into
            // as it was cancelled must not show up as that request's error.
            if (ex is not OperationCanceledException) Log.Warn("Superseded browse fetch failed.", ex);
        }
        catch (SourceException ex)
        {
            HasError = true;
            StatusMessage = ex.Message;
        }
        catch (OperationCanceledException)
        {
            // Nothing newer cancelled it, so this is the time limit above or the HTTP client's own.
            HasError = true;
            StatusMessage = $"{SelectedSource.DisplayName} did not respond in time. Try again.";
        }
        catch (Exception ex)
        {
            Log.Error("Browse fetch failed.", ex);
            HasError = true;
            StatusMessage = "Could not reach " + SelectedSource.DisplayName + ".";
        }
        finally
        {
            // A superseded fetch leaves the busy and empty state alone: the request that replaced it
            // is still loading and will set them when it finishes.
            if (ReferenceEquals(_fetchCts, cts))
            {
                _fetchCts = null;
                cts.Dispose();
                IsBusy = false;
                LoadMoreError = HasError && Photos.Count > 0 ? StatusMessage : null;
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    private SourceSelection BuildSelection() => new()
    {
        ProviderId = SelectedSource.Id,
        Mode = Mode,
        Keyword = Keyword,
        Category = SelectedCategory?.Id,
        TimeRange = TimeRange,
        Options = ExistingOptionsFor(SelectedSource.Id),
    };

    /// <summary>
    /// The category a source opens on. My collections reads its category in every mode, and the picker
    /// is hidden outside Categories, so it opens on the collection the rotation uses rather than on
    /// whichever comes first in the list, which may well be empty.
    /// </summary>
    private SourceCategory? StartingCategory(IPhotoSource source)
    {
        var categories = source.Categories;
        if (source is not CollectionSource) return categories.FirstOrDefault();

        var configured = _services.Settings.Settings.Sources
            .FirstOrDefault(s => string.Equals(s.ProviderId, source.Id, StringComparison.OrdinalIgnoreCase));
        var wanted = !string.IsNullOrWhiteSpace(configured?.Category)
            ? configured.Category
            : configured?.Options.GetValueOrDefault(CollectionSource.CollectionIdOption);
        var withPhotos = _services.Collections.FirstWithPhotos()?.Id;

        return categories.FirstOrDefault(c => c.Id == wanted)
            ?? categories.FirstOrDefault(c => c.Id == withPhotos)
            ?? categories.FirstOrDefault();
    }

    /// <summary>
    /// Reuses the options the user already configured for this source (folder paths, feed URLs), so
    /// browsing a local folder shows the same folder the rotation uses.
    /// </summary>
    private Dictionary<string, string> ExistingOptionsFor(string providerId)
    {
        var configured = _services.Settings.Settings.Sources
            .FirstOrDefault(s => string.Equals(s.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));

        return configured is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(configured.Options, StringComparer.OrdinalIgnoreCase);
    }

    // ---------------- per-photo actions ----------------

    private async Task SetWallpaperAsync(object? parameter)
    {
        if (parameter is not PhotoTileViewModel tile) return;

        StatusMessage = "Applying...";
        bool ok = await _services.Rotation.ApplySpecificAsync(tile.Photo).ConfigureAwait(true);
        StatusMessage = ok ? "Wallpaper set." : "Could not set that photo as the wallpaper.";
        HasError = !ok;
    }

    private Task ToggleSaveAsync(object? parameter) =>
        parameter is PhotoTileViewModel tile ? SaveToAsync(tile, ActiveCollection) : Task.CompletedTask;

    /// <summary>Used by the tile context menu, which passes the target collection explicitly.</summary>
    private Task SaveToCollectionAsync(object? parameter)
    {
        if (parameter is not object[] { Length: 2 } pair) return Task.CompletedTask;
        if (pair[0] is not PhotoTileViewModel tile || pair[1] is not PhotoCollection collection) return Task.CompletedTask;
        return SaveToAsync(tile, collection);
    }

    private Task SaveToAsync(PhotoTileViewModel tile, PhotoCollection? collection)
    {
        collection ??= Collections.FirstOrDefault() ?? _services.Collections.Create("Favourites");

        var store = _services.Collections;
        if (store.Contains(collection.Id, tile.Photo))
        {
            store.Remove(collection.Id, tile.Photo.Key);
            tile.RefreshSavedState();
            StatusMessage = $"Removed from {collection.Name}.";
            return Task.CompletedTask;
        }

        store.Add(collection.Id, tile.Photo);
        tile.RefreshSavedState();
        StatusMessage = $"Saved to {collection.Name}.";

        // The permanent copy is fetched in the background so the heart responds immediately. It is
        // deliberately not awaited: the command would count as running until the download finished
        // and drop every heart clicked in the meantime.
        _ = KeepPermanentCopyAsync(collection.Id, tile.Photo);
        return Task.CompletedTask;
    }

    private async Task KeepPermanentCopyAsync(string collectionId, PhotoItem photo)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var savedPath = await _services.Cache.SaveCopyAsync(photo, cts.Token).ConfigureAwait(true);

            // Does nothing if the photo was taken out of the collection while it downloaded.
            if (savedPath is not null) _services.Collections.SetSavedPath(collectionId, photo.Key, savedPath);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not store a permanent copy of " + photo.Key, ex);
        }
    }

    private void OpenSourcePage(object? parameter)
    {
        if (parameter is not PhotoTileViewModel tile) return;
        var url = tile.Photo.SourcePageUrl ?? tile.Photo.AuthorUrl;
        Shell.OpenUrl(url);
    }

    /// <summary>Turns what is on screen into a configured wallpaper source.</summary>
    private void AddAsWallpaperSource()
    {
        var selection = BuildSelection();
        selection.Enabled = true;
        selection.Weight = 2;

        // The range only tells two sources apart where the provider honours it. Elsewhere it is
        // whatever the previously selected source left behind.
        bool usesRange = SelectedSource.SupportedTimeRanges.Count > 0;
        bool added = false;

        _services.Settings.Update(settings =>
        {
            // No keyword (the defaults) and an empty one from this tab mean the same thing.
            bool duplicate = settings.Sources.Any(s =>
                s.ProviderId == selection.ProviderId &&
                s.Mode == selection.Mode &&
                string.Equals(s.Keyword ?? "", selection.Keyword ?? "", StringComparison.OrdinalIgnoreCase) &&
                // Web sources read their category only in Categories mode; My collections always does.
                (selection.Mode != QueryMode.Category && !string.Equals(selection.ProviderId, "collection", StringComparison.OrdinalIgnoreCase)
                    || s.Category == selection.Category) &&
                (!usesRange || string.Equals(s.TimeRange, selection.TimeRange, StringComparison.OrdinalIgnoreCase)));

            if (duplicate) return;
            settings.Sources.Add(selection);
            added = true;
        });

        HasError = false;
        if (!added)
        {
            StatusMessage = $"Already in your wallpaper sources ({selection.DisplayLabel}).";
            return;
        }

        _services.Rotation.ResetQueue();
        StatusMessage = $"Added to your wallpaper sources ({selection.DisplayLabel}).";
    }
}
