using Driftwall.Core;
using Microsoft.Win32;

namespace Driftwall.Scheduling;

/// <summary>
/// Registers the app to start with Windows.
/// <para>
/// Uses the per-user Run key rather than a scheduled task or a service: it needs no elevation, it is
/// visible to the user in Task Manager's Startup tab (where they would expect to find it), and it is
/// the one mechanism Windows lets them disable themselves.
/// </para>
/// </summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Driftwall";

    /// <summary>Path to the running executable, quoted and with the start-minimised switch.</summary>
    private static string CommandLine
    {
        get
        {
            // Environment.ProcessPath is the only correct answer under single-file publishing:
            // Assembly.Location is empty for assemblies embedded in the bundle.
            var executable = Environment.ProcessPath;
            return string.IsNullOrEmpty(executable) ? string.Empty : $"\"{executable}\" --minimized";
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the startup registry key.", ex);
            return false;
        }
    }

    /// <summary>Returns true when the change took effect.</summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null) return false;

            if (enabled)
            {
                var command = CommandLine;
                if (string.IsNullOrEmpty(command))
                {
                    Log.Warn("Could not determine the executable path; start-with-Windows not enabled.");
                    return false;
                }

                key.SetValue(ValueName, command, RegistryValueKind.String);
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not update the startup registry key.", ex);
            return false;
        }
    }

    /// <summary>
    /// Re-points an existing entry at the current executable. Worth doing on every launch because
    /// the path changes when the user moves the app or it updates through a store.
    /// </summary>
    public static void RepairIfNeeded()
    {
        if (!IsEnabled()) return;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(ValueName) is not string existing) return;

            var expected = CommandLine;
            if (!string.IsNullOrEmpty(expected) && !string.Equals(existing, expected, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(ValueName, expected, RegistryValueKind.String);
                Log.Info("Startup entry re-pointed at the current executable.");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not repair the startup entry.", ex);
        }
    }
}
