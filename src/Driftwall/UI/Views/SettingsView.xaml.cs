using System.Windows;
using System.Windows.Controls;
using Driftwall.Core;
using Driftwall.UI.ViewModels;

namespace Driftwall.UI.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    /// <summary>Opens the provider's terms page for the source the clicked strip belongs to.</summary>
    private void OnOpenTerms(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ConfiguredSourceViewModel source })
            Shell.OpenUrl(source.TermsUrl);
    }

    /// <summary>Applies one of the preset intervals. The preset itself is the button's data context.</summary>
    private void OnIntervalPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: int minutes } && ViewModel is { } model)
            model.IntervalMinutes = minutes;
    }

    /// <summary>Applies one of the accent swatches. The hex string is the button's data context.</summary>
    private void OnAccentClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: string color } && ViewModel is { } model)
            model.AccentColor = color;
    }
}
