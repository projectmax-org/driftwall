using Driftwall.Sources;
using Driftwall.Wallpaper;

namespace Driftwall.Core;

public sealed record RotationStatus(string Message, bool IsError = false);

/// <summary>
/// Decides which photo goes on which monitor, and when.
/// <para>
/// Holds a small look-ahead queue filled from every enabled source in weighted rotation. The queue
/// exists so that pressing "next" is instant: by the time the user asks, the next photo is usually
/// already on disk. Refilling happens on a background thread and is cancelled the moment settings
/// change, so a slow provider can never block a wallpaper change.
/// </para>
/// </summary>
public sealed class RotationService : IDisposable
{
    private const int QueueTarget = 24;
    private const int QueueRefillThreshold = 8;
    private const int FetchPageSize = 30;

    /// <summary>How many queued photos one change may burn through before giving up.</summary>
    private const int MaxApplyAttempts = 3;

    private readonly SettingsStore _settingsStore;
    private readonly SourceRegistry _registry;
    private readonly ImageCache _cache;
    private readonly WallpaperEngine _engine;
    private readonly AppState _state;

    private readonly Lock _queueGate = new();
    private readonly List<PhotoItem> _queue = new();
    private readonly List<PhotoItem> _history = new();
    private readonly HashSet<string> _recentKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _pageBySelection = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _refillLock = new(1, 1);

    private int _historyPosition = -1;
    private CancellationTokenSource? _refillCts;

    public RotationService(
        SettingsStore settingsStore, SourceRegistry registry, ImageCache cache, WallpaperEngine engine)
    {
        _settingsStore = settingsStore;
        _registry = registry;
        _cache = cache;
        _engine = engine;
        _state = AppState.Load();

        foreach (var key in _state.RecentKeys) _recentKeys.Add(key);
        _history.AddRange(_state.CurrentPhotos);
        _historyPosition = _history.Count - 1;
    }

    private AppSettings Settings => _settingsStore.Settings;

    public event EventHandler<RotationStatus>? StatusChanged;

    /// <summary>Photos currently on the desktop, primary monitor first.</summary>
    public IReadOnlyList<PhotoItem> CurrentPhotos =>
        _engine.Current?.Assignments.Select(a => a.Photo).ToList() ?? _state.CurrentPhotos;

    public DateTimeOffset? LastChangeUtc => _state.LastChangeUtc;

    public int QueueLength { get { lock (_queueGate) return _queue.Count; } }

    /// <summary>Drops the queue so the next change uses freshly configured sources.</summary>
    public void ResetQueue()
    {
        lock (_queueGate)
        {
            _queue.Clear();
            _pageBySelection.Clear();
        }

        CancelRefill();
    }

    // ---------------- the four user-facing actions ----------------

    /// <summary>Moves to the next photo. This is what the timer, the tray menu and the hotkey call.</summary>
    public async Task<bool> NextAsync(CancellationToken ct = default)
    {
        // Walking forward through history first makes "previous then next" behave the way people expect.
        if (_historyPosition >= 0 && _historyPosition < _history.Count - 1)
        {
            _historyPosition++;
            return await ApplyFromHistoryAsync(ct).ConfigureAwait(false);
        }

        var monitors = _engine.ActiveMonitors(Settings);
        var needed = Settings.MultiMonitor == MultiMonitorMode.DifferentPerMonitor ? monitors.Count : 1;

        // A photo that will not download (a provider throttling, a dead link) costs one attempt, not
        // the whole change. Otherwise the timer leaves the old wallpaper up for another interval over
        // one bad URL, and "Next" appears to do nothing.
        for (int attempt = 1; attempt <= MaxApplyAttempts; attempt++)
        {
            var picks = await TakeAsync(needed, monitors, ct).ConfigureAwait(false);
            if (picks.Count == 0)
            {
                Report("No photos available. Check your sources in Settings.", isError: true);
                return false;
            }

            RecordHistory(picks);
            if (await ApplyAsync(picks, monitors, ct, reportFailure: attempt == MaxApplyAttempts).ConfigureAwait(false))
            {
                _ = PrefetchAsync();
                return true;
            }

            if (ct.IsCancellationRequested) return false;

            ForgetHistory(picks);
            Log.Info($"Could not use {string.Join(", ", picks.Select(p => p.Key))}; trying the next photo ({attempt}/{MaxApplyAttempts}).");
        }

        return false;
    }

