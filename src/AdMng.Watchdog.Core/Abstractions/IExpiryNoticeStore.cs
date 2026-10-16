using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Abstractions;

/// <summary>Expiry notification dedup and the upcoming-expiry list (section 10.14).</summary>
public interface IExpiryNoticeStore
{
    /// <summary>False when the notice already exists (dedup).</summary>
    Task<bool> TryRecordAsync(ExpiryNotice notice, CancellationToken cancellationToken);
    Task ReplaceUpcomingAsync(ExpiryKind kind, IReadOnlyList<UpcomingExpiry> upcoming, CancellationToken cancellationToken);
    Task<IReadOnlyList<UpcomingExpiry>> GetUpcomingAsync(CancellationToken cancellationToken);
}
