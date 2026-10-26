using AdMng.Core.Model;

namespace AdMng.Core.ActiveDirectory;

/// <summary>State of a user's password at the reference time of an <see cref="AccountStatus"/>.</summary>
public enum PasswordState
{
    /// <summary>Neither the constructed expiry nor a domain maximum password age is known.</summary>
    Unknown,

    /// <summary>The password expires at <see cref="AccountStatus.PasswordExpiresAt"/> (in the future).</summary>
    Expires,

    /// <summary>The password expired at <see cref="AccountStatus.PasswordExpiresAt"/> (or the DC reports PASSWORD_EXPIRED).</summary>
    Expired,

    /// <summary>"Password never expires" (flag, or a policy / PSO without a maximum age).</summary>
    NeverExpires,

    /// <summary>"User must change password at next logon" (pwdLastSet = 0).</summary>
    MustChange,
}

/// <summary>
/// The account state of one user at a reference time, computed from the attributes of one read (<see cref="Attributes"/>):
/// lock, password expiry, account expiry, last logon, password age and the password hygiene flags. Pure and in memory;
/// used by the account review (U2), the Account tab summary and later the message fields of U1.
/// </summary>
/// <remarks>
/// <para>Password expiry comes from the constructed <c>msDS-UserPasswordExpiryTimeComputed</c>, which the DC computes from
/// the resultant password policy - fine-grained password policies (PSO) included. Order: <c>DONT_EXPIRE_PASSWORD</c> →
/// never; <c>pwdLastSet = 0</c> → must change; computed <c>2^63−1</c> → never; computed <c>0</c> → must change; computed
/// instant → expires/expired; computed attribute missing → <c>pwdLastSet + maxPwdAge</c> of the domain, flagged as
/// <see cref="IsPasswordExpiryEstimated"/> (a PSO is not known then).</para>
/// <para>Days are calendar days in <c>timeZone</c>: the last valid day of an account is the local date of
/// <c>accountExpires − 1 tick</c> (ADUC "End of: D" stores the local midnight that starts D+1), the last day of a password
/// the local date of the expiry moment (as the Watchdog expiry scanners).</para>
/// </remarks>
public sealed record AccountStatus
{
    /// <summary>Attributes <see cref="Evaluate"/> reads (constructed ones are valid in base and subtree searches).</summary>
    public static IReadOnlyList<string> Attributes { get; } =
    [
        Attr.UserAccountControl, Attr.UserAccountControlComputed, Attr.PasswordExpiryComputed, Attr.LockoutTime,
        Attr.PwdLastSet, Attr.AccountExpires, Attr.LastLogonTimestamp, Attr.WhenCreated,
    ];

    /// <summary>The reference time of the evaluation.</summary>
    public DateTimeOffset Now { get; init; }

    /// <summary>The reference date (today) in the evaluation's time zone.</summary>
    public DateOnly Today { get; init; }

    /// <summary>userAccountControl (stored and computed bits) was readable.</summary>
    public bool HasAccountControl { get; init; }

    public bool IsDisabled { get; init; }

    /// <summary>Locked out now (computed bit 0x10, or lockoutTime within the lockout duration when the bit is not available).</summary>
    public bool IsLocked { get; init; }

    /// <summary>lockoutTime while <see cref="IsLocked"/>.</summary>
    public DateTimeOffset? LockedSince { get; init; }

    public PasswordState Password { get; init; }

    /// <summary>When the password expires / expired (states <see cref="PasswordState.Expires"/> and <see cref="PasswordState.Expired"/>).</summary>
    public DateTimeOffset? PasswordExpiresAt { get; init; }

    /// <summary>The expiry is <c>pwdLastSet + maxPwdAge</c> of the domain (the DC did not return the constructed value).</summary>
    public bool IsPasswordExpiryEstimated { get; init; }

    /// <summary>Local date of <see cref="PasswordExpiresAt"/>.</summary>
    public DateOnly? PasswordLastDay { get; init; }