    /// <summary>Goes back to the previously shown photo.</summary>
    public async Task<bool> PreviousAsync(CancellationToken ct = default)
    {
        if (_historyPosition <= 0)
        {
            Report("Nothing earlier to go back to.");
            return false;
        }

        _historyPosition--;
        return await ApplyFromHistoryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-renders and re-applies what is already on screen. Useful after changing the scaling mode,
    /// plugging in a monitor, or when something else has overwritten the desktop.
    /// </summary>
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        var current = CurrentPhotos;
        if (current.Count == 0) return await NextAsync(ct).ConfigureAwait(false);

        var monitors = _engine.ActiveMonitors(Settings);
        Report("Refreshing wallpaper...");
        return await ApplyAsync(current, monitors, ct).ConfigureAwait(false);
    }

    /// <summary>Applies one specific photo to every monitor. Used by "Set as wallpaper" in the browser.</summary>
    public async Task<bool> ApplySpecificAsync(PhotoItem photo, CancellationToken ct = default)
    {
        var monitors = _engine.ActiveMonitors(Settings);
        var picks = new List<PhotoItem> { photo };
        RecordHistory(picks);
        return await ApplyAsync(picks, monitors, ct).ConfigureAwait(false);
    }

    /// <summary>Applies one specific photo to a single monitor, leaving the others alone.</summary>
    public async Task<bool> ApplyToMonitorAsync(PhotoItem photo, MonitorInfo monitor, CancellationToken ct = default)
    {
        var existing = _engine.Current?.Assignments.ToDictionary(a => a.Monitor.Id, a => a.Photo, StringComparer.OrdinalIgnoreCase)
                       ?? new Dictionary<string, PhotoItem>(StringComparer.OrdinalIgnoreCase);

        var application = await _engine.ApplyAsync(
            m => string.Equals(m.Id, monitor.Id, StringComparison.OrdinalIgnoreCase)
                ? photo
                : existing.GetValueOrDefault(m.Id),
            Settings, ct).ConfigureAwait(false);

        if (application is null) return false;

        PersistState(application.Assignments.Select(a => a.Photo).ToList());
        Report($"Applied to {monitor.Name}.");
        return true;
    }

    // ---------------- applying ----------------

    private async Task<bool> ApplyFromHistoryAsync(CancellationToken ct)
    {
        var photo = _history.ElementAtOrDefault(_historyPosition);
        if (photo is null) return false;

        var monitors = _engine.ActiveMonitors(Settings);
        return await ApplyAsync(new List<PhotoItem> { photo }, monitors, ct).ConfigureAwait(false);
    }

    private async Task<bool> ApplyAsync(
        IReadOnlyList<PhotoItem> picks, IReadOnlyList<MonitorInfo> monitors, CancellationToken ct, bool reportFailure = true)
    {
        if (picks.Count == 0) return false;

        try
        {
            var assignment = BuildAssignment(picks, monitors);
            var application = await _engine.ApplyAsync(m => assignment.GetValueOrDefault(m.Id), Settings, ct)
                .ConfigureAwait(false);

            if (application is null)
            {
                if (reportFailure) Report("Could not apply the wallpaper. See the log for details.", isError: true);
                return false;
            }

            PersistState(application.Assignments.Select(a => a.Photo).ToList());

            var primary = application.PrimaryPhoto;
            Report(primary is null
                ? "Wallpaper updated."
                : $"{primary.Title ?? "Untitled"} - {primary.AuthorName ?? primary.ProviderName}");

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("Applying the wallpaper failed.", ex);
            Report("Something went wrong applying the wallpaper.", isError: true);
            return false;
        }
    }

    /// <summary>
    /// Maps photos onto monitors. With one photo it goes everywhere; with several, each monitor gets
    /// the best orientation match still available so a portrait display is not handed a landscape shot.
    /// </summary>
    private static Dictionary<string, PhotoItem> BuildAssignment(IReadOnlyList<PhotoItem> picks, IReadOnlyList<MonitorInfo> monitors)
    {
        var result = new Dictionary<string, PhotoItem>(StringComparer.OrdinalIgnoreCase);
        if (monitors.Count == 0) return result;

        if (picks.Count == 1)
        {
            foreach (var monitor in monitors) result[monitor.Id] = picks[0];
            return result;
        }

        var remaining = picks.ToList();
        foreach (var monitor in monitors)
        {
            if (remaining.Count == 0)
            {
                result[monitor.Id] = picks[^1];
                continue;
            }

            var wanted = monitor.AspectRatio >= 1 ? PhotoOrientation.Landscape : PhotoOrientation.Portrait;
            int index = remaining.FindIndex(p => p.Orientation == wanted);
            if (index < 0) index = 0;

            result[monitor.Id] = remaining[index];
            remaining.RemoveAt(index);
        }

        return result;
    }

