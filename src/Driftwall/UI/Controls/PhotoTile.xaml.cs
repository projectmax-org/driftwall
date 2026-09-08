using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Driftwall.UI.ViewModels;

namespace Driftwall.UI.Controls;

/// <summary>
/// One photo in a grid.
/// <para>
/// The tile loads its thumbnail on Loaded and releases it on Unloaded. Because the grid uses
/// <see cref="VirtualizingWrapPanel"/>, those events fire as tiles scroll in and out of view, which
/// is what keeps the grid's memory tied to the viewport instead of to the scroll history.
/// </para>
/// Actions are surfaced as routed events so the hosting view supplies the commands; the tile itself
/// stays free of any dependency on which view it is in.
/// </summary>
public partial class PhotoTile : UserControl
{
    public static readonly RoutedEvent SetWallpaperRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(SetWallpaperRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(PhotoTile));

    public static readonly RoutedEvent SaveRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(SaveRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(PhotoTile));

    public static readonly RoutedEvent OpenSourceRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(OpenSourceRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(PhotoTile));

    public PhotoTile()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        MouseLeftButtonUp += OnTileClicked;
    }

    public event RoutedEventHandler SetWallpaperRequested
    {
        add => AddHandler(SetWallpaperRequestedEvent, value);
        remove => RemoveHandler(SetWallpaperRequestedEvent, value);
    }

    public event RoutedEventHandler SaveRequested
    {
        add => AddHandler(SaveRequestedEvent, value);
        remove => RemoveHandler(SaveRequestedEvent, value);
    }

    public event RoutedEventHandler OpenSourceRequested
    {
        add => AddHandler(OpenSourceRequestedEvent, value);
        remove => RemoveHandler(OpenSourceRequestedEvent, value);
    }

    private PhotoTileViewModel? ViewModel => DataContext as PhotoTileViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e) => _ = ViewModel?.LoadAsync();

    private void OnUnloaded(object sender, RoutedEventArgs e) => ViewModel?.Release();

    private void OnSetWallpaper(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        RaiseAction(SetWallpaperRequestedEvent);
    }

    private void OnToggleSave(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        RaiseAction(SaveRequestedEvent);
    }

    private void OnOpenSource(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        RaiseAction(OpenSourceRequestedEvent);
    }

    /// <summary>Double-clicking the tile sets the wallpaper, matching the tray icon's behaviour.</summary>
    private void OnTileClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        e.Handled = true;
        RaiseAction(SetWallpaperRequestedEvent);
    }

    private void RaiseAction(RoutedEvent routedEvent) =>
        RaiseEvent(new PhotoTileActionEventArgs(routedEvent, this, ViewModel));
}

/// <summary>Carries the tile's view model to whichever view is handling the action.</summary>
public sealed class PhotoTileActionEventArgs : RoutedEventArgs
{
    public PhotoTileActionEventArgs(RoutedEvent routedEvent, object source, PhotoTileViewModel? photo)
        : base(routedEvent, source)
    {
        Photo = photo;
    }

    public PhotoTileViewModel? Photo { get; }
}
