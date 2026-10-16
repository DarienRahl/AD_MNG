using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.Lists;

/// <summary>Server side quick filters ("chips") of object lists; also the building blocks of built-in views.</summary>
public enum QuickFilterKind
{
    Disabled,
    Enabled,
    Locked,
    PasswordNeverExpires,
    MustChangePassword,
    Inactive,
    Expired,
    Expiring,
    EmptyGroups,
}

/// <summary>
/// LDAP filters of the quick filters (Search-ADAccount equivalents). Date based filters are computed from
/// <c>now</c> at query time, so they are always current.
/// </summary>
public static class QuickFilters
{
    /// <summary>Days ahead for "expiring accounts".</summary>
    public const int ExpiringDays = 30;

    /// <summary>User accounts (not computers, not contacts) - indexed sAMAccountType.</summary>
    public static string Users => ObjectKind.User.ToLdapFilter();

    public static string Computers => ObjectKind.Computer.ToLdapFilter();

    public static string Groups => ObjectKind.Group.ToLdapFilter();

    /// <summary>User and computer accounts.</summary>
    public static string Accounts => LdapFilter.Or(Users, Computers);

    /// <summary>
    /// RIDs of the domain's default primary groups: Domain Users, Domain Guests, Domain Computers, Domain Controllers
    /// and Read-only Domain Controllers.
    /// </summary>
    public static IReadOnlyList<uint> PrimaryGroupRids { get; } =
    [
        WellKnownRids.DomainUsers, WellKnownRids.DomainGuests, WellKnownRids.DomainComputers,
        WellKnownRids.DomainControllers, WellKnownRids.ReadOnlyDomainControllers,
    ];

    /// <param name="kind">The quick filter.</param>
    /// <param name="now">Reference time of the date based filters.</param>
    /// <param name="inactiveDays">Threshold of <see cref="QuickFilterKind.Inactive"/>.</param>
    /// <param name="domainSid">SID of the session's domain (refines <see cref="QuickFilterKind.EmptyGroups"/>), null when unknown.</param>
    public static string Build(QuickFilterKind kind, DateTimeOffset now, int inactiveDays, string? domainSid = null) => kind switch
    {
        QuickFilterKind.Disabled => LdapFilter.BitAnd(Attr.UserAccountControl, (int)UserAccountControl.AccountDisable),
        QuickFilterKind.Enabled => LdapFilter.And(
            LdapFilter.Present(Attr.UserAccountControl),
            LdapFilter.Not(LdapFilter.BitAnd(Attr.UserAccountControl, (int)UserAccountControl.AccountDisable))),
        QuickFilterKind.Locked => LdapFilter.GreaterOrEqual(Attr.LockoutTime, 1),
        QuickFilterKind.PasswordNeverExpires => LdapFilter.BitAnd(Attr.UserAccountControl, (int)UserAccountControl.DontExpirePassword),
        QuickFilterKind.MustChangePassword => LdapFilter.Equal(Attr.PwdLastSet, 0),
        QuickFilterKind.Inactive => Inactive(now, inactiveDays),
        QuickFilterKind.Expired => LdapFilter.And(
            LdapFilter.GreaterOrEqual(Attr.AccountExpires, 1),
            LdapFilter.LessOrEqual(Attr.AccountExpires, AdConvert.ToFileTime(now))),
        QuickFilterKind.Expiring => LdapFilter.And(
            LdapFilter.GreaterOrEqual(Attr.AccountExpires, AdConvert.ToFileTime(now)),
            LdapFilter.LessOrEqual(Attr.AccountExpires, AdConvert.ToFileTime(now.AddDays(ExpiringDays)))),
        QuickFilterKind.EmptyGroups => EmptyGroups(domainSid),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>
    /// Accounts whose lastLogonTimestamp is older than <paramref name="days"/> days, or that never logged on and
    /// were created before that. lastLogonTimestamp replicates with up to ~14 days delay, so N should be &gt; 14.
    /// </summary>
    public static string Inactive(DateTimeOffset now, int days)
    {
        var threshold = now.AddDays(-Math.Max(1, days));
        return LdapFilter.And(
            Accounts,
            LdapFilter.Or(
                LdapFilter.LessOrEqual(Attr.LastLogonTimestamp, AdConvert.ToFileTime(threshold)),
                LdapFilter.And(
                    LdapFilter.NotPresent(Attr.LastLogonTimestamp),
                    LdapFilter.LessOrEqual(Attr.WhenCreated, AdConvert.ToGeneralizedTime(threshold)))));
    }

    /// <summary>
    /// Groups without <c>member</c> values. Accounts are not listed in <c>member</c> of their primary group (they point
    /// to it with <c>primaryGroupID</c>), so Domain Users, Domain Computers, Domain Controllers... always look empty:
    /// with the domain's SID these default primary groups (<see cref="PrimaryGroupRids"/>) are excluded server side by
    /// <c>objectSid</c> (AD accepts the SID string form in filters). Without the SID they are listed.
    /// Limitation: a custom group that is only some accounts' primary group (their primaryGroupID is its RID) and has
    /// no other members is still listed - telling it apart needs one primaryGroupID query per listed group.
    /// </summary>
    /// <param name="domainSid">SID of the session's domain (S-1-5-21-x-y-z); anything else is ignored.</param>
    public static string EmptyGroups(string? domainSid)
    {
        string? primaryGroups = null;
        if (IsDomainSid(domainSid))
        {
            primaryGroups = LdapFilter.Not(LdapFilter.Or(PrimaryGroupRids.Select(rid =>
                LdapFilter.Equal(Attr.ObjectSid, string.Create(CultureInfo.InvariantCulture, $"{domainSid}-{rid}")))));
        }

        return LdapFilter.And(Groups, LdapFilter.NotPresent(Attr.Member), primaryGroups);
    }

    /// <summary>Combines a base filter with active chips (AND).</summary>
    /// <param name="baseFilter">Filter of the location.</param>
    /// <param name="chips">Active quick filters.</param>
    /// <param name="now">Reference time of the date based filters.</param>
    /// <param name="inactiveDays">Threshold of <see cref="QuickFilterKind.Inactive"/>.</param>
    /// <param name="domainSid">SID of the session's domain (refines <see cref="QuickFilterKind.EmptyGroups"/>), null when unknown.</param>
    public static string Combine(string baseFilter, IEnumerable<QuickFilterKind> chips, DateTimeOffset now, int inactiveDays, string? domainSid = null)
    {
        ArgumentNullException.ThrowIfNull(chips);
        return LdapFilter.And([baseFilter, .. chips.Select(c => Build(c, now, inactiveDays, domainSid))]);
    }

    /// <summary>A domain SID in SDDL form: S-1-5-21 and three numeric sub-authorities (nothing else reaches a filter).</summary>
    private static bool IsDomainSid(string? sid)
    {
        var parts = sid?.Split('-');
        return parts is ["S", "1", "5", "21", _, _, _]
            && parts.Skip(4).All(p => p.Length > 0 && uint.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }
}

/// <summary>One toggleable quick filter chip above a list.</summary>
public sealed partial class FilterChipViewModel(QuickFilterKind kind, string title) : ObservableObject
{
    public QuickFilterKind Kind { get; } = kind;

    [ObservableProperty]
    public partial string Title { get; set; } = title;

    [ObservableProperty]
    public partial bool IsActive { get; set; }
}
