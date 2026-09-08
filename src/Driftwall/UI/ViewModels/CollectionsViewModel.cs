using System.Collections.ObjectModel;
using System.Windows.Input;
using Driftwall.Core;
using Driftwall.Sources;

namespace Driftwall.UI.ViewModels;

/// <summary>
/// The collections tab: the groups the user built while browsing, and the controls to use one as a
/// wallpaper source.
/// </summary>
public sealed class CollectionsViewModel : ViewModelBase
{
    private readonly AppServices _services;

    private PhotoCollection? _selected;
    private string _newCollectionName = string.Empty;
    private string? _statusMessage;
    private bool _isRenaming;
    private string _renameText = string.Empty;

    public CollectionsViewModel(AppServices services)
    {
        _services = services;
        _selected = Collections.FirstOrDefault();

        CreateCommand = new RelayCommand(Create, () => !string.IsNullOrWhiteSpace(NewCollectionName));
        DeleteCommand = new RelayCommand(Delete, () => _selected is not null);
        BeginRenameCommand = new RelayCommand(BeginRename, () => _selected is not null);
        CommitRenameCommand = new RelayCommand(CommitRename);
        CancelRenameCommand = new RelayCommand(() => IsRenaming = false);
        UseAsSourceCommand = new RelayCommand(UseAsSource, () => _selected is { Count: > 0 });
        SetWallpaperCommand = new AsyncRelayCommand(SetWallpaperAsync);
        RemoveCommand = new RelayCommand(RemovePhoto);
        OpenSourcePageCommand = new RelayCommand(OpenSourcePage);
        ShuffleNowCommand = new AsyncRelayCommand(ShuffleNowAsync, () => _selected is { Count: > 0 });

        services.Collections.CollectionsChanged += (_, _) => RefreshTiles();
        RefreshTiles();
    }

    public ObservableCollection<PhotoCollection> Collections => _services.Collections.Collections;

    /// <summary>Tiles for the selected collection. Rebuilt whenever the selection or contents change.</summary>
    public ObservableCollection<PhotoTileViewModel> Photos { get; } = new();

    public PhotoCollection? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            IsRenaming = false;
            RefreshTiles();
            RaiseCommandStates();
            Raise(nameof(HasSelection), nameof(SelectedSummary), nameof(IsEmpty));
        }
    }

    public bool HasSelection => _selected is not null;

    public bool IsEmpty => Photos.Count == 0;

    public string SelectedSummary => _selected is null
        ? string.Empty
        : _selected.Count == 1 ? "1 photo" : $"{_selected.Count} photos";

    public string NewCollectionName
    {
        get => _newCollectionName;
        set
        {
            if (!Set(ref _newCollectionName, value)) return;
            (CreateCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool IsRenaming
    {
        get => _isRenaming;
        private set => Set(ref _isRenaming, value);
    }

    public string RenameText
    {
        get => _renameText;
        set => Set(ref _renameText, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    public ICommand CreateCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand BeginRenameCommand { get; }
    public ICommand CommitRenameCommand { get; }
    public ICommand CancelRenameCommand { get; }
    public ICommand UseAsSourceCommand { get; }
    public ICommand SetWallpaperCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand OpenSourcePageCommand { get; }
    public ICommand ShuffleNowCommand { get; }

    private void RefreshTiles()
    {
        // Release the outgoing tiles' bitmaps rather than waiting for a collection.
        foreach (var tile in Photos) tile.Release();
        Photos.Clear();

        if (_selected is not null)
        {
            foreach (var photo in _services.Collections.PhotosIn(_selected.Id))
                Photos.Add(new PhotoTileViewModel(photo, _services.Cache, _services.Collections));
        }

        Raise(nameof(IsEmpty), nameof(SelectedSummary));
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        (DeleteCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (BeginRenameCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (UseAsSourceCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ShuffleNowCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private void Create()
    {
        var created = _services.Collections.Create(NewCollectionName);
        NewCollectionName = string.Empty;
        Selected = created;
        StatusMessage = $"Created \"{created.Name}\".";
    }

    private void Delete()
    {
        if (_selected is null) return;

        var name = _selected.Name;
        _services.Collections.Delete(_selected.Id);
        Selected = Collections.FirstOrDefault();
        StatusMessage = $"Deleted \"{name}\".";
    }

    private void BeginRename()
    {
        if (_selected is null) return;
        RenameText = _selected.Name;
        IsRenaming = true;
    }

    private void CommitRename()
    {
        if (_selected is not null && !string.IsNullOrWhiteSpace(RenameText))
        {
            _services.Collections.Rename(_selected.Id, RenameText);

            // The list binds to the collection objects themselves, so nudge it to redraw the name.
            int index = Collections.IndexOf(_selected);
            if (index >= 0)
            {
                var current = _selected;
                Collections.RemoveAt(index);
                Collections.Insert(index, current);
                Selected = current;
            }
        }

        IsRenaming = false;
    }

    /// <summary>Adds the selected collection to the rotation as a source.</summary>
    private void UseAsSource()
    {
        if (_selected is null) return;

        _services.Settings.Update(settings =>
        {
            bool exists = settings.Sources.Any(s =>
                s.ProviderId == "collection" &&
                s.Options.TryGetValue(CollectionSource.CollectionIdOption, out var id) &&
                id == _selected.Id);

            if (exists) return;

            settings.Sources.Add(new SourceSelection
            {
                ProviderId = "collection",
                Mode = QueryMode.Top,
                Category = _selected.Id,
                Weight = 3,
                Options = { [CollectionSource.CollectionIdOption] = _selected.Id },
            });
        });

        _services.Rotation.ResetQueue();
        StatusMessage = $"\"{_selected.Name}\" is now one of your wallpaper sources.";
    }

    private async Task ShuffleNowAsync()
    {
        if (_selected is null || _selected.Count == 0) return;

        var photos = _services.Collections.PhotosIn(_selected.Id);
        var pick = photos[Random.Shared.Next(photos.Count)];

        bool ok = await _services.Rotation.ApplySpecificAsync(pick).ConfigureAwait(true);
        StatusMessage = ok ? "Wallpaper set from " + _selected.Name + "." : "Could not apply that photo.";
    }

    private async Task SetWallpaperAsync(object? parameter)
    {
        if (parameter is not PhotoTileViewModel tile) return;

        bool ok = await _services.Rotation.ApplySpecificAsync(tile.Photo).ConfigureAwait(true);
        StatusMessage = ok ? "Wallpaper set." : "Could not set that photo as the wallpaper.";
    }

    private void RemovePhoto(object? parameter)
    {
        if (parameter is not PhotoTileViewModel tile || _selected is null) return;

        _services.Collections.Remove(_selected.Id, tile.Photo.Key);
        tile.Release();
        Photos.Remove(tile);
        Raise(nameof(IsEmpty), nameof(SelectedSummary));
        StatusMessage = "Removed from " + _selected.Name + ".";
    }

    private void OpenSourcePage(object? parameter)
    {
        if (parameter is PhotoTileViewModel tile) Shell.OpenUrl(tile.Photo.SourcePageUrl ?? tile.Photo.AuthorUrl);
    }
}
