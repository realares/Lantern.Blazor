using System.Diagnostics;
using Photino.NET;

namespace Photino.Blazor;
// Most UI platforms have a built-in SynchronizationContext/Dispatcher, e.g. Windows Forms and WPF,
// which Blazor can normally use directly.
//
// Photino provides an application dispatcher, but Blazor still needs a SynchronizationContext
// that serializes renderer work items, flows ExecutionContext, and sets SynchronizationContext.Current
// while executing component work.
//
// During pre-dispatch initialization, or after native dispatch has been disabled during shutdown,
// work items are executed directly instead of going through the Photino dispatcher.
//
// The Photino dispatcher marshals natively and does not flow ExecutionContext, so the captured
// context is applied on the thread that finally runs the callback.
//
// Queued work items are scheduled with PhotinoDispatcherTaskScheduler: while the message loop runs they
// are posted straight to the dispatcher thread; otherwise they go through the thread pool.
internal sealed class PhotinoSynchronizationContext : SynchronizationContext
{
    private static readonly ContextCallback s_executionContextThunk = static state =>
    {
        var execution = (Execution)state!;
        execution.Context.ExecuteCallback(execution.Completion, execution.Callback, execution.State);
    };

    private static readonly Action<Task, object?> s_backgroundWorkThunk = static (task, state) =>
    {
        var item = (WorkItem)state!;
        item.SynchronizationContext.ExecuteBackground(item);
    };

    private readonly IPhotinoUiDispatcher _dispatcher;

    internal PhotinoSynchronizationContext(PhotinoDispatcher dispatcher)
        : this(new PhotinoUiDispatcher(dispatcher))
    {
    }

    internal PhotinoSynchronizationContext(IPhotinoUiDispatcher dispatcher)
        : this(dispatcher, new State(dispatcher))
    {
    }

    private PhotinoSynchronizationContext(IPhotinoUiDispatcher dispatcher, State state)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(state);

