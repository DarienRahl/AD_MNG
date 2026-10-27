using AdMng.Core.Model;

namespace AdMng.Core.Directory;

/// <summary>LDAP search scope.</summary>
public enum SearchScope
{
    Base,
    OneLevel,
    Subtree,
}

/// <summary>Server side sort key (requires the sort control; AD sorts by one key only).</summary>
public sealed record SortKey(string Attribute, bool Descending = false);

/// <summary>Description of one LDAP search. Request only the attributes you display.</summary>
public sealed record DirectorySearch
{
    public required string BaseDn { get; init; }

    public SearchScope Scope { get; init; } = SearchScope.Subtree;

    /// <summary>RFC 4515 filter. Build with <see cref="Ldap.LdapFilter"/> - never concatenate user input.</summary>
    public string Filter { get; init; } = "(objectClass=*)";

    /// <summary>Attributes to return. Empty means only the DN. Use "*" sparingly (it is slow).</summary>
    public IReadOnlyList<string> Attributes { get; init; } = [];

    /// <summary>Server page size for paged results (AD MaxPageSize defaults to 1000).</summary>
    public int PageSize { get; init; } = 500;

    /// <summary>Maximum number of objects to return in total; 0 = unlimited.</summary>
    public int SizeLimit { get; init; }

    /// <summary>Optional server side sort.</summary>
    public SortKey? Sort { get; init; }

    /// <summary>Return deleted objects (Show Deleted control) - Recycle Bin view.</summary>
    public bool IncludeDeleted { get; init; }

    /// <summary>Also return recycled objects (Show Recycled control).</summary>
    public bool IncludeRecycled { get; init; }

    /// <summary>Attribute Scoped Query source attribute (e.g. "member" with Scope=Base on a group).</summary>
    public string? AttributeScopedQuery { get; init; }

    /// <summary>Read nTSecurityDescriptor without SACL (SD flags control = OWNER|GROUP|DACL).</summary>
    public bool ReadSecurityDescriptor { get; init; }

    /// <summary>
    /// Extended DN control (1.2.840.113556.1.4.529, string form): values of DN-syntax attributes are returned as
    /// <c>&lt;GUID=…&gt;;&lt;SID=…&gt;;dn</c> (SID only for security principals). Entry DNs stay plain.
    /// Parse values with <see cref="Ldap.ExtendedDistinguishedName.TryParse"/>.
    /// </summary>
    public bool ExtendedDn { get; init; }
}

/// <summary>Request for one window of a virtual list view (VLV) - server sorted, random access scrolling.</summary>
public sealed record VirtualListRequest
{
    /// <summary>Zero based index of the first item wanted.</summary>
    public int Offset { get; init; }

    /// <summary>Number of items wanted starting at <see cref="Offset"/>.</summary>
    public int Count { get; init; } = 100;

    /// <summary>When set, positions the window at the first entry whose sort key is &gt;= this value ("type to jump").</summary>
    public string? SeekValue { get; init; }

    /// <summary>Opaque server context returned by the previous window of the same list.</summary>
    public byte[]? ContextId { get; init; }

    /// <summary>Last known total, sent to the server as content count estimate.</summary>
    public int EstimatedCount { get; init; }
}

/// <summary>One window of a virtual list view.</summary>
public sealed record VirtualListPage(
    IReadOnlyList<DirectoryObject> Items,
    int Offset,
    int TotalCount,
    byte[]? ContextId);

/// <summary>Modification type of one attribute in a modify request.</summary>
public enum ModificationOperation
{
    Add,
    Delete,
    Replace,
}

/// <summary>
/// One attribute modification. An empty value list with <see cref="ModificationOperation.Delete"/>
/// or <see cref="ModificationOperation.Replace"/> clears the attribute. Values are string or byte[].
/// </summary>
public sealed record AttributeModification(string Attribute, ModificationOperation Operation, IReadOnlyList<object> Values)
{
    public static AttributeModification Replace(string attribute, params object[] values) =>
        new(attribute, ModificationOperation.Replace, values);

    public static AttributeModification Add(string attribute, params object[] values) =>
        new(attribute, ModificationOperation.Add, values);

    public static AttributeModification Delete(string attribute, params object[] values) =>
        new(attribute, ModificationOperation.Delete, values);

    public static AttributeModification Clear(string attribute) =>
        new(attribute, ModificationOperation.Replace, []);
}

/// <summary>Options for modify requests.</summary>
public sealed record ModifyOptions
{
    /// <summary>
    /// Permissive modify control: adding an existing value or deleting a missing one succeeds silently.
    /// Use for idempotent membership changes. Do NOT use when a delete+add pair is meant as compare-and-swap.
    /// </summary>
    public bool Permissive { get; init; }

    /// <summary>
    /// SD flags control (LDAP_SERVER_SD_FLAGS_OID) sent with the modify: which parts of a written nTSecurityDescriptor
    /// the DC takes (<see cref="ActiveDirectory.SecurityDescriptorRights.Dacl"/> for a DACL change – owner, group and SACL
    /// stay as they are, and no SeSecurityPrivilege is needed). <see cref="ActiveDirectory.SecurityDescriptorRights.None"/>
    /// = no control.
    /// </summary>
    public ActiveDirectory.SecurityDescriptorRights SecurityDescriptorFlags { get; init; }

    public static ModifyOptions Default { get; } = new();

    public static ModifyOptions Idempotent { get; } = new() { Permissive = true };
}

/// <summary>What the bound identity may do on an object (from constructed attributes).</summary>
public sealed record EffectivePermissions
{
    /// <summary>allowedAttributesEffective - attributes the caller can write.</summary>
    public IReadOnlySet<string> WritableAttributes { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>allowedChildClassesEffective - classes the caller can create below the object.</summary>
    public IReadOnlySet<string> CreatableChildClasses { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>sDRightsEffective.</summary>
    public ActiveDirectory.SecurityDescriptorRights SecurityDescriptorRights { get; init; }

    /// <summary>True when the permissions were actually evaluated (false = unknown, UI should allow and let the server decide).</summary>
    public bool IsKnown { get; init; }

    public bool CanWrite(string attribute) => !IsKnown || WritableAttributes.Contains(attribute);

    public bool CanCreate(string objectClass) => !IsKnown || CreatableChildClasses.Contains(objectClass);

    public static EffectivePermissions Unknown { get; } = new();
}
