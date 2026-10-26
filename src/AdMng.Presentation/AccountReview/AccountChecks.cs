using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.Presentation.Lists;

namespace AdMng.Presentation.AccountReview;

/// <summary>One check of the account review (README, U2). The order is the order of the tiles.</summary>
public enum AccountCheck
{
    Locked,
    PasswordExpired,
    PasswordExpiring,
    MustChangePassword,
    AccountExpired,
    AccountExpiring,
    Disabled,
    Inactive,
    NeverLoggedOn,
    PasswordNeverExpires,
    PasswordNotRequired,
    ReversibleEncryption,
    PasswordOld,
    NoMail,
    NoManager,
    ManagerDisabled,
    DisabledInGroups,
    DisabledInGal,
    PrivilegedWithProblem,
}

/// <summary>Area of a check (the rows of the README table; the tiles are grouped by it).</summary>
public enum AccountCheckArea
{
    Lockout,
    Password,
    AccountExpiry,
    Disabled,
    Activity,
    PasswordHygiene,
    Notifications,
    Leftovers,
    Security,
}

/// <summary>How serious a finding of a check is (tile accent).</summary>
public enum AccountCheckSeverity
{
    Info,
    Warning,
    Critical,
}

/// <summary>
/// The N-day parameters of the review. The property names avoid the word "password": they are persisted in the settings,
/// which must never contain it (settings test).
/// </summary>
public sealed record AccountReviewParameters
{
    public const int MaxDays = 3650;

    /// <summary>"Password expires in ≤ N days" (0 = today).</summary>
    public int ExpiryWarningDays { get; init; } = 14;

    /// <summary>"Account expires in ≤ N days" (0 = today).</summary>
    public int AccountExpiryWarningDays { get; init; } = 30;

    /// <summary>"Inactive &gt; N days" and the creation grace of "never logged on".</summary>
    public int InactiveDays { get; init; } = 90;

    /// <summary>"Password older than N days" (pwdLastSet).</summary>
    public int RotationDays { get; init; } = 365;

    /// <summary>Values from a hand-edited settings file brought into range.</summary>
    public AccountReviewParameters Normalized() => new()
    {
        ExpiryWarningDays = Math.Clamp(ExpiryWarningDays, 0, MaxDays),
        AccountExpiryWarningDays = Math.Clamp(AccountExpiryWarningDays, 0, MaxDays),
        InactiveDays = Math.Clamp(InactiveDays, 1, MaxDays),
        RotationDays = Math.Clamp(RotationDays, 1, MaxDays),
    };
}

/// <summary>
/// What the review knows about one account after the read: <see cref="AccountStatus"/> plus the facts of the
/// notification and leftover checks, the privileged state (from the extra query) and the manager's state (resolved after
/// the stream). In memory only.
/// </summary>
public sealed record AccountReviewFacts
{
    /// <summary>The account as read (attributes of <see cref="AccountReviewQuery.Attributes"/>).</summary>
    public required DirectoryObject Object { get; init; }

    public required AccountStatus Status { get; init; }

    public string DistinguishedName => Object.DistinguishedName;

    /// <summary>mail, or a primary <c>SMTP:</c> address in proxyAddresses (the U1 recipient rule).</summary>
    public string? NotificationAddress { get; init; }

    public string? ManagerDn { get; init; }

    /// <summary>The manager is disabled: true/false once resolved, null while unknown (not read yet, other domain).</summary>
    public bool? IsManagerDisabled { get; init; }

    /// <summary>Groups in memberOf (the primary group is not listed there).</summary>
    public IReadOnlyList<string> Groups { get; init; } = [];

    /// <summary>memberOf came back ranged (more than 1500 values): <see cref="Groups"/> is incomplete.</summary>
    public bool HasMoreGroups { get; init; }

    /// <summary>isCriticalSystemObject (krbtgt, Guest, Administrator).</summary>
    public bool IsCriticalSystemObject { get; init; }

    /// <summary>showInAddressBook has values and msExchHideFromAddressLists is not TRUE.</summary>
    public bool IsVisibleInGal { get; init; }

    /// <summary>adminCount = 1, or a (nested) member of a protected group (extra query).</summary>
    public bool IsPrivileged { get; init; }

    /// <summary>lastLogon read from the domain controllers on demand (the newest), and the DC that had it.</summary>
    public DateTimeOffset? ExactLastLogon { get; init; }

    public string? ExactLastLogonServer { get; init; }

    /// <summary>The exact last logon was read (also when no DC had a logon).</summary>
    public bool HasExactLastLogon { get; init; }

