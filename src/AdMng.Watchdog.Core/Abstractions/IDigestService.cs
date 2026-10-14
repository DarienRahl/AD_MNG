using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Watchdog.Core.Abstractions;

/// <summary>Digest preview and send-now (API side of the DigestJob).</summary>
public interface IDigestService
{
    Task<DigestPreviewDto> PreviewAsync(CancellationToken cancellationToken);
    /// <summary>Builds and enqueues the digest for the period since the last digest now (does not move the schedule).</summary>
    Task SendNowAsync(CancellationToken cancellationToken);
    DateTimeOffset? NextScheduledAt { get; }
}
