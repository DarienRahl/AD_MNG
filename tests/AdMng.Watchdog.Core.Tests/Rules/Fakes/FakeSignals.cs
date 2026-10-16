using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Tests.Rules.Fakes;

/// <summary>Records raised signals (the real implementation is W-notify's WatchdogSignals).</summary>
internal sealed class FakeSignals : IWatchdogSignals
{
    private readonly Lock _gate = new();
    private readonly List<WatchdogSignal> _raised = [];

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

    public void Raise(WatchdogSignal signal)
    {
        lock (_gate)
        {
            _raised.Add(signal);
        }
    }

    public ISignalSubscription Subscribe(IReadOnlyList<WatchdogSignal> signals) => new Subscription();

    private sealed class Subscription : ISignalSubscription
    {
        public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(false);

        public void Dispose()
        {
        }
    }
}
