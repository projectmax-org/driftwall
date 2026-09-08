using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Driftwall.UI.Controls;
using Driftwall.UI.ViewModels;

namespace Driftwall.UI.Views;

public partial class CollectionsView : UserControl
{
    public CollectionsView() => InitializeComponent();

    private CollectionsViewModel? ViewModel => DataContext as CollectionsViewModel;

    /// <summary>Enter in the create box is the same as pressing the + button.</summary>
    private void OnNewCollectionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        Execute(ViewModel?.CreateCommand);
    }

    /// <summary>Enter commits the rename, Escape abandons it.</summary>
    private void OnRenameKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                Execute(ViewModel?.CommitRenameCommand);
                break;

            case Key.Escape:
                e.Handled = true;
                Execute(ViewModel?.CancelRenameCommand);
                break;
        }
    }

    /// <summary>Puts the caret in the rename box the moment it takes the title's place.</summary>
    private void OnRenameBoxVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox box || !box.IsVisible) return;

        box.Focus();
        box.SelectAll();
    }

    private void OnSetWallpaper(object sender, RoutedEventArgs e) =>
        Invoke(e, photo => ViewModel?.SetWallpaperCommand.Execute(photo));

    /// <summary>Inside a collection the heart means "take it back out again".</summary>
    private void OnRemove(object sender, RoutedEventArgs e) =>
        Invoke(e, photo => ViewModel?.RemoveCommand.Execute(photo));

    private void OnOpenSource(object sender, RoutedEventArgs e) =>
        Invoke(e, photo => ViewModel?.OpenSourcePageCommand.Execute(photo));

    private static void Execute(ICommand? command)
    {
        if (command?.CanExecute(null) == true) command.Execute(null);
    }

    private static void Invoke(RoutedEventArgs e, Action<PhotoTileViewModel> action)
    {
        if (e is PhotoTileActionEventArgs { Photo: { } photo }) action(photo);
    }
}