        _dispatcher = dispatcher;
        _state = state;
    }

    private readonly State _state;

    public event UnhandledExceptionEventHandler? UnhandledException;

    public Task InvokeAsync(Action action)
    {
        var completion = new PhotinoSynchronizationTaskCompletionSource<Action>(action);
        ExecuteSynchronouslyIfPossible(static state =>
        {
            var completion = (PhotinoSynchronizationTaskCompletionSource<Action>)state!;
            try
            {
                completion.Callback();
                completion.SetResult();
            }
            catch (OperationCanceledException)
            {
                completion.SetCanceled();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }, completion);

        return completion.Task;
    }

    public Task InvokeAsync(Func<Task> asyncAction)
    {
        var completion = new PhotinoSynchronizationTaskCompletionSource<Func<Task>>(asyncAction);
        ExecuteSynchronouslyIfPossible(static state =>
        {
            var completion = (PhotinoSynchronizationTaskCompletionSource<Func<Task>>)state!;
            try
            {
                var task = completion.Callback();

                if (task.IsCompleted)
                {
                    task.GetAwaiter().GetResult();
                    completion.SetResult();
                    return;
                }

                _ = CompleteAsyncOperationAsync(task, completion);
            }
            catch (OperationCanceledException)
            {
                completion.SetCanceled();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }, completion);

        return completion.Task;
    }

    public Task<TResult> InvokeAsync<TResult>(Func<TResult> function)
    {
        var completion = new PhotinoSynchronizationTaskCompletionSource<Func<TResult>, TResult>(function);
        ExecuteSynchronouslyIfPossible(static state =>
        {
            var completion = (PhotinoSynchronizationTaskCompletionSource<Func<TResult>, TResult>)state!;
            try
            {
                var result = completion.Callback();
                completion.SetResult(result);
            }
            catch (OperationCanceledException)
            {
                completion.SetCanceled();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }, completion);

        return completion.Task;
    }

    public Task<TResult> InvokeAsync<TResult>(Func<Task<TResult>> asyncFunction)
    {
        var completion = new PhotinoSynchronizationTaskCompletionSource<Func<Task<TResult>>, TResult>(asyncFunction);
        ExecuteSynchronouslyIfPossible(static state =>
        {
            var completion = (PhotinoSynchronizationTaskCompletionSource<Func<Task<TResult>>, TResult>)state!;
            try
            {
                var task = completion.Callback();

                if (task.IsCompleted)
                {
                    var result = task.GetAwaiter().GetResult();
                    completion.SetResult(result);
                    return;
                }

                _ = CompleteAsyncOperationAsync(task, completion);
            }
            catch (OperationCanceledException)
            {
                completion.SetCanceled();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }, completion);

        return completion.Task;
    }

    // Runs the callback asynchronously.
    //
    // NOTE: this must always run async. It's not legal here to execute the work item synchronously.
    public override void Post(SendOrPostCallback d, object? state)
    {
        lock (_state.Lock)
        {
            _state.Task = Enqueue(_state.Task, d, state, forceAsync: true);
        }

        _state.Scheduler.RecoverDroppedTasks();
    }

    // Runs the callback synchronously.
    public override void Send(SendOrPostCallback d, object? state)
    {
        Task antecedent;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_state.Lock)
        {
            antecedent = _state.Task;
            _state.Task = completion.Task;
        }

        // A queued item dropped by a stopped message loop would otherwise never complete the antecedent.
        _state.Scheduler.RecoverDroppedTasks();

        // We have to block. That's the contract of Send - we don't expect this to be used
        // in many scenarios in Components.
        //
        // Using Wait here is ok because the antecedent task will never throw.
        antecedent.Wait();

        ExecuteSynchronously(completion, d, state, CaptureExecutionContextForDispatch(), runsOnCallerContext: true);
    }

    // Called once the application's message loop has exited. Work that was posted to the loop and dropped
    // is moved to the thread pool, and later work no longer goes through the dispatcher's post queue.
    internal void NotifyMessageLoopExited()
    {
        _state.Scheduler.NotifyMessageLoopExited();
    }

    // shallow copy
    public override SynchronizationContext CreateCopy()
    {
        return new PhotinoSynchronizationContext(_dispatcher, _state);
    }

    // Runs the callback synchronously when the context is idle; otherwise, queues it
    // after the current operation.
    private void ExecuteSynchronouslyIfPossible(SendOrPostCallback d, object? state)
    {
        TaskCompletionSource? completion = null;

        lock (_state.Lock)
        {
            if (_state.Task.IsCompleted)
            {
                // We can execute this synchronously because nothing is currently running or queued.
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _state.Task = completion.Task;
            }
            else
            {
                _state.Task = Enqueue(_state.Task, d, state);
            }
        }

        if (completion is null)
        {
            // Work queued during shutdown (for example renderer disposal) must not wait behind
            // an item that the stopped message loop dropped.
            _state.Scheduler.RecoverDroppedTasks();
            return;
        }

        ExecuteSynchronously(completion, d, state, CaptureExecutionContextForDispatch(), runsOnCallerContext: true);
    }

    // The caller's context only needs to be carried along when the callback is marshalled to the
    // dispatcher thread; on the dispatcher thread itself it is already current.
    private ExecutionContext? CaptureExecutionContextForDispatch()
    {
        return _dispatcher.CheckAccess() ? null : ExecutionContext.Capture();
    }

    private Task Enqueue(Task antecedent, SendOrPostCallback d, object? state, bool forceAsync = false)
    {
        // If we get here, it means that a callback is being explicitly queued. Let's instead add it to the queue and yield.
        //
        // We use our own queue (a chain of continuations) to maintain the execution order of the callbacks
        // scheduled here. While the message loop runs, the scheduler posts each item directly to the dispatcher
        // thread, so no thread-pool thread is blocked waiting for the UI thread.
        //
        // HideScheduler keeps TaskScheduler.Current at Default inside component code, so Task.Factory.StartNew
        // and ContinueWith without an explicit scheduler do not end up on the UI thread.
        //
        // We need to capture the execution context so we can restore it later. This code is similar to
        // the call path of ThreadPool.QueueUserWorkItem and System.Threading.QueueUserWorkItemCallback.
        ExecutionContext? executionContext = null;
        if (!ExecutionContext.IsFlowSuppressed())
        {
            executionContext = ExecutionContext.Capture();
        }

        var flags = TaskContinuationOptions.HideScheduler;
        if (forceAsync)
            flags |= TaskContinuationOptions.RunContinuationsAsynchronously;

        return antecedent.ContinueWith(s_backgroundWorkThunk, new WorkItem(
            SynchronizationContext: this,
            ExecutionContext: executionContext,
            Callback: d,
            State: state
        ), CancellationToken.None, flags, _state.Scheduler);
    }

    // executionContext is applied on the thread that runs the callback. runsOnCallerContext indicates that
    // the current thread already runs in that context, so it is not reapplied when dispatch is unavailable.
    private void ExecuteSynchronously(
        TaskCompletionSource? completion,
        SendOrPostCallback callback,
        object? state,
        ExecutionContext? executionContext,
        bool runsOnCallerContext)
    {
        var execution = new Execution(this, completion, callback, state, executionContext);

        // Anything run on the sync context should be dispatched through Photino when
        // dispatch is available, so native window/WebView access stays on the dispatcher thread.
        if (_dispatcher.TryInvoke(static execution => execution.Context.ExecuteSynchronouslyCore(execution), execution))
            return;

        ExecuteSynchronouslyCore(runsOnCallerContext ? execution with { ExecutionContext = null } : execution);
    }

    private void ExecuteSynchronouslyCore(Execution execution)
    {
        if (execution.ExecutionContext is null)
        {
            ExecuteCallback(execution.Completion, execution.Callback, execution.State);
            return;
        }

        // Perf - using a static thunk here to avoid a delegate allocation.
        ExecutionContext.Run(execution.ExecutionContext, s_executionContextThunk, execution);
    }

    private void ExecuteCallback(TaskCompletionSource? completion, SendOrPostCallback callback, object? state)
    {
        var original = Current;
        try
        {
            var busyCount = Interlocked.Increment(ref _state.BusyCount);
            Debug.Assert(busyCount == 1, $"Executing callback. BusyCount: {busyCount}");

            SetSynchronizationContext(this);
            callback(state);
        }
        finally
        {
            SetSynchronizationContext(original);

            var busyCount = Interlocked.Decrement(ref _state.BusyCount);
            Debug.Assert(busyCount == 0, $"Finished executing callback. BusyCount: {busyCount}");

            completion?.SetResult();
        }
    }

    private void ExecuteBackground(WorkItem item)
    {
        try
        {
            // The captured context is applied where the callback runs (usually the dispatcher thread),
            // not on the thread-pool thread that forwards the work item.
            ExecuteSynchronously(null, item.Callback, item.State, item.ExecutionContext, runsOnCallerContext: false);
        }
        catch (Exception ex)
        {
            DispatchException(ex);
        }
    }

    // Observes the asynchronous callback after its synchronous portion has left the
    // synchronization context queue and propagates its completion to the caller.
    private static async Task CompleteAsyncOperationAsync(Task task, PhotinoSynchronizationTaskCompletionSource<Func<Task>> completion)
    {
        try
        {
            await task.ConfigureAwait(false);
            completion.SetResult();
        }
        catch (OperationCanceledException)
        {
            completion.SetCanceled();
        }
        catch (Exception exception)
        {
            completion.SetException(exception);
        }
    }

    private static async Task CompleteAsyncOperationAsync<TResult>(Task<TResult> task, PhotinoSynchronizationTaskCompletionSource<Func<Task<TResult>>, TResult> completion)
    {
        try
        {
            var result = await task.ConfigureAwait(false);
            completion.SetResult(result);
        }
        catch (OperationCanceledException)
        {
            completion.SetCanceled();
        }
        catch (Exception exception)
        {
            completion.SetException(exception);
        }
    }

    private void DispatchException(Exception ex)
    {
        UnhandledException?.Invoke(this, new UnhandledExceptionEventArgs(ex, isTerminating: false));
    }

    private sealed class State(IPhotinoUiDispatcher dispatcher)
    {
        // Shared by copies of the context, like the queue itself.
        public readonly PhotinoDispatcherTaskScheduler Scheduler = new(dispatcher);
        internal int BusyCount;
        private bool IsBusy => Volatile.Read(ref BusyCount) > 0; // Just for debugging
#if NET9_0_OR_GREATER
        public readonly Lock Lock = new();
#else
        public readonly object Lock = new();
#endif
        public Task Task = Task.CompletedTask;

        public override string ToString()
        {
            return $"{{ Busy: {IsBusy}, Pending Task: {Task} }}";
        }
    }

    private readonly record struct Execution(
        PhotinoSynchronizationContext Context,
        TaskCompletionSource? Completion,
        SendOrPostCallback Callback,
        object? State,
        ExecutionContext? ExecutionContext);

    private record WorkItem(
        PhotinoSynchronizationContext SynchronizationContext,
        ExecutionContext? ExecutionContext,
        SendOrPostCallback Callback,
        object? State);

    private sealed class PhotinoSynchronizationTaskCompletionSource<TCallback>(TCallback callback)
        : TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        public TCallback Callback { get; } = callback;
    }

    private sealed class PhotinoSynchronizationTaskCompletionSource<TCallback, TResult>(TCallback callback)
        : TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        public TCallback Callback { get; } = callback;
    }
}