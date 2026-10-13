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

    /// <summary>How many pages in a row one change may fetch while every photo so far was a repeat.</summary>
    private const int MaxRefillRounds = 3;

    /// <summary>How many queued photos one change may burn through before giving up.</summary>
    private const int MaxApplyAttempts = 3;

    /// <summary>
    /// How many photos one change may pass over because their real file is too small for a display,
    /// before it stops checking and uses the next photo as it is.
    /// </summary>
    private const int MaxUndersizedSkips = 6;


    private readonly SettingsStore _settingsStore;
    private readonly SourceRegistry _registry;
    private readonly ImageCache _cache;
    private readonly WallpaperEngine _engine;
    private readonly AppState _state;

    private readonly Lock _queueGate = new();
    private readonly List<PhotoItem> _queue = new();

    /// <summary>
    /// One entry per change, holding every photo on screen after it, so Previous brings back the
    /// whole desktop as it was rather than one of its photos on every display.
    /// </summary>
    private readonly List<IReadOnlyList<PhotoItem>> _history = new();
    private readonly HashSet<string> _recentKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// The photo each display was last given, by monitor id. A display switched off since goes on
    /// showing it, so a re-render after it is switched back on puts that back rather than a copy of
    /// another display's. Only touched while the engine holds its apply lock: written from its
    /// Applied event, read from the per-monitor callbacks it calls.
    /// </summary>
    private readonly Dictionary<string, PhotoItem> _photoByMonitor = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Photos a refill turned away only because they were shown recently, kept in case every source
    /// runs out of new ones. Guarded by _queueGate.
    /// </summary>
    private readonly Dictionary<string, PhotoItem> _repeatCandidates = new(StringComparer.Ordinal);

    /// <summary>When each recent photo was last shown, as a rising counter. Guarded by _queueGate.</summary>
    private readonly Dictionary<string, long> _recentStamp = new(StringComparer.Ordinal);
    private long _stampCounter;

    /// <summary>Bumped by <see cref="ResetQueue"/>, so a refill that started before it throws its results away.</summary>
    private int _generation;

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

        // RecentKeys is saved oldest first, so the order photos were shown in survives a restart.
        foreach (var key in _state.RecentKeys)
        {
            _recentKeys.Add(key);
            _recentStamp[key] = ++_stampCounter;
        }

        if (_state.CurrentPhotos.Count > 0) _history.Add(_state.CurrentPhotos.ToList());
        foreach (var photo in _state.CurrentPhotos) _recentStamp[photo.Key] = ++_stampCounter;
        _historyPosition = _history.Count - 1;

        _engine.Applied += OnApplied;
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
            _repeatCandidates.Clear();
            _generation++;
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
        int undersizedSkips = 0;
        List<PhotoItem>? lastUndersized = null;

        // A photo that will not download (a provider throttling, a dead link) costs one attempt, not
        // the whole change. Otherwise the timer leaves the old wallpaper up for another interval over
        // one bad URL, and "Next" appears to do nothing.
        for (int attempt = 1; attempt <= MaxApplyAttempts; attempt++)
        {
            var picks = await TakeAsync(needed, monitors, ct).ConfigureAwait(false);
            bool checkSize = undersizedSkips < MaxUndersizedSkips;

            if (picks.Count == 0)
            {
                if (lastUndersized is null)
                {
                    Report("No photos available. Check your sources in Settings.", isError: true);
                    return false;
                }

                // Nothing else is left. The last photo turned away as too small beats showing nothing,
                // so it is used as it is, straight from here rather than through the queue.
                Log.Warn("Every available photo was too small for your displays, so the last one is used as it is. Adding a source with larger photos, or lowering the minimum resolution in Settings, avoids this.");
                picks = lastUndersized;
                lastUndersized = null;
                checkSize = false;
                undersizedSkips = MaxUndersizedSkips;
            }


            // A photo whose real file is too small for its display is passed over without costing an
            // attempt. The skips are capped so a source full of small photos cannot stall rotation.
            if (checkSize)
            {
                var undersized = await FindUndersizedAsync(picks, monitors, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return false;
                if (undersized.Count > 0)
                {
                    undersizedSkips += undersized.Count;
                    lastUndersized = picks;

                    // Only the photos that are too small are replaced. Any others go back to the head
                    // of the queue, no longer counted as shown, and are taken again straight away.
                    var usable = picks.Where(p => !undersized.Contains(p)).ToList();
                    if (usable.Count > 0) RequeueFront(usable);
                    if (undersizedSkips >= MaxUndersizedSkips)
                        Log.Warn($"{undersizedSkips} photos in a row were too small for your displays, so the next one is used as it is. Adding a source with larger photos, or lowering the minimum resolution in Settings, avoids this.");

                    attempt--;
                    continue;
                }
            }

            RecordHistory(picks);
            if (await ApplyAsync(picks, monitors, ct, reportFailure: attempt == MaxApplyAttempts).ConfigureAwait(false))
            {
                _ = PrefetchAsync();
                return true;
            }

            // Undone before anything else, cancellation included, so Previous never leads to a photo
            // that did not reach the desktop.
            ForgetHistory(picks);
            if (ct.IsCancellationRequested) return false;

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
        if (CurrentPhotos.Count == 0) return await NextAsync(ct).ConfigureAwait(false);

        Report("Refreshing wallpaper...");

        // What is on screen is read only once the engine holds its lock. Read any earlier, a change
        // still being applied would be undone straight after it landed by the photos from before it.
        Dictionary<string, PhotoItem>? assignment = null;
        return await ApplyChosenAsync(m => (assignment ??= RefreshAssignment()).GetValueOrDefault(m.Id), ct)
            .ConfigureAwait(false) is not null;
    }

    /// <summary>
    /// Re-applies what is on screen after a switch to a different photo on each display. Displays still
    /// sharing a photo from the mode before get one of their own from the queue. Only for that switch:
    /// a photo the user put on every display on purpose is left to an ordinary refresh.
    /// </summary>
    public async Task<bool> SpreadOutAsync(CancellationToken ct = default)
    {
        var current = _engine.Current;
        if (Settings.MultiMonitor != MultiMonitorMode.DifferentPerMonitor || current is null || CurrentPhotos.Count == 0)
            return await RefreshAsync(ct).ConfigureAwait(false);

        // Every display after the first to show a photo is one that shares it.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sharing = new List<MonitorInfo>();
        foreach (var monitor in _engine.ActiveMonitors(Settings))
        {
            var shown = current.Assignments
                .FirstOrDefault(a => string.Equals(a.Monitor.Id, monitor.Id, StringComparison.OrdinalIgnoreCase))?.Photo;
            if (shown is not null && !seen.Add(shown.Key)) sharing.Add(monitor);
        }

        if (sharing.Count == 0) return await RefreshAsync(ct).ConfigureAwait(false);

        Report("Finding a photo for each display...");
        var fresh = await TakeAsync(sharing.Count, sharing, ct).ConfigureAwait(false);
        var undersized = await FindUndersizedAsync(fresh, sharing, ct).ConfigureAwait(false);
        fresh.RemoveAll(undersized.Contains);
        if (fresh.Count == 0) return await RefreshAsync(ct).ConfigureAwait(false);

        var theirs = BuildAssignment(fresh, sharing);
        Dictionary<string, PhotoItem>? others = null;
        var application = await ApplyChosenAsync(
            m => theirs.TryGetValue(m.Id, out var photo) ? photo : (others ??= RefreshAssignment()).GetValueOrDefault(m.Id),
            ct).ConfigureAwait(false);
        if (application is null) return false;

        // A change in its own right, so Previous goes back to the shared photo.
        RecordHistory(application.Assignments.Select(a => a.Photo).ToList());
        _ = PrefetchAsync();
        return true;
    }

    /// <summary>Applies one specific photo to every monitor. Used by "Set as wallpaper" in the browser.</summary>
    public async Task<bool> ApplySpecificAsync(PhotoItem photo, CancellationToken ct = default)
    {
        var monitors = _engine.ActiveMonitors(Settings);
        var picks = new List<PhotoItem> { photo };
        RecordHistory(picks);
        if (await ApplyAsync(picks, monitors, ct).ConfigureAwait(false)) return true;

        // As in NextAsync: Previous never leads to a photo that did not reach the desktop.
        ForgetHistory(picks);
        return false;
    }

    /// <summary>Applies one specific photo to a single monitor, leaving the others alone.</summary>
    public async Task<bool> ApplyToMonitorAsync(PhotoItem photo, MonitorInfo monitor, CancellationToken ct = default) =>
        await ApplyToMonitorCoreAsync(photo, monitor, ct).ConfigureAwait(false) is not null;

    /// <summary>
    /// Puts the next photo from the queue on one monitor, leaving the others as they are. Where every
    /// display shares one photo, one of them cannot change on its own, so this is an ordinary change.
    /// </summary>
    public async Task<bool> NextForMonitorAsync(MonitorInfo monitor, CancellationToken ct = default)
    {
        if (!_engine.ActiveMonitors(Settings).Any(m => string.Equals(m.Id, monitor.Id, StringComparison.OrdinalIgnoreCase)))
        {
            Report($"{monitor.Name} is not included in rotation. Switch it on in Displays first.", isError: true);
            return false;
        }

        if (Settings.MultiMonitor != MultiMonitorMode.DifferentPerMonitor) return await NextAsync(ct).ConfigureAwait(false);

        var target = new List<MonitorInfo> { monitor };
        int undersizedSkips = 0;

        // The same allowances as a full change: a photo that will not download costs one attempt, and
        // one too small for the display is passed over a limited number of times.
        for (int attempt = 1; attempt <= MaxApplyAttempts; attempt++)
        {
            var picks = await TakeAsync(1, target, ct).ConfigureAwait(false);
            if (picks.Count == 0)
            {
                Report("No photos available. Check your sources in Settings.", isError: true);
                return false;
            }

            if (undersizedSkips < MaxUndersizedSkips)
            {
                var undersized = await FindUndersizedAsync(picks, target, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return false;
                if (undersized.Count > 0)
                {
                    undersizedSkips++;
                    attempt--;
                    continue;
                }
            }

            var application = await ApplyToMonitorCoreAsync(picks[0], monitor, ct, reportFailure: attempt == MaxApplyAttempts)
                .ConfigureAwait(false);
            if (application is not null)
            {
                // Recorded as the whole desktop it left, so Previous puts every display back as it was.
                RecordHistory(application.Assignments.Select(a => a.Photo).ToList());
                _ = PrefetchAsync();
                return true;
            }

            if (ct.IsCancellationRequested) return false;
            Log.Info($"Could not use {picks[0].Key} on {monitor.Name}; trying the next photo ({attempt}/{MaxApplyAttempts}).");
        }

        return false;
    }

    // ---------------- applying ----------------

    private async Task<bool> ApplyFromHistoryAsync(CancellationToken ct)
    {
        var photos = _history.ElementAtOrDefault(_historyPosition);
        if (photos is null || photos.Count == 0) return false;

        var monitors = _engine.ActiveMonitors(Settings);
        return await ApplyAsync(photos, monitors, ct).ConfigureAwait(false);
    }

    private async Task<bool> ApplyAsync(
        IReadOnlyList<PhotoItem> picks, IReadOnlyList<MonitorInfo> monitors, CancellationToken ct, bool reportFailure = true)
    {
        if (picks.Count == 0) return false;

        var assignment = BuildAssignment(picks, monitors);
        return await ApplyChosenAsync(m => assignment.GetValueOrDefault(m.Id), ct, reportFailure).ConfigureAwait(false) is not null;
    }

    private async Task<WallpaperApplication?> ApplyToMonitorCoreAsync(
        PhotoItem photo, MonitorInfo monitor, CancellationToken ct, bool reportFailure = true)
    {
        // The other displays are looked up only once the engine holds its lock, so they keep what the
        // change before this one gave them, not what they showed when this was asked for.
        Dictionary<string, PhotoItem>? others = null;
        var application = await ApplyChosenAsync(
            m => string.Equals(m.Id, monitor.Id, StringComparison.OrdinalIgnoreCase)
                ? photo
                : (others ??= RefreshAssignment()).GetValueOrDefault(m.Id),
            ct, reportFailure, reportSuccess: false).ConfigureAwait(false);

        if (application is null) return null;

        // The other displays are rendered again too, so the change can go through without reaching
        // this one: its photo would not download, say.
        if (!application.Assignments.Any(a =>
                string.Equals(a.Monitor.Id, monitor.Id, StringComparison.OrdinalIgnoreCase) && a.Photo.Key == photo.Key))
        {
            if (reportFailure) Report($"Could not apply the wallpaper to {monitor.Name}. See the log for details.", isError: true);
            return null;
        }

        Report($"Applied to {monitor.Name}.");
        return application;
    }

    /// <summary>
    /// Applies the photo <paramref name="photoForMonitor"/> chooses for each monitor, then saves and
    /// reports the result. The engine asks for each photo only once it holds its apply lock.
    /// </summary>
    private async Task<WallpaperApplication?> ApplyChosenAsync(
        Func<MonitorInfo, PhotoItem?> photoForMonitor, CancellationToken ct, bool reportFailure = true, bool reportSuccess = true)
    {
        try
        {
            var application = await _engine.ApplyAsync(photoForMonitor, Settings, ct).ConfigureAwait(false);

            if (application is null)
            {
                if (reportFailure) Report("Could not apply the wallpaper. See the log for details.", isError: true);
                return null;
            }

            PersistState(application.Assignments.Select(a => a.Photo).ToList());

            if (reportSuccess)
            {
                var primary = application.PrimaryPhoto;
                Report(primary is null
                    ? "Wallpaper updated."
                    : $"{primary.Title ?? "Untitled"} - {primary.AuthorName ?? primary.ProviderName}");
            }

            return application;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("Applying the wallpaper failed.", ex);
            Report("Something went wrong applying the wallpaper.", isError: true);
            return null;
        }
    }

    /// <summary>
    /// What a re-render puts on each active display: the photo it was last given. A display with none
    /// yet (nothing applied this session, or one just plugged in) gets one of the photos on screen,
    /// shared out the way a change does it.
    /// </summary>
    private Dictionary<string, PhotoItem> RefreshAssignment()
    {
        var monitors = _engine.ActiveMonitors(Settings);
        var assignment = BuildAssignment(CurrentPhotos, monitors);

        // Only where every display has its own photo. Where one is shared or spanned, the engine takes
        // the first display's, and an older photo remembered for it would replace what is on screen now.
        if (Settings.MultiMonitor != MultiMonitorMode.DifferentPerMonitor) return assignment;

        foreach (var monitor in monitors)
        {
            if (_photoByMonitor.TryGetValue(monitor.Id, out var photo)) assignment[monitor.Id] = photo;
        }

        return assignment;
    }

    private void OnApplied(object? sender, WallpaperApplication application)
    {
        foreach (var assignment in application.Assignments) _photoByMonitor[assignment.Monitor.Id] = assignment.Photo;
    }

    /// <summary>
    /// Returns the picks whose downloaded file would be enlarged past the "skip photos that are too
    /// small" limit on a display they are headed for. What a source reports is only a promise:
    /// Pixabay, for one, reports the original upload and serves a 1280px copy. So the real file is
    /// measured. A photo that fails to download is left to the apply attempt, which handles that.
    /// </summary>
    private async Task<List<PhotoItem>> FindUndersizedAsync(
        IReadOnlyList<PhotoItem> picks, IReadOnlyList<MonitorInfo> monitors, CancellationToken ct)
    {
        var undersized = new List<PhotoItem>();
        var settings = Settings;
        if (!settings.RejectLowResolution || settings.MinResolutionRatio <= 0 || monitors.Count == 0) return undersized;

        double limit = 1.0 / settings.MinResolutionRatio;
        var assignment = BuildAssignment(picks, monitors);

        try
        {
            foreach (var photo in picks)
            {
                var path = await _cache.GetFullAsync(photo, ct).ConfigureAwait(false);
                if (path is null) continue;

                var (width, height) = await ImageComposer.ReadDisplayDimensionsAsync(path, ct).ConfigureAwait(false);
                if (width <= 0 || height <= 0) continue;

                var (factor, display) = LargestEnlargement(photo, width, height, monitors, assignment, settings);
                if (factor <= limit + 1e-6) continue;

                undersized.Add(photo);
                Log.Info($"Skipped {photo.Key}: its {width}x{height} file would be enlarged {factor:0.00}x on {display}, past the {limit:0.00}x the minimum resolution allows.");
            }
        }
        catch (OperationCanceledException)
        {
            // Let the apply attempt meet the cancellation and report it the usual way.
            undersized.Clear();
        }

        return undersized;
    }

    /// <summary>The largest enlargement a photo of this size needs on the displays it is assigned to.</summary>
    private static (double Factor, string Display) LargestEnlargement(
        PhotoItem photo, int width, int height, IReadOnlyList<MonitorInfo> monitors,
        Dictionary<string, PhotoItem> assignment, AppSettings settings)
    {
        if (settings.MultiMonitor == MultiMonitorMode.SpanAcrossMonitors)
        {
            int spanWidth = monitors.Max(m => m.X + m.Width) - monitors.Min(m => m.X);
            int spanHeight = monitors.Max(m => m.Y + m.Height) - monitors.Min(m => m.Y);
            return (ImageComposer.EnlargementFactor(width, height, spanWidth, spanHeight, settings.Scaling),
                    $"the {spanWidth} x {spanHeight} span");
        }

        double worst = 0;
        string display = string.Empty;
        foreach (var monitor in monitors)
        {
            if (!assignment.TryGetValue(monitor.Id, out var assigned) || !ReferenceEquals(assigned, photo)) continue;

            double factor = ImageComposer.EnlargementFactor(
                width, height, monitor.Width, monitor.Height, WallpaperEngine.ScalingFor(monitor, settings));
            if (factor > worst)
            {
                worst = factor;
                display = $"{monitor.Name} ({monitor.ResolutionLabel})";
            }
        }

        return (worst, display);
    }

    /// <summary>Puts photos back at the head of the queue, no longer counted as recently shown.</summary>
    private void RequeueFront(IReadOnlyList<PhotoItem> photos)
    {
        lock (_queueGate)
        {
            foreach (var photo in photos) _recentKeys.Remove(photo.Key);
            _queue.InsertRange(0, photos);
        }
    }

    /// <summary>
    /// Maps photos onto monitors. With one photo it goes everywhere; with several, each monitor gets
    /// the best orientation match still available so a portrait display is not handed a landscape shot.
    /// </summary>
    private static Dictionary<string, PhotoItem> BuildAssignment(IReadOnlyList<PhotoItem> picks, IReadOnlyList<MonitorInfo> monitors)
    {
        var result = new Dictionary<string, PhotoItem>(StringComparer.OrdinalIgnoreCase);
        if (monitors.Count == 0 || picks.Count == 0) return result;

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
        lock (_queueGate)
        {
            // Oldest first, so the order photos were shown in survives a restart.
            _state.RecentKeys = _recentKeys
                .OrderBy(key => _recentStamp.TryGetValue(key, out long stamp) ? stamp : long.MaxValue)
                .TakeLast(Math.Max(50, Settings.NoRepeatWindow))
                .ToList();
        }
        _state.Save();
    }

    // ---------------- the queue ----------------

    /// <summary>Takes photos from the queue, refilling first when it is running low.</summary>
    private async Task<List<PhotoItem>> TakeAsync(int count, IReadOnlyList<MonitorInfo> monitors, CancellationToken ct)
    {
        if (QueueLength < Math.Max(count, QueueRefillThreshold))
        {
            // Every source starts again at its first page when the app starts, and those photos have
            // often all been shown recently. When a round brings back nothing but repeats, the next
            // page is fetched straight away instead of failing the change.
            for (int round = 1; round <= MaxRefillRounds; round++)
            {
                bool onlyRepeats = await RefillAsync(ct).ConfigureAwait(false);
                if (!onlyRepeats || QueueLength >= count) break;
                if (round < MaxRefillRounds) Log.Info("Every photo fetched was shown recently; trying the next page.");
            }

            // A narrow search can simply run out of photos that have not been shown lately. Showing
            // an older one again beats leaving the wallpaper stuck with "No photos available".
            if (QueueLength < count) EnqueueOldestRepeats(count);
        }

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

                // From the moment it leaves the queue a pick counts as recently shown, inside this lock,
                // so a refill running while it downloads cannot queue it again, and a photo turned away
                // as too small stays out of rotation for the no-repeat window like any photo already seen.
                _recentKeys.Add(picks[^1].Key);

                // Stamped as just shown too, so a pick still downloading, or one turned away as too small,
                // sorts behind every other repeat instead of being handed out again first.
                _recentStamp[picks[^1].Key] = ++_stampCounter;
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
    /// <returns>
    /// True when sources returned photos but every one was already queued or shown recently, so
    /// fetching the next page could still help.
    /// </returns>
    private async Task<bool> RefillAsync(CancellationToken ct)
    {
        if (!await _refillLock.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false)) return false;

        try
        {
            if (QueueLength >= QueueTarget) return false;

            int generation;
            lock (_queueGate) generation = _generation;

            var settings = Settings;
            var selections = _registry.Resolve(settings).ToList();
            if (selections.Count == 0)
            {
                Report("No sources are enabled. Add one in Settings.", isError: true);
                return false;
            }

            var monitors = _engine.ActiveMonitors(settings);
            var (targetWidth, targetHeight, orientation) = SourceRegistry.TargetFor(monitors, settings);
            var floor = SourceRegistry.FloorFor(monitors, settings);

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
                var query = SourceRegistry.BuildQuery(selection, settings, page, FetchPageSize, targetWidth, targetHeight, orientation, floor);
                var context = _registry.BuildContext(selection, settings, _settingsStore.GetApiKey, targetWidth, targetHeight);

                try
                {
                    var result = await source.FetchAsync(query, context, ct).ConfigureAwait(false);

                    // A source that has run out of pages starts again from the beginning. An empty page
                    // that still has more after it (every photo on it filtered out, or an APOD search
                    // window with no hits) is not the end, and the next refill asks for the page after.
                    if (!result.HasMore) ResetPageFor(selection);

                    return (selection, Items: result.Items.ToList(), Problem: result.Notice);
                }
                catch (SourceException ex)
                {
                    return (selection, Items: (List<PhotoItem>?)null, Problem: ex.Message);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // A request that timed out arrives as a cancellation nobody asked for. That is this
                    // source not responding, and the others' photos are still used.
                    Log.Warn($"Source {source.DisplayName} failed.", ex);
                    return (selection, Items: (List<PhotoItem>?)null, Problem: $"{source.DisplayName} is not responding.");
                }
            });

            // Started on the thread pool: each source checks its settings before its first await, and a
            // local folder on a network share that is not there can block that check for many seconds.
            var results = await Task.Run(() => Task.WhenAll(fetches)).ConfigureAwait(false);

            foreach (var (selection, items, problem) in results)
            {
                if (problem is not null) problems.Add(problem);
                if (items is { Count: > 0 }) buckets.Add((selection.Weight, items));
            }

            var merged = Interleave(buckets);
            var repeats = new List<PhotoItem>();
            int added = Enqueue(merged, settings, repeats, generation);

            if (added > 0)
            {
                Log.Info($"Queue topped up with {added} photos ({QueueLength} waiting).");
                return false;
            }

            // Photos came back, but all of them were already queued or shown recently. The next page
            // may well hold new ones, which is worth a try before reporting a problem.
            if (merged.Count > 0)
            {
                lock (_queueGate)
                {
                    // Photos from sources the user has changed since this refill started are no use.
                    // Returning true makes the caller fetch again, for the sources as they are now.
                    if (generation != _generation) return true;
                    foreach (var photo in repeats) _repeatCandidates[photo.Key] = photo;
                }

                // A source that failed is reported, and is not asked straight away for another page.
                if (problems.Count > 0)
                {
                    Report(problems[0], isError: true);
                    return false;
                }

                return true;
            }

            if (problems.Count > 0) Report(problems[0], isError: true);
            return false;
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

    /// <summary>
    /// Queues just enough photos for this change from those every source has already shown recently,
    /// the ones shown longest ago first. The queue stays short, so refills keep paging on and a photo
    /// never shown still comes before the next repeat. Photos on the desktop now are left out.
    /// </summary>
    private void EnqueueOldestRepeats(int count)
    {
        var onScreen = CurrentPhotos.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        List<PhotoItem> chosen;

        lock (_queueGate)
        {
            if (_repeatCandidates.Count == 0) return;

            // A key marked recent this session without ever being shown (a dead link, a photo too
            // small) has no stamp and goes last rather than first.
            var queued = _queue.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            chosen = _repeatCandidates.Values
                .Where(p => !onScreen.Contains(p.Key) && !queued.Contains(p.Key))
                .OrderBy(p => _recentStamp.TryGetValue(p.Key, out long stamp) ? stamp : long.MaxValue)
                .Take(Math.Max(1, count - _queue.Count))
                .ToList();

            if (Settings.Shuffle) chosen = chosen.OrderBy(_ => Random.Shared.Next()).ToList();

            foreach (var photo in chosen)
            {
                _queue.Add(photo);
                _repeatCandidates.Remove(photo.Key);
            }
        }

        if (chosen.Count > 0)
            Log.Info($"Every photo your sources returned was shown recently, so {chosen.Count} shown longest ago {(chosen.Count == 1 ? "comes" : "come")} round again.");
    }

    /// <summary>
    /// Adds photos to the queue, skipping duplicates and anything shown too recently. Photos skipped
    /// only for being recent are added to <paramref name="skippedAsRecent"/> when it is given.
    /// </summary>
    private int Enqueue(List<PhotoItem> photos, AppSettings settings, List<PhotoItem>? skippedAsRecent = null, int? generation = null)
    {
        int added = 0;
        lock (_queueGate)
        {
            // Results fetched for sources the user has changed since are dropped.
            if (generation is not null && generation != _generation) return 0;

            var queued = _queue.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

            foreach (var photo in photos)
            {
                if (!queued.Add(photo.Key)) continue;
                if (settings.NoRepeatWindow > 0 && _recentKeys.Contains(photo.Key))
                {
                    skippedAsRecent?.Add(photo);
                    continue;
                }


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
        // Under the queue lock: background refills read the recent set at the same time.
        lock (_queueGate)
        {
            // The list itself is the entry, which is how ForgetHistory finds it again.
            _history.Add(picks);
            foreach (var photo in picks)
            {
                _recentKeys.Add(photo.Key);
                _recentStamp[photo.Key] = ++_stampCounter;
            }

            // Trimmed by photos rather than changes, so the window means the same with any number of displays.
            int window = Math.Max(50, Settings.NoRepeatWindow);
            int photos = _history.Sum(entry => entry.Count);
            while (_history.Count > 1 && photos > window)
            {
                photos -= _history[0].Count;
                _history.RemoveAt(0);
            }

            if (_recentKeys.Count > window * 2)
            {
                // The set only guards against repeats, so trimming it to the live history is enough.
                _recentKeys.Clear();
                foreach (var photo in _history.SelectMany(entry => entry)) _recentKeys.Add(photo.Key);
                foreach (var key in _recentStamp.Keys.Where(k => !_recentKeys.Contains(k)).ToList()) _recentStamp.Remove(key);
            }

            _historyPosition = _history.Count - 1;
        }
    }

    /// <summary>
    /// Undoes <see cref="RecordHistory"/> for photos that never reached the desktop, so "Previous"
    /// does not lead back to a photo nobody saw. Their keys stay in the recent set on purpose: a
    /// link that just failed should not be the very next thing tried.
    /// </summary>
    private void ForgetHistory(IReadOnlyList<PhotoItem> picks)
    {
        // This exact entry, not the last one: a photo applied from the tray meanwhile may come after it.
        lock (_queueGate)
        {
            int index = _history.FindLastIndex(entry => ReferenceEquals(entry, picks));
            if (index >= 0) _history.RemoveAt(index);

            _historyPosition = _history.Count - 1;
        }
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
        _engine.Applied -= OnApplied;
        CancelRefill();
        _refillLock.Dispose();
        _state.Save();
    }
}
