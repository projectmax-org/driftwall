using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Driftwall.Core;

namespace Driftwall.Tray;

/// <summary>
/// The notification-area icon.
/// <para>
/// Written directly against Shell_NotifyIcon rather than using WinForms' NotifyIcon: it avoids
/// pulling the entire Windows Forms stack into the process for one icon, and it lets the right-click
/// menu be a themed WPF <see cref="ContextMenu"/> that matches the rest of the app instead of a grey
/// Win32 popup.
/// </para>
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = NativeConstants.WM_APP + 0x21;
    private const uint IconId = 1;

    private readonly uint _taskbarCreatedMessage;
    private HwndSource? _window;
    private IntPtr _icon;
    private IntPtr _balloonIcon;
    private bool _added;
    private bool _disposed;
    private string _tooltip = "Driftwall";

    public TrayIcon()
    {
        // Explorer can restart. When it does the icon is gone and every app has to re-add it.
        _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
        CreateMessageWindow();
        _icon = LoadAppIcon(GetSystemMetrics(NativeConstants.SM_CXSMICON), 16);
        _balloonIcon = LoadAppIcon(GetSystemMetrics(NativeConstants.SM_CXICON), 32);
    }

    /// <summary>Menu shown on right-click. Owned by the caller so it can be built with app state.</summary>
    public ContextMenu? Menu { get; set; }

    /// <summary>A single left click, or Enter/Space on the icon from the keyboard.</summary>
    public event EventHandler? Selected;
    public event EventHandler? DoubleClicked;
    public event EventHandler? MiddleClicked;
    /// <summary>The user clicked a notification shown with <see cref="ShowBalloon"/>.</summary>
    public event EventHandler? BalloonClicked;

    public bool IsVisible => _added;

    public void Show()
    {
        if (_disposed || _added || _window is null) return;

        var data = BuildData(NativeConstants.NIF_MESSAGE | NativeConstants.NIF_ICON | NativeConstants.NIF_TIP | NativeConstants.NIF_SHOWTIP);
        if (!Shell_NotifyIconW(NativeConstants.NIM_ADD, ref data))
        {
            Log.Warn("Shell_NotifyIcon(NIM_ADD) failed.");
            return;
        }

        // Version 4 gives richer notifications and correct behaviour on high-DPI displays.
        var version = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _window.Handle,
            uID = IconId,
            uVersionOrTimeout = NativeConstants.NOTIFYICON_VERSION_4,
        };
        Shell_NotifyIconW(NativeConstants.NIM_SETVERSION, ref version);

        _added = true;
    }

    public void Hide()
    {
        if (!_added || _window is null) return;

        var data = BuildData(0);
        Shell_NotifyIconW(NativeConstants.NIM_DELETE, ref data);
        _added = false;
    }

    public void SetVisible(bool visible)
    {
        if (visible) Show();
        else Hide();
    }

    /// <summary>Updates the hover tooltip, which is where the next-change countdown is shown.</summary>
    public void SetTooltip(string tooltip)
    {
        // Windows truncates at 128 characters including the terminator.
        _tooltip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        if (!_added) return;

        var data = BuildData(NativeConstants.NIF_TIP | NativeConstants.NIF_SHOWTIP);
        Shell_NotifyIconW(NativeConstants.NIM_MODIFY, ref data);
    }

    /// <summary>
    /// Shows a notification from the icon. On Windows 10 and 11 this arrives as a toast with the
    /// app's own icon; it respects quiet hours and never takes focus.
    /// </summary>
    public void ShowBalloon(string title, string text)
    {
        if (!_added) return;

        var data = BuildData(NativeConstants.NIF_INFO);
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        data.szInfo = text.Length > 255 ? text[..255] : text;
        data.dwInfoFlags = NativeConstants.NIIF_USER | NativeConstants.NIIF_LARGE_ICON | NativeConstants.NIIF_RESPECT_QUIET_TIME;
        data.hBalloonIcon = _balloonIcon != IntPtr.Zero ? _balloonIcon : _icon;

        if (!Shell_NotifyIconW(NativeConstants.NIM_MODIFY, ref data))
            Log.Warn("Shell_NotifyIcon(NIM_MODIFY, NIF_INFO) failed.");
    }

    // ---------------- window plumbing ----------------

    private void CreateMessageWindow()
    {
        // A hidden top-level popup rather than a true HWND_MESSAGE window. Message-only windows
        // cannot become the foreground window, and SetForegroundWindow before opening the menu is
        // what makes the menu dismiss correctly when the user clicks elsewhere.
        const int WS_POPUP = unchecked((int)0x80000000);

        var parameters = new HwndSourceParameters("Driftwall.TrayIcon")
        {
            Width = 0,
            Height = 0,
            PositionX = -32000,
            PositionY = -32000,
            WindowStyle = WS_POPUP,
        };

        _window = new HwndSource(parameters);
        _window.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            Log.Info("Explorer restarted; re-adding the tray icon.");
            _added = false;
            Show();
            handled = true;
            return IntPtr.Zero;
        }

        if (msg != CallbackMessage) return IntPtr.Zero;

        // Version 4 packs the event into the low word of lParam and the cursor into wParam.
        int notification = (int)(lParam.ToInt64() & 0xFFFF);

        switch (notification)
        {
            case NativeConstants.NIN_SELECT:
            case NativeConstants.NIN_KEYSELECT:
                // What a click on a tray icon does on Windows 11: the primary action, no double-click needed.
                Selected?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;

            case NativeConstants.NIN_BALLOONUSERCLICK:
                BalloonClicked?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;

            case NativeConstants.WM_LBUTTONDBLCLK:
                DoubleClicked?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;

            case NativeConstants.WM_MBUTTONUP:
                MiddleClicked?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;

            case NativeConstants.WM_CONTEXTMENU:
            case NativeConstants.WM_RBUTTONUP:
                ShowMenu();
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        var menu = Menu;
        if (menu is null || _window is null) return;

        // Without this the menu opens behind whatever is in front, and clicking elsewhere fails to
        // dismiss it. Giving our hidden window the foreground is the documented fix.
        SetForegroundWindow(_window.Handle);

        menu.Placement = PlacementMode.MousePoint;
        menu.StaysOpen = false;
        menu.IsOpen = true;

        // Let the popup take keyboard focus so arrow keys and Escape work.
        menu.Dispatcher.BeginInvoke(new Action(() => menu.Focus()), System.Windows.Threading.DispatcherPriority.Input);
    }

    private NOTIFYICONDATAW BuildData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _window?.Handle ?? IntPtr.Zero,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tooltip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    /// <summary>
    /// Loads the app icon at a given pixel size: the small one for the notification area itself,
    /// the large one for notifications.
    /// <para>
    /// The compiler embeds ApplicationIcon as resource 32512, and asking for it by resource keeps
    /// this working under single-file publishing, where there is no .ico file on disk to load.
    /// </para>
    /// </summary>
    private static IntPtr LoadAppIcon(int size, int fallbackSize)
    {
        if (size <= 0) size = fallbackSize;

        try
        {
            var module = GetModuleHandleW(null);
            if (LoadIconWithScaleDown(module, new IntPtr(32512), size, size, out var scaled) == 0 && scaled != IntPtr.Zero)
                return scaled;
        }
        catch (Exception ex)
        {
            Log.Warn("LoadIconWithScaleDown failed.", ex);
        }

        try
        {
            var executable = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(executable) && ExtractIconExW(executable, 0, out var large, out var small, 1) > 0)
            {
                var wanted = size >= 24 ? large : small;
                var other = size >= 24 ? small : large;
                if (other != IntPtr.Zero) DestroyIcon(other);
                if (wanted != IntPtr.Zero) return wanted;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("ExtractIconEx failed.", ex);
        }

        Log.Warn("Falling back to the generic application icon for the tray.");
        return LoadIconW(IntPtr.Zero, new IntPtr(32512));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Hide();

        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }

        if (_balloonIcon != IntPtr.Zero)
        {
            DestroyIcon(_balloonIcon);
            _balloonIcon = IntPtr.Zero;
        }

        _window?.RemoveHook(WndProc);
        _window?.Dispose();
        _window = null;
    }

    // ---------------- interop ----------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private static class NativeConstants
    {
        public const int WM_APP = 0x8000;
        public const int WM_LBUTTONDBLCLK = 0x0203;
        public const int WM_RBUTTONUP = 0x0205;
        public const int WM_MBUTTONUP = 0x0208;
        public const int WM_CONTEXTMENU = 0x007B;

        // Version-4 notifications: WM_USER + 0 and + 1.
        public const int NIN_SELECT = 0x0400;
        public const int NIN_KEYSELECT = 0x0401;
        public const int NIN_BALLOONUSERCLICK = 0x0405;

        public const uint NIM_ADD = 0x00;
        public const uint NIM_MODIFY = 0x01;
        public const uint NIM_DELETE = 0x02;
        public const uint NIM_SETVERSION = 0x04;

        public const uint NIF_MESSAGE = 0x01;
        public const uint NIF_ICON = 0x02;
        public const uint NIF_TIP = 0x04;
        public const uint NIF_INFO = 0x10;
        public const uint NIF_SHOWTIP = 0x80;

        public const uint NIIF_USER = 0x04;
        public const uint NIIF_LARGE_ICON = 0x20;
        public const uint NIIF_RESPECT_QUIET_TIME = 0x80;

        public const uint NOTIFYICON_VERSION_4 = 4;

        public const int SM_CXICON = 11;
        public const int SM_CXSMICON = 49;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern int LoadIconWithScaleDown(IntPtr hinst, IntPtr pszName, int cx, int cy, out IntPtr phico);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
