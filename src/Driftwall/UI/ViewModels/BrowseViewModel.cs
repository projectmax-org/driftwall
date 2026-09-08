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
    private int _page;
    private bool _hasLoadedOnce;
    private CancellationTokenSource? _fetchCts;

    public BrowseViewModel(AppServices services)
    {
        _services = services;

        AvailableSources = services.Sources.All.ToList();
        _selectedSource = AvailableSources.First();

        Collections = services.Collections.Collections;
        _activeCollection = Collections.FirstOrDefault();

        SearchCommand = new AsyncRelayCommand(() => ReloadAsync());
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
            _selectedCategory = value.Categories.FirstOrDefault();
            _page = 0;

            // A range the new source does not offer would leave the ComboBox blank.
            var ranges = value.SupportedTimeRanges;
            if (ranges.Count > 0 && !ranges.Contains(_timeRange, StringComparer.OrdinalIgnoreCase))
                _timeRange = ranges[Math.Min(1, ranges.Count - 1)];

            Raise(nameof(Mode), nameof(Categories), nameof(SelectedCategory), nameof(SupportsSearch),
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
            if (!Set(ref _selectedCategory, value)) return;
            if (Mode == QueryMode.Category) _ = ReloadAsync();
        }
    }

    public string TimeRange
    {
        get => _timeRange;
        set
        {
            if (!Set(ref _timeRange, value)) return;
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

    /// <summary>Loads the first page for the current source and mode, replacing what is on screen.</summary>
    public async Task ReloadAsync()
    {
        _page = 0;
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
            int width = monitors.Max(m => m.Width);
            int height = monitors.Max(m => m.Height);

            var context = _services.Sources.BuildContext(selection, settings, _services.Settings.GetApiKey, width, height);

            var problem = SelectedSource.Validate(context);
            if (problem is not null)
            {
                HasError = true;
                StatusMessage = problem;
                return;
            }

            _page++;
            var query = SourceRegistry.BuildQuery(selection, settings, _page, PageSize, width, height);
            var result = await SelectedSource.FetchAsync(query, context, cts.Token).ConfigureAwait(true);

            if (cts.IsCancellationRequested) return;

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
        catch (SourceException ex)
        {
            HasError = true;
            StatusMessage = ex.Message;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request.
        }
        catch (Exception ex)
        {
            Log.Error("Browse fetch failed.", ex);
            HasError = true;
            StatusMessage = "Could not reach " + SelectedSource.DisplayName + ".";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsEmpty));
            if (ReferenceEquals(_fetchCts, cts))
            {
                _fetchCts = null;
                cts.Dispose();
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

    private async Task SaveToAsync(PhotoTileViewModel tile, PhotoCollection? collection)
    {
        collection ??= Collections.FirstOrDefault() ?? _services.Collections.Create("Favourites");

        var store = _services.Collections;
        if (store.Contains(collection.Id, tile.Photo))
        {
            store.Remove(collection.Id, tile.Photo.Key);
            tile.RefreshSavedState();
            StatusMessage = $"Removed from {collection.Name}.";
            return;
        }

        store.Add(collection.Id, tile.Photo);
        tile.RefreshSavedState();
        StatusMessage = $"Saved to {collection.Name}.";

        // The permanent copy is fetched in the background so the heart responds immediately.
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var savedPath = await _services.Cache.SaveCopyAsync(tile.Photo, cts.Token).ConfigureAwait(true);
            if (savedPath is not null) store.SetSavedPath(collection.Id, tile.Photo.Key, savedPath);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not store a permanent copy of " + tile.Photo.Key, ex);
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

        _services.Settings.Update(settings =>
        {
            bool duplicate = settings.Sources.Any(s =>
                s.ProviderId == selection.ProviderId &&
                s.Mode == selection.Mode &&
                string.Equals(s.Keyword, selection.Keyword, StringComparison.OrdinalIgnoreCase) &&
                s.Category == selection.Category);

            if (!duplicate) settings.Sources.Add(selection);
        });

        _services.Rotation.ResetQueue();
        StatusMessage = $"Added to your wallpaper sources ({selection.DisplayLabel}).";
        HasError = false;
    }
}
