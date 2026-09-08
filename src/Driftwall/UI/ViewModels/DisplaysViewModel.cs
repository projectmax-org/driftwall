using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Driftwall.Core;
using Driftwall.Wallpaper;

namespace Driftwall.UI.ViewModels;

/// <summary>One monitor, with what is on it and the settings that apply only to it.</summary>
public sealed class MonitorViewModel : ViewModelBase
{
    private readonly SettingsStore _settings;
    private readonly Action _changed;
    private BitmapSource? _preview;
    private PhotoItem? _photo;

    public MonitorViewModel(MonitorInfo monitor, SettingsStore settings, Action changed)
    {
        Monitor = monitor;
        _settings = settings;
        _changed = changed;
    }

    public MonitorInfo Monitor { get; }

    public string Name => Monitor.Name;
    public string Resolution => Monitor.ResolutionLabel;
    public bool IsPrimary => Monitor.IsPrimary;

    /// <summary>Aspect ratio used to draw the monitor to scale in the layout map.</summary>
    public double AspectRatio => Monitor.AspectRatio <= 0 ? 16.0 / 9.0 : Monitor.AspectRatio;

    public string OrientationLabel => Monitor.Width >= Monitor.Height ? "Landscape" : "Portrait";

    public BitmapSource? Preview
    {
        get => _preview;
        private set => Set(ref _preview, value);
    }

    public PhotoItem? Photo
    {
        get => _photo;
        private set
        {
            if (!Set(ref _photo, value)) return;
            Raise(nameof(PhotoTitle), nameof(PhotoCredit), nameof(HasPhoto));
        }
    }

    public bool HasPhoto => _photo is not null;

    public string PhotoTitle => _photo?.Title is { Length: > 0 } title ? title : "No wallpaper set yet";

    public string PhotoCredit => _photo is null
        ? string.Empty
        : _photo.AuthorName is { Length: > 0 } author ? $"{author} · {_photo.ProviderName}" : _photo.ProviderName;

    public bool Enabled
    {
        get => Settings().Enabled;
        set
        {
            var entry = Settings();
            if (entry.Enabled == value) return;

            entry.Enabled = value;
            _settings.Touch();
            OnPropertyChanged();
            _changed();
        }
    }

    /// <summary>Null means "use the global scaling mode".</summary>
    public ScalingMode? ScalingOverride
    {
        get => Settings().ScalingOverride;
        set
        {
            var entry = Settings();
            if (entry.ScalingOverride == value) return;

            entry.ScalingOverride = value;
            _settings.Touch();
            OnPropertyChanged();
            _changed();
        }
    }

    public void SetApplied(PhotoItem? photo, BitmapSource? preview)
    {
        Photo = photo;
        Preview = preview;
    }

    /// <summary>Finds or creates this monitor's settings entry.</summary>
    private MonitorSettings Settings()
    {
        var existing = _settings.Settings.Monitors
            .FirstOrDefault(m => string.Equals(m.MonitorId, Monitor.Id, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            // Keep the stored name current so a disconnected display is still recognisable.
            if (existing.FriendlyName != Monitor.Name) existing.FriendlyName = Monitor.Name;
            return existing;
        }

        var created = new MonitorSettings { MonitorId = Monitor.Id, FriendlyName = Monitor.Name };
        _settings.Settings.Monitors.Add(created);
        return created;
    }
}

/// <summary>
/// The displays tab: a scale map of the monitor layout, what each one is showing, and per-monitor
/// overrides for the ones that need different treatment.
/// </summary>
public sealed class DisplaysViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _reapplyTimer;
    private string? _statusMessage;

    public DisplaysViewModel(AppServices services)
    {
        _services = services;

        RefreshCommand = new RelayCommand(Refresh);
        ApplyNowCommand = new AsyncRelayCommand(ApplyNowAsync);
        NextForMonitorCommand = new AsyncRelayCommand(NextForMonitorAsync);
        OpenRenderedCommand = new RelayCommand(OpenRendered);

        _reapplyTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(600) };
        _reapplyTimer.Tick += (_, _) =>
        {
            _reapplyTimer.Stop();
            _ = ReapplyAsync();
        };

