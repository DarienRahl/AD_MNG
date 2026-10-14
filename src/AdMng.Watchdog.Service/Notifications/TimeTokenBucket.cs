namespace AdMng.Watchdog.Service.Notifications;

/// <summary>
/// A token bucket of <c>limit</c> tokens that refills one token per <c>interval</c>, measured on the caller's clock
/// (a <see cref="TimeProvider"/>). Not thread-safe – the owner serializes access.
/// </summary>
internal sealed class TimeTokenBucket
{
    private readonly int _limit;
    private readonly TimeSpan _interval;
    private int _tokens;
    private DateTimeOffset _refilledAt;

    public TimeTokenBucket(int limit, TimeSpan interval, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        _limit = limit;
        _interval = interval;
        _tokens = limit;
        _refilledAt = now;
    }

    /// <summary>Zero when a token is available at <paramref name="now"/>, otherwise the time until the next one.</summary>
    public TimeSpan WaitFor(DateTimeOffset now)
    {
        Refill(now);
        return _tokens > 0 ? TimeSpan.Zero : _refilledAt + _interval - now;
    }

    /// <summary>Takes a token (call after <see cref="WaitFor"/> returned zero for the same instant; a full bucket was
    /// stamped with that instant there, so the refill of the token taken now starts now).</summary>
    public void Take() => _tokens--;

    private void Refill(DateTimeOffset now)
    {
        if (now <= _refilledAt)
        {
            return;
        }

        if (_tokens >= _limit)
        {
            _refilledAt = now;
            return;
        }

        var periods = (now - _refilledAt).Ticks / _interval.Ticks;
        if (periods <= 0)
        {
            return;
        }

        var added = (int)Math.Min(periods, _limit - _tokens);
        _tokens += added;
        _refilledAt = _tokens >= _limit ? now : _refilledAt + (_interval * added);
    }
}
