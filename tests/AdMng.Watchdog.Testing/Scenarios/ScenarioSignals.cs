using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Testing.Scenarios;

/// <summary>Recording <see cref="IWatchdogSignals"/> with a working latch per subscription.</summary>
public sealed class ScenarioSignals : IWatchdogSignals
{
    private readonly Lock _gate = new();
    private readonly List<WatchdogSignal> _raised = [];
    private readonly List<ScenarioSignalSubscription> _subscriptions = [];

    /// <summary>Every raised signal, in order.</summary>
    public IReadOnlyList<WatchdogSignal> Raised
    {
        get
        {
            lock (_gate)
            {
                return [.. _raised];
            }
        }
    }

    /// <summary>Number of times <paramref name="signal"/> was raised.</summary>
    public int Count(WatchdogSignal signal)
    {
        lock (_gate)
        {
            return _raised.Count(s => s == signal);
        }
    }

    public void Raise(WatchdogSignal signal)
    {
        List<ScenarioSignalSubscription> targets;
        lock (_gate)
        {
            _raised.Add(signal);
            targets = [.. _subscriptions.Where(s => s.Signals.Contains(signal))];
        }

        foreach (var subscription in targets)
        {
            subscription.Set();
        }
    }

    public ISignalSubscription Subscribe(IReadOnlyList<WatchdogSignal> signals)
    {
        ArgumentNullException.ThrowIfNull(signals);
        var subscription = new ScenarioSignalSubscription([.. signals], Remove);
        lock (_gate)
        {
            _subscriptions.Add(subscription);
        }

        return subscription;
    }

    private void Remove(ScenarioSignalSubscription subscription)
    {
        lock (_gate)
        {
            _subscriptions.Remove(subscription);
        }
    }
}
