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

    /// <summary>Room the title keeps for itself before the actions move underneath it.</summary>
    private const double TitleMinWidth = 200;

    /// <summary>Gap between the title and the actions when they share a line.</summary>
    private const double ActionsGap = 16;

    /// <summary>
    /// The header shows the title and the action buttons on one line while there is room for
    /// both. In a narrow window the buttons move to a second line, right-aligned, instead of
    /// squeezing the title to nothing or running off the edge. Underneath, they wrap if even a
    /// whole line is too narrow for them.
    /// </summary>
    private void OnHeaderSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;

        // The natural one-line width, without the margin this handler itself sets: measuring with
        // the margin in would move the threshold with the state and flip it back and forth.
        HeaderActions.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double rowWidth = HeaderActions.DesiredSize.Width - HeaderActions.Margin.Left - HeaderActions.Margin.Right;
        bool stacked = HeaderGrid.ActualWidth < rowWidth + ActionsGap + TitleMinWidth;

        Grid.SetRow(HeaderActions, stacked ? 1 : 0);
        Grid.SetColumn(HeaderActions, stacked ? 0 : 1);
        Grid.SetColumnSpan(HeaderActions, stacked ? 2 : 1);
        HeaderActions.HorizontalAlignment = HorizontalAlignment.Right;
        HeaderActions.Margin = stacked ? new Thickness(0, 12, 0, 0) : new Thickness(ActionsGap, 0, 0, 0);
    }

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
