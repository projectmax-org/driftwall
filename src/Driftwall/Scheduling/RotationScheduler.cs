using Driftwall.Core;
using Driftwall.Wallpaper;
using Microsoft.Win32;

namespace Driftwall.Scheduling;

/// <summary>
/// Drives wallpaper changes on a timer and in response to Windows session events.
/// <para>
/// One <see cref="Timer"/> handles the whole schedule: it is set to fire once, and re-armed after
/// each change. Nothing polls, nothing spins, and while the interval is an hour the app is doing
/// literally no work. Deferred conditions (a game in the foreground, battery power) are checked when
/// the timer fires, and the timer is simply re-armed for a short retry instead of changing.
/// </para>
/// </summary>
public sealed class RotationScheduler : IDisposable
{
    /// <summary>How long to wait before re-checking after deferring a change.</summary>
    private static readonly TimeSpan DeferRetry = TimeSpan.FromMinutes(2);

    /// <summary>Debounce for display changes, which arrive in bursts while Windows settles.</summary>
    private static readonly TimeSpan DisplaySettleDelay = TimeSpan.FromSeconds(3);

    private readonly SettingsStore _settingsStore;
    private readonly RotationService _rotation;
    private readonly WallpaperEngine _engine;

    private readonly Timer _timer;
    private readonly Timer _displayTimer;
    private readonly SemaphoreSlim _changeLock = new(1, 1);

    private DateTimeOffset? _nextChangeAt;
    private DeferReason _lastDeferReason;
    private bool _disposed;

    public RotationScheduler(SettingsStore settingsStore, RotationService rotation, WallpaperEngine engine)
    {
        _settingsStore = settingsStore;
        _rotation = rotation;
        _engine = engine;

        _timer = new Timer(_ => OnTimer(), null, Timeout.Infinite, Timeout.Infinite);
        _displayTimer = new Timer(_ => OnDisplaySettled(), null, Timeout.Infinite, Timeout.Infinite);

        SystemEvents.SessionEnding += OnSessionEnding;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private AppSettings Settings => _settingsStore.Settings;

    /// <summary>When the next automatic change is due, or null when rotation is off.</summary>
    public DateTimeOffset? NextChangeAt => _nextChangeAt;

    public DeferReason LastDeferReason => _lastDeferReason;

    public event EventHandler? ScheduleChanged;

    /// <summary>Runs the start-up behaviour and arms the timer. Called once, after the UI is up.</summary>
    public async Task StartAsync()
    {
        if (Settings.ChangeOnStartup)
        {
            Log.Info("Changing wallpaper on start-up.");
            await ChangeAsync(force: true).ConfigureAwait(false);
        }
        else if (_rotation.CurrentPhotos.Count > 0)
        {
            // Re-apply what was showing, in case the resolution changed while the app was closed.
            await SafeAsync(() => _rotation.RefreshAsync()).ConfigureAwait(false);
        }

        Rearm();
    }

    /// <summary>Re-reads the interval from settings and re-arms. Called when the user changes it.</summary>
    public void Rearm()
    {
        if (_disposed) return;

        var settings = Settings;
        if (!settings.RotationEnabled || settings.IntervalMinutes <= 0)
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            _nextChangeAt = null;
            ScheduleChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        Schedule(TimeSpan.FromMinutes(settings.IntervalMinutes));
    }

    private void Schedule(TimeSpan delay)
    {
        // Timer takes a long, so very large intervals are safe, but clamp anyway to stay sane.
        if (delay > TimeSpan.FromDays(7)) delay = TimeSpan.FromDays(7);
        if (delay < TimeSpan.FromSeconds(5)) delay = TimeSpan.FromSeconds(5);

        _nextChangeAt = DateTimeOffset.Now + delay;
        _timer.Change(delay, Timeout.InfiniteTimeSpan);
        ScheduleChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnTimer()
    {
        _ = Task.Run(async () =>
        {
            var deferReason = SystemConditions.Evaluate(Settings);
            if (deferReason != DeferReason.None)
            {
                _lastDeferReason = deferReason;
                Log.Info("Deferring the scheduled change: " + SystemConditions.Describe(deferReason));
                Schedule(DeferRetry);
                return;
            }

            _lastDeferReason = DeferReason.None;
            await ChangeAsync(force: false).ConfigureAwait(false);
            Rearm();
        });
    }

    /// <summary>Advances to the next wallpaper. Safe to call from anywhere; changes are serialised.</summary>
    public async Task<bool> ChangeAsync(bool force)
    {
        if (!force && !Settings.RotationEnabled) return false;

        if (!await _changeLock.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false))
        {
            Log.Info("A wallpaper change is already in progress; ignoring this request.");
            return false;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            return await _rotation.NextAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("Scheduled wallpaper change failed.", ex);
            return false;
        }
        finally
        {
            _changeLock.Release();
        }
    }

    // ---------------- Windows session events ----------------

    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        if (!Settings.ChangeOnShutdown) return;

        Log.Info("Session ending; changing wallpaper so the next sign-in is a new one.");
        try
        {
            // Windows gives a shutting-down process only a few seconds, so this deliberately blocks
            // with a hard timeout rather than fire-and-forget, which would simply be killed.
            ChangeAsync(force: true).Wait(TimeSpan.FromSeconds(8));
            _settingsStore.Save();
        }
        catch (Exception ex)
        {
            Log.Warn("Shutdown wallpaper change did not complete.", ex);
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon:
                if (Settings.ChangeOnUnlock) _ = ChangeAsync(force: true);
                else Rearm();
                break;

            case SessionSwitchReason.SessionLock:
                // Nothing to do while locked, and the timer would fire against a locked desktop.
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                MemoryTrimmer.TrimNow();
                break;
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Plugging in a monitor produces several of these in a row; wait for it to settle.
        _displayTimer.Change(DisplaySettleDelay, Timeout.InfiniteTimeSpan);
    }

    private void OnDisplaySettled()
    {
        _engine.InvalidateMonitors();
        Log.Info("Display configuration settled; re-rendering the wallpaper for the new layout.");
        _ = SafeAsync(() => _rotation.RefreshAsync());
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            // Waking from sleep: the timer's remaining delay is meaningless, so start it again.
            Log.Info("Resumed from sleep; re-arming the rotation timer.");
            Rearm();
        }
    }

    private static async Task SafeAsync(Func<Task> work)
    {
        try { await work().ConfigureAwait(false); }
        catch (Exception ex) { Log.Error("Scheduled work failed.", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        SystemEvents.SessionEnding -= OnSessionEnding;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        _timer.Dispose();
        _displayTimer.Dispose();
        _changeLock.Dispose();
    }
}
