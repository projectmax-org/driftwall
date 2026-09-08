using System.Collections.ObjectModel;
using System.Windows.Input;
using Driftwall.Core;
using Driftwall.Scheduling;
using Driftwall.Sources;

namespace Driftwall.UI.ViewModels;

/// <summary>One configured wallpaper source, edited in place.</summary>
public sealed class ConfiguredSourceViewModel : ViewModelBase
{
    private readonly SettingsStore _settings;
    private readonly Action _onChanged;

    public ConfiguredSourceViewModel(SourceSelection selection, IPhotoSource source, SettingsStore settings, Action onChanged)
    {
        Selection = selection;
        Source = source;
        _settings = settings;
        _onChanged = onChanged;
        OpenApiKeyHelpCommand = new RelayCommand(() => Shell.OpenUrl(ApiKeyHelpUrl));
    }

    public SourceSelection Selection { get; }
    public IPhotoSource Source { get; }

    public string DisplayName => Source.DisplayName;
    public string Description => Source.Description;
    public ProviderTerms Terms => ProviderTermsTable.For(Source.Id);
    public bool HasTermsWarning => Terms.Level != TermsLevel.Clear;
    public string TermsSummary => Terms.ActionRequired ?? Terms.Summary;
    public string? TermsUrl => Terms.TermsUrl;

    /// <summary>
    /// True while this source is switched on but cannot run for want of a key. Without this the
    /// source just fails quietly in the background, and the only sign is a warning in the log.
    /// </summary>
    public bool NeedsApiKey => Source.RequiresApiKey && string.IsNullOrWhiteSpace(_settings.GetApiKey(Source.Id));

    public string? ApiKeyHelpUrl => Source.ApiKeyHelpUrl;

    public ICommand OpenApiKeyHelpCommand { get; }

    /// <summary>Called when a key is added or removed under API keys.</summary>
    public void RefreshApiKeyState() => Raise(nameof(NeedsApiKey));

    public IReadOnlyList<SourceCategory> Categories => Source.Categories;
    public bool SupportsSearch => Source.SupportsSearch;
    public bool SupportsCategories => Source.SupportsCategories;
    public bool SupportsTimeRange => Source.SupportedTimeRanges.Count > 0;

    /// <summary>
    /// The windows this particular source honours. Offering a shared day/week/month/year list would
    /// let the user pick "past month" from Bing, whose archive is only about eight days deep.
    /// </summary>
    public IReadOnlyList<string> AvailableTimeRanges => Source.SupportedTimeRanges;

    public IReadOnlyList<QueryMode> AvailableModes
    {
        get
        {
            var modes = new List<QueryMode>();
            if (Source.SupportsTop) modes.Add(QueryMode.Top);
            if (Source.SupportsCategories) modes.Add(QueryMode.Category);
            if (Source.SupportsSearch) modes.Add(QueryMode.Search);
            modes.Add(QueryMode.Random);
            return modes;
        }
    }

    public bool Enabled
    {
        get => Selection.Enabled;
        set
        {
            if (Selection.Enabled == value) return;
            Selection.Enabled = value;
            Commit();
        }
    }

    public QueryMode Mode
    {
        get => Selection.Mode;
        set
        {
            if (Selection.Mode == value) return;
            Selection.Mode = value;
            Commit();
            Raise(nameof(IsSearchMode), nameof(IsCategoryMode), nameof(Summary));
        }
    }

    public bool IsSearchMode => Selection.Mode == QueryMode.Search;
    public bool IsCategoryMode => Selection.Mode == QueryMode.Category;

    public string? Keyword
    {
        get => Selection.Keyword;
        set
        {
            if (Selection.Keyword == value) return;
            Selection.Keyword = value;
            Commit();
            Raise(nameof(Summary));
        }
    }

    public SourceCategory? SelectedCategory
    {
        get => Categories.FirstOrDefault(c => c.Id == Selection.Category);
        set
        {
            if (Selection.Category == value?.Id) return;
            Selection.Category = value?.Id;
            Commit();
            Raise(nameof(Summary));
        }
    }

