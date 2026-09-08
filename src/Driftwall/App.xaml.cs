using System.Windows;
using System.Windows.Threading;
using Driftwall.Core;
using Driftwall.Scheduling;
using Driftwall.Tray;
using Driftwall.UI;
using Driftwall.UI.ViewModels;

namespace Driftwall;

public partial class App : Application
{
    private SingleInstance? _instance;
    private AppServices? _services;
    private MainViewModel? _viewModel;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A second launch should surface the running copy rather than start a rival tray icon.
        _instance = SingleInstance.Acquire();
        if (!_instance.IsFirstInstance)
        {
            _instance.SignalFirstInstance();
            _instance.Dispose();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        Log.Info("Driftwall starting.");

        try
        {
            _services = AppServices.Initialize();
        }
        catch (Exception ex)
        {
            Log.Error("Fatal error during start-up.", ex);
            MessageBox.Show(
                "Driftwall could not start.\n\n" + ex.Message,
                "Driftwall", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        ThemeManager.Apply(_services.Settings.Settings);
        StartupManager.RepairIfNeeded();

        // The first run applies the "start with Windows" default; the installer usually has done
        // it already, and a portable copy gets the same treatment because that is what the setting
        // says. From then on the Run key belongs to the user (and Task Manager's Startup tab).
        if (!_services.Settings.Settings.HasCompletedFirstRun && _services.Settings.Settings.StartWithWindows && !StartupManager.IsEnabled())
            StartupManager.SetEnabled(true);

        _viewModel = new MainViewModel(_services);
        _window = new MainWindow(_services, _viewModel);
        AttachWindow(_window);

        ConfigureTray();
        ConfigureUpdates();
        _instance.ActivationRequested += (_, _) => Dispatcher.BeginInvoke(new Action(ShowWindow));
        _instance.StartListening();

        var settings = _services.Settings.Settings;
        bool launchedAtSignIn = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        // An update the user started from Settings brings the window back afterwards.
        bool forceShow = e.Args.Any(a => a.Equals("--show", StringComparison.OrdinalIgnoreCase));

        // Sign-in launches (the Run key passes --minimized) go straight to the tray. A launch from
        // a shortcut is the user asking for the window, unless they chose "start minimised" — and
        // never on the very first run, where a hidden app looks like an app that failed to start.
        bool startHidden = !forceShow
                           && (launchedAtSignIn
                               || (settings.StartMinimized && settings.ShowTrayIcon && settings.HasCompletedFirstRun));

        if (startHidden)
        {
            Log.Info("Starting hidden in the notification area.");
            MemoryTrimmer.TrimNow();
        }
        else
        {
            ShowWindow();
        }

        if (!settings.HasCompletedFirstRun)
            _services.Settings.Update(s => s.HasCompletedFirstRun = true);

        // Kick the scheduler off after the UI exists, so the first change can report its progress.
        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await _services.Scheduler.StartAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Error("Scheduler failed to start.", ex);
            }
            finally
            {
                // The daily update check starts now, a couple of minutes out, so it never competes
                // with the first wallpaper for the network.
                _services.Updates.Start();

                // Start-up allocates a lot that is never needed again: JIT scratch, the settings
                // parse, the first wallpaper composition. When the app is going straight to the
                // notification area, hand all of it back rather than sitting on it for hours.
                if (_window is null || !_window.IsVisible) MemoryTrimmer.TrimNow();
            }
        }, DispatcherPriority.ApplicationIdle);
    }

    private void ConfigureTray()
    {
        if (_services is null || _viewModel is null) return;

        _services.Tray.Menu = TrayMenu.Build(_services, _viewModel, ShowWindow, ExitApplication);
        _services.Tray.Selected += (_, _) => Dispatcher.BeginInvoke(new Action(ShowWindow));
        _services.Tray.DoubleClicked += (_, _) => Dispatcher.BeginInvoke(new Action(ShowWindow));
        _services.Tray.MiddleClicked += (_, _) => _viewModel.NextCommand.Execute(null);
        _services.Tray.BalloonClicked += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            // The only balloon that leads somewhere specific is "a new version is available".
            if (_services.Updates.Available is not null) _viewModel.Section = AppSection.Settings;
            ShowWindow();
        }));
        _services.Tray.SetTooltip(_viewModel.TrayTooltip);
        _services.Tray.SetVisible(_services.Settings.Settings.ShowTrayIcon);
    }

    /// <summary>Gives the updater what it needs from the app: window state, a way out, a way to speak.</summary>
    private void ConfigureUpdates()
    {
        if (_services is null) return;

        _services.Updates.IsWindowVisible = () => Dispatcher.Invoke(() => _window is { IsVisible: true });
        _services.Updates.ExitForUpdate = () => Dispatcher.Invoke(ExitApplication);
        _services.Updates.NotifyAvailable = info => _services.Tray.ShowBalloon(
            $"Driftwall {info.Version} is available",
            _services.Settings.Settings.AutoUpdate
                ? "It will be installed the next time the window is closed. Click to see what is new."
                : "Click to open Settings and install it.");
    }

    /// <summary>A waiting update goes in the moment the window is out of the way.</summary>
    private void AttachWindow(MainWindow window)
    {
        window.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false) _services?.Updates.OnWindowHidden();
        };
    }

    /// <summary>Brings the window up, creating it again if it was closed to the tray.</summary>
    public void ShowWindow()
    {
        if (_services is null || _viewModel is null) return;

        if (_window is null || !_window.IsLoaded)
        {
            _window = new MainWindow(_services, _viewModel);
            AttachWindow(_window);
        }

        _window.Show();

        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;

        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    private bool _exiting;

    /// <summary>The only path that actually ends the process.</summary>
    public void ExitApplication()
    {
        // Quitting hides the window, and a waiting update may take that as its cue; one exit is enough.
        if (_exiting) return;
        _exiting = true;

        Log.Info("Driftwall shutting down.");

        try
        {
            _viewModel?.Dispose();
            _window?.CloseForReal();
            _services?.Settings.Save();
            _services?.Collections.Save();
            _services?.Dispose();
            _instance?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Error during shutdown.", ex);
        }

        Shutdown();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception.", e.Exception);

        // A wallpaper app that closes itself over a binding error is worse than one that logs and
        // carries on; the tray icon and the schedule keep working.
        e.Handled = true;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e) =>
        Log.Error("Unhandled exception.", e.ExceptionObject as Exception);

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Warn("Unobserved task exception.", e.Exception);
        e.SetObserved();
    }
}
