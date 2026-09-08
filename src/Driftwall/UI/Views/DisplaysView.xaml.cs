using System.Windows.Controls;

namespace Driftwall.UI.Views;

/// <summary>
/// The displays tab. Every action is a command on <see cref="ViewModels.DisplaysViewModel"/>, including the
/// per-monitor ones, which take the tile's <see cref="ViewModels.MonitorViewModel"/> as their parameter, so
/// this view needs nothing behind it beyond initialisation.
/// </summary>
public partial class DisplaysView : UserControl
{
    public DisplaysView() => InitializeComponent();
}
