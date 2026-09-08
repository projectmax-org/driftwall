using System.Runtime.InteropServices;
using Driftwall.Core;
using Driftwall.Wallpaper;

namespace Driftwall.Scheduling;

/// <summary>Why a scheduled change was skipped, so the UI can say something better than "paused".</summary>
public enum DeferReason
{
    None = 0,
    FullscreenApp,
    Battery,
    MeteredNetwork,
    NoNetwork,
}

/// <summary>
/// Cheap, on-demand checks for "is now a bad time to change the wallpaper".
/// <para>
/// Everything here is queried at the moment the timer fires rather than polled, which is the whole
/// point: an app that watches for fullscreen games every second is exactly the kind of background
/// process this one is trying not to be.
/// </para>
/// </summary>
public static class SystemConditions
{
    /// <summary>Returns the first reason to defer, or <see cref="DeferReason.None"/> to go ahead.</summary>
    public static DeferReason Evaluate(AppSettings settings)
    {
        if (settings.PauseOnFullscreenApp && IsFullscreenAppRunning()) return DeferReason.FullscreenApp;
        if (settings.PauseOnBattery && IsOnBattery()) return DeferReason.Battery;
        if (settings.PauseOnMeteredNetwork && IsNetworkMetered()) return DeferReason.MeteredNetwork;
        return DeferReason.None;
    }

    public static string Describe(DeferReason reason) => reason switch
    {
        DeferReason.FullscreenApp => "Paused while a fullscreen app is running",
        DeferReason.Battery => "Paused while on battery",
        DeferReason.MeteredNetwork => "Paused on a metered connection",
        DeferReason.NoNetwork => "Waiting for a network connection",
        _ => "Running",
    };

    /// <summary>
    /// True while a game, a video in fullscreen, or a presentation is in the foreground. This is the
    /// same signal Windows uses to suppress its own notifications.
    /// </summary>
    public static bool IsFullscreenAppRunning()
    {
        try
        {
            if (NativeMethods.SHQueryUserNotificationState(out var state) != 0) return false;

            return state is NativeMethods.QueryUserNotificationState.RunningDirect3DFullScreen
                        or NativeMethods.QueryUserNotificationState.PresentationMode
                        or NativeMethods.QueryUserNotificationState.Busy;
        }
        catch (Exception ex)
        {
            Log.Warn("Fullscreen check failed.", ex);
            return false;
        }
    }

    public static bool IsOnBattery()
    {
        try
        {
            if (!GetSystemPowerStatus(out var status)) return false;
            // ACLineStatus: 0 offline, 1 online, 255 unknown.
            return status.ACLineStatus == 0;
        }
        catch (Exception ex)
        {
            Log.Warn("Battery check failed.", ex);
            return false;
        }
    }

    /// <summary>Asks the Network List Manager whether the current connection is metered.</summary>
    public static bool IsNetworkMetered()
    {
        object? manager = null;
        try
        {
            var type = Type.GetTypeFromCLSID(CLSID_NetworkListManager);
            if (type is null) return false;

            manager = Activator.CreateInstance(type);
            if (manager is not INetworkCostManager costManager) return false;

            costManager.GetCost(out uint cost, IntPtr.Zero);

            const uint Unknown = 0x0;
            const uint Unrestricted = 0x1;
            if (cost == Unknown) return false;

            return (cost & Unrestricted) == 0;
        }
        catch (Exception ex)
        {
            Log.Warn("Metered-network check failed; assuming unmetered.", ex);
            return false;
        }
        finally
        {
            if (manager is not null && Marshal.IsComObject(manager)) Marshal.FinalReleaseComObject(manager);
        }
    }

    public static bool HasNetwork()
    {
        try { return System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable(); }
        catch { return true; }
    }

    // ---------------- interop ----------------

    private static readonly Guid CLSID_NetworkListManager = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

    [ComImport]
    [Guid("DCB00008-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INetworkCostManager
    {
        void GetCost(out uint pCost, IntPtr pDestIPAddr);
        void GetDataPlanStatus(IntPtr pDataPlanStatus, IntPtr pDestIPAddr);
        void SetDestinationAddresses(uint length, IntPtr pDestIPAddrList, [MarshalAs(UnmanagedType.VariantBool)] bool bAppend);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);
}
