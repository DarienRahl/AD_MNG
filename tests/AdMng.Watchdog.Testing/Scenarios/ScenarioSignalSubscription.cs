using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Testing.Scenarios;

/// <summary>One latch of <see cref="ScenarioSignals"/>; the timeout is real time (tests raise signals instead of waiting).</summary>
public sealed class ScenarioSignalSubscription : ISignalSubscription
{
    private readonly Action<ScenarioSignalSubscription> _remove;
    private TaskCompletionSource _latch = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal ScenarioSignalSubscription(HashSet<WatchdogSignal> signals, Action<ScenarioSignalSubscription> remove)
    {
        Signals = signals;
        _remove = remove;
    }

    internal HashSet<WatchdogSignal> Signals { get; }

    internal void Set() => Volatile.Read(ref _latch).TrySetResult();

    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var latch = Volatile.Read(ref _latch);
        if (!latch.Task.IsCompleted)
        {
            try
            {
                await latch.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        Interlocked.CompareExchange(ref _latch, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), latch);
        return true;
    }

    public void Dispose() => _remove(this);
}
