using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Photino.Blazor;

namespace Lantern.Blazor.Tests;

// Emulates the Photino dispatcher: a dedicated UI thread that runs marshalled callbacks
// without flowing the caller's ExecutionContext (like native dispatch).
internal sealed class TestUiDispatcher : IPhotinoUiDispatcher, IDisposable
{
    private readonly BlockingCollection<Action> _queue = [];
    private readonly Thread _thread;
    private int _marshalledInvokeCount;

    public TestUiDispatcher()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Test UI thread" };

        // Start without flowing the creator's ExecutionContext into the UI thread.
        using (ExecutionContext.SuppressFlow())
        {
            _thread.Start();
        }
    }

    public int ThreadId => _thread.ManagedThreadId;

    // Whether synchronous dispatch is available (the message loop runs).
    public bool IsRunning { get; set; } = true;

    public bool CanPost { get; set; }

    // Accepts posts but never runs them, like a message loop that exits with pending callbacks.
    public bool DropPosts { get; set; }

    // Number of TryInvoke calls that blocked a caller thread to run on the UI thread.
    public int MarshalledInvokeCount => Volatile.Read(ref _marshalledInvokeCount);

    public bool CheckAccess() => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    public bool TryInvoke<TState>(Action<TState> callback, TState state)
    {
        if (CheckAccess())
        {
            callback(state);
            return true;
        }

        if (!IsRunning)
            return false;

        Interlocked.Increment(ref _marshalledInvokeCount);

        using var done = new ManualResetEventSlim();
        ExceptionDispatchInfo? exception = null;

        _queue.Add(() =>
        {
            try
            {
                callback(state);
            }
            catch (Exception ex)
            {
                exception = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                done.Set();
            }
        });

        done.Wait();
        exception?.Throw();
        return true;
    }

    public bool Post(SendOrPostCallback callback, object? state)
    {
        if (!IsRunning)
            return false;

        if (!DropPosts)
            _queue.Add(() => callback(state));

        return true;
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join();
        _queue.Dispose();
    }

    private void Run()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
            action();
    }
}
