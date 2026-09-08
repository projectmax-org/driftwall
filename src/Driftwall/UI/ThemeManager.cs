using System.Windows;
using System.Windows.Media;
using Driftwall.Core;
using Microsoft.Win32;

namespace Driftwall.UI;

/// <summary>
/// Applies the palette and accent colour.
/// <para>
/// The palette dictionary lives at a known index in the application's merged dictionaries and is
/// swapped wholesale. Because every style references brushes with DynamicResource, the whole window
/// re-colours without being rebuilt.
/// </para>
/// </summary>
public static class ThemeManager
{
    private const int PaletteIndex = 0;

    private static readonly Uri DarkPalette = new("pack://application:,,,/UI/Theme/Palette.Dark.xaml", UriKind.Absolute);
    private static readonly Uri LightPalette = new("pack://application:,,,/UI/Theme/Palette.Light.xaml", UriKind.Absolute);

    private static AppTheme _requested = AppTheme.System;
    private static bool _watchingSystem;

    /// <summary>True when the palette currently in use is the dark one.</summary>
    public static bool IsDark { get; private set; } = true;

    public static event EventHandler? ThemeChanged;

    public static void Apply(AppSettings settings)
    {
        _requested = settings.Theme;
        ApplyResolved(Resolve(settings.Theme));
        ApplyAccent(settings.AccentColor);
        WatchSystemTheme();
    }

    public static void ApplyAccent(string accentColor)
    {
        var resources = Application.Current?.Resources;
        if (resources is null) return;

        try
        {
            if (ColorConverter.ConvertFromString(accentColor) is not Color accent) return;

            resources["Color.Accent"] = accent;
            resources["Color.AccentHover"] = Shift(accent, 1.12);
            resources["Color.AccentPressed"] = Shift(accent, 0.88);
            resources["Color.AccentSoft"] = Color.FromArgb(IsDark ? (byte)0x38 : (byte)0x24, accent.R, accent.G, accent.B);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not apply the accent colour " + accentColor, ex);
        }
    }

    private static bool Resolve(AppTheme theme) => theme switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        _ => IsSystemDark(),
    };

    private static void ApplyResolved(bool dark)
    {
        var app = Application.Current;
        if (app is null) return;

        if (IsDark == dark && app.Resources.MergedDictionaries.Count > PaletteIndex) return;

        var palette = new ResourceDictionary { Source = dark ? DarkPalette : LightPalette };

        if (app.Resources.MergedDictionaries.Count > PaletteIndex)
            app.Resources.MergedDictionaries[PaletteIndex] = palette;
        else
            app.Resources.MergedDictionaries.Insert(PaletteIndex, palette);

        IsDark = dark;
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Reads the Windows "choose your app mode" preference.</summary>
    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the system theme preference.", ex);
            return true;
        }
    }

    private static void WatchSystemTheme()
    {
        if (_watchingSystem) return;
        _watchingSystem = true;

        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General) return;
            if (_requested != AppTheme.System) return;

            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                ApplyResolved(IsSystemDark());
                var accent = Application.Current?.Resources["Color.Accent"];
                if (accent is Color color) ApplyAccent(color.ToString());
            }));
        };
    }

    /// <summary>Lightens or darkens a colour by scaling its channels, clamped to the byte range.</summary>
    private static Color Shift(Color color, double factor) => Color.FromArgb(
        color.A,
        (byte)Math.Clamp(color.R * factor, 0, 255),
        (byte)Math.Clamp(color.G * factor, 0, 255),
        (byte)Math.Clamp(color.B * factor, 0, 255));
}
