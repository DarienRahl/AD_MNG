namespace AdMng.Watchdog.Core.Notifications.Digest;

/// <summary>The digest sections in display order (section 13.4); the resx title of each is <c>Digest.Section.{Kind}</c>.</summary>
public enum DigestSectionKind
{
    Health,
    NewAccounts,
    NewComputers,
    DeletedRestored,
    DisabledEnabled,
    MovedRenamed,
    Privileged,
    GroupMembership,
    AccountParameters,
    Lockouts,
    Passwords,
    DomainPolicy,
    SecurityReview,
    UpcomingExpiry,
    InactiveAccounts,
    Hygiene,
    ActiveExceptions,
    WatchdogConfiguration,
}
