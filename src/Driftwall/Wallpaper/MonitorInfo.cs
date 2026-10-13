using System.Runtime.InteropServices;
using Driftwall.Core;
using static Driftwall.Wallpaper.NativeMethods;

namespace Driftwall.Wallpaper;

/// <summary>One connected display, described in true physical pixels.</summary>
public sealed record MonitorInfo
{
    /// <summary>
    /// Stable identity: the monitor's device path from the display driver, for example
    /// "\\?\DISPLAY#DEL41A3#...#{e6f07b5f-...}". Settings and the UI key displays by it.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The id IDesktopWallpaper knows this display by. Usually the same as <see cref="Id"/>, but
    /// Explorer's monitor list can go stale after monitors are swapped on one port and then carries
    /// an attached display under an empty path, which is what this holds then. Null when there is no
    /// IDesktopWallpaper at all, so only the single-image legacy API can set the wallpaper.
    /// </summary>
    public string? ShellId { get; init; }

    /// <summary>EDID name where available (for example "DELL U2720Q"), otherwise a generated label.</summary>
    public required string Name { get; init; }

    /// <summary>Position on the virtual desktop, in physical pixels.</summary>
    public int X { get; init; }
    public int Y { get; init; }

    /// <summary>Native resolution in physical pixels.</summary>
    public int Width { get; init; }
    public int Height { get; init; }

    public bool IsPrimary { get; init; }

    /// <summary>Left-to-right ordering index, used for fallback names and UI layout.</summary>
    public int Index { get; init; }

    public double AspectRatio => Height > 0 ? (double)Width / Height : 0d;

    public string ResolutionLabel => Width + " x " + Height;

    public string DisplayLabel => Name + "  -  " + ResolutionLabel;
}

/// <summary>Discovers connected displays by combining three Windows APIs, each covering the gaps in the others.</summary>
public static class MonitorEnumerator
{
    private const uint DISPLAY_DEVICE_ACTIVE = 0x00000001;
    private const uint DISPLAY_DEVICE_PRIMARY_DEVICE = 0x00000004;

    private static string? _lastUnnamedWarning;

    /// <summary>
    /// Enumerates connected displays. Never throws: on any failure it falls back to a single
    /// virtual-screen-sized monitor so the wallpaper engine always has something to render for.
    /// </summary>
    public static IReadOnlyList<MonitorInfo> Enumerate()
    {
        try
        {
            var monitors = EnumerateCore();
            if (monitors.Count > 0) return monitors;
        }
        catch (Exception ex)
        {
            Log.Warn("Monitor enumeration failed, falling back to the virtual screen.", ex);
        }

        return new[] { FallbackMonitor() };
    }

    private static List<MonitorInfo> EnumerateCore()
    {
        // The display driver is the authority on which displays are on the desktop, where, and at
        // what size. IDesktopWallpaper only supplies the id to set each one's wallpaper with: its
        // list also holds displays that were plugged in once and are gone, and it can hold an
        // attached display under an empty path, so it is not trusted to say what is connected.
        var modes = QueryDisplayModes();
        var friendlyNames = QueryFriendlyNames();
        var shell = QueryShellMonitors(out bool shellAvailable);

        var result = modes.Count > 0
            ? FromDisplayModes(modes, shell, shellAvailable, friendlyNames)
            : FromShell(shell, friendlyNames);

        // Collapse mirrored displays that report identical geometry, then order left to right.
        return result
            .GroupBy(m => (m.X, m.Y, m.Width, m.Height))
            .Select(g => g.First())
            .OrderBy(m => m.X).ThenBy(m => m.Y)
            .Select((m, i) => m with { Index = i, Name = string.IsNullOrWhiteSpace(m.Name) ? "Display " + (i + 1) : m.Name })
            .ToList();
    }