    /// <summary>Evaluates a read account.</summary>
    /// <param name="obj">The account.</param>
    /// <param name="now">Reference time.</param>
    /// <param name="timeZone">Time zone of dates and days.</param>
    /// <param name="maxPasswordAge">Domain maxPwdAge (fallback when the constructed expiry is missing).</param>
    /// <param name="isLockedByTime">Lock state from lockoutTime when the computed UAC is missing.</param>
    /// <param name="privileged">DnKeys of the privileged accounts (null when that check does not run).</param>
    public static AccountReviewFacts From(
        DirectoryObject obj,
        DateTimeOffset now,
        TimeZoneInfo timeZone,
        TimeSpan? maxPasswordAge,
        Func<long, bool>? isLockedByTime,
        IReadOnlySet<string>? privileged)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var groups = obj.TryGet(Attr.MemberOf, out var memberOf) ? memberOf.Strings : [];
        var ranged = memberOf is not null && memberOf.Name.Contains(";range=", StringComparison.OrdinalIgnoreCase)
            && !memberOf.Name.EndsWith("-*", StringComparison.Ordinal);
        var hidden = string.Equals(obj.GetString(Attr.HideFromAddressLists), "TRUE", StringComparison.OrdinalIgnoreCase);
        return new AccountReviewFacts
        {
            Object = obj,
            Status = AccountStatus.Evaluate(obj, now, timeZone, maxPasswordAge, isLockedByTime),
            NotificationAddress = NotificationAddressOf(obj),
            ManagerDn = obj.GetString(Attr.Manager),
            Groups = groups,
            HasMoreGroups = ranged,
            IsCriticalSystemObject = obj.GetBoolean(Attr.IsCriticalSystemObject) == true,
            IsVisibleInGal = obj.GetStrings(Attr.ShowInAddressBook).Count > 0 && !hidden,
            IsPrivileged = obj.IsProtectedByAdminSdHolder || privileged?.Contains(DnKey.For(obj.DistinguishedName)) == true,
        };
    }

    /// <summary>mail, else the primary SMTP address of proxyAddresses (upper-case prefix), else null.</summary>
    public static string? NotificationAddressOf(DirectoryObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        if (obj.GetString(Attr.Mail) is { Length: > 0 } mail && !string.IsNullOrWhiteSpace(mail))
        {
            return mail.Trim();
        }

        return obj.GetStrings(Attr.ProxyAddresses)
            .Where(a => a.StartsWith("SMTP:", StringComparison.Ordinal) && a.Length > 5)
            .Select(a => a[5..].Trim())
            .FirstOrDefault(a => a.Length > 0);
    }
}

/// <summary>
/// Server pre-filters, attributes and client side predicates of the account review (design: docs/dev/u2-account-review.md).
/// Constructed attributes (msDS-User-Account-Control-Computed, msDS-UserPasswordExpiryTimeComputed) cannot be used in a
/// filter, so the server narrows down with stored attributes - the phase-1 chips where one exists - and the exact
/// decision is made here.
/// </summary>
public static class AccountReviewQuery
{
    /// <summary>Well-known SIDs of the protected built-in groups (AdminSDHolder): Administrators and the operators.</summary>
    public static IReadOnlyList<string> BuiltinProtectedGroupSids { get; } =
        ["S-1-5-32-544", "S-1-5-32-548", "S-1-5-32-549", "S-1-5-32-550", "S-1-5-32-551", "S-1-5-32-552"];

    /// <summary>RIDs of the protected groups of the domain (Domain Admins … Enterprise Key Admins).</summary>
    public static IReadOnlyList<uint> ProtectedGroupRids { get; } =
    [
        WellKnownRids.DomainAdmins, WellKnownRids.DomainControllers, WellKnownRids.CertPublishers, WellKnownRids.SchemaAdmins,
        WellKnownRids.EnterpriseAdmins, WellKnownRids.GroupPolicyCreatorOwners, WellKnownRids.ReadOnlyDomainControllers,
        WellKnownRids.KeyAdmins, WellKnownRids.EnterpriseKeyAdmins,
    ];

    /// <summary>The checks a privileged account is reported for ("Konta uprzywilejowane z problemem").</summary>
    public static IReadOnlyList<AccountCheck> PrivilegedProblems { get; } =
    [
        AccountCheck.Locked, AccountCheck.PasswordExpired, AccountCheck.MustChangePassword, AccountCheck.AccountExpired,
        AccountCheck.Inactive, AccountCheck.NeverLoggedOn, AccountCheck.PasswordNeverExpires, AccountCheck.PasswordNotRequired,
        AccountCheck.ReversibleEncryption, AccountCheck.PasswordOld, AccountCheck.DisabledInGroups,
    ];