    /// <summary>Days from today to <see cref="PasswordLastDay"/> (0 = today, negative = days ago).</summary>
    public int? PasswordDaysLeft { get; init; }

    /// <summary>pwdLastSet (null when 0 or missing).</summary>
    public DateTimeOffset? PasswordLastSet { get; init; }

    /// <summary>Whole days since <see cref="PasswordLastSet"/>.</summary>
    public int? PasswordAgeDays { get; init; }

    /// <summary>The DONT_EXPIRE_PASSWORD flag is set.</summary>
    public bool HasPasswordNeverExpiresFlag { get; init; }

    /// <summary>PASSWD_NOTREQD - the account may have an empty password.</summary>
    public bool IsPasswordNotRequired { get; init; }

    /// <summary>ENCRYPTED_TEXT_PWD_ALLOWED - "Store password using reversible encryption".</summary>
    public bool HasReversibleEncryption { get; init; }

    /// <summary>accountExpires as an instant (null = never).</summary>
    public DateTimeOffset? AccountExpiresAt { get; init; }

    /// <summary>The last day the account may be used ("End of" date in the Account tab).</summary>
    public DateOnly? AccountLastDay { get; init; }

    /// <summary>Days from today to <see cref="AccountLastDay"/> (negative after it).</summary>
    public int? AccountDaysLeft { get; init; }

    public bool IsAccountExpired { get; init; }

    /// <summary>lastLogonTimestamp (replicated, up to ~14 days behind the real last logon); null = never.</summary>
    public DateTimeOffset? LastLogon { get; init; }

    /// <summary>Whole days since <see cref="LastLogon"/>.</summary>
    public int? DaysSinceLogon { get; init; }

    public DateTimeOffset? Created { get; init; }

    /// <summary>Whole days since <see cref="Created"/>.</summary>
    public int? DaysSinceCreated { get; init; }

    /// <summary>
    /// Evaluates <paramref name="account"/> (read with <see cref="Attributes"/>).
    /// </summary>
    /// <param name="account">The user.</param>
    /// <param name="now">Reference time.</param>
    /// <param name="timeZone">Time zone of the dates and day counts (the operator's local zone).</param>
    /// <param name="domainMaxPasswordAge">Domain maxPwdAge, used only when the constructed expiry is missing (null = unknown).</param>
    /// <param name="isLockedByTime">
    /// Lock state from lockoutTime when <c>msDS-User-Account-Control-Computed</c> is missing (lockout duration of the
    /// domain); null = lockoutTime &gt; 0.
    /// </param>
    public static AccountStatus Evaluate(
        DirectoryObject account,
        DateTimeOffset now,
        TimeZoneInfo timeZone,
        TimeSpan? domainMaxPasswordAge = null,
        Func<long, bool>? isLockedByTime = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(timeZone);
        var today = LocalDate(now, timeZone);
        var stored = account.GetInt32(Attr.UserAccountControl);
        var computedBits = account.GetInt32(Attr.UserAccountControlComputed);
        var uac = (UserAccountControl)((stored ?? 0) | (computedBits ?? 0));

        var lockoutTime = account.GetInt64(Attr.LockoutTime) ?? 0;
        var locked = computedBits is { } bits
            ? (bits & (int)UserAccountControl.Lockout) != 0
            : lockoutTime > 0 && (isLockedByTime?.Invoke(lockoutTime) ?? true);

        var pwdLastSetRaw = account.GetInt64(Attr.PwdLastSet);
        var pwdLastSet = pwdLastSetRaw is { } p ? AdConvert.FromFileTime(p) : null;
        var (password, expiresAt, estimated) = EvaluatePassword(account, uac, pwdLastSetRaw, pwdLastSet, now, domainMaxPasswordAge);
        DateOnly? passwordLastDay = expiresAt is { } e ? LocalDate(e, timeZone) : null;

        var accountExpires = AdConvert.FromFileTime(account.GetString(Attr.AccountExpires));
        DateOnly? accountLastDay = accountExpires is { } a ? LocalDate(a.AddTicks(-1), timeZone) : null;

        var lastLogon = AdConvert.FromFileTime(account.GetString(Attr.LastLogonTimestamp));
        var created = account.GetGeneralizedTime(Attr.WhenCreated);
        return new AccountStatus
        {
            Now = now,
            Today = today,
            HasAccountControl = stored is not null,
            IsDisabled = uac.HasFlag(UserAccountControl.AccountDisable),
            IsLocked = locked,
            LockedSince = locked ? AdConvert.FromFileTime(lockoutTime) : null,
            Password = password,
            PasswordExpiresAt = expiresAt,
            IsPasswordExpiryEstimated = estimated,
            PasswordLastDay = passwordLastDay,
            PasswordDaysLeft = passwordLastDay is { } pd ? pd.DayNumber - today.DayNumber : null,
            PasswordLastSet = pwdLastSet,
            PasswordAgeDays = WholeDays(pwdLastSet, now),
            HasPasswordNeverExpiresFlag = uac.HasFlag(UserAccountControl.DontExpirePassword),
            IsPasswordNotRequired = uac.HasFlag(UserAccountControl.PasswordNotRequired),
            HasReversibleEncryption = uac.HasFlag(UserAccountControl.EncryptedTextPasswordAllowed),
            AccountExpiresAt = accountExpires,
            AccountLastDay = accountLastDay,
            AccountDaysLeft = accountLastDay is { } ad ? ad.DayNumber - today.DayNumber : null,
            IsAccountExpired = accountExpires is { } x && x <= now,
            LastLogon = lastLogon,
            DaysSinceLogon = WholeDays(lastLogon, now),
            Created = created,
            DaysSinceCreated = WholeDays(created, now),
        };
    }

