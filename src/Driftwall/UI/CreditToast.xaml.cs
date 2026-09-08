using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Driftwall.Core;
using Driftwall.Scheduling;
using Driftwall.Wallpaper;

namespace Driftwall.UI;

/// <summary>
/// A brief card in the corner of the primary display naming the photographer when the wallpaper
/// changes.
/// <para>
/// This is how the app satisfies the attribution some providers require without leaving anything
/// permanently on screen. It shows for four seconds and closes itself, so there is no persistent
/// window and nothing to cover a game — and it is suppressed outright while a fullscreen app is
/// running, along with the wallpaper change that would have triggered it.
/// </para>
/// </summary>
public partial class CreditToast : Window
{
    private static readonly TimeSpan VisibleFor = TimeSpan.FromSeconds(4);
    private static CreditToast? _current;

    private readonly string? _sourceUrl;
    private readonly DispatcherTimer _dismissTimer;
    private bool _closing;

    private CreditToast(PhotoItem photo, BitmapSource? preview)
    {
        InitializeComponent();

        _sourceUrl = photo.SourcePageUrl ?? photo.AuthorUrl;

        TitleText.Text = string.IsNullOrWhiteSpace(photo.Title) ? "New wallpaper" : photo.Title;
        CreditText.Text = photo.AuthorName is { Length: > 0 } author
            ? $"{author} · {photo.ProviderName}"
            : photo.ProviderName;

        Preview.Source = preview;

        _dismissTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = VisibleFor };
        _dismissTimer.Tick += (_, _) => BeginDismiss();

        Card.MouseLeftButtonUp += OnCardClicked;
        Card.MouseEnter += (_, _) => _dismissTimer.Stop();
        Card.MouseLeave += (_, _) => _dismissTimer.Start();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
    }

    /// <summary>
    /// Shows the toast for a wallpaper change, replacing any toast already on screen.
    /// Safe to call from any thread; does nothing when the setting is off or a game is in front.
    /// </summary>
    public static void ShowFor(WallpaperApplication application, AppSettings settings)
    {
        if (!settings.ShowAttributionOverlay) return;

        var photo = application.PrimaryPhoto;
        if (photo is null) return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (SystemConditions.IsFullscreenAppRunning()) return;

                _current?.CloseImmediately();

                var preview = LoadPreview(application.Assignments.FirstOrDefault()?.RenderedPath);
                var toast = new CreditToast(photo, preview);
                _current = toast;
                toast.Show();
            }
            catch (Exception ex)
            {
                Log.Warn("Could not show the credit toast.", ex);
            }
        }));
    }

    private static BitmapSource? LoadPreview(string? renderedPath)
    {
        if (string.IsNullOrEmpty(renderedPath) || !File.Exists(renderedPath)) return null;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(renderedPath, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 160;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not load the toast preview.", ex);
            return null;
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // No activation, no Alt-Tab entry: the toast must never steal focus from what the user is doing.
        var handle = new WindowInteropHelper(this).Handle;
        int style = GetWindowLong(handle, GWL_EXSTYLE);
        SetWindowLong(handle, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionOnPrimaryDisplay();

        Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });

        Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });

        _dismissTimer.Start();
    }

    /// <summary>Pins the toast to the bottom-right working area of the primary display.</summary>
    private void PositionOnPrimaryDisplay()
    {
        var work = SystemParameters.WorkArea;
        Left = work.Right - ActualWidth;
        Top = work.Bottom - ActualHeight;
    }

    private void OnCardClicked(object sender, MouseButtonEventArgs e)
    {
        Shell.OpenUrl(_sourceUrl);
        BeginDismiss();
    }

    private void BeginDismiss()
    {
        if (_closing) return;
        _closing = true;
        _dismissTimer.Stop();

        var fade = new DoubleAnimation(Card.Opacity, 0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) => CloseImmediately();
        Card.BeginAnimation(OpacityProperty, fade);
    }

    private void CloseImmediately()
    {
        _dismissTimer.Stop();
        if (ReferenceEquals(_current, this)) _current = null;

        try { Close(); }
        catch (InvalidOperationException) { /* already closing */ }
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
