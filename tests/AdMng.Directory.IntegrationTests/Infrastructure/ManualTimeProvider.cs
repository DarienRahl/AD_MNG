namespace AdMng.Directory.IntegrationTests.Infrastructure;

/// <summary>A clock the test moves forward explicitly (timestamps for durations still use the real clock).</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public void Advance(TimeSpan delta)
    {
        lock (_gate)
        {
            _now += delta;
        }
    }
}
