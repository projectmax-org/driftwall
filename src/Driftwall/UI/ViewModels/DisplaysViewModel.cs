using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Driftwall.Core;
using Driftwall.Wallpaper;

namespace Driftwall.UI.ViewModels;

/// <summary>One entry in a display's own scaling list. A null mode follows the setting for every display.</summary>
public sealed record ScalingChoice(ScalingMode? Mode, string Name);

/// <summary>One monitor, with what is on it and the settings that apply only to it.</summary>
public sealed class MonitorViewModel : ViewModelBase
{
    /// <summary>
    /// A display's own scaling list. "Same as above" is a real entry rather than an empty box, so a
    /// display given a mode of its own can be put back to following the setting above.
    /// </summary>
    private static readonly IReadOnlyList<ScalingChoice> AllScalingChoices =
    [
        new(null, "Same as above"),
        new(ScalingMode.Fill, "Fill"),
        new(ScalingMode.Fit, "Fit"),
        new(ScalingMode.Stretch, "Stretch"),
        new(ScalingMode.Center, "Center"),
        new(ScalingMode.Tile, "Tile"),
    ];

    private readonly SettingsStore _settings;
    private readonly Action _changed;
    private readonly Func<bool> _anyIncluded;
    private BitmapSource? _preview;
    private PhotoItem? _photo;

    /// <param name="anyIncluded">Whether any connected display is switched on for rotation.</param>
    public MonitorViewModel(MonitorInfo monitor, SettingsStore settings, Action changed, Func<bool> anyIncluded)
    {
        Monitor = monitor;
        _settings = settings;
        _changed = changed;
        _anyIncluded = anyIncluded;
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

    /// <summary>
    /// True when rotation passes this display by. With every display switched off none is: the
    /// wallpaper then goes on all of them, as there would otherwise be nothing to change.
    /// </summary>
    public bool IsLeftAlone => !Enabled && _anyIncluded();

    /// <summary>Called when any display is switched on or off, since that can change this one's answer.</summary>
    public void RaiseIsLeftAlone() => Raise(nameof(IsLeftAlone));

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
            Raise(nameof(SelectedScaling));
            _changed();
        }
    }

    public IReadOnlyList<ScalingChoice> ScalingChoices => AllScalingChoices;

    /// <summary>The entry in <see cref="ScalingChoices"/> for <see cref="ScalingOverride"/>.</summary>
    public ScalingChoice? SelectedScaling
    {
        get => AllScalingChoices.FirstOrDefault(c => c.Mode == ScalingOverride);
        set
        {
            // The list hands back null while its row is being taken down, which is no choice at all.
            if (value is not null) ScalingOverride = value.Mode;
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

    /// <summary>Set by a switch to a different photo on each display, for the re-apply that follows.</summary>
    private bool _spreadOutOnReapply;

    public DisplaysViewModel(AppServices services)
    {
        _services = services;

        RefreshCommand = new RelayCommand(_ => Refresh(announce: true));
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

            // Coming from a shared or spanned photo, a re-render alone would leave that one photo on
            // every display; each of them is given its own instead.
            if (value == MultiMonitorMode.DifferentPerMonitor) _spreadOutOnReapply = true;
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

    /// <summary>
    /// Re-reads the displays. Only a scan the user asked for gets a status line: the header already
    /// says how many displays there are, so repeating it on every visit to the page says nothing.
    /// </summary>
    public void Refresh(bool announce = false)
    {
        _services.Engine.InvalidateMonitors();

        foreach (var monitor in Monitors) monitor.SetApplied(null, null);
        Monitors.Clear();

        foreach (var monitor in _services.Engine.Monitors)
            Monitors.Add(new MonitorViewModel(monitor, _services.Settings, OnMonitorChanged, () => Monitors.Any(m => m.Enabled)));

        Raise(nameof(HasMultipleMonitors), nameof(LayoutSummary));
        UpdatePreviews();
        StatusMessage = announce ? $"Scanned again: {LayoutSummary.ToLowerInvariant()}." : null;
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

    private void OnMonitorChanged()
    {
        // Whether a display is left alone depends on the others too, so every row is asked again.
        foreach (var monitor in Monitors) monitor.RaiseIsLeftAlone();
        ScheduleReapply();
    }

    private async Task ReapplyAsync()
    {
        // Nothing on the desktop yet means nothing to re-fit; the first change will use the new settings.
        if (_services.Rotation.CurrentPhotos.Count == 0) return;

        StatusMessage = "Updating the wallpaper...";
        bool spreadOut = _spreadOutOnReapply;
        _spreadOutOnReapply = false;
        bool ok = spreadOut
            ? await _services.Rotation.SpreadOutAsync().ConfigureAwait(true)
            : await _services.Rotation.RefreshAsync().ConfigureAwait(true);
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

        if (monitor.IsLeftAlone)
        {
            StatusMessage = $"{monitor.Name} is not included in rotation. Switch it on under Per display first.";
            return;
        }

        StatusMessage = $"Finding a new photo for {monitor.Name}...";

        // Reuses the rotation queue so the photo respects the user's configured sources. The service
        // reports why when it fails, so the line here only says which display it was.
        bool ok = await _services.Rotation.NextForMonitorAsync(monitor.Monitor).ConfigureAwait(true);
        bool shared = MultiMonitor != MultiMonitorMode.DifferentPerMonitor && Monitors.Count(m => !m.IsLeftAlone) > 1;
        StatusMessage = !ok ? $"Could not update {monitor.Name}."
            : shared ? "Every display updated, as they share one photo."
            : $"{monitor.Name} updated.";
    }

    private void OpenRendered(object? parameter)
    {
        if (parameter is not MonitorViewModel monitor) return;

        var assignment = _services.Engine.Current?.Assignments
            .FirstOrDefault(a => string.Equals(a.Monitor.Id, monitor.Monitor.Id, StringComparison.OrdinalIgnoreCase));

        if (assignment is not null) Shell.OpenFolder(assignment.RenderedPath, selectFile: true);
    }
}
