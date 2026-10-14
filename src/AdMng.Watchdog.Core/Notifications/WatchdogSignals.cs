using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>
/// <see cref="IWatchdogSignals"/> with one latch per subscription: a raised signal sets the latch of every subscription
/// that includes it, so overlapping consumers never steal each other's wake-ups and a signal raised while nobody waits
/// is not lost (the next <see cref="ISignalSubscription.WaitAsync"/> returns at once). Timeouts run on the injected
/// <see cref="TimeProvider"/>.
/// </summary>
public sealed class WatchdogSignals : IWatchdogSignals
{
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private readonly List<SignalSubscription> _subscriptions = [];

    public WatchdogSignals(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public void Raise(WatchdogSignal signal)
    {
        SignalSubscription[] targets;
        lock (_gate)
        {
            targets = [.. _subscriptions.Where(s => s.Includes(signal))];
        }

        foreach (var subscription in targets)
        {
            subscription.Set();
        }
    }

    /// <inheritdoc/>
    public ISignalSubscription Subscribe(IReadOnlyList<WatchdogSignal> signals)
    {
        ArgumentNullException.ThrowIfNull(signals);
        var subscription = new SignalSubscription(signals, _timeProvider, Remove);
        lock (_gate)
        {
            _subscriptions.Add(subscription);
        }

        return subscription;
    }

    private void Remove(SignalSubscription subscription)
    {
        lock (_gate)
        {
            _subscriptions.Remove(subscription);
        }
    }
}
