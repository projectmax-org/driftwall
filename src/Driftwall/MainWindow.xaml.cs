using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Driftwall.Core;
using Driftwall.UI.ViewModels;

namespace Driftwall;

public partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly MainViewModel _viewModel;
    private bool _reallyClosing;

    public MainWindow(AppServices services, MainViewModel viewModel)
    {
        _services = services;
        _viewModel = viewModel;

        InitializeComponent();
        DataContext = viewModel;

        Width = Math.Max(MinWidth, services.Settings.Settings.WindowWidth);
        Height = Math.Max(MinHeight, services.Settings.Settings.WindowHeight);

        SourceInitialized += OnSourceInitialized;
        IsVisibleChanged += OnIsVisibleChanged;
        StateChanged += OnStateChanged;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Closes the window for real, bypassing the close-to-tray behaviour.</summary>
    public void CloseForReal()
    {
        _reallyClosing = true;
        Close();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        ApplyRoundedCorners(handle);
        ApplyImmersiveDarkMode(handle);

        Driftwall.UI.ThemeManager.ThemeChanged += (_, _) => ApplyImmersiveDarkMode(handle);
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            _viewModel.StartCountdown();
            return;
        }

        // Hidden: stop ticking and give the memory back. This is the moment the app goes from
        // "an open window" to "a tray icon", and its footprint should reflect that.
        _viewModel.StopCountdown();
        if (_services.Settings.Settings.AggressiveMemoryTrim) MemoryTrimmer.TrimNow();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized) _viewModel.StopCountdown();
        else _viewModel.StartCountdown();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape when _services.Settings.Settings.CloseToTray:
                if (EscapeBelongsToFocusedControl()) break;
                HideToTray();
                e.Handled = true;
                break;

            case Key.F5:
                _viewModel.RefreshCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Right when Keyboard.Modifiers == ModifierKeys.Control:
                _viewModel.NextCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Left when Keyboard.Modifiers == ModifierKeys.Control:
                _viewModel.PreviousCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// True when the focused control has its own use for Escape. In a text box the first Escape
    /// only leaves the box, so a window does not vanish from under someone who was clearing a search;
    /// with a drop-down open, Escape closes the list. The next Escape closes the window as usual.
    /// </summary>
    private bool EscapeBelongsToFocusedControl()
    {
        if (Keyboard.FocusedElement is not DependencyObject focused) return false;

        TextBoxBase? textBox = null;
        for (var node = focused; node is not null; node = GetParent(node))
        {
            if (node is ComboBox { IsDropDownOpen: true }) return true;
            textBox ??= node as TextBoxBase;
        }

        if (textBox is null) return false;

        // Land on the selected section in the rail: a visible, sensible place for focus, and one
        // that keeps the window listening. A Window is not focusable itself, and clearing focus
        // outright would mean no element receives the next key press at all.
        IInputElement target = NavRail.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true) ?? (IInputElement)this;
        Keyboard.Focus(target);
        return true;
    }

    private static DependencyObject? GetParent(DependencyObject node) =>
        node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    /// <summary>Hides the window and leaves the app running in the notification area.</summary>
    private void HideToTray()
    {
        Hide();
        ShowTrayHintOnce();
    }

    /// <summary>
    /// The first time the window goes to the tray, say so. A window that closes while the app keeps
    /// running is a surprise unless someone points out where it went.
    /// </summary>
    private void ShowTrayHintOnce()
    {
        if (_services.Settings.Settings.HasShownTrayHint) return;

        _services.Settings.Update(s => s.HasShownTrayHint = true);
        _services.Tray.ShowBalloon(
            "Driftwall is still running",
            "It keeps changing your wallpaper from the notification area. Click the icon to open it again, or right-click it to quit.");
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (WindowState == WindowState.Normal)
        {
            _services.Settings.Update(s =>
            {
                s.WindowWidth = Width;
                s.WindowHeight = Height;
            });
        }

        // Closing the window is not quitting: the app lives in the notification area. Quitting is
        // done from the tray menu, which calls CloseForReal first.
        if (!_reallyClosing && _services.Settings.Settings.CloseToTray && _services.Settings.Settings.ShowTrayIcon)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        base.OnClosing(e);

        // With no tray icon there is nothing left to interact with, so closing means quitting.
        if (!_reallyClosing && !_services.Settings.Settings.ShowTrayIcon)
            (Application.Current as App)?.ExitApplication();
    }

    // ---------------- native window polish ----------------

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWCP_ROUND = 2;

    /// <summary>
    /// Asks DWM for Windows 11 rounded corners. Doing it this way keeps the window opaque and
    /// hardware-composited; achieving rounded corners with AllowsTransparency would force the whole
    /// window into software rendering.
    /// </summary>
    private static void ApplyRoundedCorners(IntPtr handle)
    {
        try
        {
            int preference = DWMWCP_ROUND;
            DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
        catch (Exception ex)
        {
            // Pre-Windows 11: square corners, which is correct for that OS anyway.
            Log.Warn("Rounded corners are unavailable on this version of Windows.", ex);
        }
    }

    /// <summary>Matches the window's drop shadow and resize border to the app theme.</summary>
    private static void ApplyImmersiveDarkMode(IntPtr handle)
    {
        try
        {
            int dark = Driftwall.UI.ThemeManager.IsDark ? 1 : 0;
            DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        }
        catch (Exception ex)
        {
            Log.Warn("Could not set the immersive dark mode attribute.", ex);
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