    private static readonly string[] BaseAttributes =
    [
        Attr.Name, Attr.DisplayName, Attr.SamAccountName, Attr.UserPrincipalName, Attr.ObjectClass, Attr.Description,
        Attr.UserAccountControl, Attr.UserAccountControlComputed, Attr.PasswordExpiryComputed, Attr.LockoutTime,
        Attr.PwdLastSet, Attr.AccountExpires, Attr.LastLogonTimestamp, Attr.WhenCreated, Attr.Mail, Attr.ProxyAddresses,
        Attr.Manager, Attr.MemberOf, Attr.AdminCount, Attr.IsCriticalSystemObject, Attr.ShowInAddressBook, Attr.Department,
    ];

    /// <summary>Area of a check.</summary>
    public static AccountCheckArea AreaOf(AccountCheck check) => check switch
    {
        AccountCheck.Locked => AccountCheckArea.Lockout,
        AccountCheck.PasswordExpired or AccountCheck.PasswordExpiring or AccountCheck.MustChangePassword => AccountCheckArea.Password,
        AccountCheck.AccountExpired or AccountCheck.AccountExpiring => AccountCheckArea.AccountExpiry,
        AccountCheck.Disabled => AccountCheckArea.Disabled,
        AccountCheck.Inactive or AccountCheck.NeverLoggedOn => AccountCheckArea.Activity,
        AccountCheck.PasswordNeverExpires or AccountCheck.PasswordNotRequired or AccountCheck.ReversibleEncryption
            or AccountCheck.PasswordOld => AccountCheckArea.PasswordHygiene,
        AccountCheck.NoMail or AccountCheck.NoManager or AccountCheck.ManagerDisabled => AccountCheckArea.Notifications,
        AccountCheck.DisabledInGroups or AccountCheck.DisabledInGal => AccountCheckArea.Leftovers,
        _ => AccountCheckArea.Security,
    };

    /// <summary>Accent of a check's tile.</summary>
    public static AccountCheckSeverity SeverityOf(AccountCheck check) => check switch
    {
        AccountCheck.Locked or AccountCheck.PasswordExpired or AccountCheck.AccountExpired or AccountCheck.PasswordNotRequired
            or AccountCheck.ReversibleEncryption or AccountCheck.PrivilegedWithProblem => AccountCheckSeverity.Critical,
        AccountCheck.PasswordExpiring or AccountCheck.AccountExpiring or AccountCheck.Inactive or AccountCheck.NeverLoggedOn
            or AccountCheck.DisabledInGroups or AccountCheck.DisabledInGal or AccountCheck.PasswordNeverExpires
            or AccountCheck.PasswordOld or AccountCheck.ManagerDisabled => AccountCheckSeverity.Warning,
        _ => AccountCheckSeverity.Info,
    };

    /// <summary>The attributes of the review query (Exchange's msExchHideFromAddressLists only when the schema has it).</summary>
    public static IReadOnlyList<string> Attributes(ISchemaCatalog? schema) =>
        HasAttribute(schema, Attr.HideFromAddressLists) ? [.. BaseAttributes, Attr.HideFromAddressLists] : BaseAttributes;

    /// <summary>Enabled accounts (UAC bit 2 not set).</summary>
    public static string Enabled => QuickFilters.Build(QuickFilterKind.Enabled, DateTimeOffset.UnixEpoch, 1);

    /// <summary>Disabled accounts (chip "Wyłączone").</summary>
    public static string Disabled => QuickFilters.Build(QuickFilterKind.Disabled, DateTimeOffset.UnixEpoch, 1);

