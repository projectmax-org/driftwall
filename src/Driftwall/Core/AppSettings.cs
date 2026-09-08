using System.Text.Json.Serialization;

namespace Driftwall.Core;

public enum ScalingMode
{
    /// <summary>Scale to cover the screen, cropping the overflow. Best default.</summary>
    Fill = 0,
    /// <summary>Scale so the whole photo is visible; empty area filled per <see cref="AppSettings.LetterboxStyle"/>.</summary>
    Fit = 1,
    /// <summary>Distort to exactly match the screen. Rarely wanted.</summary>
    Stretch = 2,
    /// <summary>No scaling; original pixels centred.</summary>
    Center = 3,
    /// <summary>Repeat the image from the top-left.</summary>
    Tile = 4,
}

public enum LetterboxStyle
{
    /// <summary>Blurred, darkened copy of the photo behind it. Looks best.</summary>
    Blur = 0,
    /// <summary>Flat colour from <see cref="AppSettings.LetterboxColor"/>.</summary>
    SolidColor = 1,
    /// <summary>Average colour of the photo.</summary>
    AverageColor = 2,
}

public enum MultiMonitorMode
{
    /// <summary>Every monitor gets its own photo, rendered at that monitor's exact resolution.</summary>
    DifferentPerMonitor = 0,
    /// <summary>All monitors show the same photo, each rendered for its own resolution.</summary>
    SameOnAllMonitors = 1,
    /// <summary>One photo stretched across the whole virtual desktop.</summary>
    SpanAcrossMonitors = 2,
}

public enum AppTheme
{
    System = 0,
    Dark = 1,
    Light = 2,
}

/// <summary>One configured "place to get photos from" that the user has enabled.</summary>
public sealed class SourceSelection
{
    /// <summary>Matches <see cref="Sources.IPhotoSource.Id"/>.</summary>
    public string ProviderId { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public QueryMode Mode { get; set; } = QueryMode.Top;

    public string? Keyword { get; set; }

    public string? Category { get; set; }

    /// <summary>day | week | month | year | all — used for "top" ordering where supported.</summary>
    public string TimeRange { get; set; } = "month";

    /// <summary>Relative pick weight against other enabled selections (1–10).</summary>
    public int Weight { get; set; } = 1;

    /// <summary>Free-form per-source extras (local folder path, subreddit list, feed url, collection id).</summary>
    public Dictionary<string, string> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public string DisplayLabel => Mode switch
    {
        QueryMode.Search => $"\"{Keyword}\"",
        QueryMode.Category => Category ?? "Category",
        QueryMode.Random => "Random",
        _ => "Top",
    };

    public SourceSelection Clone() => new()
    {
        ProviderId = ProviderId,
        Enabled = Enabled,
        Mode = Mode,
        Keyword = Keyword,
        Category = Category,
        TimeRange = TimeRange,
        Weight = Weight,
        Options = new Dictionary<string, string>(Options, StringComparer.OrdinalIgnoreCase),
    };
}

/// <summary>Per-monitor overrides. Keyed by the stable Windows monitor device id.</summary>
public sealed class MonitorSettings
{
    public string MonitorId { get; set; } = "";
    public string? FriendlyName { get; set; }
    public bool Enabled { get; set; } = true;
    public ScalingMode? ScalingOverride { get; set; }

    /// <summary>Optional: restrict this monitor to one source selection index. Null = use the shared pool.</summary>
    public string? PinnedCollectionId { get; set; }
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    // ---------- Sources ----------
    public List<SourceSelection> Sources { get; set; } = new();

    /// <summary>Provider id -> API key. Stored DPAPI-encrypted on disk by SettingsStore.</summary>
    public Dictionary<string, string> ApiKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // ---------- Rotation ----------
    public bool RotationEnabled { get; set; } = true;

    /// <summary>Minutes between changes. 0 = never (manual only).</summary>
    public int IntervalMinutes { get; set; } = 30;

    public bool ChangeOnStartup { get; set; } = true;
    public bool ChangeOnShutdown { get; set; }
    public bool ChangeOnUnlock { get; set; }

    /// <summary>Shuffle the queue instead of walking it in fetch order.</summary>
    public bool Shuffle { get; set; } = true;

    /// <summary>Don't repeat a photo until this many others have been shown.</summary>
    public int NoRepeatWindow { get; set; } = 200;

    // ---------- Rendering ----------
    public ScalingMode Scaling { get; set; } = ScalingMode.Fill;
    public LetterboxStyle LetterboxStyle { get; set; } = LetterboxStyle.Blur;
    public string LetterboxColor { get; set; } = "#FF101014";
    public MultiMonitorMode MultiMonitor { get; set; } = MultiMonitorMode.DifferentPerMonitor;
    public List<MonitorSettings> Monitors { get; set; } = new();

    /// <summary>JPEG quality for the rendered wallpaper file (75–100).</summary>
    public int OutputQuality { get; set; } = 92;

    /// <summary>Skip photos whose native resolution is far below the target monitor.</summary>
    public bool RejectLowResolution { get; set; } = true;

    /// <summary>Fraction of the monitor's width a photo must reach to be accepted (0.5 = half).</summary>
    public double MinResolutionRatio { get; set; } = 0.65;

    // ---------- Behaviour / performance ----------
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; } = true;
    public bool ShowTrayIcon { get; set; } = true;
    public bool CloseToTray { get; set; } = true;

    /// <summary>Suspend all network + rendering work while a game or fullscreen app is in front.</summary>
    public bool PauseOnFullscreenApp { get; set; } = true;

    /// <summary>Suspend rotation while on battery.</summary>
    public bool PauseOnBattery { get; set; }

    /// <summary>Suspend rotation while a metered network connection is in use.</summary>
    public bool PauseOnMeteredNetwork { get; set; } = true;

    /// <summary>How many photos to pre-download ahead of the current one.</summary>
    public int PrefetchCount { get; set; } = 2;

    /// <summary>Upper bound for the on-disk image cache.</summary>
    public int CacheSizeMegabytes { get; set; } = 1024;

    /// <summary>Release cached bitmaps and trim the working set when the window is hidden.</summary>
    public bool AggressiveMemoryTrim { get; set; } = true;

    // ---------- UI ----------
    public AppTheme Theme { get; set; } = AppTheme.System;
    public string AccentColor { get; set; } = "#FF7C6CF6";
    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 760;
    public bool ShowAttributionOverlay { get; set; } = true;
    public bool HasCompletedFirstRun { get; set; }

    /// <summary>Set once the "still running in the notification area" hint has been shown.</summary>
    public bool HasShownTrayHint { get; set; }

    public AppSettings Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this, DriftwallJson.Options);
        return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, DriftwallJson.Options) ?? new AppSettings();
    }

    public static AppSettings CreateDefault()
    {
        var s = new AppSettings();
        // Sources that work with zero configuration, so the app is useful on first launch.
        s.Sources.Add(new SourceSelection { ProviderId = "wallhaven", Mode = QueryMode.Top, TimeRange = "month", Weight = 3 });
        s.Sources.Add(new SourceSelection { ProviderId = "bing", Mode = QueryMode.Top, Weight = 1 });
        return s;
    }
}
