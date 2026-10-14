using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>IWatchdogJob: lease → transport → result (section 15.1). Scaffold stub – replaced by W-notify.</summary>
public sealed class OutboxSenderJob : IWatchdogJob
{
    public string Name => throw new NotImplementedException();

    public TimeSpan DefaultInterval => throw new NotImplementedException();

    public IReadOnlyList<WatchdogSignal> WakeSignals => throw new NotImplementedException();

    public Task<JobResult> RunOnceAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
}
