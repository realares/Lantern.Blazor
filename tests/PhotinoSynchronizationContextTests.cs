using Photino.Blazor;
using Photino.NET;

namespace Lantern.Blazor.Tests;

[TestClass]
public sealed class PhotinoSynchronizationContextTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task InvokeAsync_AsyncActionWithYield_Completes()
    {
        var context = new PhotinoSynchronizationContext(new PhotinoDispatcher());

        var callbackStarted = false;
        var callbackCompleted = false;

        await context.InvokeAsync(async () =>
        {
            callbackStarted = true;

            await Task.Yield();

            callbackCompleted = true;
        }).WaitAsync(s_timeout);

        Assert.IsTrue(callbackStarted);
        Assert.IsTrue(callbackCompleted);
    }

    [TestMethod]
    public async Task InvokeAsync_AsyncFunctionWithYield_ReturnsResult()
    {
        var context = new PhotinoSynchronizationContext(new PhotinoDispatcher());

        var result = await context.InvokeAsync(async () =>
        {
            await Task.Yield();
            return 42;
        }).WaitAsync(s_timeout);

        Assert.AreEqual(42, result);
    }

    [TestMethod]
    public async Task InvokeAsync_AsyncContinuation_IsSerializedWithQueuedWork()
    {
        var context = new PhotinoSynchronizationContext(new PhotinoDispatcher());
        var executionOrder = new List<string>();

        var firstTask = context.InvokeAsync(async () =>
        {
            executionOrder.Add("first-start");

            await Task.Yield();

            executionOrder.Add("first-complete");
        });

        var secondTask = context.InvokeAsync(() =>
        {
            executionOrder.Add("second");
        });

        await Task.WhenAll(firstTask, secondTask).WaitAsync(s_timeout);

        Assert.AreSequenceEqual(
            [
                "first-start",
                "first-complete",
                "second"
            ],
            executionOrder);
    }

    [TestMethod]
    public async Task InvokeAsync_ExceptionAfterYield_FaultsReturnedTask()
    {
        var context = new PhotinoSynchronizationContext(new PhotinoDispatcher());

        var task = context.InvokeAsync(async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("Expected test exception.");
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await task.WaitAsync(s_timeout));

        Assert.AreEqual("Expected test exception.", exception.Message);
        Assert.IsTrue(task.IsFaulted);
    }

    [TestMethod]
    public async Task InvokeAsync_CancellationAfterYield_CancelsReturnedTask()
    {
        var context = new PhotinoSynchronizationContext(new PhotinoDispatcher());

        using var cancellationTokenSource = new CancellationTokenSource();
        var token = cancellationTokenSource.Token;
        cancellationTokenSource.Cancel();

        var task = context.InvokeAsync(async () =>
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
        });

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await task.WaitAsync(s_timeout));

        Assert.IsTrue(task.IsCanceled);
    }

    [TestMethod]
    public async Task Post_WhenDispatcherCanPost_RunsInOrderWithoutBlockingInvoke()
    {
        using var dispatcher = new TestUiDispatcher { CanPost = true };
        var context = new PhotinoSynchronizationContext(dispatcher);
        var executed = new List<(int Index, int ThreadId)>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int count = 50;

        for (var i = 0; i < count; i++)
        {
            var index = i;
            context.Post(_ =>
            {
                executed.Add((index, Environment.CurrentManagedThreadId));
                if (index == count - 1)
                    done.SetResult();
            }, null);
        }

        await done.Task.WaitAsync(s_timeout);

        CollectionAssert.AreEqual(Enumerable.Range(0, count).ToArray(), executed.Select(item => item.Index).ToArray());
        Assert.IsTrue(executed.All(item => item.ThreadId == dispatcher.ThreadId));
        Assert.AreEqual(0, dispatcher.MarshalledInvokeCount);
    }

    [TestMethod]
    public async Task Post_DroppedByStoppedMessageLoop_IsRecoveredByLaterWork()
    {
        using var dispatcher = new TestUiDispatcher { CanPost = true, DropPosts = true };
        var context = new PhotinoSynchronizationContext(dispatcher);
        var executed = new List<string>();

        context.Post(_ => executed.Add("posted"), null);

        // The message loop exits; the posted item was accepted but never runs on the UI thread.
        dispatcher.CanPost = false;
        dispatcher.IsRunning = false;

        await context.InvokeAsync(() => executed.Add("after shutdown")).WaitAsync(s_timeout);

        CollectionAssert.AreEqual(new[] { "posted", "after shutdown" }, executed);
    }

    [TestMethod]
    public async Task Post_HidesDispatcherSchedulerFromCallback()
    {
        using var dispatcher = new TestUiDispatcher { CanPost = true };
        var context = new PhotinoSynchronizationContext(dispatcher);
        var observed = new TaskCompletionSource<TaskScheduler>(TaskCreationOptions.RunContinuationsAsynchronously);

        context.Post(_ => observed.SetResult(TaskScheduler.Current), null);

        Assert.AreSame(TaskScheduler.Default, await observed.Task.WaitAsync(s_timeout));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Post_FlowsExecutionContextToDispatcherThread(bool canPost)
    {
        using var dispatcher = new TestUiDispatcher { CanPost = canPost };
        var context = new PhotinoSynchronizationContext(dispatcher);
        var asyncLocal = new AsyncLocal<string?> { Value = "posted" };
        var observed = new TaskCompletionSource<(string? Value, int ThreadId)>(TaskCreationOptions.RunContinuationsAsynchronously);

        context.Post(_ => observed.SetResult((asyncLocal.Value, Environment.CurrentManagedThreadId)), null);

        var (value, threadId) = await observed.Task.WaitAsync(s_timeout);
        Assert.AreEqual("posted", value);
        Assert.AreEqual(dispatcher.ThreadId, threadId);
    }

    [TestMethod]
    public async Task InvokeAsync_FromBackgroundThread_FlowsExecutionContextToDispatcherThread()
    {
        using var dispatcher = new TestUiDispatcher();
        var context = new PhotinoSynchronizationContext(dispatcher);

        var (value, threadId) = await Task.Run(() =>
        {
            var asyncLocal = new AsyncLocal<string?> { Value = "invoked" };
            return context.InvokeAsync(() => (asyncLocal.Value, Environment.CurrentManagedThreadId));
        }).WaitAsync(s_timeout);

        Assert.AreEqual("invoked", value);
        Assert.AreEqual(dispatcher.ThreadId, threadId);
    }

    [TestMethod]
    public async Task InvokeAsync_ContinuationAfterAwait_KeepsCulture()
    {
        using var dispatcher = new TestUiDispatcher();
        var context = new PhotinoSynchronizationContext(dispatcher);
        var culture = new System.Globalization.CultureInfo("de-DE");

        var observed = await context.InvokeAsync(async () =>
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
            await Task.Yield();
            return (System.Globalization.CultureInfo.CurrentCulture.Name, Environment.CurrentManagedThreadId);
        }).WaitAsync(s_timeout);

        Assert.AreEqual("de-DE", observed.Name);
        Assert.AreEqual(dispatcher.ThreadId, observed.CurrentManagedThreadId);
    }

    [TestMethod]
    public async Task InvokeAsync_SynchronouslyCompletedTask_Completes()
    {
        var context = new PhotinoSynchronizationContext(new PhotinoDispatcher());

        var callbackInvoked = false;

        var task = context.InvokeAsync(() =>
        {
            callbackInvoked = true;
            return Task.CompletedTask;
        });

        await task.WaitAsync(s_timeout);

        Assert.IsTrue(callbackInvoked);
        Assert.IsTrue(task.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task InvokeAsync_SynchronouslyCompletedFunction_ReturnsResult()
    {
        var context = new PhotinoSynchronizationContext(new PhotinoDispatcher());

        var task = context.InvokeAsync(() => Task.FromResult(42));

        var result = await task.WaitAsync(s_timeout);

        Assert.AreEqual(42, result);
        Assert.IsTrue(task.IsCompletedSuccessfully);
    }
}