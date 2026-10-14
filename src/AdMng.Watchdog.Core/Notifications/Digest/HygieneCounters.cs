namespace AdMng.Watchdog.Core.Notifications.Digest;

/// <summary>The weekly hygiene counters (13.4, live LDAP counts of enabled accounts).</summary>
public sealed record HygieneCounters
{
    /// <summary>Users with PASSWD_NOTREQD.</summary>
    public int PasswordNotRequired { get; init; }

    /// <summary>Users with DONT_EXPIRE_PASSWORD.</summary>
    public int PasswordNeverExpires { get; init; }

    /// <summary>Accounts trusted for unconstrained delegation that are not domain controllers.</summary>
    public int UnconstrainedDelegation { get; init; }

    /// <summary>Accounts with adminCount = 1 that are not privileged any more.</summary>
    public int AdminCountOrphans { get; init; }

    /// <summary>Password age of krbtgt in days (null = not found).</summary>
    public int? KrbtgtPasswordAgeDays { get; init; }

    /// <summary>Password age of the built-in administrator (RID 500) in days (null = not found).</summary>
    public int? AdministratorPasswordAgeDays { get; init; }
}