    private static List<MonitorInfo> FromDisplayModes(
        List<DisplayMode> modes, List<ShellMonitor> shell, bool shellAvailable, Dictionary<string, string> friendlyNames)
    {
        var result = new List<MonitorInfo>();
        var unclaimed = shell.ToList();

        foreach (var mode in modes)
        {
            // Normally the shell knows the display by the same device path. When its list is stale
            // the attached entry has an empty path instead, and is found by its position.
            var match = unclaimed.FirstOrDefault(s => s.Id.Length > 0 && string.Equals(s.Id, mode.DevicePath, StringComparison.OrdinalIgnoreCase))
                        ?? unclaimed.FirstOrDefault(s => s.Rect.Left == mode.X && s.Rect.Top == mode.Y);
            if (match is not null) unclaimed.Remove(match);

            // Without the shell right now (Explorer not up yet at sign-in, say), the device path is
            // still the id it will most likely know the display by once it is.
            string? shellId = shellAvailable ? match?.Id ?? mode.DevicePath
                            : !string.IsNullOrEmpty(mode.DevicePath) ? mode.DevicePath : null;
            string id = !string.IsNullOrEmpty(mode.DevicePath) ? mode.DevicePath
                      : !string.IsNullOrEmpty(match?.Id) ? match!.Id
                      : "display:" + mode.AdapterName;

            if (shellAvailable && shellId == string.Empty)
            {
                // Enumeration runs on every display change; the situation is worth one line, not one each time.
                var warning = $"Windows lists the {mode.Width}x{mode.Height} display at ({mode.X},{mode.Y}) without a device path, " +
                              "so its wallpaper is set through that unnamed entry. Signing out and back in rebuilds the list.";
                if (!string.Equals(Interlocked.Exchange(ref _lastUnnamedWarning, warning), warning, StringComparison.Ordinal))
                    Log.Warn(warning);
            }

            result.Add(new MonitorInfo
            {
                Id = id,
                ShellId = shellId,
                Name = NameFor(id, mode.MonitorName, friendlyNames),
                X = mode.X,
                Y = mode.Y,
                Width = mode.Width,
                Height = mode.Height,
                IsPrimary = mode.IsPrimary || (mode.X == 0 && mode.Y == 0),
            });
        }

        return result;
    }

    /// <summary>The older route, for when the driver query returned nothing: the shell's attached entries.</summary>
    private static List<MonitorInfo> FromShell(List<ShellMonitor> shell, Dictionary<string, string> friendlyNames)
    {
        var result = new List<MonitorInfo>();
        foreach (var entry in shell)
        {
            var rect = entry.Rect;
            string id = entry.Id.Length > 0 ? entry.Id : $"shell:{rect.Left},{rect.Top}";
            result.Add(new MonitorInfo
            {
                Id = id,
                ShellId = entry.Id,
                Name = NameFor(entry.Id, null, friendlyNames),
                X = rect.Left,
                Y = rect.Top,
                Width = rect.Width,
                Height = rect.Height,
                IsPrimary = rect.Left == 0 && rect.Top == 0,
            });
        }

        return result;
    }

    private static string NameFor(string devicePath, string? adapterMonitorName, Dictionary<string, string> friendlyNames)
    {
        if (friendlyNames.TryGetValue(devicePath, out var friendly) && !string.IsNullOrWhiteSpace(friendly))
            return friendly.Trim();
        if (!string.IsNullOrWhiteSpace(adapterMonitorName) &&
            !adapterMonitorName.Equals("Generic PnP Monitor", StringComparison.OrdinalIgnoreCase))
            return adapterMonitorName.Trim();
        return string.Empty; // numbered once the list is ordered
    }

    private sealed record ShellMonitor(string Id, RECT Rect);