    /// <summary>The server pre-filter of one check (see the design note, section 3).</summary>
    /// <param name="check">The check.</param>
    /// <param name="parameters">The N days.</param>
    /// <param name="now">Reference time.</param>
    /// <param name="schema">Schema of the session: attributes it does not define are left out of the filter.</param>
    public static string PreFilter(AccountCheck check, AccountReviewParameters parameters, DateTimeOffset now, ISchemaCatalog? schema = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var neverExpires = LdapFilter.BitAnd(Attr.UserAccountControl, (int)UserAccountControl.DontExpirePassword);
        var passwordCanExpire = LdapFilter.And(LdapFilter.GreaterOrEqual(Attr.PwdLastSet, 1), LdapFilter.Not(neverExpires));
        return check switch
        {
            AccountCheck.Locked => LdapFilter.And(Enabled, Chip(QuickFilterKind.Locked, now)),
            AccountCheck.PasswordExpired or AccountCheck.PasswordExpiring => LdapFilter.And(Enabled, passwordCanExpire),
            AccountCheck.MustChangePassword => LdapFilter.And(Enabled, Chip(QuickFilterKind.MustChangePassword, now), LdapFilter.Not(neverExpires)),
            AccountCheck.AccountExpired => LdapFilter.And(Enabled, Chip(QuickFilterKind.Expired, now)),
            AccountCheck.AccountExpiring => LdapFilter.And(
                Enabled,
                LdapFilter.GreaterOrEqual(Attr.AccountExpires, AdConvert.ToFileTime(now)),
                LdapFilter.LessOrEqual(Attr.AccountExpires, AdConvert.ToFileTime(now.AddDays(parameters.AccountExpiryWarningDays + 1)))),
            AccountCheck.Disabled => Disabled,
            AccountCheck.Inactive => LdapFilter.And(
                Enabled,
                LdapFilter.LessOrEqual(Attr.LastLogonTimestamp, AdConvert.ToFileTime(now.AddDays(-parameters.InactiveDays)))),
            AccountCheck.NeverLoggedOn => LdapFilter.And(
                Enabled,
                LdapFilter.NotPresent(Attr.LastLogonTimestamp),
                LdapFilter.LessOrEqual(Attr.WhenCreated, AdConvert.ToGeneralizedTime(now.AddDays(-parameters.InactiveDays)))),
            AccountCheck.PasswordNeverExpires => LdapFilter.And(Enabled, Chip(QuickFilterKind.PasswordNeverExpires, now)),
            AccountCheck.PasswordNotRequired => LdapFilter.And(Enabled, LdapFilter.BitAnd(Attr.UserAccountControl, (int)UserAccountControl.PasswordNotRequired)),
            AccountCheck.ReversibleEncryption => LdapFilter.And(Enabled, LdapFilter.BitAnd(Attr.UserAccountControl, (int)UserAccountControl.EncryptedTextPasswordAllowed)),
            AccountCheck.PasswordOld => LdapFilter.And(
                Enabled,
                LdapFilter.GreaterOrEqual(Attr.PwdLastSet, 1),
                LdapFilter.LessOrEqual(Attr.PwdLastSet, AdConvert.ToFileTime(now.AddDays(-parameters.RotationDays)))),
            AccountCheck.NoMail => LdapFilter.And(Enabled, LdapFilter.NotPresent(Attr.Mail)),
            AccountCheck.NoManager => LdapFilter.And(Enabled, LdapFilter.NotPresent(Attr.Manager)),
            AccountCheck.ManagerDisabled => LdapFilter.And(Enabled, LdapFilter.Present(Attr.Manager)),
            AccountCheck.DisabledInGroups => LdapFilter.And(Disabled, LdapFilter.Present(Attr.MemberOf)),
            AccountCheck.DisabledInGal => LdapFilter.And(
                Disabled,
                LdapFilter.Present(Attr.ShowInAddressBook),
                HasAttribute(schema, Attr.HideFromAddressLists) ? LdapFilter.Not(LdapFilter.Equal(Attr.HideFromAddressLists, "TRUE")) : null),
            AccountCheck.PrivilegedWithProblem => LdapFilter.Or(PrivilegedProblems.Select(c => PreFilter(c, parameters, now, schema))),
            _ => throw new ArgumentOutOfRangeException(nameof(check), check, null),
        };
    }

    /// <summary>
    /// The filter of the review query: user accounts AND the scope AND the OR of the pre-filters of <paramref name="checks"/>.
    /// </summary>
    public static string Filter(
        IEnumerable<AccountCheck> checks,
        string? scopeFilter,
        AccountReviewParameters parameters,
        DateTimeOffset now,
        ISchemaCatalog? schema = null)
    {
        ArgumentNullException.ThrowIfNull(checks);
        var preFilters = checks.Distinct().Order().Select(c => PreFilter(c, parameters, now, schema)).Distinct(StringComparer.Ordinal).ToList();
        if (preFilters.Count == 0)
        {
            throw new ArgumentException("At least one check is required.", nameof(checks));
        }

        return LdapFilter.And(QuickFilters.Users, scopeFilter, preFilters.Count == 1 ? preFilters[0] : LdapFilter.Or(preFilters));
    }

    /// <summary>The protected groups by objectSid (needs the domain SID for the domain groups).</summary>
    public static string ProtectedGroupsFilter(string? domainSid)
    {
        var sids = BuiltinProtectedGroupSids.ToList();
        if (!string.IsNullOrWhiteSpace(domainSid) && domainSid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase))
        {
            sids.AddRange(ProtectedGroupRids.Select(rid => $"{domainSid}-{rid}"));
        }

