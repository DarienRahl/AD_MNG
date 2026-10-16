namespace AdMng.Watchdog.Testing.Client;

/// <summary>
/// Records the API calls of <see cref="FakeWatchdogClient"/> by method name and lets tests await a number of calls
/// (no sleeps: a waiter completes as soon as the call is recorded).
/// </summary>
public sealed class FakeCallRecorder
{
    private readonly Lock _gate = new();
    private readonly List<string> _calls = [];
    private readonly List<(string Method, int Count, TaskCompletionSource Completion)> _waiters = [];

    /// <summary>Every call so far (method names, e.g. "GetEventsAsync"), in order.</summary>
    public IReadOnlyList<string> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>Number of calls of <paramref name="method"/> so far.</summary>
    public int Count(string method)
    {
        lock (_gate)
        {
            return _calls.Count(c => c == method);
        }
    }

    /// <summary>Completes when <paramref name="method"/> has been called at least <paramref name="count"/> times.</summary>
    public Task WaitForAsync(string method, int count, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(method);
        lock (_gate)
        {
            if (_calls.Count(c => c == method) >= count)
            {
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((method, count, completion));
            return completion.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Forgets the recorded calls (pending waiters keep waiting for new calls).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _calls.Clear();
        }
    }

    internal void Record(string method)
    {
        List<TaskCompletionSource> due;
        lock (_gate)
        {
            _calls.Add(method);
            var count = _calls.Count(c => c == method);
            due = [.. _waiters.Where(w => w.Method == method && count >= w.Count).Select(w => w.Completion)];
            _waiters.RemoveAll(w => due.Contains(w.Completion));
        }

        foreach (var completion in due)
        {
            completion.TrySetResult();
        }
    }
}
