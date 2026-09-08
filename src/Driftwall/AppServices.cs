using Driftwall.Core;
using Driftwall.Scheduling;
using Driftwall.Sources;
using Driftwall.Tray;
using Driftwall.Wallpaper;

namespace Driftwall;

/// <summary>
/// Composition root. Everything long-lived is created here, in dependency order, and disposed in
/// reverse. A hand-rolled container rather than a DI framework: there are nine objects and the
/// wiring is easier to read than a registration list.
/// </summary>
public sealed class AppServices : IDisposable
{
    private bool _disposed;

    public AppServices()
    {
        Settings = new SettingsStore();
        Collections = new CollectionsStore();

        Cache = new ImageCache(Net.Client)
        {
            MaxBytes = Settings.Settings.CacheSizeMegabytes * 1024L * 1024L,
        };

        Sources = new SourceRegistry(Collections);
        Engine = new WallpaperEngine(Cache);
        Rotation = new RotationService(Settings, Sources, Cache, Engine);
        Scheduler = new RotationScheduler(Settings, Rotation, Engine);
        Tray = new TrayIcon();

        MemoryTrimmer.Enabled = Settings.Settings.AggressiveMemoryTrim;

        Settings.Changed += OnSettingsChanged;
        Engine.Applied += (_, application) => UI.CreditToast.ShowFor(application, Settings.Settings);
    }

    public static AppServices Current { get; private set; } = null!;

    public SettingsStore Settings { get; }
    public CollectionsStore Collections { get; }
    public ImageCache Cache { get; }
    public SourceRegistry Sources { get; }
    public WallpaperEngine Engine { get; }
    public RotationService Rotation { get; }
    public RotationScheduler Scheduler { get; }
    public TrayIcon Tray { get; }

    public static AppServices Initialize()
    {
        Current = new AppServices();
        return Current;
    }

    /// <summary>Keeps the pieces that cache settings values in step when the user changes them.</summary>
    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        Cache.MaxBytes = settings.CacheSizeMegabytes * 1024L * 1024L;
        MemoryTrimmer.Enabled = settings.AggressiveMemoryTrim;
        Tray.SetVisible(settings.ShowTrayIcon);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Settings.Changed -= OnSettingsChanged;

        Tray.Dispose();
        Scheduler.Dispose();
        Rotation.Dispose();
        Collections.Dispose();
        Settings.Dispose();

        RenderThread.Shutdown();
    }
}
