using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>IWatchdogJob + IDigestService: the daily digest (section 13.4). Scaffold stub – replaced by W-notify.</summary>
public sealed class DigestJob : IWatchdogJob, IDigestService
{
    public string Name => throw new NotImplementedException();

    public TimeSpan DefaultInterval => throw new NotImplementedException();

    public IReadOnlyList<WatchdogSignal> WakeSignals => throw new NotImplementedException();

    public DateTimeOffset? NextScheduledAt => throw new NotImplementedException();

    public Task<JobResult> RunOnceAsync(CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<DigestPreviewDto> PreviewAsync(CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task SendNowAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
}