        return LdapFilter.And(QuickFilters.Groups, LdapFilter.Or(sids.Select(s => LdapFilter.Equal(Attr.ObjectSid, s))));
    }

    /// <summary>adminCount = 1 or a nested member (LDAP_MATCHING_RULE_IN_CHAIN on memberOf) of one of the groups.</summary>
    public static string PrivilegedFilter(IEnumerable<string> protectedGroupDns)
    {
        ArgumentNullException.ThrowIfNull(protectedGroupDns);
        return LdapFilter.Or([LdapFilter.Equal(Attr.AdminCount, 1), .. protectedGroupDns.Select(dn => LdapFilter.InChain(Attr.MemberOf, dn))]);
    }

    /// <summary>Members of a group, nested, and the accounts whose primary group it is.</summary>
    /// <param name="groupDn">The group.</param>
    /// <param name="groupRid">RID of the group (from its objectSid), null when unknown.</param>
    public static string GroupMembersFilter(string groupDn, uint? groupRid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupDn);
        var inChain = LdapFilter.InChain(Attr.MemberOf, groupDn);
        return groupRid is { } rid ? LdapFilter.Or(inChain, LdapFilter.Equal(Attr.PrimaryGroupId, rid)) : inChain;
    }

    /// <summary>Whether an account is a finding of <paramref name="check"/> (exact, client side).</summary>
    public static bool Matches(AccountCheck check, AccountReviewFacts facts, AccountReviewParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(parameters);
        var s = facts.Status;
        if (!s.HasAccountControl)
        {
            return false;
        }

        if (check is AccountCheck.Disabled)
        {
            return s.IsDisabled;
        }

        if (check is AccountCheck.DisabledInGroups or AccountCheck.DisabledInGal)
        {
            return s.IsDisabled && !facts.IsCriticalSystemObject
                && (check == AccountCheck.DisabledInGroups ? facts.Groups.Count > 0 : facts.IsVisibleInGal);
        }

        if (s.IsDisabled)
        {
            return check == AccountCheck.PrivilegedWithProblem && facts.IsPrivileged && Matches(AccountCheck.DisabledInGroups, facts, parameters);
        }

        return check switch
        {
            AccountCheck.Locked => s.IsLocked,
            AccountCheck.PasswordExpired => s.Password == PasswordState.Expired,
            AccountCheck.PasswordExpiring => s.Password == PasswordState.Expires && s.PasswordDaysLeft <= parameters.ExpiryWarningDays,
            AccountCheck.MustChangePassword => s.Password == PasswordState.MustChange,
            AccountCheck.AccountExpired => s.IsAccountExpired,
            AccountCheck.AccountExpiring => !s.IsAccountExpired && s.AccountDaysLeft <= parameters.AccountExpiryWarningDays,
            AccountCheck.Inactive => s.LastLogon is not null && s.DaysSinceLogon >= parameters.InactiveDays,
            AccountCheck.NeverLoggedOn => s.LastLogon is null && s.DaysSinceCreated >= parameters.InactiveDays,
            AccountCheck.PasswordNeverExpires => s.HasPasswordNeverExpiresFlag,
            AccountCheck.PasswordNotRequired => s.IsPasswordNotRequired,
            AccountCheck.ReversibleEncryption => s.HasReversibleEncryption,
            AccountCheck.PasswordOld => s.PasswordAgeDays >= parameters.RotationDays,
            AccountCheck.NoMail => facts.NotificationAddress is null,
            AccountCheck.NoManager => facts.ManagerDn is null,
            AccountCheck.ManagerDisabled => facts.ManagerDn is not null && facts.IsManagerDisabled == true,
            AccountCheck.PrivilegedWithProblem => facts.IsPrivileged && PrivilegedProblems.Any(c => Matches(c, facts, parameters)),
            _ => false,
        };
    }

    /// <summary>The problem checks a privileged account is reported for (the "Problems" column).</summary>
    public static IReadOnlyList<AccountCheck> PrivilegedProblemsOf(AccountReviewFacts facts, AccountReviewParameters parameters) =>
        [.. PrivilegedProblems.Where(c => Matches(c, facts, parameters))];

    private static string Chip(QuickFilterKind kind, DateTimeOffset now) => QuickFilters.Build(kind, now, 1);

    private static bool HasAttribute(ISchemaCatalog? schema, string attribute) =>
        schema is not null && schema.Attributes.Count > 0 && schema.FindAttribute(attribute) is not null;
}
