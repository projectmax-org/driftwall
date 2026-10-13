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
        try
        {
            _instance = SingleInstance.Acquire();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            // A copy running with other rights made names this launch may not open: an older version
            // run as administrator, say. That copy is still running, so this launch simply ends.
            Log.Warn("Another Driftwall is running with different rights; this launch ends here.", ex);
            Shutdown();
            return;
        }

        if (!_instance.IsFirstInstance)
        {
            // The running copy gets this launch's arguments through a file, so a shortcut such as
            // "Driftwall.exe --section=Settings" works when the app is already in the tray too.
            try { File.WriteAllLines(ActivationArgsFile, e.Args); }
            catch (Exception ex) { Log.Warn("Could not pass the launch arguments to the running copy.", ex); }

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
        // The first run applies the "start with Windows" default to a portable copy. A copy the setup
        // wizard or tools\install-local.ps1 put in place follows the choice made there instead, which
        // the user may have unticked. From then on the Run key belongs to the user (and Task Manager's
        // Startup tab). This runs before the repair below, which would otherwise adopt a stale entry.
        if (!_services.Settings.Settings.HasCompletedFirstRun)
        {
            bool byWizard = UpdateService.IsInstalled;
            bool byScript = !byWizard && File.Exists(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? string.Empty, "uninstall.ps1"));

            if (byWizard || byScript)
            {
                // An entry naming a program that is gone was not a choice about this copy.
                if (StartupManager.HasStaleEntry()) StartupManager.SetEnabled(false);

                bool enabled = StartupManager.IsEnabled();

                // The wizard writes the entry only into the hive of whoever ran it, so another user of
                // an all-users install gets it from the task Inno Setup recorded.
                // Only when there is no entry: one switched off in Task Manager stays off.
                if (!StartupManager.HasEntry() && byWizard && UpdateService.InstallerSelectedTask("startup"))
                    enabled = StartupManager.SetEnabled(true);

                _services.Settings.Update(s => s.StartWithWindows = enabled);
            }
            else if (_services.Settings.Settings.StartWithWindows && !StartupManager.HasEntry())
            {
                StartupManager.SetEnabled(true);
            }
        }

        StartupManager.RepairIfNeeded();

        _viewModel = new MainViewModel(_services);

        // --section=Settings opens straight onto that section: for shortcuts, and for the screenshot
        // tooling, which cannot click a window parked off-screen.
        if (SectionFromArgs(e.Args) is { } section) _viewModel.Section = section;

        // No window yet: ShowWindow builds it when it is first wanted. One built here and never shown
        // would only be replaced, yet WPF would keep it, and close it at quit after the services had gone.
        ConfigureTray();
        ConfigureUpdates();
        _instance.ActivationRequested += (_, _) => Dispatcher.BeginInvoke(new Action(OnActivationRequested));
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
        _services.Updates.SaveBeforeUpdate = () => Dispatcher.Invoke(() =>
        {
            _services.Settings.Save();
            _services.Collections.Save();
        });
        _services.Updates.NotifyAvailable = info => _services.Tray.ShowBalloon(
            $"Driftwall {info.Version} is available",
            !UpdateService.CanReplaceItself
                ? "This copy cannot replace itself where it is. Download the new version from the releases page."
                : _services.Settings.Settings.AutoUpdate && UpdateService.CanInstallUnattended
                    ? "It will be installed the next time the window is closed. Click to see what is new."
                    : "Click to open Settings and install it.");
    }

    /// <summary>A waiting update goes in the moment the window is out of the way.</summary>
    private void AttachWindow(MainWindow window)
    {
        window.IsVisibleChanged += (_, e) =>
        {
            // Quitting hides the window too, and that must not install an update and bring the app back.
            // Nor must the close WPF makes as the session ends: setup would be cut off with everything else.
            if (e.NewValue is false && !_exiting && !_sessionEnding) _services?.Updates.OnWindowHidden();
        };
    }

    /// <summary>Where a second launch leaves its arguments for the running copy.</summary>
    private static string ActivationArgsFile => Path.Combine(Paths.DataDirectory, "activate.args");

    /// <summary>The section named by a --section= argument, when there is one and it exists.</summary>
    private static AppSection? SectionFromArgs(IEnumerable<string> args)
    {
        var value = args.FirstOrDefault(a => a.StartsWith("--section=", StringComparison.OrdinalIgnoreCase))?["--section=".Length..];
        return value is not null && Enum.TryParse(value, ignoreCase: true, out AppSection section) && Enum.IsDefined(section)
            ? section
            : null;
    }

    /// <summary>A second launch asked for the window: honour its --section first, then show it.</summary>
    private void OnActivationRequested()
    {
        try
        {
            if (File.Exists(ActivationArgsFile))
            {
                var args = File.ReadAllLines(ActivationArgsFile);
                File.Delete(ActivationArgsFile);
                if (_viewModel is not null && SectionFromArgs(args) is { } section) _viewModel.Section = section;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the launch arguments of the second copy.", ex);
        }

        ShowWindow();
    }

    /// <summary>Brings the window up, creating it when there is none yet or the last one was closed.</summary>
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
    private bool _sessionEnding;

    /// <summary>
    /// How the app ends itself. Windows ending the session is the one other way out, and it goes
    /// through WPF's own shutdown instead; see <see cref="OnSessionEnding"/>.
    /// </summary>
    public void ExitApplication()
    {
        // Quitting hides the window, and a waiting update may take that as its cue; one exit is enough.
        if (_exiting) return;
        _exiting = true;

        Log.Info("Driftwall shutting down.");

        // Each step on its own: a window that fails to close must not cost the user their settings.
        Attempt(() => _viewModel?.Dispose());
        Attempt(() => _window?.CloseForReal());
        Attempt(() => _services?.Settings.Save());
        Attempt(() => _services?.Collections.Save());
        Attempt(() => _services?.Dispose());
        Attempt(() => _instance?.Dispose());

        Shutdown();
    }

    /// <summary>
    /// Sign-out, restart and shutdown. WPF answers Windows by shutting the app down itself, so
    /// ExitApplication never runs, and the process can be ended before the delayed saves fire: a
    /// photo saved a second earlier would be gone at the next sign-in. Both stores are written now,
    /// while the process is sure to be alive. Nothing is disposed here: the scheduler may still have
    /// its own session-ending work to do, the shutdown wallpaper change.
    /// </summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (e.Cancel) return;

        // WPF closes the windows next, and a hidden window is a waiting update's cue to install.
        _sessionEnding = true;

        Log.Info("Session ending; saving settings and collections.");
        Attempt(() => _services?.Settings.Save());
        Attempt(() => _services?.Collections.Save());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // After a session end WPF has closed the window by now, and the size it records on closing
        // would otherwise wait on a delayed save. ExitApplication has saved everything already.
        if (!_exiting)
        {
            Attempt(() => _services?.Settings.Save());
            Attempt(() => _services?.Collections.Save());
        }

        base.OnExit(e);
    }

    private static void Attempt(Action step)
    {
        try { step(); }
        catch (Exception ex) { Log.Warn("Error during shutdown.", ex); }
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
