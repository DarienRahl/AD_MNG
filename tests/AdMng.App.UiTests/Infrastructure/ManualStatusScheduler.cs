using AdMng.App.Controls;

namespace AdMng.App.UiTests.Infrastructure;

/// <summary>
/// A clock that only moves when the test says so (the <see cref="IStatusScheduler"/> of a status line under test): the
/// delay before "Working" and the throttle of the summary are then asserted at the millisecond, without a single wait.
/// It uses no WPF type, so the tests of <see cref="StatusFlow"/> need no UI thread.
/// </summary>
internal sealed class ManualStatusScheduler : IStatusScheduler
{
    private readonly List<Entry> _entries = [];
    private long _sequence;

    /// <summary>How far the clock has been moved.</summary>
    public TimeSpan Now { get; private set; }

    /// <summary>The calls that wait for their time (cancelled and finished ones are not counted).</summary>
    public int Pending => _entries.Count;

    /// <summary>The delay of every call ever made, in order.</summary>
    public List<TimeSpan> Requested { get; } = [];

    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        Requested.Add(delay);
        var entry = new Entry(this, Now + delay, _sequence++, callback);
        _entries.Add(entry);
        return entry;
    }

    /// <summary>Moves the clock on and runs, in order of time, every call that falls due on the way.</summary>
    public void Advance(TimeSpan by)
    {
        Assert.True(by >= TimeSpan.Zero, "the clock does not go back");
        var target = Now + by;
        while (_entries.OrderBy(e => e.Due).ThenBy(e => e.Sequence).FirstOrDefault(e => e.Due <= target) is { } next)
        {
            Now = next.Due;
            _entries.Remove(next);
            next.Callback();
        }

        Now = target;
    }

    public void Advance(int milliseconds) => Advance(TimeSpan.FromMilliseconds(milliseconds));

    private sealed class Entry(ManualStatusScheduler owner, TimeSpan due, long sequence, Action callback) : IDisposable
    {
        public TimeSpan Due { get; } = due;

        public long Sequence { get; } = sequence;

        public Action Callback { get; } = callback;

        public void Dispose() => owner._entries.Remove(this);
    }
}
