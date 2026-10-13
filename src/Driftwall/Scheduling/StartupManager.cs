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

    // Where Task Manager and Settings > Apps > Startup record that the user turned an entry off. The
    // Run value itself stays; a binary value of the same name here says whether Windows may start it.
    private const string ApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

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
            if (key?.GetValue(ValueName) is not string value || value.Length == 0) return false;

            // An odd first byte (03, 07) is a disable made in Task Manager: Windows skips the entry at
            // sign-in. No value at all, or an even first byte, means it runs.
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath);
            return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } flag || (flag[0] & 1) == 0;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the startup registry key.", ex);
            return false;
        }
    }

    /// <summary>
    /// True when there is a startup entry at all, switched off in Task Manager or not. Code that only
    /// makes sure an entry exists asks this, so it never undoes a switch-off the user made there.
    /// </summary>
    public static bool HasEntry()
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

            // Switched on here, the entry must really run, whatever Task Manager was told before.
            // Switched off, no old disable should be left waiting for the next time it is turned on.
            ClearStartupApproval();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not update the startup registry key.", ex);
            return false;
        }
    }

    /// <summary>Forgets a disable recorded by Task Manager. With no value there, Windows starts the entry.</summary>
    private static void ClearStartupApproval()
    {
        try
        {
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, writable: true);
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not clear the Task Manager startup setting.", ex);
        }
    }

    /// <summary>
    /// Re-points an existing entry at the current executable when the program it names is gone,
    /// which is what happens when the user moves the app. An entry that still names a working copy
    /// is left alone: with an installed and a portable copy side by side, the one launched last must
    /// not take start-with-Windows away from the other.
    /// </summary>
    public static void RepairIfNeeded()
    {
        try
        {
            // An entry turned off in Task Manager is re-pointed too, and stays off: the disable is
            // kept by name, so turning it back on there then starts a program that exists.
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(ValueName) is not string existing || existing.Length == 0) return;

            var expected = CommandLine;
            if (!string.IsNullOrEmpty(expected) && !string.Equals(existing, expected, StringComparison.OrdinalIgnoreCase) &&
                !File.Exists(ExecutableIn(existing)))
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

    /// <summary>True when a Run entry exists but the program it names is gone.</summary>
    public static bool HasStaleEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string command && command.Trim().Length > 0 && !File.Exists(ExecutableIn(command));
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the startup registry key.", ex);
            return false;
        }
    }

    /// <summary>
    /// The program a Run command starts. A quoted path is taken as is; an unquoted one is read the way
    /// CreateProcess reads it, trying each space in turn, so "C:\Program Files\App\App.exe --x" works.
    /// </summary>
    private static string ExecutableIn(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : command.Trim('"');
        }

        for (int space = command.IndexOf(' '); space > 0; space = command.IndexOf(' ', space + 1))
        {
            var prefix = command[..space];
            if (File.Exists(prefix)) return prefix;
            if (File.Exists(prefix + ".exe")) return prefix + ".exe";
        }

        return command;
    }
}
