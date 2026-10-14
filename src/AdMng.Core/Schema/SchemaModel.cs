using System.Globalization;

namespace AdMng.Core.Schema;

/// <summary>Attribute syntaxes of the AD schema (attributeSyntax 2.5.5.x refined by oMSyntax).</summary>
public enum AttributeSyntax
{
    Unknown,
    DistinguishedName,
    ObjectIdentifier,
    CaseExactString,
    CaseIgnoreString,
    PrintableString,
    IA5String,
    NumericString,
    DnWithBinary,
    DnWithString,
    ORName,
    Boolean,
    Integer,
    Enumeration,
    OctetString,
    ReplicaLink,
    GeneralizedTime,
    UtcTime,
    UnicodeString,
    PresentationAddress,
    AccessPoint,
    SecurityDescriptor,
    LargeInteger,
    Sid,
}

/// <summary>objectClassCategory values.</summary>
public enum ClassCategory
{
    Type88 = 0,
    Structural = 1,
    Abstract = 2,
    Auxiliary = 3,
}

/// <summary>Definition of one attributeSchema object.</summary>
public sealed record AttributeDefinition
{
    public required string LdapName { get; init; }

    public string? Cn { get; init; }

    public string? AttributeId { get; init; }

    public AttributeSyntax Syntax { get; init; }

    public bool IsSingleValued { get; init; }

    public long? RangeLower { get; init; }

    public long? RangeUpper { get; init; }

    /// <summary>systemOnly - only the system can write it.</summary>
    public bool IsSystemOnly { get; init; }

    /// <summary>Constructed attribute (systemFlags 0x4) - computed by the DC, never writable.</summary>
    public bool IsConstructed { get; init; }

    /// <summary>searchFlags bit 0 - indexed, cheap to filter on.</summary>
    public bool IsIndexed { get; init; }

    /// <summary>
    /// searchFlags 0x80 (fCONFIDENTIAL) - reading the values needs the control access right; the values are treated as
    /// secrets (<see cref="SecretAttributes"/>).
    /// </summary>
    public bool IsConfidential { get; init; }

    /// <summary>Replicated to the global catalog (isMemberOfPartialAttributeSet).</summary>
    public bool IsInGlobalCatalog { get; init; }

    /// <summary>linkID; odd values are back links (e.g. memberOf) and cannot be written directly.</summary>
    public int? LinkId { get; init; }

    public Guid SchemaIdGuid { get; init; }

    /// <summary>Property set this attribute belongs to (attributeSecurityGUID), used in delegation.</summary>
    public Guid? PropertySetGuid { get; init; }

    public string? Description { get; init; }

    public bool IsBackLink => LinkId is { } id && id % 2 == 1;

    public bool IsLinked => LinkId is not null;

    /// <summary>Values must be transferred as byte[] (not text).</summary>
    public bool IsBinary => Syntax is AttributeSyntax.OctetString or AttributeSyntax.Sid
        or AttributeSyntax.SecurityDescriptor or AttributeSyntax.ReplicaLink;

    /// <summary>Can never be written by an operator.</summary>
    public bool IsReadOnly => IsSystemOnly || IsConstructed || IsBackLink;

