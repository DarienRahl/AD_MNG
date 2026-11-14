namespace AdMng.Watchdog.IntegrationTests.Infrastructure;

/// <summary>
/// Waits for a condition of an external system that offers no notification (the real service's background jobs,
/// Mailpit): probes every <see cref="Interval"/> until the condition holds or the timeout fails the test. Only the
/// end-to-end tests use it – everything driven by the tests themselves runs deterministically.
/// </summary>
internal static class Poll
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    // describe: what the failure says about the last answer when the wait timed out (the state of the service, the end
    // of its log) - a timeout alone names no cause.
    public static async Task<T> UntilAsync<T>(
        Func<Task<T>> probe,
        Func<T, bool> done,
        TimeSpan timeout,
        string what,
        CancellationToken cancellationToken,
        Func<T, string>? describe = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        while (true)
        {
            var value = await probe();
            if (done(value))
            {
                return value;
            }

            try
            {
                await Task.Delay(Interval, deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Assert.Fail(describe is null
                    ? $"{what} did not happen within {timeout}."
                    : $"{what} did not happen within {timeout}. Last seen: {describe(value)}");
            }
        }
    }
}
