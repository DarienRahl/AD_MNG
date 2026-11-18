using System.Runtime.CompilerServices;
using AdMng.Presentation.Infrastructure;

namespace AdMng.Presentation.Tests.Infrastructure;

/// <summary>
/// <see cref="AdMng.Presentation.Infrastructure.TaskExtensions.WaitOrAbandonAsync{T}(Task{T}, CancellationToken)"/>: a wait that a Stop ends leaves work running that cannot be
/// interrupted (a file read on a share, a parse, a COM call). When that work fails afterwards, its exception must be
/// observed - an unobserved one is the application's "unexpected error" dialog about work the operator gave up on.
/// </summary>
public class TaskExtensionsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AWaitThatAStopEnded_KeepsObservingTheWork_SoItsLaterFailureIsNotAnUnobservedException()
    {
        using var unobserved = new UnobservedTaskExceptions();
        var marker = "abandoned-" + Guid.NewGuid().ToString("N");

        await AbandonAsync(marker, static (work, token) => work.WaitOrAbandonAsync(token));

        Assert.False(await unobserved.CollectAsync(e => e.Message == marker, rounds: 8));
    }

    [Fact]
    public async Task APlainWaitAsync_LeavesTheLaterFailureUnobserved_WhichIsWhyTheHelperExists()
    {
        using var unobserved = new UnobservedTaskExceptions();
        var marker = "abandoned-" + Guid.NewGuid().ToString("N");

        await AbandonAsync(marker, static (work, token) => work.WaitAsync(token));

        Assert.True(await unobserved.CollectAsync(e => e.Message == marker));
    }

    [Fact]
    public async Task AWaitThatWasNotCancelled_GivesTheResult_OrThrowsTheFailureOfTheWork()
    {
        Assert.Equal(7, await Task.FromResult(7).WaitOrAbandonAsync(Ct));
        await Task.CompletedTask.WaitOrAbandonAsync(Ct);

        var failing = Task.FromException<int>(new InvalidDataException("not a workbook"));
        var thrown = await Assert.ThrowsAsync<InvalidDataException>(() => failing.WaitOrAbandonAsync(Ct));
        Assert.Equal("not a workbook", thrown.Message);
    }

    [Fact]
    public async Task ACancelledWait_ThrowsForTheCallersToken_AtOnce()
    {
        using var stop = new CancellationTokenSource();
        var work = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = work.Task.WaitOrAbandonAsync(stop.Token);

        stop.Cancel();

        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(stop.Token, cancelled.CancellationToken);
        work.SetResult(1);
    }

    /// <summary>
    /// Starts work, waits for it with <paramref name="wait"/>, cancels the wait, and lets the work fail afterwards. In a
    /// method of its own so that no local keeps the task alive when the test collects.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task AbandonAsync(string marker, Func<Task<int>, CancellationToken, Task<int>> wait)
    {
        using var stop = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = wait(Task.Run(() => FailAfterAsync(release.Task, ended, marker)), stop.Token);

        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        release.SetResult();
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
    }

    private static async Task<int> FailAfterAsync(Task release, TaskCompletionSource ended, string marker)
    {
        try
        {
            await release.ConfigureAwait(false);
            throw new IOException(marker);
        }
        finally
        {
            ended.SetResult();
        }
    }
}
