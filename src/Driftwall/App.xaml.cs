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

        _viewModel = new MainViewModel(_services);
        _window = new MainWindow(_services, _viewModel);

        ConfigureTray();
        _instance.ActivationRequested += (_, _) => Dispatcher.BeginInvoke(new Action(ShowWindow));
        _instance.StartListening();

        var settings = _services.Settings.Settings;
        bool launchedAtSignIn = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

        // Sign-in launches (the Run key passes --minimized) go straight to the tray. A launch from
        // a shortcut is the user asking for the window, unless they chose "start minimised" — and
        // never on the very first run, where a hidden app looks like an app that failed to start.
        bool startHidden = launchedAtSignIn
                           || (settings.StartMinimized && settings.ShowTrayIcon && settings.HasCompletedFirstRun);

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
        _services.Tray.SetTooltip(_viewModel.TrayTooltip);
        _services.Tray.SetVisible(_services.Settings.Settings.ShowTrayIcon);
    }

    /// <summary>Brings the window up, creating it again if it was closed to the tray.</summary>
    public void ShowWindow()
    {
        if (_services is null || _viewModel is null) return;

        if (_window is null || !_window.IsLoaded)
            _window = new MainWindow(_services, _viewModel);

        _window.Show();

        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;

        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    /// <summary>The only path that actually ends the process.</summary>
    public void ExitApplication()
    {
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