    /// <summary>The shell's entries that are attached right now. Remembered displays fail GetMonitorRECT.</summary>
    private static List<ShellMonitor> QueryShellMonitors(out bool available)
    {
        var list = new List<ShellMonitor>();
        IDesktopWallpaper? shell = null;
        try
        {
            shell = (IDesktopWallpaper)new DesktopWallpaperClass();
            uint count = shell.GetMonitorDevicePathCount();
            for (uint i = 0; i < count; i++)
            {
                try
                {
                    var id = shell.GetMonitorDevicePathAt(i) ?? string.Empty;
                    var rect = shell.GetMonitorRECT(id);
                    if (rect.Width > 0 && rect.Height > 0) list.Add(new ShellMonitor(id, rect));
                }
                catch (COMException)
                {
                    // Remembered but not connected.
                }
            }

            available = true;
        }
        catch (Exception ex)
        {
            Log.Warn("IDesktopWallpaper is unavailable; wallpapers can only be set for all displays at once.", ex);
            available = false;
        }
        finally
        {
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }

        return list;
    }

    private sealed record DisplayMode(string AdapterName, int X, int Y, int Width, int Height, bool IsPrimary, string? MonitorName, string DevicePath);

    private static List<DisplayMode> QueryDisplayModes()
    {
        var list = new List<DisplayMode>();

        for (uint i = 0; ; i++)
        {
            var device = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevicesW(null, i, ref device, 0)) break;

            if ((device.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;
            if ((device.StateFlags & DISPLAY_DEVICE_MIRRORING_DRIVER) != 0) continue;

            // DEVMODE is authoritative for native pixel size and position regardless of the
            // process's DPI awareness.
            var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettingsW(device.DeviceName, ENUM_CURRENT_SETTINGS, ref dm)) continue;
            if (dm.dmPelsWidth == 0 || dm.dmPelsHeight == 0) continue;

            // The monitor on this output: its interface path is the device path IDesktopWallpaper
            // and DisplayConfig use, and its description is a fallback name.
            string? monitorName = null;
            string devicePath = string.Empty;
            for (uint m = 0; ; m++)
            {
                var monitor = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (!EnumDisplayDevicesW(device.DeviceName, m, ref monitor, EDD_GET_DEVICE_INTERFACE_NAME)) break;
                if ((monitor.StateFlags & DISPLAY_DEVICE_ACTIVE) == 0 && m > 0) continue;

                monitorName = monitor.DeviceString;
                devicePath = monitor.DeviceID ?? string.Empty;
                if ((monitor.StateFlags & DISPLAY_DEVICE_ACTIVE) != 0) break;
            }

            list.Add(new DisplayMode(
                device.DeviceName, dm.dmPosition.x, dm.dmPosition.y, (int)dm.dmPelsWidth, (int)dm.dmPelsHeight,
                (device.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0, monitorName, devicePath));
        }

        return list;
    }

    /// <summary>Maps shell monitor device paths to EDID friendly names via the DisplayConfig API.</summary>
    private static Dictionary<string, string> QueryFriendlyNames()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != ERROR_SUCCESS)
                return map;
            if (pathCount == 0) return map;

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != ERROR_SUCCESS)
                return map;

            for (int i = 0; i < pathCount; i++)
            {
                var request = new DISPLAYCONFIG_TARGET_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                        adapterId = paths[i].targetInfo.adapterId,
                        id = paths[i].targetInfo.id,
                    },
                    monitorFriendlyDeviceName = string.Empty,
                    monitorDevicePath = string.Empty,
                };

                if (DisplayConfigGetDeviceInfo(ref request) != ERROR_SUCCESS) continue;
                if (string.IsNullOrWhiteSpace(request.monitorDevicePath)) continue;

                map[request.monitorDevicePath] = request.monitorFriendlyDeviceName;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("DisplayConfig friendly-name lookup failed.", ex);
        }

        return map;
    }

    private static MonitorInfo FallbackMonitor()
    {
        int w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        return new MonitorInfo
        {
            Id = "virtual-screen",
            ShellId = null,
            Name = "Display 1",
            X = GetSystemMetrics(SM_XVIRTUALSCREEN),
            Y = GetSystemMetrics(SM_YVIRTUALSCREEN),
            Width = w > 0 ? w : 1920,
            Height = h > 0 ? h : 1080,
            IsPrimary = true,
            Index = 0,
        };
    }
}
