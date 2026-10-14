using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>One consumer's latch of <see cref="WatchdogSignals"/>.</summary>
internal sealed class SignalSubscription : ISignalSubscription
{
    private readonly HashSet<WatchdogSignal> _signals;
    private readonly TimeProvider _timeProvider;
    private readonly Action<SignalSubscription> _onDispose;
    private readonly Lock _gate = new();
    private bool _signaled;
    private bool _disposed;
    private TaskCompletionSource? _waiter;

    public SignalSubscription(IEnumerable<WatchdogSignal> signals, TimeProvider timeProvider, Action<SignalSubscription> onDispose)
    {
        _signals = [.. signals];
        _timeProvider = timeProvider;
        _onDispose = onDispose;
    }

    public bool Includes(WatchdogSignal signal) => _signals.Contains(signal);

    /// <summary>Sets the latch and wakes a pending <see cref="WaitAsync"/>.</summary>
    public void Set()
    {
        TaskCompletionSource? waiter;
        lock (_gate)
        {
            _signaled = true;
            waiter = _waiter;
        }

        waiter?.TrySetResult();
    }

    /// <inheritdoc/>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        TaskCompletionSource waiter;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_signaled)
            {
                _signaled = false;
                return true;
            }

            waiter = _waiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan ? TimeSpan.Zero : timeout, _timeProvider, delayCancellation.Token);
        await Task.WhenAny(waiter.Task, delay).ConfigureAwait(false);
        await delayCancellation.CancelAsync().ConfigureAwait(false);
        lock (_gate)
        {
            if (ReferenceEquals(_waiter, waiter))
            {
                _waiter = null;
            }

            if (_signaled)
            {
                _signaled = false;
                return true;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    public void Dispose()
    {
        TaskCompletionSource? waiter;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            waiter = _waiter;
        }

        _onDispose(this);
        waiter?.TrySetResult();
    }
}