    /// <summary>The local calendar date of an instant.</summary>
    public static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, timeZone).DateTime);
    }

    /// <summary>
    /// The newest of <see cref="LastLogon"/> and an exact (non-replicated) lastLogon read from the domain controllers.
    /// </summary>
    public AccountStatus WithExactLastLogon(DateTimeOffset? exactLastLogon)
    {
        var newest = exactLastLogon is { } exact && (LastLogon is null || exact > LastLogon) ? exact : LastLogon;
        return this with { LastLogon = newest, DaysSinceLogon = WholeDays(newest, Now) };
    }

    private static (PasswordState State, DateTimeOffset? ExpiresAt, bool Estimated) EvaluatePassword(
        DirectoryObject account,
        UserAccountControl uac,
        long? pwdLastSetRaw,
        DateTimeOffset? pwdLastSet,
        DateTimeOffset now,
        TimeSpan? domainMaxPasswordAge)
    {
        if (uac.HasFlag(UserAccountControl.DontExpirePassword))
        {
            return (PasswordState.NeverExpires, null, false);
        }

        if (pwdLastSetRaw == 0)
        {
            return (PasswordState.MustChange, null, false);
        }

        var expiredBit = uac.HasFlag(UserAccountControl.PasswordExpired);
        if (account.GetInt64(Attr.PasswordExpiryComputed) is { } computed)
        {
            if (computed == AdConvert.FileTimeNever)
            {
                return (PasswordState.NeverExpires, null, false);
            }

            if (computed <= 0)
            {
                return (PasswordState.MustChange, null, false);
            }

            var at = AdConvert.FromFileTime(computed);
            return (at is { } instant && instant > now && !expiredBit ? PasswordState.Expires : PasswordState.Expired, at, false);
        }

        if (pwdLastSet is { } set && domainMaxPasswordAge is { } maxAge && maxAge > TimeSpan.Zero)
        {
            var estimate = set + maxAge;
            return (estimate > now && !expiredBit ? PasswordState.Expires : PasswordState.Expired, estimate, true);
        }

        return expiredBit ? (PasswordState.Expired, null, false) : (PasswordState.Unknown, null, false);
    }

    private static int? WholeDays(DateTimeOffset? since, DateTimeOffset now) =>
        since is { } s ? (int)Math.Floor((now - s).TotalDays) : null;
}
