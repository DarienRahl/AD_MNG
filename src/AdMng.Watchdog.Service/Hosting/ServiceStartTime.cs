namespace AdMng.Watchdog.Service.Hosting;

/// <summary>When the service started (<c>StatusDto.StartedAt</c>).</summary>
public sealed class ServiceStartTime(TimeProvider timeProvider)
{
    public DateTimeOffset StartedAt { get; } = timeProvider.GetUtcNow();
}
