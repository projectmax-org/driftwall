using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Driftwall.Core;

/// <summary>One photo the user chose to keep, plus where its permanent copy lives.</summary>
public sealed class CollectionEntry
{
    public required PhotoItem Photo { get; set; }

    /// <summary>Copy kept outside the eviction-managed cache, so favourites never disappear.</summary>
    public string? SavedPath { get; set; }

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore]
    public string Key => Photo.Key;
}

/// <summary>A named group of saved photos, selectable as a wallpaper source.</summary>
/// <remarks>
/// The UI binds to these objects directly, so a new name or photo count is announced rather than
/// left for the lists to find on their next redraw.
/// </remarks>
public sealed class PhotoCollection : INotifyPropertyChanged
{
    private string _name = "New collection";

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public List<CollectionEntry> Entries { get; set; } = new();

    [JsonIgnore]
    public int Count => Entries.Count;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Called by the store after it adds or removes an entry.</summary>
    internal void CountChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
}

/// <summary>
/// Stores the user's saved photo groups.
/// <para>
/// Kept separate from settings because it grows without bound and is written on a different rhythm:
/// settings change when the user opens a dialog, collections change when they click a heart.
/// </para>
/// </summary>
public sealed class CollectionsStore : IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private readonly Lock _gate = new();
    private readonly Lock _saveGate = new();
    private readonly Timer _saveTimer;
    private readonly HashSet<string> _savedKeys = new(StringComparer.Ordinal);
    private bool _dirty;
    private bool _disposed;

    /// <summary>Set when the file on disk could not be read or moved aside, so it must not be written over.</summary>
    private bool _saveBlocked;

    public CollectionsStore()
    {
        Collections = new ObservableCollection<PhotoCollection>(Load());
        RebuildKeyIndex();
        _saveTimer = new Timer(_ => FlushIfDirty(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Bound directly by the UI, so it is only ever mutated on the UI thread. The rotation reads it
    /// from pool threads, so the store changes it, and its entry lists, only under the lock.
    /// </summary>
    public ObservableCollection<PhotoCollection> Collections { get; }

    public event EventHandler? CollectionsChanged;

    /// <summary>True when the photo is in at least one collection. Drives the heart icon in the grid.</summary>
    public bool Contains(PhotoItem photo)
    {
        lock (_gate) return _savedKeys.Contains(photo.Key);
    }

    public bool Contains(string collectionId, PhotoItem photo)
    {
        var collection = Find(collectionId);
        return collection is not null && collection.Entries.Any(e => e.Key == photo.Key);
    }

    public PhotoCollection? Find(string? id)
    {
        if (id is null) return null;
        lock (_gate) return Collections.FirstOrDefault(c => c.Id == id);
    }

    /// <summary>The first collection with anything in it, for a source that names none.</summary>
    public PhotoCollection? FirstWithPhotos()
    {
        lock (_gate) return Collections.FirstOrDefault(c => c.Count > 0);
    }

    public PhotoCollection Create(string name)
    {
        var collection = new PhotoCollection { Name = string.IsNullOrWhiteSpace(name) ? "New collection" : name.Trim() };
        lock (_gate) Collections.Add(collection);
        MarkDirty();
        return collection;
    }

    public void Rename(string id, string name)
    {
        var collection = Find(id);
        if (collection is null || string.IsNullOrWhiteSpace(name)) return;
        collection.Name = name.Trim();
        MarkDirty();
    }

    public void Delete(string id)
    {
        var collection = Find(id);
        if (collection is null) return;

        lock (_gate) Collections.Remove(collection);
        RebuildKeyIndex();
        MarkDirty();
    }

    /// <summary>Adds a photo. Returns false when it was already there.</summary>
    public bool Add(string collectionId, PhotoItem photo, string? savedPath = null)
    {
        var collection = Find(collectionId);
        if (collection is null) return false;
        if (collection.Entries.Any(e => e.Key == photo.Key)) return false;

        lock (_gate)
        {
            collection.Entries.Insert(0, new CollectionEntry { Photo = photo, SavedPath = savedPath });
            _savedKeys.Add(photo.Key);
        }

        collection.CountChanged();
        MarkDirty();
        return true;
    }

    public bool Remove(string collectionId, string photoKey)
    {
        var collection = Find(collectionId);
        var entry = collection?.Entries.FirstOrDefault(e => e.Key == photoKey);
        if (collection is null || entry is null) return false;

        lock (_gate) collection.Entries.Remove(entry);
        collection.CountChanged();
        RebuildKeyIndex();
        MarkDirty();
        return true;
    }

    /// <summary>Rebinds an entry to its permanent copy once the download finishes.</summary>
    public void SetSavedPath(string collectionId, string photoKey, string savedPath)
    {
        var entry = Find(collectionId)?.Entries.FirstOrDefault(e => e.Key == photoKey);
        if (entry is null) return;

        // The photo itself keeps its own LocalPath (none for a web photo): PhotosIn points it at the
        // saved copy while that exists, and the original stays as the way back if it ever goes.
        entry.SavedPath = savedPath;
        MarkDirty();
    }

    /// <summary>Photos in a collection, ready to be handed to the rotation queue.</summary>
    public IReadOnlyList<PhotoItem> PhotosIn(string collectionId)
    {
        var collection = Find(collectionId);
        if (collection is null) return Array.Empty<PhotoItem>();

        // The rotation calls this from a pool thread while the user may be adding or removing
        // photos, so copy the list under the lock and do the slow file checks on the copy.
        CollectionEntry[] entries;
        lock (_gate) entries = collection.Entries.ToArray();

        return entries.Select(PhotoFor).ToList();
    }

    private static PhotoItem PhotoFor(CollectionEntry entry)
    {
        if (entry.SavedPath is null) return entry.Photo;
        if (File.Exists(entry.SavedPath)) return entry.Photo with { LocalPath = entry.SavedPath };

        // Earlier versions also wrote the saved path into the photo, which left it pointing at the
        // missing file for good. Without it, a web photo downloads again from its URL.
        return string.Equals(entry.Photo.LocalPath, entry.SavedPath, StringComparison.OrdinalIgnoreCase)
            ? entry.Photo with { LocalPath = null }
            : entry.Photo;
    }

    private void RebuildKeyIndex()
    {
        lock (_gate)
        {
            _savedKeys.Clear();
            foreach (var collection in Collections)
                foreach (var entry in collection.Entries)
                    _savedKeys.Add(entry.Key);
        }
    }

    private void MarkDirty()
    {
        lock (_gate)
        {
            _dirty = true;
            _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }

        CollectionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private List<PhotoCollection> Load()
    {
        try
        {
            if (!File.Exists(Paths.CollectionsFile)) return DefaultCollections();

            var json = File.ReadAllText(Paths.CollectionsFile);
            var loaded = JsonSerializer.Deserialize<List<PhotoCollection>>(json, DriftwallJson.Options);
            return loaded is { Count: > 0 } ? loaded : DefaultCollections();
        }
        catch (Exception ex)
        {
            // That file may be the only record of every collection the user built, so the empty set
            // below must never be saved over it. A file that would not parse is moved aside and kept.
            // One that could not be opened is most likely fine and only busy (a virus scanner, a
            // backup), so it is left where it is for the next start and nothing is saved until then.
            bool couldNotOpen = ex is IOException or UnauthorizedAccessException;
            if (!couldNotOpen && TryBackupUnreadableFile())
            {
                Log.Error("Collections file could not be read; it has been backed up and a new set started.", ex);
            }
            else
            {
                _saveBlocked = File.Exists(Paths.CollectionsFile);
                Log.Error("Collections file could not be read; it has been left as it was and collections will not be saved until the next start.", ex);
            }

            return DefaultCollections();
        }
    }

    private static bool TryBackupUnreadableFile()
    {
        try
        {
            if (!File.Exists(Paths.CollectionsFile)) return true;

            // Unlike settings, an earlier backup is kept: it may be the only copy of the user's collections.
            var backup = Paths.CollectionsFile + ".corrupt";
            if (File.Exists(backup)) backup = Paths.CollectionsFile + $".{DateTime.Now:yyyyMMdd-HHmmss}.corrupt";
            File.Move(Paths.CollectionsFile, backup);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not back up the unreadable collections file.", ex);
            return false;
        }
    }

    private static List<PhotoCollection> DefaultCollections() =>
        [new PhotoCollection { Id = "favourites", Name = "Favourites" }];

    private void FlushIfDirty()
    {
        lock (_gate)
        {
            if (!_dirty) return;
            _dirty = false;
        }

        if (!TrySave()) RetryLater();
    }

    /// <summary>
    /// A save that failed (the file held open by a virus scanner, say) is tried again, rather than
    /// waiting for the next change or the save on exit.
    /// </summary>
    private void RetryLater()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _dirty = true;
            _saveTimer.Change(RetryDelay, Timeout.InfiniteTimeSpan);
        }
    }

    public void Save() => TrySave();

    /// <summary>Returns false only when the write failed and is worth trying again.</summary>
    private bool TrySave()
    {
        if (_saveBlocked)
        {
            Log.Warn("Collections not saved: the file on disk could not be read at start-up, so it is left as it was.");
            return true;
        }

        // The timer and a save on exit or sign-out can arrive together; one at a time, so they do not
        // fight over the temporary file and the later one writes the later snapshot.
        lock (_saveGate)
        {
            // Copied under the lock, entries included, so a heart clicked mid-save cannot change a
            // list while the serialiser walks it.
            List<PhotoCollection> snapshot;
            lock (_gate)
            {
                snapshot = Collections
                    .Select(c => new PhotoCollection { Id = c.Id, Name = c.Name, CreatedAt = c.CreatedAt, Entries = c.Entries.ToList() })
                    .ToList();
            }

            try
            {
                var json = JsonSerializer.Serialize(snapshot, DriftwallJson.Options);
                var temp = Paths.CollectionsFile + ".tmp";
                DriftwallJson.WriteDurably(temp, json);

                if (File.Exists(Paths.CollectionsFile)) File.Replace(temp, Paths.CollectionsFile, null);
                else File.Move(temp, Paths.CollectionsFile);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Could not save collections.", ex);
                return false;
            }
        }
    }

    public void Dispose()
    {
        FlushIfDirty();
        lock (_gate) _disposed = true;
        _saveTimer.Dispose();
    }
}
