using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Views;

/// <summary>Built-in quick filter views (Search-ADAccount equivalents and common security reports).</summary>
public enum BuiltInViewKind
{
    LockedUsers,
    DisabledUsers,
    DisabledComputers,
    InactiveUsers,
    InactiveComputers,
    PasswordNeverExpires,
    MustChangePassword,
    NeverLoggedOn,
    ExpiringAccounts,
    ExpiredAccounts,
    EmptyGroups,
    PrivilegedAccounts,
    UsersWithoutManager,
    PasswordNotRequired,
    PreAuthNotRequired,
    UnconstrainedDelegation,
}

/// <summary>Definition of one built-in view. Filters with dates are computed at query time.</summary>
public sealed record BuiltInViewDefinition(
    BuiltInViewKind Kind,
    ObjectIcon Icon,
    IReadOnlyList<string> Columns,
    ListPostFilter PostFilter = ListPostFilter.None)
{
    public const string IdPrefix = "builtin:";

    public string Id => IdPrefix + Kind;

    /// <summary>Views whose title contains the number of inactive days.</summary>
    public bool UsesInactiveDays => Kind is BuiltInViewKind.InactiveUsers or BuiltInViewKind.InactiveComputers;

    public string Title(ILocalizer localizer, int inactiveDays)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        var key = DisplayNames.BuiltInViewKey(Kind);
        return UsesInactiveDays ? localizer.Format(key, inactiveDays) : localizer[key];
    }

    public string BuildFilter(DateTimeOffset now, int inactiveDays)
    {
        var users = QuickFilters.Users;
        var computers = QuickFilters.Computers;
        return Kind switch
        {
            BuiltInViewKind.LockedUsers => LdapFilter.And(users, QuickFilters.Build(QuickFilterKind.Locked, now, inactiveDays)),
            BuiltInViewKind.DisabledUsers => LdapFilter.And(users, QuickFilters.Build(QuickFilterKind.Disabled, now, inactiveDays)),
            BuiltInViewKind.DisabledComputers => LdapFilter.And(computers, QuickFilters.Build(QuickFilterKind.Disabled, now, inactiveDays)),
            BuiltInViewKind.InactiveUsers => LdapFilter.And(users, QuickFilters.Inactive(now, inactiveDays)),
            BuiltInViewKind.InactiveComputers => LdapFilter.And(computers, QuickFilters.Inactive(now, inactiveDays)),
            BuiltInViewKind.PasswordNeverExpires => LdapFilter.And(users, QuickFilters.Build(QuickFilterKind.PasswordNeverExpires, now, inactiveDays)),
            BuiltInViewKind.MustChangePassword => LdapFilter.And(users, QuickFilters.Build(QuickFilterKind.MustChangePassword, now, inactiveDays)),
            BuiltInViewKind.NeverLoggedOn => LdapFilter.And(
                users,
                LdapFilter.Or(LdapFilter.NotPresent(Attr.LastLogonTimestamp), LdapFilter.Equal(Attr.LastLogonTimestamp, 0))),
            BuiltInViewKind.ExpiringAccounts => LdapFilter.And(users, QuickFilters.Build(QuickFilterKind.Expiring, now, inactiveDays)),
            BuiltInViewKind.ExpiredAccounts => LdapFilter.And(users, QuickFilters.Build(QuickFilterKind.Expired, now, inactiveDays)),
            BuiltInViewKind.EmptyGroups => QuickFilters.Build(QuickFilterKind.EmptyGroups, now, inactiveDays),
            BuiltInViewKind.PrivilegedAccounts => LdapFilter.And(
                LdapFilter.Equal(Attr.AdminCount, 1),
                LdapFilter.Or(users, computers, QuickFilters.Groups)),
            BuiltInViewKind.UsersWithoutManager => LdapFilter.And(users, LdapFilter.NotPresent(Attr.Manager)),
            BuiltInViewKind.PasswordNotRequired => LdapFilter.And(
                users,
                LdapFilter.BitAnd(Attr.UserAccountControl, (int)UserAccountControl.PasswordNotRequired)),
            BuiltInViewKind.PreAuthNotRequired => LdapFilter.And(
                users,
                LdapFilter.BitAnd(Attr.UserAccountControl, (int)UserAccountControl.DontRequirePreauth)),
            BuiltInViewKind.UnconstrainedDelegation => LdapFilter.And(
                QuickFilters.Accounts,
                LdapFilter.BitAnd(Attr.UserAccountControl, (int)UserAccountControl.TrustedForDelegation),
                LdapFilter.Not(LdapFilter.BitAnd(Attr.UserAccountControl, (int)UserAccountControl.ServerTrustAccount))),
            _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null),
        };
    }

    /// <summary>
    /// The view as a static base filter plus the quick filter chip that carries its date threshold or post-filter
    /// (chips are evaluated at query time) - what "Save as view" stores, so a saved copy stays current instead of
    /// freezing today's dates (and keeps "locked out" exact). Views without dates or post-filter: their filter, no chip.
    /// </summary>
    public (string Filter, QuickFilterKind? Chip) ToSavedDefinition(DateTimeOffset now, int inactiveDays) => Kind switch
    {
        BuiltInViewKind.LockedUsers => (QuickFilters.Users, QuickFilterKind.Locked),
        BuiltInViewKind.InactiveUsers => (QuickFilters.Users, QuickFilterKind.Inactive),
        BuiltInViewKind.InactiveComputers => (QuickFilters.Computers, QuickFilterKind.Inactive),
        BuiltInViewKind.ExpiringAccounts => (QuickFilters.Users, QuickFilterKind.Expiring),
        BuiltInViewKind.ExpiredAccounts => (QuickFilters.Users, QuickFilterKind.Expired),
        _ => (BuildFilter(now, inactiveDays), null),
    };

    /// <summary>The list location (subtree of the domain) with the filter computed for <paramref name="now"/>.</summary>
    public ListLocation ToLocation(ILocalizer localizer, string defaultNamingContext, DateTimeOffset now, int inactiveDays) => new()
    {
        Kind = ListLocationKind.View,
        BaseDn = defaultNamingContext,
        Scope = SearchScope.Subtree,
        Filter = BuildFilter(now, inactiveDays),
        Title = Title(localizer, inactiveDays),
        Icon = ObjectIcon.QuickFilter,
        ViewId = Id,
        ColumnLayoutKey = Id,
        DefaultColumns = Columns,
        PostFilter = PostFilter,
    };
}

