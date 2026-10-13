using System.Security.AccessControl;
using System.Security.Principal;

namespace Driftwall.Core;

/// <summary>
/// Ensures only one copy of Driftwall runs, and lets a second launch bring the first one forward.
/// <para>
/// A mutex detects the duplicate and a named event carries the "show yourself" signal. This is used
/// rather than a window broadcast because the first instance usually has no visible window to find:
/// it is sitting in the notification area.
/// </para>
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Driftwall.SingleInstance";
    private const string SignalName = @"Local\Driftwall.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _signal;
    private CancellationTokenSource? _listener;

    private SingleInstance(Mutex mutex, EventWaitHandle signal, bool isFirst)
    {
        _mutex = mutex;
        _signal = signal;
        IsFirstInstance = isFirst;
    }

    public bool IsFirstInstance { get; }

    /// <summary>Raised on a background thread when another launch asks this instance to show itself.</summary>
    public event EventHandler? ActivationRequested;

    public static SingleInstance Acquire()
    {
        // The mutex and the event are this user's, elevated or not. Left to the default, a copy run as
        // administrator would make them open to administrators alone, and a normal launch beside it
        // could then neither find it nor wake it. Nobody else is given anything.
        MutexSecurity? mutexSecurity = null;
        EventWaitHandleSecurity? signalSecurity = null;
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (identity.User is { } user)
            {
                mutexSecurity = new MutexSecurity();
                mutexSecurity.AddAccessRule(new MutexAccessRule(user, MutexRights.FullControl, AccessControlType.Allow));
                signalSecurity = new EventWaitHandleSecurity();
                signalSecurity.AddAccessRule(new EventWaitHandleAccessRule(user, EventWaitHandleRights.FullControl, AccessControlType.Allow));
            }
        }

        var mutex = MutexAcl.Create(initiallyOwned: true, MutexName, out bool createdNew, mutexSecurity);
        var signal = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, SignalName, out _, signalSecurity);
        return new SingleInstance(mutex, signal, createdNew);
    }

    /// <summary>Called by a duplicate launch to wake the running instance.</summary>
    public void SignalFirstInstance()
    {
        try { _signal.Set(); }
        catch (Exception ex) { Log.Warn("Could not signal the running instance.", ex); }
    }

    /// <summary>Starts listening for activation requests. Only the first instance should call this.</summary>
    public void StartListening()
    {
        if (!IsFirstInstance || _listener is not null) return;

        _listener = new CancellationTokenSource();
        var token = _listener.Token;

        var thread = new Thread(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // A timeout keeps the thread responsive to cancellation on shutdown.
                    if (_signal.WaitOne(TimeSpan.FromSeconds(1)))
                        ActivationRequested?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    Log.Warn("Activation listener failed.", ex);
                    return;
                }
            }
        })
        {
            Name = "Driftwall Activation",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };

        thread.Start();
    }

    public void Dispose()
    {
        _listener?.Cancel();
        _listener?.Dispose();

        try
        {
            if (IsFirstInstance) _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned; nothing to release.
        }

        _mutex.Dispose();
        _signal.Dispose();
    }
}