    private void PersistState(List<PhotoItem> photos)
    {
        _state.CurrentPhotos = photos;
        _state.LastChangeUtc = DateTimeOffset.UtcNow;
        _state.RecentKeys = _recentKeys.TakeLast(Math.Max(50, Settings.NoRepeatWindow)).ToList();
        _state.Save();
    }

    // ---------------- the queue ----------------

    /// <summary>Takes photos from the queue, refilling first when it is running low.</summary>
    private async Task<List<PhotoItem>> TakeAsync(int count, IReadOnlyList<MonitorInfo> monitors, CancellationToken ct)
    {
        if (QueueLength < Math.Max(count, QueueRefillThreshold))
            await RefillAsync(ct).ConfigureAwait(false);

        var picks = new List<PhotoItem>(count);
        lock (_queueGate)
        {
            for (int i = 0; i < count && _queue.Count > 0; i++)
            {
                int index = 0;
                if (i < monitors.Count)
                {
                    // Prefer a photo that suits this monitor's shape, but never search far enough to
                    // starve the queue.
                    var wanted = monitors[i].AspectRatio >= 1 ? PhotoOrientation.Landscape : PhotoOrientation.Portrait;
                    int match = _queue.Take(12).ToList().FindIndex(p => p.Orientation == wanted);
                    if (match >= 0) index = match;
                }

                picks.Add(_queue[index]);
                _queue.RemoveAt(index);
            }
        }

        // Kick off a background top-up so the next change does not have to wait.
        if (QueueLength < QueueRefillThreshold) _ = RefillInBackgroundAsync();

        return picks;
    }

    private Task RefillInBackgroundAsync()
    {
        CancelRefill();
        var cts = new CancellationTokenSource();
        _refillCts = cts;

        return Task.Run(async () =>
        {
            try { await RefillAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Warn("Background queue refill failed.", ex); }
            finally { cts.Dispose(); }
        }, CancellationToken.None);
    }

    private void CancelRefill()
    {
        var cts = Interlocked.Exchange(ref _refillCts, null);
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Fetches from every enabled source and merges the results, interleaved by weight so a source
    /// with weight 3 contributes roughly three times as often as one with weight 1.
    /// </summary>
    private async Task RefillAsync(CancellationToken ct)
    {
        if (!await _refillLock.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false)) return;

        try
        {
            if (QueueLength >= QueueTarget) return;

            var settings = Settings;
            var selections = _registry.Resolve(settings).ToList();
            if (selections.Count == 0)
            {
                Report("No sources are enabled. Add one in Settings.", isError: true);
                return;
            }

            var monitors = _engine.ActiveMonitors(settings);
            int targetWidth = monitors.Max(m => m.Width);
            int targetHeight = monitors.Max(m => m.Height);

            var buckets = new List<(int Weight, List<PhotoItem> Items)>();
            var problems = new List<string>();

            // Sources are queried in parallel: one slow provider should not delay the others, and
            // they are all independent network calls.
            var fetches = selections.Select(async pair =>
            {
                var (source, selection) = pair;

                var problem = source.Validate(_registry.BuildContext(selection, settings, _settingsStore.GetApiKey, targetWidth, targetHeight));
                if (problem is not null) return (selection, Items: (List<PhotoItem>?)null, Problem: problem);

                int page = NextPageFor(selection);
                var query = SourceRegistry.BuildQuery(selection, settings, page, FetchPageSize, targetWidth, targetHeight);
                var context = _registry.BuildContext(selection, settings, _settingsStore.GetApiKey, targetWidth, targetHeight);

                try
                {
                    var result = await source.FetchAsync(query, context, ct).ConfigureAwait(false);

                    // A source that has run out of pages starts again from the beginning.
                    if (!result.HasMore || result.Items.Count == 0) ResetPageFor(selection);

                    return (selection, Items: result.Items.ToList(), Problem: result.Notice);
                }
                catch (SourceException ex)
                {
                    return (selection, Items: (List<PhotoItem>?)null, Problem: ex.Message);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Source {source.DisplayName} failed.", ex);
                    return (selection, Items: (List<PhotoItem>?)null, Problem: $"{source.DisplayName} is not responding.");
                }
            });

            var results = await Task.WhenAll(fetches).ConfigureAwait(false);

            foreach (var (selection, items, problem) in results)
            {
                if (problem is not null) problems.Add(problem);
                if (items is { Count: > 0 }) buckets.Add((selection.Weight, items));
            }

            var merged = Interleave(buckets);
            int added = Enqueue(merged, settings);

            if (added == 0 && problems.Count > 0)
                Report(problems[0], isError: true);
            else if (added > 0)
                Log.Info($"Queue topped up with {added} photos ({QueueLength} waiting).");
        }
        finally
        {
            _refillLock.Release();
        }
    }