/// <summary>Catalog of <see cref="BuiltInViewDefinition"/>s in display order.</summary>
public static class BuiltInViews
{
    public static IReadOnlyList<BuiltInViewDefinition> All { get; } =
    [
        new(BuiltInViewKind.LockedUsers, ObjectIcon.UserLocked, ColumnSets.Lockout, ListPostFilter.LockedOnly),
        new(BuiltInViewKind.DisabledUsers, ObjectIcon.UserDisabled, ColumnSets.Users),
        new(BuiltInViewKind.DisabledComputers, ObjectIcon.ComputerDisabled, ColumnSets.Computers),
        new(BuiltInViewKind.InactiveUsers, ObjectIcon.User, ColumnSets.Activity),
        new(BuiltInViewKind.InactiveComputers, ObjectIcon.Computer, ColumnSets.Activity),
        new(BuiltInViewKind.PasswordNeverExpires, ObjectIcon.User, ColumnSets.Users),
        new(BuiltInViewKind.MustChangePassword, ObjectIcon.User, ColumnSets.Users),
        new(BuiltInViewKind.NeverLoggedOn, ObjectIcon.User, ColumnSets.Activity),
        new(BuiltInViewKind.ExpiringAccounts, ObjectIcon.User, ColumnSets.Expiry),
        new(BuiltInViewKind.ExpiredAccounts, ObjectIcon.User, ColumnSets.Expiry),
        new(BuiltInViewKind.EmptyGroups, ObjectIcon.Group, ColumnSets.Groups),
        new(BuiltInViewKind.PrivilegedAccounts, ObjectIcon.User, ColumnSets.Search),
        new(BuiltInViewKind.UsersWithoutManager, ObjectIcon.User, ColumnSets.Users),
        new(BuiltInViewKind.PasswordNotRequired, ObjectIcon.User, ColumnSets.Users),
        new(BuiltInViewKind.PreAuthNotRequired, ObjectIcon.User, ColumnSets.Users),
        new(BuiltInViewKind.UnconstrainedDelegation, ObjectIcon.Computer, ColumnSets.Search),
    ];

    public static BuiltInViewDefinition? Find(string? id) =>
        id is null ? null : All.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));
}
