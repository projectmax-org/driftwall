using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
// System.IO is a global using in this project, so Path needs disambiguating.
using Path = System.Windows.Shapes.Path;
using Driftwall.Core;
using Driftwall.UI.ViewModels;

namespace Driftwall.Tray;

/// <summary>
/// Builds the notification-area right-click menu.
/// <para>
/// A WPF menu rather than a Win32 one, so it picks up the app's palette, rounded corners and icons.
/// It is built once and refreshed on open, which keeps "Pause"/"Resume" and the collection list
/// honest without rebuilding the menu on every settings change.
/// </para>
/// </summary>
public static class TrayMenu
{
    public static ContextMenu Build(AppServices services, MainViewModel viewModel, Action showWindow, Action exit)
    {
        var menu = new ContextMenu();

        var open = Item("Open Driftwall", "Icon.Browse", () => showWindow());
        open.FontWeight = FontWeights.SemiBold;

        var next = Item("Next wallpaper", "Icon.Next", () => viewModel.NextCommand.Execute(null));
        var previous = Item("Previous wallpaper", "Icon.Previous", () => viewModel.PreviousCommand.Execute(null));
        var refresh = Item("Refresh current", "Icon.Refresh", () => viewModel.RefreshCommand.Execute(null));
        var pause = Item("Pause rotation", "Icon.Pause", () => viewModel.TogglePauseCommand.Execute(null));

        var saveCurrent = Item("Save current to collection", "Icon.Heart", () => viewModel.SaveCurrentCommand.Execute(null));
        var viewSource = Item("View photo online", "Icon.External", () => viewModel.OpenCurrentSourceCommand.Execute(null));

        var collections = new MenuItem { Header = "Set from collection", Icon = Icon("Icon.Grid") };

        var browse = Item("Browse photos", "Icon.Search", () =>
        {
            viewModel.Section = AppSection.Browse;
            showWindow();
        });

        var settings = Item("Settings", "Icon.Settings", () =>
        {
            viewModel.Section = AppSection.Settings;
            showWindow();
        });

        var quit = Item("Quit Driftwall", "Icon.Close", exit);

        menu.Items.Add(open);
        menu.Items.Add(new Separator());
        menu.Items.Add(next);
        menu.Items.Add(previous);
        menu.Items.Add(refresh);
        menu.Items.Add(pause);
        menu.Items.Add(new Separator());
        menu.Items.Add(saveCurrent);
        menu.Items.Add(viewSource);
        menu.Items.Add(collections);
        menu.Items.Add(new Separator());
        menu.Items.Add(browse);
        menu.Items.Add(settings);
        menu.Items.Add(new Separator());
        menu.Items.Add(quit);

        menu.Opened += (_, _) =>
        {
            bool paused = viewModel.IsPaused;
            pause.Header = paused ? "Resume rotation" : "Pause rotation";
            pause.Icon = Icon(paused ? "Icon.Play" : "Icon.Pause");

            bool hasPhoto = viewModel.CurrentPhoto is not null;
            saveCurrent.IsEnabled = hasPhoto;
            viewSource.IsEnabled = viewModel.CurrentPhoto?.SourcePageUrl is { Length: > 0 };

            RebuildCollections(collections, services, viewModel);
        };

        return menu;
    }

    private static void RebuildCollections(MenuItem parent, AppServices services, MainViewModel viewModel)
    {
        parent.Items.Clear();

        var withPhotos = services.Collections.Collections.Where(c => c.Count > 0).ToList();
        if (withPhotos.Count == 0)
        {
            parent.Items.Add(new MenuItem { Header = "No saved photos yet", IsEnabled = false });
            return;
        }

        foreach (var collection in withPhotos)
        {
            var id = collection.Id;
            var item = Item($"{collection.Name}  ({collection.Count})", null, () =>
            {
                var photos = services.Collections.PhotosIn(id);
                if (photos.Count == 0) return;

                var pick = photos[Random.Shared.Next(photos.Count)];
                _ = services.Rotation.ApplySpecificAsync(pick);
            });

            parent.Items.Add(item);
        }
    }

    private static MenuItem Item(string header, string? iconKey, Action action)
    {
        var item = new MenuItem { Header = header };
        if (iconKey is not null) item.Icon = Icon(iconKey);
        item.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception ex) { Log.Error("Tray menu action failed: " + header, ex); }
        };
        return item;
    }

    /// <summary>Builds a themed icon for a menu row from the shared geometry resources.</summary>
    private static Path Icon(string key)
    {
        var path = new Path
        {
            Width = 15,
            Height = 15,
            StrokeThickness = 1.7,
            Fill = Brushes.Transparent,
            Stretch = Stretch.Uniform,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };

        path.SetResourceReference(Path.DataProperty, key);
        path.SetResourceReference(Shape.StrokeProperty, "Brush.TextSecondary");
        return path;
    }
}
