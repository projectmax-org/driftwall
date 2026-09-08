using System.Diagnostics;

namespace Driftwall.Core;

/// <summary>Small wrappers around shell operations, each one non-throwing.</summary>
public static class Shell
{
    /// <summary>Opens a URL in the default browser. Ignores anything that is not http(s).</summary>
    public static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        // Only http(s) is launched: photo metadata comes from the network, and handing an arbitrary
        // scheme to ShellExecute would let a provider choose what runs on the user's machine.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("Could not open " + url, ex);
        }
    }

    /// <summary>Opens a folder in File Explorer, optionally with a file selected.</summary>
    public static void OpenFolder(string? path, bool selectFile = false)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            if (selectFile && File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("Could not open " + path, ex);
        }
    }

    /// <summary>Shows the folder picker. Returns null when the user cancels.</summary>
    public static string? PickFolder(string title = "Choose a folder")
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = title,
                Multiselect = false,
            };

            return dialog.ShowDialog() == true ? dialog.FolderName : null;
        }
        catch (Exception ex)
        {
            Log.Warn("Folder picker failed.", ex);
            return null;
        }
    }
}
