using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Driftwall.UI;

/// <summary>true (or a non-default value) becomes Visible, everything else Collapsed. Invert with "invert".</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value switch
        {
            bool b => b,
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            int i => i != 0,
            double d => d != 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };

        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not bool b || !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not bool b || !b;
}

/// <summary>Compares a value to the parameter; used to bind radio buttons and chips to an enum.</summary>
public sealed class EnumMatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return false;
        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b && parameter is not null && targetType.IsEnum)
            return Enum.Parse(targetType, parameter.ToString()!, ignoreCase: true);

        return Binding.DoNothing;
    }
}

/// <summary>Turns a hex colour string from settings into a brush.</summary>
public sealed class StringToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            if (value is string text && ColorConverter.ConvertFromString(text) is Color color)
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }
        }
        catch
        {
            // Fall through to a neutral brush rather than crashing a binding.
        }

        return Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SolidColorBrush brush ? brush.Color.ToString() : Binding.DoNothing;
}

/// <summary>Formats a minute count the way a person would say it.</summary>
public sealed class MinutesToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int minutes = value switch
        {
            int i => i,
            double d => (int)d,
            _ => 0,
        };

        return Format(minutes);
    }

    public static string Format(int minutes) => minutes switch
    {
        <= 0 => "Manual only",
        1 => "Every minute",
        < 60 => $"Every {minutes} minutes",
        60 => "Every hour",
        < 1440 when minutes % 60 == 0 => $"Every {minutes / 60} hours",
        < 1440 => $"Every {minutes / 60}h {minutes % 60}m",
        1440 => "Every day",
        _ when minutes % 1440 == 0 => $"Every {minutes / 1440} days",
        _ => $"Every {minutes / 60} hours",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Shortens a number of bytes for display.</summary>
public sealed class BytesToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        long bytes = value switch
        {
            long l => l,
            int i => i,
            _ => 0,
        };

        return Format(bytes);
    }

    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{size:0.#} {units[unit]}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Scales a number by the parameter. Used to keep photo tiles at a fixed aspect ratio.</summary>
public sealed class MultiplyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double number) return value ?? 0d;
        if (parameter is null || !double.TryParse(parameter.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var factor))
            return number;

        return number * factor;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Null or empty becomes Collapsed.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool present = value is not null && (value is not string s || !string.IsNullOrWhiteSpace(s));
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase)) present = !present;
        return present ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
