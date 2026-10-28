using System.Collections.Concurrent;

namespace AdMng.Outlook.Tests;

/// <summary>The session's worker thread (design note 4.1): one thread, items in order, cancellation, disposal.</summary>
public class StaWorkerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RunAsync_RunsEveryItemOnTheOneWorkerThread_InOrder_OneAtATime()
    {
        await using var worker = new StaWorker("test", useMessageFilter: false);
        var order = new ConcurrentQueue<int>();
        var threads = new ConcurrentBag<int>();
        var running = 0;
        var overlapped = false;

        var tasks = Enumerable.Range(0, 50).Select(index => worker.RunAsync(
            () =>
            {
                if (Interlocked.Increment(ref running) > 1)
                {
                    overlapped = true;
                }

                threads.Add(Environment.CurrentManagedThreadId);
                Thread.SpinWait(2000);
                order.Enqueue(index);
                Interlocked.Decrement(ref running);
                return index * 2;
            },
            Ct)).ToList();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(Enumerable.Range(0, 50).Select(index => index * 2), results);
        Assert.Equal(Enumerable.Range(0, 50), order);
        Assert.False(overlapped);
        Assert.Equal(worker.ThreadId, Assert.Single(threads.Distinct()));
        Assert.NotEqual(Environment.CurrentManagedThreadId, worker.ThreadId);
    }

    [Fact]
    public async Task Apartment_IsStaOnWindows_AndNoStaElsewhere()
    {
        await using var worker = new StaWorker("test", useMessageFilter: false);
        var apartment = await worker.RunAsync(() => Thread.CurrentThread.GetApartmentState(), Ct);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(ApartmentState.STA, apartment);
        }
        else
        {
            Assert.NotEqual(ApartmentState.STA, apartment);
        }
    }

    [Fact]
    public async Task RunAsync_ExceptionsReachTheCaller_AndTheWorkerGoesOn()
    {
        await using var worker = new StaWorker("test", useMessageFilter: false);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.RunAsync<int>(() => throw new InvalidOperationException("x"), Ct));
        Assert.Equal("x", failure.Message);
        Assert.Equal(7, await worker.RunAsync(() => 7, Ct));
    }

    [Fact]
    public async Task RunAsync_CancelledWhileQueued_NeverRuns()
    {
        await using var worker = new StaWorker("test", useMessageFilter: false);
        using var gate = new ManualResetEventSlim();
        var blocker = worker.RunAsync(() => gate.Wait(TimeSpan.FromSeconds(10)), Ct);
        using var cancellation = new CancellationTokenSource();
        var ran = false;
        var queued = worker.RunAsync(() => ran = true, cancellation.Token);

        await cancellation.CancelAsync();
        gate.Set();
        await blocker;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.False(ran);
        Assert.True(queued.IsCanceled);
        Assert.Equal(1, await worker.RunAsync(() => 1, Ct));
    }

    [Fact]
    public async Task RunAsync_CancelledAfterItStarted_Completes()
    {
        await using var worker = new StaWorker("test", useMessageFilter: false);
        using var started = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var running = worker.RunAsync(
            () =>
            {
                started.Set();
                gate.Wait(TimeSpan.FromSeconds(10));
                return "gotowe";
            },
            cancellation.Token);

        Assert.True(started.Wait(TimeSpan.FromSeconds(10), Ct));
        await cancellation.CancelAsync();
        gate.Set();

        Assert.Equal("gotowe", await running);
    }

    [Fact]
    public async Task RunAsync_AlreadyCancelled_IsCancelledAtOnce()
    {
        await using var worker = new StaWorker("test", useMessageFilter: false);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var ran = false;

        var task = worker.RunAsync(() => ran = true, cancellation.Token);

        Assert.True(task.IsCanceled);
        await Task.Delay(50, Ct);
        Assert.False(ran);
    }

    [Fact]
    public async Task RunAsync_FromTheWorkerItself_RunsInline_WithoutDeadlock()
    {
        await using var worker = new StaWorker("test", useMessageFilter: false);

        var inner = await worker.RunAsync(() => worker.RunAsync(() => Environment.CurrentManagedThreadId, Ct), Ct);

        Assert.True(inner.IsCompletedSuccessfully);
        Assert.Equal(worker.ThreadId, await inner);
    }

    [Fact]
    public async Task DisposeAsync_RunsTheQueuedItems_EndsTheThread_AndRefusesNewOnes()
    {
        var worker = new StaWorker("test", useMessageFilter: false);
        using var gate = new ManualResetEventSlim();
        var first = worker.RunAsync(() => gate.Wait(TimeSpan.FromSeconds(10)), Ct);
        var second = worker.RunAsync(() => "druga", Ct);

        var disposing = worker.DisposeAsync().AsTask();
        Assert.IsType<ObjectDisposedException>(Record.Exception(() =>
        {
            _ = worker.RunAsync(() => 1, Ct);
        }));
        gate.Set();
        await disposing;

        Assert.True(await first);
        Assert.Equal("druga", await second);
        Assert.True(worker.Completion.IsCompleted);
        Assert.True(worker.IsDisposed);
        await worker.DisposeAsync();
    }

    [Fact]
    public async Task ToString_NamesTheType()
    {
        await using var worker = new StaWorker("test", useMessageFilter: false);

        Assert.Equal(nameof(StaWorker), worker.ToString());
    }

    [Theory]
    [InlineData(ComRetryPolicy.ServerCallRetryLater, 0, 150)]
    [InlineData(ComRetryPolicy.ServerCallRetryLater, 59_999, 150)]
    [InlineData(ComRetryPolicy.ServerCallRetryLater, 60_000, -1)]
    [InlineData(ComRetryPolicy.ServerCallRetryLater, -5, -1)]
    [InlineData(ComRetryPolicy.ServerCallRejected, 10, -1)]
    [InlineData(0, 10, -1)]
    public void RetryPolicy_RetriesABusyOutlookFor60Seconds(int rejectType, int elapsed, int expected) =>
        Assert.Equal(expected, ComRetryPolicy.RetryDelay(rejectType, elapsed));
}
