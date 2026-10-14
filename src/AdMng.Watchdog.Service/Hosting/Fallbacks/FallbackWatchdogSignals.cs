using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Service.Hosting.Fallbacks;

/// <summary>
/// <see cref="IWatchdogSignals"/> used only when the Core registration provides none (see
/// <see cref="CoreFallbacks"/>): one latch per subscription, set by <see cref="Raise"/> for every subscription that
/// includes the signal and reset when <c>WaitAsync</c> returns; the timeout runs on the injected <see cref="TimeProvider"/>.
/// </summary>
public sealed class FallbackWatchdogSignals(TimeProvider timeProvider) : IWatchdogSignals
{
    private readonly Lock _gate = new();
    private readonly List<Subscription> _subscriptions = [];

    public void Raise(WatchdogSignal signal)
    {
        Subscription[] targets;
        lock (_gate)
        {
            targets = [.. _subscriptions.Where(s => s.Signals.Contains(signal))];
        }

        foreach (var target in targets)
        {
            target.Set();
        }
    }

    public ISignalSubscription Subscribe(IReadOnlyList<WatchdogSignal> signals)
    {
        ArgumentNullException.ThrowIfNull(signals);
        var subscription = new Subscription(this, [.. signals], timeProvider);
        lock (_gate)
        {
            _subscriptions.Add(subscription);
        }

        return subscription;
    }

    private void Remove(Subscription subscription)
    {
        lock (_gate)
        {
            _subscriptions.Remove(subscription);
        }
    }

    private sealed class Subscription(FallbackWatchdogSignals owner, HashSet<WatchdogSignal> signals, TimeProvider timeProvider) : ISignalSubscription
    {
        private readonly Lock _gate = new();
        private TaskCompletionSource _latch = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HashSet<WatchdogSignal> Signals => signals;

        public void Set()
        {
            lock (_gate)
            {
                _latch.TrySetResult();
            }
        }

        public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Task latch;
            lock (_gate)
            {
                latch = _latch.Task;
            }

            if (!latch.IsCompleted)
            {
                using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var delay = Task.Delay(timeout, timeProvider, timer.Token);
                await Task.WhenAny(latch, delay).ConfigureAwait(false);
                await timer.CancelAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            lock (_gate)
            {
                if (!_latch.Task.IsCompleted)
                {
                    return false;
                }

                _latch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return true;
            }
        }

        public void Dispose() => owner.Remove(this);
    }
}
