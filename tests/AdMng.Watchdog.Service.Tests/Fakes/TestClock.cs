using Microsoft.Extensions.Time.Testing;

namespace AdMng.Watchdog.Service.Tests.Fakes;

/// <summary>A <see cref="FakeTimeProvider"/> that reports created timers, so a test can wait until the code under test is
/// actually waiting (e.g. a long-poll <c>Task.Delay</c>) before it advances the clock – no sleeps.</summary>
public sealed class TestClock(DateTimeOffset start) : FakeTimeProvider(start)
{
    private readonly Lock _gate = new();
    private readonly List<(TimeSpan? DueTime, TaskCompletionSource Created)> _waiters = [];

    /// <summary>Completes when the next timer of any due time is created (from now on).</summary>
    public Task WaitForAnyTimerAsync() => Register(null);

    /// <summary>Completes when a timer with <paramref name="dueTime"/> is created (from now on).</summary>
    public Task WaitForTimerAsync(TimeSpan dueTime) => Register(dueTime);

    private Task Register(TimeSpan? dueTime)
    {
        var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _waiters.Add((dueTime, created));
        }

        return created.Task;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        lock (_gate)
        {
            foreach (var waiter in _waiters.Where(w => w.DueTime is null || w.DueTime == dueTime).ToList())
            {
                waiter.Created.TrySetResult();
                _waiters.Remove(waiter);
            }
        }

        return timer;
    }
}
