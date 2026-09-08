using System.Runtime.InteropServices;
using Driftwall.Core;
using static Driftwall.Wallpaper.NativeMethods;

namespace Driftwall.Wallpaper;

/// <summary>One connected display, described in true physical pixels.</summary>
public sealed record MonitorInfo
{
    /// <summary>Shell monitor device path. Passed to IDesktopWallpaper.SetWallpaper.</summary>
    public required string Id { get; init; }

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
        // IDesktopWallpaper supplies the monitor IDs that SetWallpaper needs, plus each one's desktop
        // rect. Displays that are remembered but not currently attached are also in this list, and
        // GetMonitorRECT throws for those, which is how we filter them out.
        var shell = (IDesktopWallpaper)new DesktopWallpaperClass();
        var result = new List<MonitorInfo>();

        try
        {
            // DEVMODE is authoritative for native pixel size regardless of the process's DPI
            // awareness, and dmPosition lets us match it back to a shell monitor rect.
            var modes = QueryDisplayModes();
            var friendlyNames = QueryFriendlyNames();

            uint count = shell.GetMonitorDevicePathCount();
            for (uint i = 0; i < count; i++)
            {
                string id;
                RECT rect;
                try
                {
                    id = shell.GetMonitorDevicePathAt(i);
                    if (string.IsNullOrEmpty(id)) continue;
                    rect = shell.GetMonitorRECT(id);
                }
                catch (COMException)
                {
                    continue;
                }

                if (rect.Width <= 0 || rect.Height <= 0) continue;

                var mode = modes.FirstOrDefault(m => m.X == rect.Left && m.Y == rect.Top);
                int width = mode is { Width: > 0 } ? mode.Width : rect.Width;
                int height = mode is { Height: > 0 } ? mode.Height : rect.Height;

                string? name = null;
                if (friendlyNames.TryGetValue(id, out var friendly) && !string.IsNullOrWhiteSpace(friendly))
                    name = friendly.Trim();
                if (string.IsNullOrWhiteSpace(name)) name = mode?.MonitorName;
                if (string.IsNullOrWhiteSpace(name) || name.Equals("Generic PnP Monitor", StringComparison.OrdinalIgnoreCase))
                    name = "Display " + (result.Count + 1);

                result.Add(new MonitorInfo
                {
                    Id = id,
                    Name = name,
                    X = rect.Left,
                    Y = rect.Top,
                    Width = width,
                    Height = height,
                    IsPrimary = rect.Left == 0 && rect.Top == 0,
                    Index = result.Count,
                });
            }
        }
        finally
        {
            if (Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }

        // Collapse mirrored displays that report identical geometry, then order left to right.
        return result
            .GroupBy(m => (m.X, m.Y, m.Width, m.Height))
            .Select(g => g.First())
            .OrderBy(m => m.X).ThenBy(m => m.Y)
            .Select((m, i) => m with { Index = i })
            .ToList();
    }

    private sealed record DisplayMode(int X, int Y, int Width, int Height, string? MonitorName);

    private static List<DisplayMode> QueryDisplayModes()
    {
        var list = new List<DisplayMode>();

        for (uint i = 0; ; i++)
        {
            var device = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevicesW(null, i, ref device, 0)) break;

            if ((device.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;
            if ((device.StateFlags & DISPLAY_DEVICE_MIRRORING_DRIVER) != 0) continue;

            var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettingsW(device.DeviceName, ENUM_CURRENT_SETTINGS, ref dm)) continue;

            // Asking the adapter for its attached monitor yields a better label than the adapter name.
            string? monitorName = null;
            var monitor = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (EnumDisplayDevicesW(device.DeviceName, 0, ref monitor, EDD_GET_DEVICE_INTERFACE_NAME))
                monitorName = monitor.DeviceString;

            list.Add(new DisplayMode(dm.dmPosition.x, dm.dmPosition.y, (int)dm.dmPelsWidth, (int)dm.dmPelsHeight, monitorName));
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
            Id = string.Empty,
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
