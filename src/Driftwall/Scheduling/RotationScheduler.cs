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

    /// <summary>
    /// How long the change at shut-down may take to get its photo ready. Windows puts up its
    /// "preventing shutdown" screen after five seconds, and ends an app with no window on screen, which
    /// is how this one usually sits in the tray. Handing the files to Windows cannot be called off once
    /// it starts, and on a display set through the empty id it takes about half a second more, so that
    /// part gets the time left over: see <see cref="ShutdownHandOff"/>.
    /// </summary>
    private static readonly TimeSpan ShutdownChangeBudget = TimeSpan.FromSeconds(2.5);

    /// <summary>The extra time the session-ending handler waits for the hand-off, staying under five seconds.</summary>
    private static readonly TimeSpan ShutdownHandOff = TimeSpan.FromSeconds(2);

    private readonly SettingsStore _settingsStore;
    private readonly RotationService _rotation;
    private readonly WallpaperEngine _engine;

    private readonly Timer _timer;
    private readonly Timer _displayTimer;
    private readonly SemaphoreSlim _changeLock = new(1, 1);

    /// <summary>Held while the timer is armed or stopped, so a lock cannot slip between the check and the arming.</summary>
    private readonly Lock _armGate = new();

    private DateTimeOffset? _nextChangeAt;
    private DeferReason _lastDeferReason;
    private volatile bool _locked;
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

    /// <summary>True while a change is running. That change reports its own outcome when it ends.</summary>
    public bool IsChanging => _changeLock.CurrentCount == 0;

    public event EventHandler? ScheduleChanged;

    /// <summary>Runs the start-up behaviour and arms the timer. Called once, after the UI is up.</summary>
    public async Task StartAsync()
    {
        // Paused means the photo stays until the user presses Next, sign-in included.
        if (Settings.ChangeOnStartup && Settings.RotationEnabled)
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

        // A new schedule replaces any deferred retry, and the conditions are checked again when it
        // fires. A reason left over from before would keep the header saying "paused" when nothing is.
        _lastDeferReason = DeferReason.None;

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

    /// <summary>
    /// Picks the schedule up again after a lock or sleep, keeping the time the change was due rather
    /// than starting a whole interval over. Starting over meant a daily change never came round for
    /// anyone who locks or sleeps the PC once a day. A change that fell due meanwhile runs a few
    /// seconds later, because <see cref="Schedule"/> never arms for less than that.
    /// </summary>
    private void ResumeSchedule()
    {
        if (_disposed || _locked) return;

        var settings = Settings;
        if (!settings.RotationEnabled || settings.IntervalMinutes <= 0 || _nextChangeAt is not { } due)
        {
            Rearm();
            return;
        }

        Schedule(due - DateTimeOffset.Now);
    }

    private void Schedule(TimeSpan delay)
    {
        // Timer takes a long, so very large intervals are safe, but clamp anyway to stay sane.
        if (delay > TimeSpan.FromDays(7)) delay = TimeSpan.FromDays(7);
        if (delay < TimeSpan.FromSeconds(5)) delay = TimeSpan.FromSeconds(5);

        _nextChangeAt = DateTimeOffset.Now + delay;

        // While the PC is locked the due time is only remembered, and the unlock arms the timer from it.
        // A change that finishes after a quick re-lock would otherwise keep changing a locked desktop.
        lock (_armGate)
        {
            if (_locked) _timer.Change(Timeout.Infinite, Timeout.Infinite);
            else _timer.Change(delay, Timeout.InfiniteTimeSpan);
        }

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

    /// <summary>
    /// Advances to the next wallpaper. Safe to call from anywhere; changes are serialised. A change
    /// that runs past <paramref name="budget"/> (three minutes unless given) is cancelled.
    /// </summary>
    public async Task<bool> ChangeAsync(bool force, TimeSpan? budget = null)
    {
        if (!force && !Settings.RotationEnabled) return false;

        // The budget starts now, so time spent waiting for the lock counts against it too.
        using var cts = new CancellationTokenSource(budget ?? TimeSpan.FromMinutes(3));
        if (!await _changeLock.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false))
        {
            Log.Info("A wallpaper change is already in progress; ignoring this request.");
            return false;
        }

        try
        {
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
        if (!Settings.ChangeOnShutdown || !Settings.RotationEnabled) return;

        Log.Info("Session ending; changing wallpaper so the next sign-in is a new one.");
        try
        {
            // Saved first, so a change that runs long cannot cost the settings.
            _settingsStore.Save();

            // Windows gives a shutting-down process only a few seconds, so this deliberately blocks
            // with a hard timeout rather than fire-and-forget, which would simply be killed. A change
            // that misses its budget is cancelled before it reaches the desktop. One already handing
            // its files to Windows gets the hand-off time on top, though a very slow Explorer can
            // still outlast it.
            ChangeAsync(force: true, budget: ShutdownChangeBudget).Wait(ShutdownChangeBudget + ShutdownHandOff);
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
                _locked = false;
                if (Settings.ChangeOnUnlock && Settings.RotationEnabled)
                {
                    // The lock stopped the timer, so it is armed again once the change is done, the
                    // same way OnTimer does it, whether or not the change worked.
                    _ = Task.Run(async () =>
                    {
                        await ChangeAsync(force: true).ConfigureAwait(false);
                        Rearm();
                    });
                }
                else ResumeSchedule();
                break;

            case SessionSwitchReason.SessionLock:
                // Nothing to do while locked, and the timer would fire against a locked desktop.
                // The due time is kept, so the unlock carries on from it.
                lock (_armGate)
                {
                    _locked = true;
                    _timer.Change(Timeout.Infinite, Timeout.Infinite);
                }

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
            // Waking from sleep: the timer does not count time asleep, so its remaining delay is
            // meaningless. It is armed again for whatever is left until the change was due; on a
            // PC still locked from before the sleep, the unlock does that instead.
            Log.Info("Resumed from sleep; re-arming the rotation timer.");
            ResumeSchedule();
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
