namespace AdMng.Watchdog.Core.Notifications.Digest;

/// <summary>The counters of a digest period (all events of the period, not only the ones shown in sections).</summary>
public sealed record DigestCounts
{
    public int Total { get; init; }

    public int Critical { get; init; }

    public int High { get; init; }

    public int NewUsers { get; init; }

    public int NewComputers { get; init; }

    public int Deleted { get; init; }

    public int Disabled { get; init; }

    public int Enabled { get; init; }

    public int Lockouts { get; init; }

    /// <summary>Passwords set by another account (tag passwordReset).</summary>
    public int PasswordResets { get; init; }

    /// <summary>Own password changes (tag selfChange).</summary>
    public int OwnPasswordChanges { get; init; }

    /// <summary>Password changes whose actor is not known.</summary>
    public int OtherPasswordChanges { get; init; }

    public int PasswordAgeResets { get; init; }

    public int PrivilegedChanges { get; init; }

    public int DomainChanges { get; init; }

    /// <summary>Events ≥ High ("Wymaga uwagi").</summary>
    public int Attention { get; init; }

    /// <summary>Events ≥ High not acknowledged yet.</summary>
    public int Unacknowledged { get; init; }

    /// <summary>camelCase name → value (webhook <c>watchdog.digest</c> counts).</summary>
    public IReadOnlyDictionary<string, int> ToDictionary() => new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["total"] = Total,
        ["critical"] = Critical,
        ["high"] = High,
        ["newUsers"] = NewUsers,
        ["newComputers"] = NewComputers,
        ["deleted"] = Deleted,
        ["disabled"] = Disabled,
        ["enabled"] = Enabled,
        ["lockouts"] = Lockouts,
        ["passwordResets"] = PasswordResets,
        ["ownPasswordChanges"] = OwnPasswordChanges,
        ["otherPasswordChanges"] = OtherPasswordChanges,
        ["passwordAgeResets"] = PasswordAgeResets,
        ["privilegedChanges"] = PrivilegedChanges,
        ["domainChanges"] = DomainChanges,
        ["attention"] = Attention,
        ["unacknowledged"] = Unacknowledged,
    };
}
