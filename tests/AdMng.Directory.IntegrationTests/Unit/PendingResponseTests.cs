using System.DirectoryServices.Protocols;

namespace AdMng.Directory.IntegrationTests.Unit;

/// <summary>
/// <see cref="PendingResponse{TResponse}.ReceiveAsync"/>: the pool frees a connection's request slot in the callback,
/// so a request that finished must be accounted finished before its response or error reaches the caller - otherwise
/// the caller's next request finds the connection still busy.
/// </summary>
public sealed class PendingResponseTests
{
    [Fact]
    public async Task BlockingPath_RequestIsFinished_BeforeTheCallerContinues()
    {
        var ct = TestContext.Current.CancellationToken;

        // LdapAsync's blocking path: the response is a continuation of the worker task, and the worker completes with
        // synchronous continuations - the caller resumes inline, ahead of anything registered on the worker later.
        var work = new TaskCompletionSource<SearchResponse>();
        var pending = new PendingResponse<SearchResponse>(work.Task.WaitAsync(TimeSpan.FromMinutes(1), ct), work.Task);
        var recorder = new FinishRecorder();
        var caller = FinishedWhenTheCallerContinuesAsync(pending, recorder);

        work.SetResult(null!);

        Assert.Equal(1, await caller);
        Assert.Equal(1, recorder.Count);
        Assert.Same(work.Task, recorder.Completion);
    }

    [Fact]
    public async Task FailedRequest_IsFinished_BeforeTheErrorReachesTheCaller()
    {
        var ct = TestContext.Current.CancellationToken;
        var work = new TaskCompletionSource<SearchResponse>();
        var pending = new PendingResponse<SearchResponse>(work.Task.WaitAsync(TimeSpan.FromMinutes(1), ct), work.Task);
        var recorder = new FinishRecorder();
        var caller = FinishedWhenTheCallerContinuesAsync(pending, recorder);

        // The callback sees the failure (the pool drops a broken connection there) before the caller retries.
        work.SetException(new LdapException(81, "server down"));

        Assert.Equal(1, await caller);
        Assert.True(recorder.Completion!.IsFaulted);
    }

    [Fact]
    public async Task CallerStoppedWaiting_RequestIsFinished_OnlyWhenItEnds()
    {
        var ct = TestContext.Current.CancellationToken;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var work = new TaskCompletionSource<SearchResponse>();
        var pending = new PendingResponse<SearchResponse>(work.Task.WaitAsync(TimeSpan.FromMinutes(1), cts.Token), work.Task);
        var recorder = new FinishRecorder();
        var receive = pending.ReceiveAsync(recorder.Finish);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receive);

        // The uninterruptible request still occupies the connection ...
        Assert.Equal(0, recorder.Count);

        // ... until it really ended.
        work.SetResult(null!);
        Assert.Equal(1, recorder.Count);
        Assert.Same(work.Task, recorder.Completion);
    }

    [Fact]
    public async Task AsynchronousCompletion_RequestIsFinishedOnce_BeforeTheCallerContinues()
    {
        var ct = TestContext.Current.CancellationToken;
        var recorders = new List<FinishRecorder>();
        for (var i = 0; i < 100; i++)
        {
            // LdapAsync's APM path: response and completion are one task whose continuations are queued, so the
            // caller and a continuation on the completion race.
            var request = new TaskCompletionSource<SearchResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new PendingResponse<SearchResponse>(request.Task, request.Task);
            var recorder = new FinishRecorder();
            recorders.Add(recorder);
            var caller = FinishedWhenTheCallerContinuesAsync(pending, recorder);

            request.SetResult(null!);

            Assert.Equal(1, await caller.WaitAsync(ct));
        }

        // Neither path calls the callback a second time (the pool would release the connection's slot twice).
        await Task.Delay(50, ct);
        Assert.All(recorders, r => Assert.Equal(1, r.Count));
    }

    private static async Task<int> FinishedWhenTheCallerContinuesAsync(PendingResponse<SearchResponse> pending, FinishRecorder recorder)
    {
        try
        {
            await pending.ReceiveAsync(recorder.Finish).ConfigureAwait(false);
        }
        catch (LdapException)
        {
            // The failure itself is not the point; when the caller saw it is.
        }

        return recorder.Count;
    }

    private sealed class FinishRecorder
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public Task? Completion { get; private set; }

        public void Finish(Task completion)
        {
            Completion = completion;
            Interlocked.Increment(ref _count);
        }
    }
}