    public string TimeRange
    {
        get
        {
            // A stored range the source no longer offers would leave the ComboBox blank; fall back
            // to something it does support.
            var supported = Source.SupportedTimeRanges;
            if (supported.Count == 0) return Selection.TimeRange;

            return supported.Contains(Selection.TimeRange, StringComparer.OrdinalIgnoreCase)
                ? Selection.TimeRange
                : supported[Math.Min(1, supported.Count - 1)];
        }
        set
        {
            if (value is null || Selection.TimeRange == value) return;
            Selection.TimeRange = value;
            Commit();
        }
    }

    public int Weight
    {
        get => Selection.Weight;
        set
        {
            int clamped = Math.Clamp(value, 1, 10);
            if (Selection.Weight == clamped) return;
            Selection.Weight = clamped;
            Commit();
            Raise(nameof(WeightLabel));
        }
    }

    public string WeightLabel => Weight switch
    {
        1 => "Occasionally",
        <= 3 => "Sometimes",
        <= 6 => "Often",
        _ => "Mostly",
    };

    public string Summary => Selection.DisplayLabel;

    /// <summary>Folders for the local source, one per line. Empty for every other source.</summary>
    public string FolderList
    {
        get => Selection.Options.GetValueOrDefault("folders", string.Empty).Replace("|", Environment.NewLine);
        set
        {
            var joined = string.Join('|', (value ?? string.Empty)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            Selection.Options["folders"] = joined;
            Commit();
            OnPropertyChanged();
        }
    }

    /// <summary>Feed URLs for the RSS source, one per line.</summary>
    public string FeedList
    {
        get => Selection.Options.GetValueOrDefault("feeds", string.Empty).Replace(",", Environment.NewLine);
        set
        {
            var joined = string.Join('\n', (value ?? string.Empty)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            Selection.Options["feeds"] = joined;
            Commit();
            OnPropertyChanged();
        }
    }

    public bool IsLocalFolder => Source.Id == "local";
    public bool IsRssFeed => Source.Id == "rss";

    private void Commit()
    {
        _settings.Touch();
        _onChanged();
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>An API key field for one provider.</summary>
public sealed class ApiKeyViewModel : ViewModelBase
{
    private readonly SettingsStore _settings;
    private readonly Action _onChanged;
    private string _value;

    public ApiKeyViewModel(IPhotoSource source, SettingsStore settings, Action onChanged)
    {
        Source = source;
        _settings = settings;
        _onChanged = onChanged;
        _value = settings.GetApiKey(source.Id) ?? string.Empty;
    }

    public IPhotoSource Source { get; }
    public string DisplayName => Source.DisplayName;
    public string? HelpUrl => Source.ApiKeyHelpUrl;
    public bool IsOptional => !Source.RequiresApiKey;

    public string Hint => IsOptional
        ? "Optional — raises the rate limit"
        : "Required";

    public string Value
    {
        get => _value;
        set
        {
            if (!Set(ref _value, value)) return;
            _settings.SetApiKey(Source.Id, value);
            Raise(nameof(IsSet));
            _onChanged();
        }
    }

    public bool IsSet => !string.IsNullOrWhiteSpace(_value);

    public ICommand OpenHelpCommand => new RelayCommand(() => Shell.OpenUrl(HelpUrl));
}

/// <summary>Everything on the settings tab.</summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private string? _statusMessage;
    private IPhotoSource? _sourceToAdd;

    public SettingsViewModel(AppServices services)
    {
        _services = services;

        AddSourceCommand = new RelayCommand(AddSource, () => _sourceToAdd is not null);
        RemoveSourceCommand = new RelayCommand(RemoveSource);
        BrowseFolderCommand = new RelayCommand(BrowseFolder);
        ClearCacheCommand = new RelayCommand(ClearCache);
        OpenDataFolderCommand = new RelayCommand(() => Shell.OpenFolder(Paths.DataDirectory));
        OpenLogCommand = new RelayCommand(() => Shell.OpenFolder(Log.LogPath, selectFile: true));
        OpenWebsiteCommand = new RelayCommand(() => Shell.OpenUrl(WebsiteUrl));
        OpenSourceCodeCommand = new RelayCommand(() => Shell.OpenUrl(RepositoryUrl));

        CheckForUpdatesCommand = new AsyncRelayCommand(() => services.Updates.CheckAsync(), () => !IsUpdateBusy);
        InstallUpdateCommand = new AsyncRelayCommand(() => services.Updates.InstallLatestAsync(relaunchVisible: true), () => !IsUpdateBusy);
        OpenReleaseNotesCommand = new RelayCommand(() => Shell.OpenUrl(services.Updates.Available?.ReleaseUrl ?? UpdateService.ReleasesUrl));
        services.Updates.Changed += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            Raise(nameof(UpdateStatus), nameof(UpdateDetail), nameof(IsUpdateAvailable), nameof(IsUpdateBusy),
                  nameof(ShowUpdateProgress), nameof(UpdateProgress), nameof(InstallButtonLabel));
            (CheckForUpdatesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (InstallUpdateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }));
        ResetSourcesCommand = new RelayCommand(ResetSources);

        ApiKeys = new ObservableCollection<ApiKeyViewModel>(
            services.Sources.All
                .Where(s => s.RequiresApiKey || s.ApiKeyHelpUrl is not null)
                .Select(s => new ApiKeyViewModel(s, services.Settings, OnApiKeyChanged)));

        RebuildSources();
        RefreshCacheSize();
    }

    private AppSettings S => _services.Settings.Settings;

    public ObservableCollection<ConfiguredSourceViewModel> Sources { get; } = new();

    public ObservableCollection<ApiKeyViewModel> ApiKeys { get; }

    public IReadOnlyList<IPhotoSource> AddableSources => _services.Sources.All;

    public IPhotoSource? SourceToAdd
    {
        get => _sourceToAdd;
        set
        {
            if (!Set(ref _sourceToAdd, value)) return;
            (AddSourceCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    // ---------------- rotation ----------------

    public bool RotationEnabled
    {
        get => S.RotationEnabled;
        set
        {
            if (S.RotationEnabled == value) return;
            _services.Settings.Update(s => s.RotationEnabled = value);
            _services.Scheduler.Rearm();
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<int> IntervalPresets { get; } = [5, 10, 15, 30, 60, 120, 360, 720, 1440];

    public int IntervalMinutes
    {
        get => S.IntervalMinutes;
        set
        {
            int clamped = Math.Max(0, value);
            if (S.IntervalMinutes == clamped) return;

            _services.Settings.Update(s => s.IntervalMinutes = clamped);
            _services.Scheduler.Rearm();
            Raise(nameof(IntervalMinutes), nameof(IntervalLabel));
        }
    }

    public string IntervalLabel => MinutesToTextConverter.Format(IntervalMinutes);

    public bool ChangeOnStartup
    {
        get => S.ChangeOnStartup;
        set { _services.Settings.Update(s => s.ChangeOnStartup = value); OnPropertyChanged(); }
    }

    public bool ChangeOnShutdown
    {
        get => S.ChangeOnShutdown;
        set { _services.Settings.Update(s => s.ChangeOnShutdown = value); OnPropertyChanged(); }
    }

    public bool ChangeOnUnlock
    {
        get => S.ChangeOnUnlock;
        set { _services.Settings.Update(s => s.ChangeOnUnlock = value); OnPropertyChanged(); }
    }

    public bool Shuffle
    {
        get => S.Shuffle;
        set { _services.Settings.Update(s => s.Shuffle = value); OnPropertyChanged(); }
    }

    public int NoRepeatWindow
    {
        get => S.NoRepeatWindow;
        set { _services.Settings.Update(s => s.NoRepeatWindow = Math.Clamp(value, 0, 5000)); Raise(nameof(NoRepeatWindow), nameof(NoRepeatLabel)); }
    }

    public string NoRepeatLabel => NoRepeatWindow == 0
        ? "Repeats allowed"
        : $"Don't repeat within {NoRepeatWindow} photos";

    // ---------------- rendering ----------------
    // Fitting, letterboxing and multi-display placement are edited on the Displays tab, next to the
    // layout map, so they are deliberately not repeated here.

    public int OutputQuality
    {
        get => S.OutputQuality;
        set { _services.Settings.Update(s => s.OutputQuality = Math.Clamp(value, 60, 100)); Raise(nameof(OutputQuality), nameof(OutputQualityLabel)); }
    }

    public string OutputQualityLabel => OutputQuality switch
    {
        >= 96 => "Maximum — largest files",
        >= 88 => "High — recommended",
        >= 78 => "Balanced",
        _ => "Small files",
    };

    public bool RejectLowResolution
    {
        get => S.RejectLowResolution;
        set { _services.Settings.Update(s => s.RejectLowResolution = value); OnPropertyChanged(); }
    }

    public double MinResolutionRatio
    {
        get => S.MinResolutionRatio;
        set { _services.Settings.Update(s => s.MinResolutionRatio = Math.Clamp(value, 0.1, 2.0)); Raise(nameof(MinResolutionRatio), nameof(MinResolutionLabel)); }
    }

    public string MinResolutionLabel => $"At least {MinResolutionRatio * 100:0}% of your screen's width";

    // ---------------- behaviour ----------------

    public bool StartWithWindows
    {
        // The registry Run key is the truth, not the saved setting: the installer can create the
        // entry, and the user can remove it from Task Manager's Startup tab, and the switch must
        // reflect either without the app having been told.
        get => StartupManager.IsEnabled();
        set
        {
            if (!StartupManager.SetEnabled(value))
            {
                StatusMessage = "Could not change the Windows startup setting.";
                OnPropertyChanged();
                return;
            }

            _services.Settings.Update(s => s.StartWithWindows = value);
            OnPropertyChanged();
        }
    }

    public bool StartMinimized
    {
        get => S.StartMinimized;
        set { _services.Settings.Update(s => s.StartMinimized = value); OnPropertyChanged(); }
    }

    public bool ShowTrayIcon
    {
        get => S.ShowTrayIcon;
        set
        {
            _services.Settings.Update(s => s.ShowTrayIcon = value);
            _services.Tray.SetVisible(value);
            OnPropertyChanged();
        }
    }

    public bool CloseToTray
    {
        get => S.CloseToTray;
        set { _services.Settings.Update(s => s.CloseToTray = value); OnPropertyChanged(); }
    }

    public bool PauseOnFullscreenApp
    {
        get => S.PauseOnFullscreenApp;
        set { _services.Settings.Update(s => s.PauseOnFullscreenApp = value); OnPropertyChanged(); }
    }

    public bool PauseOnBattery
    {
        get => S.PauseOnBattery;
        set { _services.Settings.Update(s => s.PauseOnBattery = value); OnPropertyChanged(); }
    }

    public bool PauseOnMeteredNetwork
    {
        get => S.PauseOnMeteredNetwork;
        set { _services.Settings.Update(s => s.PauseOnMeteredNetwork = value); OnPropertyChanged(); }
    }

    public bool AggressiveMemoryTrim
    {
        get => S.AggressiveMemoryTrim;
        set
        {
            _services.Settings.Update(s => s.AggressiveMemoryTrim = value);
            MemoryTrimmer.Enabled = value;
            OnPropertyChanged();
        }
    }

    public int PrefetchCount
    {
        get => S.PrefetchCount;
        set { _services.Settings.Update(s => s.PrefetchCount = Math.Clamp(value, 0, 10)); Raise(nameof(PrefetchCount), nameof(PrefetchLabel)); }
    }

    public string PrefetchLabel => PrefetchCount == 0
        ? "Download only when needed"
        : $"Keep {PrefetchCount} photo{(PrefetchCount == 1 ? "" : "s")} ready in advance";

    // ---------------- appearance ----------------

    public IReadOnlyList<AppTheme> Themes { get; } = [AppTheme.System, AppTheme.Dark, AppTheme.Light];

    public AppTheme Theme
    {
        get => S.Theme;
        set
        {
            if (S.Theme == value) return;
            _services.Settings.Update(s => s.Theme = value);
            ThemeManager.Apply(S);
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<string> AccentColors { get; } =
        ["#FF7C6CF6", "#FF4C8DFF", "#FF19B7A6", "#FF3FCF8E", "#FFF0B347", "#FFF0556B", "#FFE060C8", "#FF8E8E9E"];

    public string AccentColor
    {
        get => S.AccentColor;
        set
        {
            if (S.AccentColor == value) return;
            _services.Settings.Update(s => s.AccentColor = value);
            ThemeManager.ApplyAccent(value);
            OnPropertyChanged();
        }
    }

    public bool ShowAttributionOverlay
    {
        get => S.ShowAttributionOverlay;
        set { _services.Settings.Update(s => s.ShowAttributionOverlay = value); OnPropertyChanged(); }
    }

    // ---------------- storage ----------------

    public int CacheSizeMegabytes
    {
        get => S.CacheSizeMegabytes;
        set
        {
            _services.Settings.Update(s => s.CacheSizeMegabytes = Math.Clamp(value, 64, 65536));
            _services.Cache.MaxBytes = S.CacheSizeMegabytes * 1024L * 1024L;
            Raise(nameof(CacheSizeMegabytes), nameof(CacheLimitLabel));
        }
    }

    public string CacheLimitLabel => $"Keep up to {CacheSizeMegabytes} MB of downloaded photos";

    private string _cacheUsage = "-";

    public string CacheUsage
    {
        get => _cacheUsage;
        private set => Set(ref _cacheUsage, value);
    }

    public string DataFolder => Paths.DataDirectory;

    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    public ICommand AddSourceCommand { get; }
    public ICommand RemoveSourceCommand { get; }
    public ICommand BrowseFolderCommand { get; }
    public ICommand ClearCacheCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenLogCommand { get; }
    public ICommand OpenWebsiteCommand { get; }
    public ICommand OpenSourceCodeCommand { get; }

    // ---------------- updates ----------------

    public ICommand CheckForUpdatesCommand { get; }
    public ICommand InstallUpdateCommand { get; }
    public ICommand OpenReleaseNotesCommand { get; }

    public bool AutoUpdate
    {
        get => S.AutoUpdate;
        set { _services.Settings.Update(s => s.AutoUpdate = value); OnPropertyChanged(); }
    }

    private UpdateService Updates => _services.Updates;

    public string UpdateStatus => Updates.State switch
    {
        UpdateState.Idle => $"Driftwall {UpdateService.CurrentVersion}",
        _ => Updates.Message ?? $"Driftwall {UpdateService.CurrentVersion}",
    };

    public string UpdateDetail
    {
        get
        {
            var last = Updates.LastChecked;
            var when = last is null ? "Not checked yet." : "Last checked " + Describe(DateTimeOffset.UtcNow - last.Value) + ".";
            return when + " Releases come from GitHub and are verified against their published checksums.";
        }
    }

    public bool IsUpdateAvailable => Updates.State is UpdateState.Available or UpdateState.Ready;
    public bool IsUpdateBusy => Updates.State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Installing;
    public bool ShowUpdateProgress => Updates.State == UpdateState.Downloading;
    public double UpdateProgress => Updates.Progress * 100;

    public string InstallButtonLabel => Updates.State == UpdateState.Ready
        ? "Restart to update"
        : "Install " + (Updates.Available?.Version.ToString() ?? "update");

    private static string Describe(TimeSpan ago) => ago.TotalMinutes < 1
        ? "just now"
        : ago.TotalHours < 1
            ? $"{(int)ago.TotalMinutes} min ago"
            : ago.TotalDays < 1
                ? $"{(int)ago.TotalHours} h ago"
                : $"{(int)ago.TotalDays} d ago";

    /// <summary>Driftwall is a Project Max application; these are the project's public places.</summary>
    public const string WebsiteUrl = "https://projectmax-org.github.io/driftwall/";
    public const string RepositoryUrl = "https://github.com/projectmax-org/driftwall";
    public ICommand ResetSourcesCommand { get; }

    // ---------------- actions ----------------

    private void RebuildSources()
    {
        Sources.Clear();

        foreach (var selection in S.Sources)
        {
            var source = _services.Sources.Find(selection.ProviderId);
            if (source is null) continue;

            Sources.Add(new ConfiguredSourceViewModel(selection, source, _services.Settings, OnSourcesEdited));
        }
    }

    private void OnSourcesEdited() => _services.Rotation.ResetQueue();

    private void OnApiKeyChanged()
    {
        foreach (var source in Sources) source.RefreshApiKeyState();

        // A source that was waiting on its key can start contributing straight away.
        _services.Rotation.ResetQueue();
    }

    private void AddSource()
    {
        if (_sourceToAdd is null) return;

        var terms = ProviderTermsTable.For(_sourceToAdd.Id);
        var selection = new SourceSelection
        {
            ProviderId = _sourceToAdd.Id,
            Mode = _sourceToAdd.SupportsTop ? QueryMode.Top : QueryMode.Search,
            Category = _sourceToAdd.Categories.FirstOrDefault()?.Id,
            // Anything with restricted terms is added switched off, so it cannot be used by accident.
            Enabled = terms.Level != TermsLevel.Restricted,
        };

        _services.Settings.Update(s => s.Sources.Add(selection));
        RebuildSources();
        OnSourcesEdited();

        StatusMessage = terms.Level == TermsLevel.Restricted
            ? $"{_sourceToAdd.DisplayName} was added but left switched off — {terms.ActionRequired}"
            : $"Added {_sourceToAdd.DisplayName}.";
    }

    private void RemoveSource(object? parameter)
    {
        if (parameter is not ConfiguredSourceViewModel item) return;

        _services.Settings.Update(s => s.Sources.Remove(item.Selection));
        Sources.Remove(item);
        OnSourcesEdited();
        StatusMessage = $"Removed {item.DisplayName}.";
    }

    private void BrowseFolder(object? parameter)
    {
        if (parameter is not ConfiguredSourceViewModel item) return;

        var folder = Shell.PickFolder("Choose a folder of photos");
        if (folder is null) return;

        var existing = item.FolderList;
        item.FolderList = string.IsNullOrWhiteSpace(existing)
            ? folder
            : existing.TrimEnd() + Environment.NewLine + folder;
    }

    private void ResetSources()
    {
        _services.Settings.Update(s =>
        {
            s.Sources.Clear();
            s.Sources.AddRange(AppSettings.CreateDefault().Sources);
        });

        RebuildSources();
        OnSourcesEdited();
        StatusMessage = "Sources reset to the defaults.";
    }

    private void ClearCache()
    {
        _services.Cache.Clear();
        RefreshCacheSize();
        StatusMessage = "Cache cleared.";
    }

    /// <summary>Called when rotation is paused or resumed from the header or the tray menu.</summary>
    public void OnPauseChangedExternally() => OnPropertyChanged(nameof(RotationEnabled));

    /// <summary>Called when the Settings page comes into view, so "last checked" reads right.</summary>
    public void RefreshUpdateStatus() =>
        Raise(nameof(UpdateStatus), nameof(UpdateDetail), nameof(IsUpdateAvailable), nameof(InstallButtonLabel));

    public void RefreshCacheSize()
    {
        // Off the UI thread: this walks two directories that can hold thousands of files.
        _ = Task.Run(() =>
        {
            long bytes = ImageCache.DirectorySize(Paths.ImageCacheDirectory)
                       + ImageCache.DirectorySize(Paths.ThumbnailCacheDirectory)
                       + ImageCache.DirectorySize(Paths.SavedDirectory);

            var text = BytesToTextConverter.Format(bytes) + " used";
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => CacheUsage = text));
        });
    }
}
