namespace Photino.Blazor;

// Runs the synchronization context's queued work items directly on the dispatcher thread while the
// message loop is running, instead of blocking a thread-pool thread in a synchronous invoke for each item.
//
// Photino may drop posted callbacks without running them when the message loop exits. Tasks posted to
// the dispatcher are therefore tracked until they start; RecoverDroppedTasks moves them to the thread pool
// once posting is no longer possible. TaskScheduler.TryExecuteTask guarantees each task runs at most once.
//
// Nothing may be queued after a dropped task (the code awaiting it is exactly what stopped), so recovery
// cannot rely on later work alone: NotifyMessageLoopExited recovers as soon as the message loop has exited.
internal sealed class PhotinoDispatcherTaskScheduler : TaskScheduler
{
    private readonly IPhotinoUiDispatcher _dispatcher;
    private readonly SendOrPostCallback _executeOnDispatcher;
    private readonly Action<Task> _executeOnThreadPool;
    private readonly HashSet<Task> _postedTasks = [];
    private volatile bool _messageLoopExited;

    public PhotinoDispatcherTaskScheduler(IPhotinoUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        _dispatcher = dispatcher;
        _executeOnDispatcher = state => ExecuteOnDispatcher((Task)state!);
        _executeOnThreadPool = task => TryExecuteTask(task);
    }

    private bool CanPost => !_messageLoopExited && _dispatcher.CanPost;

    // The message loop has exited: nothing posted to it will run anymore. Stops posting for good
    // and requeues what is still pending.
    public void NotifyMessageLoopExited()
    {
        _messageLoopExited = true;
        RecoverDroppedTasks();
    }

    // Requeues tasks that were posted to a dispatcher that no longer processes posts.
    public void RecoverDroppedTasks()
    {
        if (CanPost)
            return;

        Task[] droppedTasks;

        lock (_postedTasks)
        {
            if (_postedTasks.Count == 0)
                return;

            droppedTasks = [.. _postedTasks];
            _postedTasks.Clear();
        }

        foreach (var task in droppedTasks)
            QueueOnThreadPool(task);
    }

    protected override void QueueTask(Task task)
    {
        if (CanPost)
        {
            lock (_postedTasks)
                _postedTasks.Add(task);

            if (_dispatcher.Post(_executeOnDispatcher, task))
            {
                // The loop may have exited between the check above and the post.
                if (_messageLoopExited)
                    RecoverDroppedTasks();

                return;
            }

            lock (_postedTasks)
                _postedTasks.Remove(task);
        }

        // Before the message loop starts or during shutdown: the work item marshals itself
        // synchronously when dispatch is still possible, or runs directly otherwise.
        QueueOnThreadPool(task);
    }

    // Work items must keep their queue order, so they never run inline.
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

    protected override IEnumerable<Task> GetScheduledTasks()
    {
        lock (_postedTasks)
            return [.. _postedTasks];
    }

    private void ExecuteOnDispatcher(Task task)
    {
        lock (_postedTasks)
            _postedTasks.Remove(task);

        TryExecuteTask(task);
    }

    private void QueueOnThreadPool(Task task)
    {
        // Work items carry their own ExecutionContext, so the thread pool does not need to flow one.
        ThreadPool.UnsafeQueueUserWorkItem(_executeOnThreadPool, task, preferLocal: false);
    }
}