    /// <summary>Maps attributeSyntax OID + oMSyntax to <see cref="AttributeSyntax"/>.</summary>
    public static AttributeSyntax MapSyntax(string? attributeSyntax, int oMSyntax) => attributeSyntax switch
    {
        "2.5.5.1" => AttributeSyntax.DistinguishedName,
        "2.5.5.2" => AttributeSyntax.ObjectIdentifier,
        "2.5.5.3" => AttributeSyntax.CaseExactString,
        "2.5.5.4" => AttributeSyntax.CaseIgnoreString,
        "2.5.5.5" => oMSyntax == 22 ? AttributeSyntax.IA5String : AttributeSyntax.PrintableString,
        "2.5.5.6" => AttributeSyntax.NumericString,
        "2.5.5.7" => oMSyntax == 127 ? AttributeSyntax.DnWithBinary : AttributeSyntax.ORName,
        "2.5.5.8" => AttributeSyntax.Boolean,
        "2.5.5.9" => oMSyntax == 10 ? AttributeSyntax.Enumeration : AttributeSyntax.Integer,
        "2.5.5.10" => oMSyntax == 127 ? AttributeSyntax.ReplicaLink : AttributeSyntax.OctetString,
        "2.5.5.11" => oMSyntax == 23 ? AttributeSyntax.UtcTime : AttributeSyntax.GeneralizedTime,
        "2.5.5.12" => AttributeSyntax.UnicodeString,
        "2.5.5.13" => AttributeSyntax.PresentationAddress,
        "2.5.5.14" => oMSyntax == 127 ? AttributeSyntax.DnWithString : AttributeSyntax.AccessPoint,
        "2.5.5.15" => AttributeSyntax.SecurityDescriptor,
        "2.5.5.16" => AttributeSyntax.LargeInteger,
        "2.5.5.17" => AttributeSyntax.Sid,
        _ => AttributeSyntax.Unknown,
    };
}

/// <summary>Definition of one classSchema object.</summary>
public sealed record ClassDefinition
{
    public required string LdapName { get; init; }

    public string? Cn { get; init; }

    public string? GovernsId { get; init; }

    public string? SubClassOf { get; init; }

    public ClassCategory Category { get; init; }

    public IReadOnlyList<string> AuxiliaryClasses { get; init; } = [];

    /// <summary>mustContain + systemMustContain (this class only, not inherited).</summary>
    public IReadOnlyList<string> MustContain { get; init; } = [];

    /// <summary>mayContain + systemMayContain (this class only, not inherited).</summary>
    public IReadOnlyList<string> MayContain { get; init; } = [];

    /// <summary>possSuperiors + systemPossSuperiors.</summary>
    public IReadOnlyList<string> PossibleSuperiors { get; init; } = [];

    public string? DefaultObjectCategory { get; init; }

    public Guid SchemaIdGuid { get; init; }
}

/// <summary>
/// Read-only view of the directory schema. Loaded once per session into memory (metadata only, no
/// directory content) and used for value conversion, validation and the attribute editor.
/// </summary>
public interface ISchemaCatalog
{
    IReadOnlyCollection<AttributeDefinition> Attributes { get; }

    IReadOnlyCollection<ClassDefinition> Classes { get; }

    AttributeDefinition? FindAttribute(string ldapName);

    ClassDefinition? FindClass(string ldapName);

    /// <summary>True when the attribute must be read/written as bytes.</summary>
    bool IsBinary(string ldapName);

    /// <summary>All attributes (must + may, inherited and from auxiliary classes) allowed for an object with these classes.</summary>
    IReadOnlyCollection<string> GetAllowedAttributes(IEnumerable<string> objectClasses);

    /// <summary>Mandatory attributes for an object with these classes.</summary>
    IReadOnlyCollection<string> GetMandatoryAttributes(IEnumerable<string> objectClasses);

    /// <summary>
    /// Friendly attribute name from display specifiers for the UI culture (e.g. "Dział" for department in pl-PL),
    /// falling back to English and then to the LDAP name.
    /// </summary>
    string GetDisplayName(string ldapName, CultureInfo culture);
}

/// <summary>In-memory <see cref="ISchemaCatalog"/> built from definitions read from the server.</summary>
public sealed class SchemaCatalog : ISchemaCatalog
{
    /// <summary>Attributes that AD returns as binary although their syntax could be read as text.</summary>
    private static readonly HashSet<string> AlwaysBinary = new(StringComparer.OrdinalIgnoreCase)
    {
        "objectGUID", "objectSid", "sIDHistory", "nTSecurityDescriptor", "thumbnailPhoto", "jpegPhoto",
        "userCertificate", "msExchMailboxGuid", "msExchMailboxSecurityDescriptor", "logonHours", "tokenGroups",
        "msDS-GenerationId", "unicodePwd", "mS-DS-ConsistencyGuid", "msDS-KeyCredentialLink",
        "msFVE-KeyPackage", "msFVE-RecoveryGuid", "msFVE-VolumeGuid", "schemaIDGUID", "attributeSecurityGUID",
    };