        services.Engine.Applied += (_, _) =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(UpdatePreviews));

        Refresh();
    }

    public ObservableCollection<MonitorViewModel> Monitors { get; } = new();

    public IReadOnlyList<MultiMonitorMode> MultiMonitorModes { get; } =
        [MultiMonitorMode.DifferentPerMonitor, MultiMonitorMode.SameOnAllMonitors, MultiMonitorMode.SpanAcrossMonitors];

    public IReadOnlyList<ScalingMode> ScalingModes { get; } =
        [ScalingMode.Fill, ScalingMode.Fit, ScalingMode.Stretch, ScalingMode.Center, ScalingMode.Tile];

    public MultiMonitorMode MultiMonitor
    {
        get => _services.Settings.Settings.MultiMonitor;
        set
        {
            if (_services.Settings.Settings.MultiMonitor == value) return;

            _services.Settings.Update(s => s.MultiMonitor = value);
            OnPropertyChanged();
            Raise(nameof(MultiMonitorDescription));
            ScheduleReapply();
        }
    }

    public string MultiMonitorDescription => MultiMonitor switch
    {
        MultiMonitorMode.SameOnAllMonitors =>
            "One photo on every display, rendered separately for each so nothing is stretched.",
        MultiMonitorMode.SpanAcrossMonitors =>
            "A single photo stretched across the whole desktop, sliced per display so mixed DPI stays aligned.",
        _ => "Each display gets its own photo, matched to its shape where possible.",
    };

    public ScalingMode Scaling
    {
        get => _services.Settings.Settings.Scaling;
        set
        {
            if (_services.Settings.Settings.Scaling == value) return;

            _services.Settings.Update(s => s.Scaling = value);
            OnPropertyChanged();
            Raise(nameof(ScalingDescription), nameof(ShowsLetterboxOptions));
            ScheduleReapply();
        }
    }

    public string ScalingDescription => Scaling switch
    {
        ScalingMode.Fit => "The whole photo is visible; the leftover area is filled in.",
        ScalingMode.Stretch => "Distorted to fit exactly. Rarely what you want.",
        ScalingMode.Center => "Shown at its original size, centred.",
        ScalingMode.Tile => "Repeated from the top-left corner.",
        _ => "Scaled to cover the screen, trimming the overflow. Best for most photos.",
    };

    public bool ShowsLetterboxOptions => Scaling is ScalingMode.Fit or ScalingMode.Center;

    public LetterboxStyle LetterboxStyle
    {
        get => _services.Settings.Settings.LetterboxStyle;
        set
        {
            if (_services.Settings.Settings.LetterboxStyle == value) return;

            _services.Settings.Update(s => s.LetterboxStyle = value);
            OnPropertyChanged();
            ScheduleReapply();
        }
    }

    public IReadOnlyList<LetterboxStyle> LetterboxStyles { get; } =
        [LetterboxStyle.Blur, LetterboxStyle.AverageColor, LetterboxStyle.SolidColor];

    public bool HasMultipleMonitors => Monitors.Count > 1;

    public string LayoutSummary => Monitors.Count == 1
        ? "1 display detected"
        : $"{Monitors.Count} displays detected";

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    public ICommand RefreshCommand { get; }
    public ICommand ApplyNowCommand { get; }
    public ICommand NextForMonitorCommand { get; }
    public ICommand OpenRenderedCommand { get; }

    public void Refresh()
    {
        _services.Engine.InvalidateMonitors();

        foreach (var monitor in Monitors) monitor.SetApplied(null, null);
        Monitors.Clear();

        foreach (var monitor in _services.Engine.Monitors)
            Monitors.Add(new MonitorViewModel(monitor, _services.Settings, ScheduleReapply));

        Raise(nameof(HasMultipleMonitors), nameof(LayoutSummary));
        UpdatePreviews();
        StatusMessage = LayoutSummary + ".";
    }

    /// <summary>
    /// Placement settings take effect on the desktop as soon as they change, so the screens
    /// themselves are the preview and nobody has to know about "Re-apply now". Debounced, because
    /// three chips and two drop-downs can be clicked through quickly and every render is real work.
    /// </summary>
    private void ScheduleReapply()
    {
        _reapplyTimer.Stop();
        _reapplyTimer.Start();
    }

    private async Task ReapplyAsync()
    {
        // Nothing on the desktop yet means nothing to re-fit; the first change will use the new settings.
        if (_services.Rotation.CurrentPhotos.Count == 0) return;

        StatusMessage = "Updating the wallpaper...";
        bool ok = await _services.Rotation.RefreshAsync().ConfigureAwait(true);
        StatusMessage = ok ? "Wallpaper updated with the new settings." : "Could not re-render the wallpaper.";
    }

    private void UpdatePreviews()
    {
        var current = _services.Engine.Current;
        if (current is null) return;

        foreach (var monitor in Monitors)
        {
            var assignment = current.Assignments
                .FirstOrDefault(a => string.Equals(a.Monitor.Id, monitor.Monitor.Id, StringComparison.OrdinalIgnoreCase));

            if (assignment is null)
            {
                monitor.SetApplied(null, null);
                continue;
            }

            monitor.SetApplied(assignment.Photo, LoadPreview(assignment.RenderedPath));
        }
    }

    /// <summary>Loads the rendered wallpaper at thumbnail size for the layout map.</summary>
    private static BitmapSource? LoadPreview(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 420;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not load the wallpaper preview " + path, ex);
            return null;
        }
    }

    private async Task ApplyNowAsync()
    {
        StatusMessage = "Re-rendering for the current layout...";
        bool ok = await _services.Rotation.RefreshAsync().ConfigureAwait(true);
        StatusMessage = ok ? "Wallpaper re-rendered." : "Could not re-render the wallpaper.";
    }

    private async Task NextForMonitorAsync(object? parameter)
    {
        if (parameter is not MonitorViewModel monitor) return;

        StatusMessage = $"Finding a new photo for {monitor.Name}...";

        // Reuses the rotation queue so the photo respects the user's configured sources.
        var photo = await NextPhotoAsync().ConfigureAwait(true);
        if (photo is null)
        {
            StatusMessage = "No photos available. Check your sources.";
            return;
        }

        bool ok = await _services.Rotation.ApplyToMonitorAsync(photo, monitor.Monitor).ConfigureAwait(true);
        StatusMessage = ok ? $"{monitor.Name} updated." : $"Could not update {monitor.Name}.";
    }

    private async Task<PhotoItem?> NextPhotoAsync()
    {
        // The rotation service owns the queue, so ask it for a change and read back what it chose.
        var before = _services.Rotation.CurrentPhotos.FirstOrDefault()?.Key;
        await _services.Rotation.NextAsync().ConfigureAwait(true);

        var after = _services.Rotation.CurrentPhotos.FirstOrDefault();
        return after?.Key == before ? null : after;
    }

    private void OpenRendered(object? parameter)
    {
        if (parameter is not MonitorViewModel monitor) return;

        var assignment = _services.Engine.Current?.Assignments
            .FirstOrDefault(a => string.Equals(a.Monitor.Id, monitor.Monitor.Id, StringComparison.OrdinalIgnoreCase));

        if (assignment is not null) Shell.OpenFolder(assignment.RenderedPath, selectFile: true);
    }
}
