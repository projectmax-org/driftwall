using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Driftwall.UI.Controls;
using Driftwall.UI.ViewModels;

namespace Driftwall.UI.Views;

public partial class BrowseView : UserControl
{
    /// <summary>How close to the bottom, in pixels, triggers the next page.</summary>
    private const double InfiniteScrollThreshold = 400;

    public BrowseView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => _ = ViewModel?.EnsureLoadedAsync();

    private BrowseViewModel? ViewModel => DataContext as BrowseViewModel;

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        ViewModel?.SearchCommand.Execute(null);
    }

    /// <summary>Loads the next page as the user approaches the end of the grid.</summary>
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var model = ViewModel;
        if (model is null || !model.HasMore || model.IsBusy) return;
        if (e.VerticalChange <= 0) return;

        double remaining = e.ExtentHeight - (e.VerticalOffset + e.ViewportHeight);
        if (remaining > InfiniteScrollThreshold) return;

        if (model.LoadMoreCommand.CanExecute(null)) model.LoadMoreCommand.Execute(null);
    }

    private void OnSetWallpaper(object sender, RoutedEventArgs e) =>
        Invoke(e, photo => ViewModel?.SetWallpaperCommand.Execute(photo));

    private void OnSave(object sender, RoutedEventArgs e) =>
        Invoke(e, photo => ViewModel?.ToggleSaveCommand.Execute(photo));

    private void OnOpenSource(object sender, RoutedEventArgs e) =>
        Invoke(e, photo => ViewModel?.OpenSourcePageCommand.Execute(photo));

    private static void Invoke(RoutedEventArgs e, Action<PhotoTileViewModel> action)
    {
        if (e is PhotoTileActionEventArgs { Photo: { } photo }) action(photo);
    }
}
