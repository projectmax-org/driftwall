using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Driftwall.Core;
using Driftwall.Scheduling;

namespace Driftwall.UI.ViewModels;

public enum AppSection
{
    Browse = 0,
    Collections = 1,
    Displays = 2,
    Settings = 3,
}

/// <summary>
/// The window shell: navigation, the current-wallpaper strip, and the rotation controls.
/// <para>
/// The countdown timer only runs while the window is on screen. A hidden window has nothing to tick
/// for, and a per-second dispatcher timer in a tray app that sits idle for hours is exactly the kind
/// of background cost this app is trying not to have.
/// </para>
/// </summary>
public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _countdown;

    private AppSection _section = AppSection.Browse;
    private string _statusMessage = "Ready";
    private bool _statusIsError;
    private BitmapSource? _currentPreview;

    public MainViewModel(AppServices services)
    {
        _services = services;

        Browse = new BrowseViewModel(services);
        Collections = new CollectionsViewModel(services);
        Displays = new DisplaysViewModel(services);
        Settings = new SettingsViewModel(services);

        NextCommand = new AsyncRelayCommand(NextAsync);
        PreviousCommand = new AsyncRelayCommand(() => _services.Rotation.PreviousAsync());
        RefreshCommand = new AsyncRelayCommand(() => _services.Rotation.RefreshAsync());
        TogglePauseCommand = new RelayCommand(TogglePause);
        NavigateCommand = new RelayCommand(p =>
        {
            if (p is AppSection section) Section = section;
            else if (p is string name && Enum.TryParse<AppSection>(name, out var parsed)) Section = parsed;
        });
        OpenCurrentSourceCommand = new RelayCommand(() => Shell.OpenUrl(CurrentPhoto?.SourcePageUrl));
        SaveCurrentCommand = new AsyncRelayCommand(SaveCurrentAsync);

        _countdown = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += (_, _) => Raise(nameof(NextChangeLabel));

        _services.Rotation.StatusChanged += OnRotationStatus;
        _services.Engine.Applied += OnWallpaperApplied;
        _services.Scheduler.ScheduleChanged += (_, _) => RunOnUi(() =>
        {
            Raise(nameof(NextChangeLabel), nameof(IsPaused), nameof(PauseLabel), nameof(TrayTooltip));
            _services.Tray.SetTooltip(TrayTooltip);
        });

        UpdateCurrentPreview();
    }

    public BrowseViewModel Browse { get; }
    public CollectionsViewModel Collections { get; }
    public DisplaysViewModel Displays { get; }
    public SettingsViewModel Settings { get; }

    public AppSection Section
    {
        get => _section;
        set
        {
            if (!Set(ref _section, value)) return;

            Raise(nameof(IsBrowse), nameof(IsCollections), nameof(IsDisplays), nameof(IsSettings), nameof(SectionTitle));

            // Refresh the things that go stale while the user is looking elsewhere.
            if (value == AppSection.Displays) Displays.Refresh();
            if (value == AppSection.Settings) Settings.RefreshCacheSize();
        }
    }

    // Two-way so the navigation rail drives Section through IsChecked rather than through a Click
    // command. A RadioButton can be selected by mouse, by keyboard arrows, and by assistive
    // technology through the UI Automation SelectionItem pattern — only the first of those raises
    // Click, so binding the command would leave the other two navigating nowhere.
    public bool IsBrowse
    {
        get => Section == AppSection.Browse;
        set { if (value) Section = AppSection.Browse; }
    }

    public bool IsCollections
    {
        get => Section == AppSection.Collections;
        set { if (value) Section = AppSection.Collections; }
    }

    public bool IsDisplays
    {
        get => Section == AppSection.Displays;
        set { if (value) Section = AppSection.Displays; }
    }

    public bool IsSettings
    {
        get => Section == AppSection.Settings;
        set { if (value) Section = AppSection.Settings; }
    }

    public string SectionTitle => Section switch
    {
        AppSection.Collections => "Collections",
        AppSection.Displays => "Displays",
        AppSection.Settings => "Settings",
        _ => "Browse",
    };

    public PhotoItem? CurrentPhoto => _services.Rotation.CurrentPhotos.FirstOrDefault();

    public BitmapSource? CurrentPreview
    {
        get => _currentPreview;
        private set => Set(ref _currentPreview, value);
    }

    public string CurrentTitle => CurrentPhoto?.Title is { Length: > 0 } title ? title : "No wallpaper set yet";

    public string CurrentCredit
    {
        get
        {
            var photo = CurrentPhoto;
            if (photo is null) return "Press Next to get started";

            return photo.AuthorName is { Length: > 0 } author
                ? $"{author} · {photo.ProviderName}"
                : photo.ProviderName;
        }
    }

    public bool HasCurrentPhoto => CurrentPhoto is not null;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    public bool StatusIsError
    {
        get => _statusIsError;
        private set => Set(ref _statusIsError, value);
    }

    public bool IsPaused => !_services.Settings.Settings.RotationEnabled;

    public string PauseLabel => IsPaused ? "Resume" : "Pause";

    /// <summary>Human-readable time until the next automatic change, for the header.</summary>
    public string NextChangeLabel
    {
        get
        {
            var settings = _services.Settings.Settings;
            if (!settings.RotationEnabled) return "Rotation paused";
            if (settings.IntervalMinutes <= 0) return "Manual changes only";

            var deferReason = _services.Scheduler.LastDeferReason;
            if (deferReason != DeferReason.None) return SystemConditions.Describe(deferReason);

            var next = _services.Scheduler.NextChangeAt;
            if (next is null) return "Rotation paused";

            var remaining = next.Value - DateTimeOffset.Now;
            if (remaining <= TimeSpan.Zero) return "Changing now...";

            return remaining.TotalHours >= 1
                ? $"Next in {(int)remaining.TotalHours}h {remaining.Minutes}m"
                : remaining.TotalMinutes >= 1
                    ? $"Next in {(int)remaining.TotalMinutes}m"
                    : $"Next in {remaining.Seconds}s";
        }
    }

    /// <summary>Compact form for the tray tooltip, which is read at a glance.</summary>
    public string TrayTooltip
    {
        get
        {
            var photo = CurrentPhoto;
            var line = photo is null ? "Driftwall" : "Driftwall — " + (photo.Title ?? photo.ProviderName);
            return line + Environment.NewLine + TrayScheduleLabel;
        }
    }

    /// <summary>
    /// The schedule as a clock time rather than a countdown. The tooltip is only rewritten when the
    /// schedule changes, so "Next in 30m" would be wrong within a minute of being written.
    /// </summary>
    private string TrayScheduleLabel
    {
        get
        {
            var settings = _services.Settings.Settings;
            if (!settings.RotationEnabled) return "Rotation paused";
            if (settings.IntervalMinutes <= 0) return "Manual changes only";

            var deferReason = _services.Scheduler.LastDeferReason;
            if (deferReason != DeferReason.None) return SystemConditions.Describe(deferReason);

            var next = _services.Scheduler.NextChangeAt;
            return next is null ? "Rotation paused" : "Next change at " + next.Value.LocalDateTime.ToString("t");
        }
    }

    public ICommand NextCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand TogglePauseCommand { get; }
    public ICommand NavigateCommand { get; }
    public ICommand OpenCurrentSourceCommand { get; }
    public ICommand SaveCurrentCommand { get; }

    /// <summary>Called when the window becomes visible.</summary>
    public void StartCountdown()
    {
        Raise(nameof(NextChangeLabel));
        _countdown.Start();
    }

    /// <summary>Called when the window is hidden or minimised.</summary>
    public void StopCountdown() => _countdown.Stop();

    private async Task NextAsync()
    {
        StatusMessage = "Finding the next photo...";
        StatusIsError = false;
        await _services.Scheduler.ChangeAsync(force: true).ConfigureAwait(true);
    }

    private void TogglePause()
    {
        bool resume = IsPaused;
        _services.Settings.Update(s => s.RotationEnabled = resume);
        _services.Scheduler.Rearm();
        Raise(nameof(IsPaused), nameof(PauseLabel), nameof(NextChangeLabel));
        Settings.OnPauseChangedExternally();
    }

    private async Task SaveCurrentAsync()
    {
        var photo = CurrentPhoto;
        if (photo is null) return;

        var collection = Collections.Selected ?? _services.Collections.Collections.FirstOrDefault()
                         ?? _services.Collections.Create("Favourites");

        if (!_services.Collections.Add(collection.Id, photo))
        {
            StatusMessage = $"Already in {collection.Name}.";
            return;
        }

        StatusMessage = $"Saved to {collection.Name}.";

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var path = await _services.Cache.SaveCopyAsync(photo, cts.Token).ConfigureAwait(true);
            if (path is not null) _services.Collections.SetSavedPath(collection.Id, photo.Key, path);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not save a permanent copy of the current wallpaper.", ex);
        }
    }

    private void OnRotationStatus(object? sender, RotationStatus status)
    {
        RunOnUi(() =>
        {
            StatusMessage = status.Message;
            StatusIsError = status.IsError;
        });
    }

    private void OnWallpaperApplied(object? sender, Wallpaper.WallpaperApplication application)
    {
        RunOnUi(() =>
        {
            UpdateCurrentPreview();
            Raise(nameof(CurrentPhoto), nameof(CurrentTitle), nameof(CurrentCredit),
                  nameof(HasCurrentPhoto), nameof(NextChangeLabel), nameof(TrayTooltip));

            _services.Tray.SetTooltip(TrayTooltip);
        });
    }

    private void UpdateCurrentPreview()
    {
        var rendered = _services.Engine.Current?.Assignments.FirstOrDefault()?.RenderedPath;
        if (rendered is null || !File.Exists(rendered))
        {
            CurrentPreview = null;
            return;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(rendered, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 280;
            image.EndInit();
            image.Freeze();
            CurrentPreview = image;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not load the current wallpaper preview.", ex);
            CurrentPreview = null;
        }
    }

    private void RaiseOnUi(params string[] names) => RunOnUi(() => Raise(names));

    private static void RunOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    public void Dispose()
    {
        _countdown.Stop();
        _services.Rotation.StatusChanged -= OnRotationStatus;
        _services.Engine.Applied -= OnWallpaperApplied;
    }
}
