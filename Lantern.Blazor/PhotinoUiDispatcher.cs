using Photino.NET;

namespace Photino.Blazor;

// The parts of the Photino dispatcher used by PhotinoSynchronizationContext.
// Abstracted so the synchronization context can be exercised against a real UI thread in tests.
internal interface IPhotinoUiDispatcher
{
    // Whether asynchronous posts are currently processed by the dispatcher thread
    // (the message loop is running and not shutting down).
    bool CanPost { get; }

    // Whether the current thread is the dispatcher (UI) thread.
    bool CheckAccess();

    // Runs the callback on the dispatcher thread and waits for it. Returns false when dispatch is not
    // available (before the message loop starts or after shutdown) without running the callback.
    // ExecutionContext does not flow to the dispatcher thread.
    bool TryInvoke<TState>(Action<TState> callback, TState state);

    // Queues the callback to the dispatcher thread without waiting. Returns false when it could not be queued.
    // A queued callback may still be dropped without running when the message loop exits.
    // ExecutionContext does not flow to the dispatcher thread.
    bool Post(SendOrPostCallback callback, object? state);
}

internal sealed class PhotinoUiDispatcher : IPhotinoUiDispatcher
{
    private readonly PhotinoDispatcher _dispatcher;
    private readonly PhotinoApplication? _application;

    // Without an application, posting is never used and work is marshalled synchronously.
    public PhotinoUiDispatcher(PhotinoDispatcher dispatcher, PhotinoApplication? application = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        _dispatcher = dispatcher;
        _application = application;
    }

    public bool CanPost => _application is { IsRunning: true, IsShuttingDown: false };

    public bool CheckAccess() => _dispatcher.CheckAccess();

    public bool TryInvoke<TState>(Action<TState> callback, TState state) => _dispatcher.TryInvoke(callback, state);

    public bool Post(SendOrPostCallback callback, object? state) => _dispatcher.Post(callback, state);
}
