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

    public static string Build(QuickFilterKind kind, DateTimeOffset now, int inactiveDays) => kind switch
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
        QuickFilterKind.EmptyGroups => LdapFilter.And(Groups, LdapFilter.NotPresent(Attr.Member)),
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

    /// <summary>Combines a base filter with active chips (AND).</summary>
    public static string Combine(string baseFilter, IEnumerable<QuickFilterKind> chips, DateTimeOffset now, int inactiveDays)
    {
        ArgumentNullException.ThrowIfNull(chips);
        return LdapFilter.And([baseFilter, .. chips.Select(c => Build(c, now, inactiveDays))]);
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
