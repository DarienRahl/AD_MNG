namespace AdMng.Presentation.Tests.Infrastructure;

/// <summary>The test UI thread of <see cref="UiThreadAttribute"/> (applied to the whole assembly).</summary>
public class UiThreadAttributeTests
{
    [Fact]
    public async Task ATest_ContinuesOnOneUiThread_AfterItsFirstAwait()
    {
        await Task.Yield();
        Assert.IsType<UiThreadSynchronizationContext>(SynchronizationContext.Current);
        var ui = Environment.CurrentManagedThreadId;

        await Task.Run(() => Assert.NotEqual(ui, Environment.CurrentManagedThreadId), TestContext.Current.CancellationToken);
        Assert.Equal(ui, Environment.CurrentManagedThreadId);

        await Task.Delay(1, TestContext.Current.CancellationToken);
        Assert.Equal(ui, Environment.CurrentManagedThreadId);
    }

    [Fact]
    public async Task TheContinuationOfBackgroundWork_WaitsUntilTheTestYields_LikeOnTheWpfUiThread()
    {
        await Task.Yield();
        var steps = new List<string>();
        var done = new TaskCompletionSource();
        var continuation = ContinueAsync();

        // The background work completes on the pool, but its continuation is queued behind the test's own code.
        _ = Task.Run(done.SetResult, TestContext.Current.CancellationToken);
        Assert.True(SpinWait.SpinUntil(() => done.Task.IsCompleted, TimeSpan.FromSeconds(10)));
        Thread.Sleep(50);
        Assert.Empty(steps);

        await continuation;
        Assert.Equal(["continued"], steps);

        async Task ContinueAsync()
        {
            await done.Task;
            steps.Add("continued");
        }
    }
}
