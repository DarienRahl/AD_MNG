using AdMng.Watchdog.Testing.Client;

namespace AdMng.Presentation.Tests.Watchdog;

/// <summary>
/// Waits for state that background loops (live polling) hand to the UI thread. No sleeps: the UI thread's queue is
/// drained until the condition holds; a generous real-time limit turns a broken expectation into a failure, not a hang.
/// </summary>
internal static class WatchdogTestWait
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <summary>Completes when the fake client recorded <paramref name="count"/> calls of <paramref name="method"/>.</summary>
    public static Task CallsAsync(this FakeCallRecorder calls, string method, int count)
    {
        ArgumentNullException.ThrowIfNull(calls);
        return calls.WaitForAsync(method, count, TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Completes when the poller of a visible tab waits in its long-poll (<see cref="FakeWatchdogClient.WaitingLongPolls"/>).
    /// The poll's deadline counts from its request: move the clock to the next status round only after this.
    /// </summary>
    public static Task LongPollAsync(WatchdogHarness harness)
    {
        ArgumentNullException.ThrowIfNull(harness);
        return UntilAsync(harness.Ui, () => harness.Client.WaitingLongPolls > 0, "the poller's long-poll");
    }

    /// <summary>Completes when <paramref name="condition"/> (evaluated on the UI thread) holds.</summary>
    public static Task UntilAsync(WatchdogHarness harness, Func<bool> condition, string? what = null)
    {
        ArgumentNullException.ThrowIfNull(harness);
        return UntilAsync(harness.Ui, condition, what);
    }

    /// <summary>Completes when <paramref name="condition"/> (evaluated on the UI thread) holds.</summary>
    public static async Task UntilAsync(TestUiThread ui, Func<bool> condition, string? what = null)
    {
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(condition);
        var started = Environment.TickCount64;
        while (true)
        {
            var met = false;
            await ui.InvokeAsync(() => met = condition());
            if (met)
            {
                return;
            }

            if (Environment.TickCount64 - started > Limit.TotalMilliseconds)
            {
                throw new TimeoutException($"Not met in time: {what ?? "condition"}.");
            }

            await Task.Yield();
        }
    }
}
