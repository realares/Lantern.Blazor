using Photino.NET;

namespace Photino.Blazor;

// The parts of the Photino dispatcher used by PhotinoSynchronizationContext.
// Abstracted so the synchronization context can be exercised against a real UI thread in tests.
internal interface IPhotinoUiDispatcher
{
    // Whether the current thread is the dispatcher (UI) thread.
    bool CheckAccess();

    // Runs the callback on the dispatcher thread and waits for it. Returns false when dispatch is not
    // available (before the message loop starts or after shutdown) without running the callback.
    // ExecutionContext does not flow to the dispatcher thread.
    bool TryInvoke<TState>(Action<TState> callback, TState state);
}

internal sealed class PhotinoUiDispatcher(PhotinoDispatcher dispatcher) : IPhotinoUiDispatcher
{
    private readonly PhotinoDispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public bool CheckAccess() => _dispatcher.CheckAccess();

    public bool TryInvoke<TState>(Action<TState> callback, TState state) => _dispatcher.TryInvoke(callback, state);
}
