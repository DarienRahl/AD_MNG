using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
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

    /// <summary>Enroll on a certificate template (P1, docs/dev/p-certificate-services.md 2.3).</summary>
    public const string CertificateEnrollment = "Certificate-Enrollment";

    /// <summary>Autoenroll on a certificate template (P1, 2.3).</summary>
    public const string CertificateAutoEnrollment = "Certificate-AutoEnrollment";

    /// <summary>Explicit certificate mappings (ESC14, P1 2.3).</summary>
    public const string AltSecurityIdentities = "altSecurityIdentities";

    private const string RightsGuidAttribute = "rightsGuid";
    private const string ValidAccessesAttribute = "validAccesses";

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
        (new("0e10c968-78fb-11d2-90d4-00c04f79dc55"), CertificateEnrollment, AclGuidKind.ExtendedRight),
        (new("a05b8cc2-17bc-4802-a710-e7c15ab866a2"), CertificateAutoEnrollment, AclGuidKind.ExtendedRight),
        (new("bc0ac240-79a9-11d0-9020-00c04fc2d4cf"), "Membership", AclGuidKind.PropertySet),
        (new("4c164200-20c0-11d0-a768-00aa006e0529"), "User-Account-Restrictions", AclGuidKind.PropertySet),

        // AD CS (P1, docs/dev/p-certificate-services.md 2.3): the schemaIDGUIDs of the nine ESC4-sensitive template
        // attributes (PkiAttributes.SensitiveTemplateAttributes), of altSecurityIdentities (ESC14) and of the template class
        // (the InheritedObjectType check), as MS-ADA1/MS-ADA2/MS-ADSC publish them (checked against the Windows Server
        // schema LDF). None of the nine is in a property set; altSecurityIdentities is in Public-Information.
        (new("ea1dddc4-60ff-416e-8cc0-17cee534bce7"), "msPKI-Certificate-Name-Flag", AclGuidKind.Attribute),
        (new("d15ef7d8-f226-46db-ae79-b34e560bd12c"), "msPKI-Enrollment-Flag", AclGuidKind.Attribute),
        (new("18976af6-3b9e-11d2-90cc-00c04fd91ab1"), "pKIExtendedKeyUsage", AclGuidKind.Attribute),
        (new("dbd90548-aa37-4202-9966-8c537ba5ce32"), "msPKI-Certificate-Application-Policy", AclGuidKind.Attribute),
        (new("fe17e04b-937d-4f7e-8e0e-9292c8d5683e"), "msPKI-RA-Signature", AclGuidKind.Attribute),
        (new("3c91fbbf-4773-4ccd-a87b-85d53e7bcf6a"), "msPKI-RA-Application-Policies", AclGuidKind.Attribute),
        (new("d546ae22-0951-4d47-817e-1c9f96faad46"), "msPKI-RA-Policies", AclGuidKind.Attribute),
        (new("38942346-cc5b-424b-a7d8-6ffd12029c5f"), "msPKI-Certificate-Policy", AclGuidKind.Attribute),
        (new("0c15e9f5-491d-4594-918f-32813a091da9"), "msPKI-Template-Schema-Version", AclGuidKind.Attribute),
        (new("00fbf30c-91fe-11d1-aebc-0000f80367c1"), AltSecurityIdentities, AclGuidKind.Attribute),
        (new("e5209ca2-3bba-11d2-90cc-00c04fd91ab1"), "pKICertificateTemplate", AclGuidKind.Class),
        (new("e48d0154-bcf8-11d1-8702-00c04fb96050"), "Public-Information", AclGuidKind.PropertySet),
    ];

    /// <summary>Fallback members of the base property sets (used only when the schema does not list them).</summary>
    private static readonly (string PropertySet, string Attribute)[] FallbackPropertySets =
    [
        ("Membership", Attr.Member),
        ("Membership", Attr.MemberOf),
        ("User-Account-Restrictions", Attr.AllowedToActOnBehalfOfOtherIdentity),
        ("Public-Information", AltSecurityIdentities),
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

    /// <summary>
    /// Reads the session's schema and <c>CN=Extended-Rights</c> (rightsGuid, cn, validAccesses) into the object types every
    /// caller shares (P1 2.3: the read <c>DirectoryAclScan</c> does today, moved here by C3).
    /// </summary>
    public static async Task<AclObjectTypes> ReadAsync(IDirectoryReader reader, int pageSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var rights = new List<(Guid, string, int)>();
        if (DistinguishedName.TryParse(reader.Info.ConfigurationNamingContext, out var configuration) && !configuration.IsRoot)
        {
            var search = new DirectorySearch
            {
                BaseDn = configuration.Child("CN", "Extended-Rights").ToString(),
                Scope = SearchScope.OneLevel,
                Filter = LdapFilter.Equal(Attr.ObjectClass, "controlAccessRight"),
                Attributes = [Attr.Cn, RightsGuidAttribute, ValidAccessesAttribute],
                PageSize = pageSize,
            };
            try
            {
                await foreach (var right in reader.SearchAsync(search, cancellationToken).ConfigureAwait(false))
                {
                    if (Guid.TryParse(right.GetString(RightsGuidAttribute), out var guid) && (right.GetString(Attr.Cn) ?? right.Name) is { Length: > 0 } name)
                    {
                        rights.Add((guid, name, right.GetInt32(ValidAccessesAttribute) ?? 0));
                    }
                }
            }
            catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.NoSuchObject)
            {
                // Only the fixed GUIDs of the base schema are known.
            }
        }

        return new AclObjectTypes(reader.Schema, rights);
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
