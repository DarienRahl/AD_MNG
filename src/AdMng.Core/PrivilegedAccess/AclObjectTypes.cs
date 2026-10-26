using AdMng.Core.ActiveDirectory;
using AdMng.Core.Schema;

namespace AdMng.Core.PrivilegedAccess;

/// <summary>
/// The meaning of the object-type GUIDs of ACEs (README U4b): attributes, classes and property sets from the session's
/// schema (<c>schemaIDGUID</c>, <c>attributeSecurityGUID</c>) and the extended rights, property sets and validated writes
/// of <c>CN=Extended-Rights</c> (<c>rightsGuid</c>). The GUIDs of the base schema are fixed in every forest and serve as a
/// fallback when the schema or the configuration could not tell; schema extensions (legacy LAPS) are only known from the
/// schema.
/// </summary>
public sealed class AclObjectTypes
{
    /// <summary>The LAPS password attributes (legacy LAPS and Windows LAPS; read needs control access on them).</summary>
    public static IReadOnlyList<string> LapsPasswordAttributes { get; } =
    [
        "ms-Mcs-AdmPwd", "msLAPS-Password", "msLAPS-EncryptedPassword", "msLAPS-EncryptedPasswordHistory",
        "msLAPS-EncryptedDSRMPassword", "msLAPS-EncryptedDSRMPasswordHistory",
    ];

    /// <summary>Shadow credentials.</summary>
    public const string KeyCredentialLink = "msDS-KeyCredentialLink";

    /// <summary>The class of the GPOs in CN=Policies.</summary>
    public const string GroupPolicyContainer = "groupPolicyContainer";

    /// <summary>Extended right names (cn of the controlAccessRight objects).</summary>
    public const string GetChanges = "DS-Replication-Get-Changes";
    public const string GetChangesAll = "DS-Replication-Get-Changes-All";
    public const string GetChangesInFilteredSet = "DS-Replication-Get-Changes-In-Filtered-Set";
    public const string ForceChangePassword = "User-Force-Change-Password";
    public const string SelfMembership = "Self-Membership";

    /// <summary>Fixed GUIDs of the base schema and of the extended rights every forest has.</summary>
    private static readonly (Guid Guid, string Name, AclGuidKind Kind)[] Fallbacks =
    [
        (new("bf9679c0-0de6-11d0-a285-00aa003049e2"), Attr.Member, AclGuidKind.Attribute),
        (new("bf967991-0de6-11d0-a285-00aa003049e2"), Attr.MemberOf, AclGuidKind.Attribute),
        (new("f30e3bbe-9ff0-11d1-b603-0000f80367c1"), Attr.GpLink, AclGuidKind.Attribute),
        (new("5b47d60f-6090-40b2-9f37-2a4de88f3063"), KeyCredentialLink, AclGuidKind.Attribute),
        (new("3f78c3e5-f79a-46bd-a0b8-9d18116ddc79"), Attr.AllowedToActOnBehalfOfOtherIdentity, AclGuidKind.Attribute),
        (new("bf967aba-0de6-11d0-a285-00aa003049e2"), ObjectClasses.User, AclGuidKind.Class),
        (new("bf967a9c-0de6-11d0-a285-00aa003049e2"), ObjectClasses.Group, AclGuidKind.Class),
        (new("bf967a86-0de6-11d0-a285-00aa003049e2"), ObjectClasses.Computer, AclGuidKind.Class),
        (new("bf967aa5-0de6-11d0-a285-00aa003049e2"), ObjectClasses.OrganizationalUnit, AclGuidKind.Class),
        (new("4828cc14-1437-45bc-9b07-ad6f015e5f28"), ObjectClasses.InetOrgPerson, AclGuidKind.Class),
        (new("f30e3bc2-9ff0-11d1-b603-0000f80367c1"), GroupPolicyContainer, AclGuidKind.Class),
        (new("1131f6aa-9c07-11d1-f79f-00c04fc2dcd2"), GetChanges, AclGuidKind.ExtendedRight),
        (new("1131f6ad-9c07-11d1-f79f-00c04fc2dcd2"), GetChangesAll, AclGuidKind.ExtendedRight),
        (new("89e95b76-444d-4c62-991a-0facbeda640c"), GetChangesInFilteredSet, AclGuidKind.ExtendedRight),
        (new("00299570-246d-11d0-a768-00aa006e0529"), ForceChangePassword, AclGuidKind.ExtendedRight),
        (new("bc0ac240-79a9-11d0-9020-00c04fc2d4cf"), "Membership", AclGuidKind.PropertySet),
        (new("4c164200-20c0-11d0-a768-00aa006e0529"), "User-Account-Restrictions", AclGuidKind.PropertySet),
    ];

    /// <summary>Fallback members of the base property sets (used only when the schema does not list them).</summary>
    private static readonly (string PropertySet, string Attribute)[] FallbackPropertySets =
    [
        ("Membership", Attr.Member),
        ("Membership", Attr.MemberOf),
        ("User-Account-Restrictions", Attr.AllowedToActOnBehalfOfOtherIdentity),
    ];

