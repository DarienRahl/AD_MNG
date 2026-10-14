using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Notifications;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Watchdog.Core.Tests.Notifications;

public sealed class SignalsTests
{
    private static readonly TimeSpan Long = TimeSpan.FromMinutes(5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task TwoSubscriptionsOnOneSignal_BothWake()
    {
        var signals = new WatchdogSignals(_clock);
        using var dispatch = signals.Subscribe([WatchdogSignal.NotificationsQueued, WatchdogSignal.AttributionUpdated]);
        using var rules = signals.Subscribe([WatchdogSignal.EventsStored, WatchdogSignal.AttributionUpdated]);
        var first = dispatch.WaitAsync(Long, Ct);
        var second = rules.WaitAsync(Long, Ct);

        signals.Raise(WatchdogSignal.AttributionUpdated);

        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public async Task ASignalRaisedWhileNobodyWaits_IsNotLost_AndIsConsumedOnce()
    {
        var signals = new WatchdogSignals(_clock);
        using var subscription = signals.Subscribe([WatchdogSignal.OutboxEnqueued]);

        signals.Raise(WatchdogSignal.OutboxEnqueued);
        signals.Raise(WatchdogSignal.OutboxEnqueued);

        Assert.True(await subscription.WaitAsync(Long, Ct));
        var next = subscription.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.False(next.IsCompleted);
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(await next.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public async Task OtherSignals_DoNotWakeASubscription()
    {
        var signals = new WatchdogSignals(_clock);
        using var subscription = signals.Subscribe([WatchdogSignal.DigestRequested]);
        var wait = subscription.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        signals.Raise(WatchdogSignal.EventsStored);
        _clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(wait.IsCompleted);
        _clock.Advance(TimeSpan.FromSeconds(1));

        Assert.False(await wait.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public async Task ASignalDuringTheWait_WakesItBeforeTheTimeout()
    {
        var signals = new WatchdogSignals(_clock);
        using var subscription = signals.Subscribe([WatchdogSignal.ConfigChanged]);
        var wait = subscription.WaitAsync(Long, Ct);
        Assert.False(wait.IsCompleted);

        signals.Raise(WatchdogSignal.ConfigChanged);

        Assert.True(await wait.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public async Task Cancellation_EndsTheWait()
    {
        var signals = new WatchdogSignals(_clock);
        using var subscription = signals.Subscribe([WatchdogSignal.PollRequested]);
        using var cancellation = new CancellationTokenSource();
        var wait = subscription.WaitAsync(Long, cancellation.Token);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public async Task DisposedSubscriptions_AreNoLongerSignalled()
    {
        var signals = new WatchdogSignals(_clock);
        var subscription = signals.Subscribe([WatchdogSignal.EventsStored]);
        var wait = subscription.WaitAsync(Long, Ct);

        subscription.Dispose();
        signals.Raise(WatchdogSignal.EventsStored);

        Assert.False(await wait.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => subscription.WaitAsync(Long, Ct));
    }

    [Fact]
    public async Task ManyRaisesFromParallelProducers_LoseNoWakeUp()
    {
        var signals = new WatchdogSignals(_clock);
        using var subscription = signals.Subscribe([WatchdogSignal.EventsStored]);
        for (var round = 0; round < 50; round++)
        {
            var wait = subscription.WaitAsync(Long, Ct);
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => signals.Raise(WatchdogSignal.EventsStored), Ct)));
            Assert.True(await wait.WaitAsync(TimeSpan.FromSeconds(10), Ct));
            await subscription.WaitAsync(TimeSpan.Zero, Ct);   // drain a latch set after the wake-up
        }
    }
}
