using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Presentation.Icons;

namespace AdMng.Presentation.Lists;

/// <summary>What an object list document shows.</summary>
public enum ListLocationKind
{
    /// <summary>Children of a container (one level), like the ADUC result pane.</summary>
    Container,

    /// <summary>Global quick search results (ANR, subtree of the domain).</summary>
    Search,

    /// <summary>A built-in quick filter view or an operator defined saved view.</summary>
    View,

    /// <summary>An ad-hoc LDAP query (Find dialog results).</summary>
    Query,
}

/// <summary>Client side refinement of server results that LDAP cannot express.</summary>
public enum ListPostFilter
{
    None,

    /// <summary>Drop rows whose lockoutTime is set but whose lockout already expired (domain lockout duration).</summary>
    LockedOnly,
}

/// <summary>
/// Immutable description of a list's source (search base, scope, filter) and presentation (title, icon,
/// default columns). Documents navigate between locations; history entries are locations.
/// </summary>
public sealed record ListLocation
{
    /// <summary>Default filter of container listings.</summary>
    public const string AllObjectsFilter = "(objectClass=*)";

    public required ListLocationKind Kind { get; init; }

    public required string BaseDn { get; init; }

    public SearchScope Scope { get; init; } = SearchScope.OneLevel;

    /// <summary>RFC 4515 filter (validated/escaped by whoever built it).</summary>
    public string Filter { get; init; } = AllObjectsFilter;

    /// <summary>Localized tab title.</summary>
    public required string Title { get; init; }

    public ObjectIcon Icon { get; init; } = ObjectIcon.Container;

    /// <summary>Key of the persisted column layout (settings.ColumnLayouts).</summary>
    public string ColumnLayoutKey { get; init; } = ColumnSets.ContainerLayoutKey;

    /// <summary>Columns used when no layout was persisted for <see cref="ColumnLayoutKey"/>.</summary>
    public IReadOnlyList<string> DefaultColumns { get; init; } = ColumnSets.Container;

    /// <summary>Saved/built-in view id for <see cref="ListLocationKind.View"/>.</summary>
    public string? ViewId { get; init; }

    /// <summary>Search text for <see cref="ListLocationKind.Search"/>.</summary>
    public string? SearchText { get; init; }

    /// <summary>Initial sort (e.g. from a saved view).</summary>
    public SortKey? Sort { get; init; }

    public ListPostFilter PostFilter { get; init; }

    /// <summary>Quick filter chips activated when the location is shown (saved views).</summary>
    public IReadOnlyList<QuickFilterKind> QuickFilters { get; init; } = [];

    /// <summary>Parent container for "Up" (containers: the parent DN; other kinds: the search base).</summary>
    public string? UpDn => Kind == ListLocationKind.Container
        ? (DistinguishedName.TryParse(BaseDn, out var dn) && dn.Parent is { IsRoot: false } parent ? parent.ToString() : null)
        : BaseDn;

    /// <summary>Children of a container.</summary>
    public static ListLocation ForContainer(string distinguishedName, string title, ObjectIcon icon = ObjectIcon.Container) => new()
    {
        Kind = ListLocationKind.Container,
        BaseDn = distinguishedName,
        Scope = SearchScope.OneLevel,
        Title = title,
        Icon = icon,
    };

    /// <summary>A container identified only by its DN: title = RDN value, icon guessed from the RDN type.</summary>
    public static ListLocation ForContainer(string distinguishedName)
    {
        ArgumentNullException.ThrowIfNull(distinguishedName);
        var parsed = DistinguishedName.TryParse(distinguishedName, out var dn) ? dn : DistinguishedName.Root;
        if (parsed.IsRoot)
        {
            return ForContainer(distinguishedName, distinguishedName);
        }

        var type = parsed.Leaf.Type;
        var icon = type.Equals("OU", StringComparison.OrdinalIgnoreCase) ? ObjectIcon.OrganizationalUnit
            : type.Equals("DC", StringComparison.OrdinalIgnoreCase) ? ObjectIcon.Domain
            : ObjectIcon.Container;
        var title = icon == ObjectIcon.Domain ? parsed.DnsDomain : parsed.Name;
        return ForContainer(distinguishedName, title, icon);
    }

    /// <summary>Global search results (no size limit, streamed).</summary>
    public static ListLocation ForSearch(string text, string domainDn, string title, string filter) => new()
    {
        Kind = ListLocationKind.Search,
        BaseDn = domainDn,
        Scope = SearchScope.Subtree,
        Filter = filter,
        Title = title,
        Icon = ObjectIcon.Query,
        SearchText = text,
        ColumnLayoutKey = ColumnSets.SearchLayoutKey,
        DefaultColumns = ColumnSets.Search,
    };

    /// <summary>An ad-hoc query (Find dialog).</summary>
    public static ListLocation ForQuery(string title, string baseDn, SearchScope scope, string filter, IReadOnlyList<string>? columns = null) => new()
    {
        Kind = ListLocationKind.Query,
        BaseDn = baseDn,
        Scope = scope,
        Filter = filter,
        Title = title,
        Icon = ObjectIcon.Query,
        ColumnLayoutKey = ColumnSets.SearchLayoutKey,
        DefaultColumns = columns ?? ColumnSets.Search,
    };
}
