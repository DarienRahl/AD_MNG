namespace AdMng.Watchdog.Core.Notifications.Digest;

/// <summary>"Nieaktywne konta": one inactive account (weekly section).</summary>
public sealed record DigestInactiveRow(InactiveAccount Account) : DigestRow
{
    /// <inheritdoc/>
    public override DateTimeOffset? Time => Account.LastLogon;
}
