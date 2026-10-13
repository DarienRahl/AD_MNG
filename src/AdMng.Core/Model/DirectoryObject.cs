using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;

namespace AdMng.Core.Model;

/// <summary>
/// Values of one attribute as returned by the server. Each value is either a <see cref="string"/>
/// (text syntaxes, numbers, DNs, GeneralizedTime) or a <see cref="byte"/>[] (octet string, SID, GUID,
/// security descriptor). The directory layer decides the representation from the schema.
/// </summary>
public sealed class AttributeValues : IReadOnlyList<object>
{
    private readonly object[] _values;

    public AttributeValues(string name, IEnumerable<object> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(values);
        Name = name;
        _values = values.Select(v => v switch
        {
            string or byte[] => v,
            null => throw new ArgumentException("Attribute values cannot be null.", nameof(values)),
            _ => throw new ArgumentException($"Unsupported attribute value type {v.GetType()}.", nameof(values)),
        }).ToArray();
    }

    /// <summary>LDAP display name as returned by the server (may contain a range option, e.g. member;range=0-1499).</summary>
    public string Name { get; }

    public int Count => _values.Length;

    public object this[int index] => _values[index];

    /// <summary>First value as string (binary values are decoded as UTF-8), or null.</summary>
    public string? FirstString => _values.Length == 0 ? null : AsString(_values[0]);

    /// <summary>All values as strings (binary values decoded as UTF-8).</summary>
    public IReadOnlyList<string> Strings => _values.Select(AsString).ToArray();

    /// <summary>First value as bytes (strings are UTF-8 encoded), or null.</summary>
    public byte[]? FirstBytes => _values.Length == 0 ? null : AsBytes(_values[0]);

    public IEnumerator<object> GetEnumerator() => ((IEnumerable<object>)_values).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _values.GetEnumerator();

    private static string AsString(object value) => value as string ?? Encoding.UTF8.GetString((byte[])value);

    private static byte[] AsBytes(object value) => value as byte[] ?? Encoding.UTF8.GetBytes((string)value);
}

/// <summary>
/// An immutable snapshot of a directory object as returned by one LDAP search. It lives only in memory
/// for as long as the UI displays it - nothing is ever cached to disk (see docs/ARCHITECTURE.md).
/// </summary>
public sealed class DirectoryObject
{
    private readonly Dictionary<string, AttributeValues> _attributes;
    private DistinguishedName? _parsedDn;
    private ObjectKind? _kind;

