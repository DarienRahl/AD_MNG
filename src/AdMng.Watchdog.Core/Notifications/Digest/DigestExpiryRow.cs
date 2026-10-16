using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Notifications.Digest;

/// <summary>"Nadchodzące terminy": an account or password that expires within <c>UpcomingExpiryDays</c>.</summary>
public sealed record DigestExpiryRow(UpcomingExpiry Expiry) : DigestRow
{
    /// <inheritdoc/>
    public override DateTimeOffset? Time => Expiry.ExpiresAt;
}
