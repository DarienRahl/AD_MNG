using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Core.Schema;

namespace AdMng.Core.Comparison;

/// <summary>
/// One side of a comparison: an object read through one connection, with the facts of that side (naming context,
/// domain, schema) its values are interpreted with. In memory only, for as long as the comparison window shows it
/// (CLAUDE.md rule 1). Values of attributes that the side's own schema marks secret are never kept: such an attribute is
/// recorded as present, with <see cref="SecretAttributes.Mask"/> as its only value.
/// </summary>
public sealed class ObjectSnapshot
{
    private readonly Dictionary<string, IReadOnlyList<object>> _attributes;

    /// <param name="obj">The object as read (ranged attributes completed by the caller where possible).</param>
    /// <param name="info">The facts of the session it was read through.</param>
    /// <param name="schema">The schema of that session.</param>
    public ObjectSnapshot(DirectoryObject obj, SessionInfo info, ISchemaCatalog schema)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(schema);
        DistinguishedName = obj.DistinguishedName;
        DefaultNamingContext = info.DefaultNamingContext;
        DomainDnsName = info.DomainDnsName;
        DomainNetBiosName = info.DomainNetBiosName;
        Schema = schema;
        _attributes = new Dictionary<string, IReadOnlyList<object>>(StringComparer.OrdinalIgnoreCase);
        var truncated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<AttributeValues>();
        foreach (var (name, values) in obj.Attributes)
        {
            if (values.Name.Contains(";range=", StringComparison.OrdinalIgnoreCase) && !values.Name.EndsWith("-*", StringComparison.Ordinal))
            {
                truncated.Add(name);
            }

            AttributeValues stored = SecretAttributes.IsSecret(name, schema) ? new AttributeValues(name, [SecretAttributes.Mask]) : values;
            _attributes[name] = [.. stored];
            kept.Add(new AttributeValues(name, stored));
        }

        TruncatedAttributes = truncated;
        Object = new DirectoryObject(obj.DistinguishedName, kept);
        ObjectClasses = Object.GetStrings(Attr.ObjectClass);
        Kind = Object.Kind;
        ObjectGuid = Object.ObjectGuid;
        ObjectSid = ReadSid(Object);
    }

    /// <summary>The DN as returned by the side's DC.</summary>
    public string DistinguishedName { get; }

    /// <summary>The object (secret values masked) - for summaries built with the side's formatter.</summary>
    public DirectoryObject Object { get; }

    public IReadOnlyList<string> ObjectClasses { get; }

    public ObjectKind Kind { get; }

    /// <summary>Values per attribute (base names, case-insensitive): <see cref="string"/> or <see cref="byte"/>[].</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<object>> Attributes => _attributes;

    /// <summary>Multi-valued attributes the DC returned only partially (ranged retrieval was not possible).</summary>
    public IReadOnlySet<string> TruncatedAttributes { get; }

    /// <summary>The default naming context of the side (<c>DC=contoso,DC=test</c>) - DN values are portable against it.</summary>
    public string DefaultNamingContext { get; }

    public string DomainDnsName { get; }

    public string? DomainNetBiosName { get; }

    /// <summary>The side's schema (syntaxes, confidential flags, display names).</summary>
    public ISchemaCatalog Schema { get; }

    public Guid? ObjectGuid { get; }

    public string? ObjectSid { get; }

    /// <summary>The RDN value (what the Name column shows).</summary>
    public string Name => Object.Name;

    /// <summary>The attribute count only (never a DN or a value).</summary>
    public override string ToString() => $"ObjectSnapshot({_attributes.Count} attributes)";

    private static string? ReadSid(DirectoryObject obj)
    {
        try
        {
            return obj.ObjectSid;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