    private readonly Dictionary<string, AttributeDefinition> _attributes;
    private readonly Dictionary<string, ClassDefinition> _classes;
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _displayNames;

    /// <param name="attributes">Attribute definitions.</param>
    /// <param name="classes">Class definitions.</param>
    /// <param name="displayNames">
    /// Two letter ISO language (e.g. "pl", "en") -> attribute LDAP name -> display name. Optional.
    /// </param>
    public SchemaCatalog(
        IEnumerable<AttributeDefinition> attributes,
        IEnumerable<ClassDefinition> classes,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? displayNames = null)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(classes);
        _attributes = new Dictionary<string, AttributeDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in attributes)
        {
            _attributes[a.LdapName] = a;
        }

        _classes = new Dictionary<string, ClassDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in classes)
        {
            _classes[c.LdapName] = c;
        }

        _displayNames = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (displayNames is not null)
        {
            foreach (var (lang, map) in displayNames)
            {
                _displayNames[lang] = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>An empty catalog (all lookups fall back to defaults).</summary>
    public static SchemaCatalog Empty { get; } = new([], []);

    public IReadOnlyCollection<AttributeDefinition> Attributes => _attributes.Values;

    public IReadOnlyCollection<ClassDefinition> Classes => _classes.Values;

    public AttributeDefinition? FindAttribute(string ldapName) =>
        _attributes.TryGetValue(StripOptions(ldapName), out var a) ? a : null;

    public ClassDefinition? FindClass(string ldapName) => _classes.TryGetValue(ldapName, out var c) ? c : null;

    public bool IsBinary(string ldapName)
    {
        var name = StripOptions(ldapName);
        return AlwaysBinary.Contains(name) || (FindAttribute(name)?.IsBinary ?? false);
    }

    public IReadOnlyCollection<string> GetAllowedAttributes(IEnumerable<string> objectClasses) =>
        Collect(objectClasses, c => c.MustContain.Concat(c.MayContain));

    public IReadOnlyCollection<string> GetMandatoryAttributes(IEnumerable<string> objectClasses) =>
        Collect(objectClasses, c => c.MustContain);

    public string GetDisplayName(string ldapName, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var name = StripOptions(ldapName);
        if (_displayNames.TryGetValue(culture.TwoLetterISOLanguageName, out var local) && local.TryGetValue(name, out var dn))
        {
            return dn;
        }

        if (_displayNames.TryGetValue("en", out var en) && en.TryGetValue(name, out var dnEn))
        {
            return dnEn;
        }

        return name;
    }

    private HashSet<string> Collect(IEnumerable<string> objectClasses, Func<ClassDefinition, IEnumerable<string>> selector)
    {
        ArgumentNullException.ThrowIfNull(objectClasses);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(objectClasses);
        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (!visited.Add(name) || !_classes.TryGetValue(name, out var cls))
            {
                continue;
            }

            result.UnionWith(selector(cls));
            if (!string.IsNullOrEmpty(cls.SubClassOf) && !string.Equals(cls.SubClassOf, cls.LdapName, StringComparison.OrdinalIgnoreCase))
            {
                queue.Enqueue(cls.SubClassOf);
            }

            foreach (var aux in cls.AuxiliaryClasses)
            {
                queue.Enqueue(aux);
            }
        }

        return result;
    }

    private static string StripOptions(string ldapName)
    {
        ArgumentNullException.ThrowIfNull(ldapName);
        var i = ldapName.IndexOf(';', StringComparison.Ordinal);
        return i < 0 ? ldapName : ldapName[..i];
    }
}
