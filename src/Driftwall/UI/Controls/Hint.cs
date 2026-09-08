using System.Windows;

namespace Driftwall.UI.Controls;

/// <summary>
/// Placeholder text for inputs, shown by the TextBox template while the box is empty.
/// <para>
/// An attached property rather than a custom control so the standard <see cref="System.Windows.Controls.TextBox"/>
/// keeps all of its behaviour, and so the placeholder can be set from XAML wherever a TextBox appears.
/// </para>
/// </summary>
public static class Hint
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Hint), new PropertyMetadata(string.Empty));

    public static string GetText(DependencyObject element) => (string)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string value) => element.SetValue(TextProperty, value);
}
