using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>IWatchdogJob: due buckets → render → outbox (section 13.3). Scaffold stub – replaced by W-notify.</summary>
public sealed class NotificationDispatchJob : IWatchdogJob
{
    public string Name => throw new NotImplementedException();

    public TimeSpan DefaultInterval => throw new NotImplementedException();

    public IReadOnlyList<WatchdogSignal> WakeSignals => throw new NotImplementedException();

    public Task<JobResult> RunOnceAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
}