    public DirectoryObject(string distinguishedName, IEnumerable<AttributeValues> attributes)
    {
        ArgumentNullException.ThrowIfNull(distinguishedName);
        ArgumentNullException.ThrowIfNull(attributes);
        DistinguishedName = distinguishedName;
        _attributes = new Dictionary<string, AttributeValues>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in attributes)
        {
            // Ranged attributes ("member;range=0-1499") are stored under their base name.
            var key = BaseName(a.Name);
            _attributes[key] = a;
        }
    }

    /// <summary>The object's DN exactly as returned by the server.</summary>
    public string DistinguishedName { get; }

    /// <summary>Parsed DN (lazy).</summary>
    public DistinguishedName Dn => _parsedDn ??= Ldap.DistinguishedName.TryParse(DistinguishedName, out var dn) ? dn : Ldap.DistinguishedName.Root;

    /// <summary>Attributes returned by the server (case-insensitive by LDAP display name).</summary>
    public IReadOnlyDictionary<string, AttributeValues> Attributes => _attributes;

    /// <summary>Kind derived from objectClass (Unknown when objectClass was not requested).</summary>
    public ObjectKind Kind => _kind ??= ObjectKinds.FromObjectClasses(GetStrings(Attr.ObjectClass));

    /// <summary>Display name for lists: the RDN value (what ADUC shows in the Name column).</summary>
    public string Name => GetString(Attr.Name) ?? Dn.Name;

    public bool Has(string attribute) => _attributes.ContainsKey(attribute);

    public bool TryGet(string attribute, [NotNullWhen(true)] out AttributeValues? values) =>
        _attributes.TryGetValue(attribute, out values);

    public string? GetString(string attribute) => _attributes.TryGetValue(attribute, out var v) ? v.FirstString : null;

    public IReadOnlyList<string> GetStrings(string attribute) =>
        _attributes.TryGetValue(attribute, out var v) ? v.Strings : [];

    public byte[]? GetBytes(string attribute) => _attributes.TryGetValue(attribute, out var v) ? v.FirstBytes : null;

    public long? GetInt64(string attribute) => AdConvert.ParseInt64(GetString(attribute));

    public int? GetInt32(string attribute)
    {
        var v = GetInt64(attribute);
        return v is null ? null : unchecked((int)v.Value);
    }

    public bool? GetBoolean(string attribute) => GetString(attribute) switch
    {
        null => null,
        var s when s.Equals("TRUE", StringComparison.OrdinalIgnoreCase) => true,
        var s when s.Equals("FALSE", StringComparison.OrdinalIgnoreCase) => false,
        _ => null,
    };

    /// <summary>FILETIME attribute (pwdLastSet, accountExpires, lastLogonTimestamp...) as UTC timestamp.</summary>
    public DateTimeOffset? GetFileTime(string attribute) => AdConvert.FromFileTime(GetString(attribute));

    /// <summary>GeneralizedTime attribute (whenCreated, whenChanged) as UTC timestamp.</summary>
    public DateTimeOffset? GetGeneralizedTime(string attribute) => AdConvert.FromGeneralizedTime(GetString(attribute));

    public Guid? ObjectGuid
    {
        get
        {
            var b = GetBytes(Attr.ObjectGuid);
            return b is { Length: 16 } ? AdConvert.GuidFromBytes(b) : null;
        }
    }

    public string? ObjectSid
    {
        get
        {
            var b = GetBytes(Attr.ObjectSid);
            return b is { Length: >= 8 } ? AdConvert.SidToString(b) : null;
        }
    }

    /// <summary>userAccountControl combined with the computed flags (lockout / password expired) when available.</summary>
    public UserAccountControl? AccountControl
    {
        get
        {
            var raw = GetInt32(Attr.UserAccountControl);
            if (raw is null)
            {
                return null;
            }

            var computed = GetInt32(Attr.UserAccountControlComputed) ?? 0;
            return (UserAccountControl)(raw.Value | computed);
        }
    }

    public bool IsDisabled => AccountControl?.HasFlag(UserAccountControl.AccountDisable) == true;

    /// <summary>
    /// Locked out according to msDS-User-Account-Control-Computed when it was requested,
    /// otherwise approximated by lockoutTime &gt; 0 (may be stale after the lockout duration expired).
    /// </summary>
    public bool IsLockedOut => Has(Attr.UserAccountControlComputed)
        ? AccountControl?.HasFlag(UserAccountControl.Lockout) == true
        : GetInt64(Attr.LockoutTime) > 0;

    /// <summary>adminCount=1 - protected by AdminSDHolder (Tier 0 / privileged group member, now or in the past).</summary>
    public bool IsProtectedByAdminSdHolder => GetInt32(Attr.AdminCount) == 1;

    /// <summary>Returns a copy with additional/overridden attributes (used after a successful modify).</summary>
    public DirectoryObject With(IEnumerable<AttributeValues> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var merged = new Dictionary<string, AttributeValues>(_attributes, StringComparer.OrdinalIgnoreCase);
        foreach (var c in changes)
        {
            if (c.Count == 0)
            {
                merged.Remove(BaseName(c.Name));
            }
            else
            {
                merged[BaseName(c.Name)] = c;
            }
        }

        return new DirectoryObject(DistinguishedName, merged.Values);
    }

    public override string ToString() => DistinguishedName;

    private static string BaseName(string name)
    {
        var semicolon = name.IndexOf(';', StringComparison.Ordinal);
        return semicolon < 0 ? name : name[..semicolon];
    }
}
