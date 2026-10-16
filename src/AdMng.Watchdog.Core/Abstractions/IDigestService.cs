using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Watchdog.Core.Abstractions;

/// <summary>Digest preview and send-now (API side of the DigestJob).</summary>
public interface IDigestService
{
    Task<DigestPreviewDto> PreviewAsync(CancellationToken cancellationToken);

    /// <summary>The preview as a caller with <paramref name="callerLevel"/> may see it (17.2: Viewer at
    /// <c>ViewerDetailLevel</c>): never more detailed than the digest recipients get. The default ignores the level.</summary>
    Task<DigestPreviewDto> PreviewAsync(AdMng.Watchdog.Contracts.Configuration.DetailLevel callerLevel, CancellationToken cancellationToken) =>
        PreviewAsync(cancellationToken);
    /// <summary>Builds and enqueues the digest for the period since the last digest now (does not move the schedule).</summary>
    Task SendNowAsync(CancellationToken cancellationToken);
    DateTimeOffset? NextScheduledAt { get; }
}
