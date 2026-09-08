using System.Collections.ObjectModel;
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
public sealed class PhotoCollection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    public string Name { get; set; } = "New collection";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public List<CollectionEntry> Entries { get; set; } = new();

    [JsonIgnore]
    public int Count => Entries.Count;
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

    private readonly Lock _gate = new();
    private readonly Timer _saveTimer;
    private readonly HashSet<string> _savedKeys = new(StringComparer.Ordinal);
    private bool _dirty;

    public CollectionsStore()
    {
        Collections = new ObservableCollection<PhotoCollection>(Load());
        RebuildKeyIndex();
        _saveTimer = new Timer(_ => FlushIfDirty(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Bound directly by the UI, so it is only ever mutated on the UI thread.</summary>
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

    public PhotoCollection? Find(string? id) =>
        id is null ? null : Collections.FirstOrDefault(c => c.Id == id);

    public PhotoCollection Create(string name)
    {
        var collection = new PhotoCollection { Name = string.IsNullOrWhiteSpace(name) ? "New collection" : name.Trim() };
        Collections.Add(collection);
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

        Collections.Remove(collection);
        RebuildKeyIndex();
        MarkDirty();
    }

    /// <summary>Adds a photo. Returns false when it was already there.</summary>
    public bool Add(string collectionId, PhotoItem photo, string? savedPath = null)
    {
        var collection = Find(collectionId);
        if (collection is null) return false;
        if (collection.Entries.Any(e => e.Key == photo.Key)) return false;

        collection.Entries.Insert(0, new CollectionEntry { Photo = photo, SavedPath = savedPath });
        lock (_gate) _savedKeys.Add(photo.Key);
        MarkDirty();
        return true;
    }

    public bool Remove(string collectionId, string photoKey)
    {
        var collection = Find(collectionId);
        var entry = collection?.Entries.FirstOrDefault(e => e.Key == photoKey);
        if (collection is null || entry is null) return false;

        collection.Entries.Remove(entry);
        RebuildKeyIndex();
        MarkDirty();
        return true;
    }

    /// <summary>Rebinds an entry to its permanent copy once the download finishes.</summary>
    public void SetSavedPath(string collectionId, string photoKey, string savedPath)
    {
        var entry = Find(collectionId)?.Entries.FirstOrDefault(e => e.Key == photoKey);
        if (entry is null) return;

        entry.SavedPath = savedPath;
        // The saved copy outlives the cache, so point the item at it for future wallpaper renders.
        entry.Photo = entry.Photo with { LocalPath = savedPath };
        MarkDirty();
    }

    /// <summary>Photos in a collection, ready to be handed to the rotation queue.</summary>
    public IReadOnlyList<PhotoItem> PhotosIn(string collectionId)
    {
        var collection = Find(collectionId);
        if (collection is null) return Array.Empty<PhotoItem>();

        return collection.Entries
            .Select(e => e.SavedPath is not null && File.Exists(e.SavedPath)
                ? e.Photo with { LocalPath = e.SavedPath }
                : e.Photo)
            .ToList();
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

    private static List<PhotoCollection> Load()
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
            Log.Error("Collections file could not be read; starting with an empty set.", ex);
            return DefaultCollections();
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

        Save();
    }

    public void Save()
    {
        List<PhotoCollection> snapshot;
        lock (_gate) snapshot = Collections.ToList();

        try
        {
            var json = JsonSerializer.Serialize(snapshot, DriftwallJson.Options);
            var temp = Paths.CollectionsFile + ".tmp";
            File.WriteAllText(temp, json);

            if (File.Exists(Paths.CollectionsFile)) File.Replace(temp, Paths.CollectionsFile, null);
            else File.Move(temp, Paths.CollectionsFile);
        }
        catch (Exception ex)
        {
            Log.Error("Could not save collections.", ex);
        }
    }

    public void Dispose()
    {
        FlushIfDirty();
        _saveTimer.Dispose();
    }
}