    /// <summary>Round-robins the buckets, taking <c>Weight</c> items from each per pass.</summary>
    private static List<PhotoItem> Interleave(List<(int Weight, List<PhotoItem> Items)> buckets)
    {
        var merged = new List<PhotoItem>();
        var cursors = new int[buckets.Count];
        bool progressed = true;

        while (progressed)
        {
            progressed = false;
            for (int b = 0; b < buckets.Count; b++)
            {
                var (weight, items) = buckets[b];
                for (int n = 0; n < weight && cursors[b] < items.Count; n++)
                {
                    merged.Add(items[cursors[b]++]);
                    progressed = true;
                }
            }
        }

        return merged;
    }

    /// <summary>Adds photos to the queue, skipping duplicates and anything shown too recently.</summary>
    private int Enqueue(List<PhotoItem> photos, AppSettings settings)
    {
        int added = 0;
        lock (_queueGate)
        {
            var queued = _queue.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

            foreach (var photo in photos)
            {
                if (!queued.Add(photo.Key)) continue;
                if (settings.NoRepeatWindow > 0 && _recentKeys.Contains(photo.Key)) continue;

                _queue.Add(photo);
                added++;
            }

            if (settings.Shuffle && added > 0)
            {
                // Shuffle only the newly added tail so the ordering the user is already partway
                // through is left alone.
                int start = _queue.Count - added;
                var random = Random.Shared;
                for (int i = _queue.Count - 1; i > start; i--)
                {
                    int j = random.Next(start, i + 1);
                    (_queue[i], _queue[j]) = (_queue[j], _queue[i]);
                }
            }
        }

        return added;
    }

    private int NextPageFor(SourceSelection selection)
    {
        var key = SelectionKey(selection);
        lock (_queueGate)
        {
            int page = _pageBySelection.GetValueOrDefault(key, 0) + 1;
            _pageBySelection[key] = page;
            return page;
        }
    }

    private void ResetPageFor(SourceSelection selection)
    {
        lock (_queueGate) _pageBySelection[SelectionKey(selection)] = 0;
    }

    private static string SelectionKey(SourceSelection selection) =>
        $"{selection.ProviderId}|{selection.Mode}|{selection.Keyword}|{selection.Category}|{selection.TimeRange}";

    private void RecordHistory(IReadOnlyList<PhotoItem> picks)
    {
        foreach (var photo in picks)
        {
            _history.Add(photo);
            _recentKeys.Add(photo.Key);
        }

        int window = Math.Max(50, Settings.NoRepeatWindow);
        if (_history.Count > window) _history.RemoveRange(0, _history.Count - window);
        if (_recentKeys.Count > window * 2)
        {
            // The set only guards against repeats, so trimming it to the live history is enough.
            _recentKeys.Clear();
            foreach (var photo in _history) _recentKeys.Add(photo.Key);
        }

        _historyPosition = _history.Count - 1;
    }

    /// <summary>
    /// Undoes <see cref="RecordHistory"/> for photos that never reached the desktop, so "Previous"
    /// does not lead back to a photo nobody saw. Their keys stay in the recent set on purpose: a
    /// link that just failed should not be the very next thing tried.
    /// </summary>
    private void ForgetHistory(IReadOnlyList<PhotoItem> picks)
    {
        int start = Math.Max(0, _history.Count - picks.Count);
        _history.RemoveRange(start, _history.Count - start);
        _historyPosition = _history.Count - 1;
    }

    // ---------------- prefetch ----------------

    /// <summary>
    /// Downloads the next few photos so switching is instant. Runs at low priority and swallows
    /// failures: a prefetch that does not finish costs nothing but a slightly slower next change.
    /// </summary>
    private async Task PrefetchAsync()
    {
        int count = Settings.PrefetchCount;
        if (count <= 0) return;

        List<PhotoItem> upcoming;
        lock (_queueGate) upcoming = _queue.Take(count).ToList();

        foreach (var photo in upcoming)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                await _cache.GetFullAsync(photo, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn("Prefetch failed for " + photo.Key, ex);
            }
        }
    }

    private void Report(string message, bool isError = false)
    {
        if (isError) Log.Warn(message);
        StatusChanged?.Invoke(this, new RotationStatus(message, isError));
    }

    public void Dispose()
    {
        CancelRefill();
        _refillLock.Dispose();
        _state.Save();
    }
}