    private readonly Dictionary<Guid, string> _attributes = [];
    private readonly Dictionary<Guid, string> _classes = [];
    private readonly Dictionary<Guid, string> _rights = [];
    private readonly Dictionary<Guid, string> _propertySetNames = [];
    private readonly Dictionary<Guid, HashSet<string>> _propertySets = [];
    private readonly Dictionary<string, HashSet<Guid>> _attributeGuids = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<Guid>> _classGuids = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<Guid>> _rightGuids = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="schema">The session's schema (attributes with schemaIDGUID and attributeSecurityGUID, classes).</param>
    /// <param name="controlAccessRights">CN=Extended-Rights: rightsGuid, cn and validAccesses (0x30 = property set).</param>
    public AclObjectTypes(ISchemaCatalog? schema, IEnumerable<(Guid Guid, string Name, int ValidAccesses)>? controlAccessRights)
    {
        foreach (var (guid, name, kind) in Fallbacks)
        {
            Add(guid, name, kind);
        }

        foreach (var (guid, name, validAccesses) in controlAccessRights ?? [])
        {
            if (guid != Guid.Empty && !string.IsNullOrWhiteSpace(name))
            {
                Add(guid, name, validAccesses is 0x30 or 0x10 or 0x20 ? AclGuidKind.PropertySet : AclGuidKind.ExtendedRight);
            }
        }

        var schemaSets = new HashSet<Guid>();
        foreach (var attribute in schema?.Attributes ?? [])
        {
            if (attribute.SchemaIdGuid != Guid.Empty)
            {
                Add(attribute.SchemaIdGuid, attribute.LdapName, AclGuidKind.Attribute);
            }

            if (attribute.PropertySetGuid is { } set && set != Guid.Empty)
            {
                Member(set).Add(attribute.LdapName);
                schemaSets.Add(set);
            }
        }

        foreach (var definition in schema?.Classes ?? [])
        {
            if (definition.SchemaIdGuid != Guid.Empty)
            {
                Add(definition.SchemaIdGuid, definition.LdapName, AclGuidKind.Class);
            }
        }

        foreach (var (setName, attribute) in FallbackPropertySets)
        {
            foreach (var set in _propertySetNames.Where(p => string.Equals(p.Value, setName, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key))
            {
                if (!schemaSets.Contains(set))
                {
                    Member(set).Add(attribute);
                }
            }
        }
    }

    /// <summary>The LDAP name of an attribute GUID, or null.</summary>
    public string? AttributeName(Guid guid) => _attributes.GetValueOrDefault(guid);

    /// <summary>The LDAP name of a class GUID, or null.</summary>
    public string? ClassName(Guid guid) => _classes.GetValueOrDefault(guid);

    /// <summary>The name (cn) of an extended right or validated write, or null.</summary>
    public string? RightName(Guid guid) => _rights.GetValueOrDefault(guid);

    /// <summary>A readable name of any object-type GUID (attribute, property set, class, right), else the GUID.</summary>
    public string Describe(Guid guid) =>
        AttributeName(guid) ?? _propertySetNames.GetValueOrDefault(guid) ?? ClassName(guid) ?? RightName(guid) ?? guid.ToString("D");

    /// <summary>The GUID is the class (one of its GUIDs).</summary>
    public bool IsClass(Guid guid, string className) => _classGuids.TryGetValue(className, out var guids) && guids.Contains(guid);

    /// <summary>The GUID is the extended right (or validated write) of that name.</summary>
    public bool IsRight(Guid guid, string rightName) => _rightGuids.TryGetValue(rightName, out var guids) && guids.Contains(guid);

    /// <summary>A write-property / read-property ACE with this object type covers the attribute (itself or its property set).</summary>
    public bool Covers(Guid objectType, string attribute) =>
        (_attributeGuids.TryGetValue(attribute, out var guids) && guids.Contains(objectType))
        || (_propertySets.TryGetValue(objectType, out var members) && members.Contains(attribute));

    /// <summary>The LAPS password attribute the object type names, or null (the schema decides; legacy LAPS GUIDs vary).</summary>
    public string? LapsAttribute(Guid objectType) =>
        AttributeName(objectType) is { } name && LapsPasswordAttributes.Contains(name, StringComparer.OrdinalIgnoreCase) ? name : null;

    /// <summary>The schema knows a LAPS password attribute (only then are LAPS rights possible).</summary>
    public bool HasLaps => _attributeGuids.Keys.Any(a => LapsPasswordAttributes.Contains(a, StringComparer.OrdinalIgnoreCase));

    private HashSet<string> Member(Guid set)
    {
        if (!_propertySets.TryGetValue(set, out var members))
        {
            _propertySets[set] = members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return members;
    }

    private void Add(Guid guid, string name, AclGuidKind kind)
    {
        switch (kind)
        {
            case AclGuidKind.Attribute:
                _attributes[guid] = name;
                Index(_attributeGuids, name, guid);
                break;
            case AclGuidKind.Class:
                _classes[guid] = name;
                Index(_classGuids, name, guid);
                break;
            case AclGuidKind.PropertySet:
                _propertySetNames[guid] = name;
                break;
            default:
                _rights[guid] = name;
                Index(_rightGuids, name, guid);
                break;
        }
    }

    private static void Index(Dictionary<string, HashSet<Guid>> index, string name, Guid guid)
    {
        if (!index.TryGetValue(name, out var guids))
        {
            index[name] = guids = [];
        }

        guids.Add(guid);
    }

    private enum AclGuidKind
    {
        Attribute,
        Class,
        PropertySet,
        ExtendedRight,
    }
}
