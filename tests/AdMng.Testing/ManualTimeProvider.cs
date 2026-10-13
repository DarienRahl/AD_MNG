namespace AdMng.Testing;

/// <summary>
/// A <see cref="TimeProvider"/> whose clock only moves when the test says so. Timers (Task.Delay with a time
/// provider) still use real time, so retry back-off and delays behave normally while timestamps stay deterministic.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private DateTimeOffset _utcNow;

    /// <summary>Starts at 2026-10-13 12:00:00 UTC unless another time is given.</summary>
    public ManualTimeProvider(DateTimeOffset? start = null, TimeZoneInfo? localTimeZone = null)
    {
        _utcNow = (start ?? new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero)).ToUniversalTime();
        LocalTimeZone = localTimeZone ?? TimeZoneInfo.Utc;
    }

    public override TimeZoneInfo LocalTimeZone { get; }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    public void Advance(TimeSpan delta)
    {
        lock (_gate)
        {
            _utcNow += delta;
        }
    }

    public void SetUtcNow(DateTimeOffset value)
    {
        lock (_gate)
        {
            _utcNow = value.ToUniversalTime();
        }
    }
}
