using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Lists;

namespace AdMng.Presentation.Views;

/// <summary>
/// An operator defined view ("Widok"): a named LDAP query with columns and sort. Persisted in settings and
/// shareable as JSON - it contains names, DNs and filters only, never directory content.
/// </summary>
public sealed record SavedView
{
    /// <summary>Placeholder for the default naming context, so views are portable between domains (test/prod).</summary>
    public const string DomainPlaceholder = DistinguishedName.DomainPlaceholder;

    public string Id { get; init; } = NewId();

    public string Name { get; init; } = string.Empty;

    public ObjectIcon Icon { get; init; } = ObjectIcon.Query;

    /// <summary>Search base; may contain <see cref="DomainPlaceholder"/>, e.g. <c>OU=Firma,{domain}</c>.</summary>
    public string BaseDn { get; init; } = DomainPlaceholder;

    public SearchScope Scope { get; init; } = SearchScope.Subtree;

    public string Filter { get; init; } = ListLocation.AllObjectsFilter;

    /// <summary>Columns (LDAP names / pseudo columns); empty = search defaults.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary>Quick filter chips applied on top of <see cref="Filter"/> (date based chips stay current).</summary>
    public IReadOnlyList<QuickFilterKind> QuickFilters { get; init; } = [];

    /// <summary>Ids of saved filters (README U4) applied on top of <see cref="Filter"/>, shown as active filter chips.</summary>
    public IReadOnlyList<string> SavedFilters { get; init; } = [];

    public string? SortAttribute { get; init; }

    public bool SortDescending { get; init; }

    public string? Description { get; init; }

    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>Column layout key of a saved view.</summary>
    public static string LayoutKey(string id) => $"view:{id}";

    /// <summary>The base DN with <see cref="DomainPlaceholder"/> replaced.</summary>
    public string ResolveBaseDn(string defaultNamingContext) =>
        BaseDn.Replace(DomainPlaceholder, defaultNamingContext, StringComparison.OrdinalIgnoreCase);

    /// <summary>Replaces the domain suffix of a DN with <see cref="DomainPlaceholder"/> (portable form, <see cref="DistinguishedName.ToPortable"/>).</summary>
    public static string ToPortableDn(string distinguishedName, string defaultNamingContext) =>
        DistinguishedName.ToPortable(distinguishedName, defaultNamingContext);

    /// <summary>The list location of this view.</summary>
    public ListLocation ToLocation(string defaultNamingContext) => new()
    {
        Kind = ListLocationKind.View,
        BaseDn = ResolveBaseDn(defaultNamingContext),
        Scope = Scope,
        Filter = Filter,
        Title = Name,
        Icon = Icon,
        ViewId = Id,
        ColumnLayoutKey = LayoutKey(Id),
        DefaultColumns = Columns.Count > 0 ? Columns : ColumnSets.Search,
        Sort = string.IsNullOrWhiteSpace(SortAttribute) ? null : new SortKey(SortAttribute, SortDescending),
        QuickFilters = QuickFilters ?? [],
        SavedFilters = SavedFilters ?? [],
    };
}
