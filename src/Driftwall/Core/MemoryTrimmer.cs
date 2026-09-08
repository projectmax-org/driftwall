using System.Runtime;
using Driftwall.Wallpaper;

namespace Driftwall.Core;

/// <summary>
/// Keeps the idle footprint small without turning garbage collection into a background cost.
/// <para>
/// Image work allocates in tens-of-megabytes bursts on the large object heap. Left alone, a mostly
/// idle app can sit on that memory for a long time, which is exactly what a user notices in Task
/// Manager. Requests are therefore debounced: a burst of ten renders causes one collection, not ten.
/// </para>
/// </summary>
public static class MemoryTrimmer
{
    private static readonly Lock Gate = new();
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(4);

    private static Timer? _timer;
    private static bool _pending;

    public static bool Enabled { get; set; } = true;

    /// <summary>Asks for a collection soon. Cheap to call, safe to call often.</summary>
    public static void RequestCollect()
    {
        if (!Enabled) return;

        lock (Gate)
        {
            _pending = true;
            _timer ??= new Timer(_ => Run(compactLargeObjectHeap: false, trimWorkingSet: false), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Full clean-up for moments when the user cannot be inconvenienced by it: the window has just
    /// been hidden, or rotation has just finished and nothing is scheduled for a while.
    /// </summary>
    public static void TrimNow()
    {
        if (!Enabled) return;
        Run(compactLargeObjectHeap: true, trimWorkingSet: true);
    }

    private static void Run(bool compactLargeObjectHeap, bool trimWorkingSet)
    {
        lock (Gate)
        {
            if (!_pending && !compactLargeObjectHeap) return;
            _pending = false;
        }

        try
        {
            if (compactLargeObjectHeap)
            {
                // Image buffers land on the LOH, which is not compacted by default. Doing this on
                // every render would be wasteful; doing it when the window closes is nearly free.
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            }
            else
            {
                GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
            }

            GC.WaitForPendingFinalizers();

            if (trimWorkingSet) TrimWorkingSet();
        }
        catch (Exception ex)
        {
            Log.Warn("Memory trim failed.", ex);
        }
    }

    /// <summary>
    /// Hands committed pages back to Windows. They fault back in on next use, so this is only worth
    /// doing when the app is about to go quiet for a while.
    /// </summary>
    private static void TrimWorkingSet()
    {
        try
        {
            NativeMethods.SetProcessWorkingSetSizeEx(
                NativeMethods.GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1), 0);
        }
        catch (Exception ex)
        {
            Log.Warn("Working-set trim failed.", ex);
        }
    }
}
