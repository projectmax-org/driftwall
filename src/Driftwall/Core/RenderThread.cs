using System.Windows.Threading;

namespace Driftwall.Core;

/// <summary>
/// A single long-lived STA thread that owns all WPF imaging work.
/// <para>
/// WPF imaging types are <see cref="DispatcherObject"/>s, so composing on random thread-pool threads
/// would create (and leak) one <see cref="Dispatcher"/> per thread. Doing it on the UI thread would
/// stutter the window. One dedicated thread avoids both, and keeps image work off the UI thread so
/// the app stays responsive while a 40-megapixel photo is being resized.
/// </para>
/// The thread runs at below-normal priority: wallpaper rendering must never compete with a game.
/// </summary>
public static class RenderThread
{
    private static readonly Lock Gate = new();
    private static Dispatcher? _dispatcher;

    private static Dispatcher Dispatcher
    {
        get
        {
            if (_dispatcher is { HasShutdownStarted: false }) return _dispatcher;

            lock (Gate)
            {
                if (_dispatcher is { HasShutdownStarted: false }) return _dispatcher;

                using var ready = new ManualResetEventSlim(false);
                var thread = new Thread(() =>
                {
                    _dispatcher = Dispatcher.CurrentDispatcher;
                    ready.Set();
                    Dispatcher.Run();
                })
                {
                    Name = "Driftwall Render",
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal,
                };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                ready.Wait();

                return _dispatcher!;
            }
        }
    }

    public static Task<T> InvokeAsync<T>(Func<T> work, CancellationToken ct = default) =>
        Dispatcher.InvokeAsync(work, DispatcherPriority.Background, ct).Task;

    public static Task InvokeAsync(Action work, CancellationToken ct = default) =>
        Dispatcher.InvokeAsync(work, DispatcherPriority.Background, ct).Task;

    /// <summary>Stops the render thread. Called during shutdown only.</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            var dispatcher = _dispatcher;
            _dispatcher = null;
            if (dispatcher is { HasShutdownStarted: false }) dispatcher.InvokeShutdown();
        }
    }
}
