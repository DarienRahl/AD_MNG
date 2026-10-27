using System.Globalization;
using System.Text;

namespace AdMng.Core.Ldap;

/// <summary>One relative distinguished name component, e.g. <c>OU=Księgowość</c>.</summary>
/// <remarks>Multi-valued RDNs (<c>CN=a+UID=b</c>) are not used by Active Directory and are kept as a single
/// component whose <see cref="Value"/> contains the remainder verbatim (unescaped).</remarks>
public readonly record struct Rdn(string Type, string Value)
{
    /// <summary>Returns the RFC 4514 string form with the value escaped.</summary>
    public override string ToString() => $"{Type}={DistinguishedName.EscapeValue(Value)}";
}

/// <summary>
/// Immutable, parsed LDAP distinguished name (RFC 4514) with Active Directory friendly helpers.
/// Comparison is case-insensitive on attribute types and values (AD semantics for DN syntax).
/// </summary>
public sealed class DistinguishedName : IEquatable<DistinguishedName>
{
    private readonly Rdn[] _rdns;
    private string? _text;

    private DistinguishedName(Rdn[] rdns, string? text = null)
    {
        _rdns = rdns;
        _text = text;
    }

    /// <summary>
    /// Placeholder for the default naming context in portable DNs (<c>OU=Firma,{domain}</c>): saved views and settings
    /// stay valid in another domain (test/prod), and the comparison of two connections matches DN values across domains.
    /// </summary>
    public const string DomainPlaceholder = "{domain}";

    /// <summary>The empty DN (RootDSE).</summary>
    public static DistinguishedName Root { get; } = new([], string.Empty);

    /// <summary>RDN components, leaf first (e.g. CN=user, OU=Sales, DC=contoso, DC=com).</summary>
    public IReadOnlyList<Rdn> Rdns => _rdns;

    /// <summary>True for the empty DN.</summary>
    public bool IsRoot => _rdns.Length == 0;

    /// <summary>The leaf RDN. Throws for the root DN.</summary>
    public Rdn Leaf => _rdns.Length > 0 ? _rdns[0] : throw new InvalidOperationException("The root DN has no RDN.");

    /// <summary>The unescaped value of the leaf RDN (what ADUC shows in the Name column).</summary>
    public string Name => _rdns.Length > 0 ? _rdns[0].Value : string.Empty;

    /// <summary>The parent DN, or <c>null</c> for the root DN.</summary>
    public DistinguishedName? Parent => _rdns.Length == 0 ? null : new DistinguishedName(_rdns[1..]);

    /// <summary>Domain component part, e.g. <c>DC=contoso,DC=com</c>.</summary>
    public DistinguishedName DomainComponent
    {
        get
        {
            var start = _rdns.Length;
            while (start > 0 && string.Equals(_rdns[start - 1].Type, "DC", StringComparison.OrdinalIgnoreCase))
            {
                start--;
            }

            return new DistinguishedName(_rdns[start..]);
        }
    }

    /// <summary>DNS domain name derived from DC components, e.g. <c>contoso.com</c>.</summary>
    public string DnsDomain => string.Join('.', DomainComponent._rdns.Select(r => r.Value));

    /// <summary>Parses an RFC 4514 DN. Throws <see cref="FormatException"/> on malformed input.</summary>
    public static DistinguishedName Parse(string dn)
    {
        ArgumentNullException.ThrowIfNull(dn);
        if (!TryParse(dn, out var result, out var error))
        {
            throw new FormatException($"Invalid distinguished name '{dn}': {error}");
        }

        return result;
    }

    /// <summary>Tries to parse an RFC 4514 DN.</summary>
    public static bool TryParse(string? dn, out DistinguishedName result) => TryParse(dn, out result, out _);

    private static bool TryParse(string? dn, out DistinguishedName result, out string? error)
    {
        result = Root;
        error = null;
        if (dn is null)
        {
            error = "null";
            return false;
        }

        if (dn.Trim().Length == 0)
        {
            return true;
        }

        var rdns = new List<Rdn>();
        var i = 0;
        while (i < dn.Length)
        {
            // attribute type
            while (i < dn.Length && dn[i] == ' ')
            {
                i++;
            }

            var typeStart = i;
            while (i < dn.Length && dn[i] != '=' && dn[i] != ',' && dn[i] != ';' && dn[i] != '+')
            {
                i++;
            }

            if (i >= dn.Length || dn[i] != '=')
            {
                error = $"expected '=' at position {i}";
                return false;
            }

            var type = dn[typeStart..i].Trim();
            if (type.Length == 0)
            {
                error = $"empty attribute type at position {typeStart}";
                return false;
            }

            i++; // skip '='
            if (!TryReadValue(dn, ref i, out var value, out error))
            {
                return false;
            }

            rdns.Add(new Rdn(type, value));
            if (i < dn.Length)
            {
                if (dn[i] is ',' or ';')
                {
                    i++;
                    if (i >= dn.Length)
                    {
                        error = "trailing separator";
                        return false;
                    }
                }
                else
                {
                    error = $"unexpected character '{dn[i]}' at position {i}";
                    return false;
                }
            }
        }

        result = new DistinguishedName([.. rdns]);
        return true;
    }

    private static bool TryReadValue(string dn, ref int i, out string value, out string? error)
    {
        value = string.Empty;
        error = null;
        while (i < dn.Length && dn[i] == ' ')
        {
            i++;
        }

        if (i < dn.Length && dn[i] == '#')
        {
            // BER encoded value - keep verbatim.
            var start = i;
            while (i < dn.Length && dn[i] != ',' && dn[i] != ';')
            {
                i++;
            }

            value = dn[start..i].TrimEnd();
            return true;
        }

        if (i < dn.Length && dn[i] == '"')
        {
            // Legacy quoted form (RFC 2253 allows on input).
            i++;
            var sbq = new StringBuilder();
            while (i < dn.Length && dn[i] != '"')
            {
                if (dn[i] == '\\' && i + 1 < dn.Length)
                {
                    i++;
                }

                sbq.Append(dn[i]);
                i++;
            }

            if (i >= dn.Length)
            {
                error = "unterminated quoted value";
                return false;
            }

            i++; // closing quote
            while (i < dn.Length && dn[i] == ' ')
            {
                i++;
            }

            value = sbq.ToString();
            return true;
        }

        var bytes = new List<byte>();
        var sb = new StringBuilder();
        var lastSignificant = 0; // length of sb up to the last escaped or non-space char

        void FlushBytes()
        {
            if (bytes.Count > 0)
            {
                sb.Append(Encoding.UTF8.GetString([.. bytes]));
                bytes.Clear();
                lastSignificant = sb.Length;
            }
        }

        while (i < dn.Length)
        {
            var c = dn[i];
            if (c is ',' or ';')
            {
                break;
            }

            if (c == '+')
            {
                // Multi-valued RDN: keep the remainder of the component verbatim in the value.
                FlushBytes();
                sb.Append(c);
                lastSignificant = sb.Length;
                i++;
                continue;
            }

            if (c == '\\')
            {
                if (i + 1 >= dn.Length)
                {
                    error = "dangling escape";
                    return false;
                }

                var n = dn[i + 1];
                if (IsHex(n) && i + 2 < dn.Length && IsHex(dn[i + 2]))
                {
                    bytes.Add(byte.Parse(dn.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 3;
                    continue;
                }

                FlushBytes();
                sb.Append(n);
                lastSignificant = sb.Length;
                i += 2;
                continue;
            }

            FlushBytes();
            sb.Append(c);
            if (c != ' ')
            {
                lastSignificant = sb.Length;
            }

            i++;
        }

        FlushBytes();
        value = sb.ToString(0, lastSignificant);
        return true;
    }

    private static bool IsHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    /// <summary>Escapes an attribute value for use in an RDN (RFC 4514 section 2.4).</summary>
    public static string EscapeValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
        {
            return value;
        }

        var sb = new StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case ',' or '+' or '"' or '\\' or '<' or '>' or ';' or '=':
                    sb.Append('\\').Append(c);
                    break;
                case '#' when i == 0:
                    sb.Append("\\#");
                    break;
                case ' ' when i == 0 || i == value.Length - 1:
                    sb.Append("\\ ");
                    break;
                case '\0':
                    sb.Append("\\00");
                    break;
                case '\n':
                    sb.Append("\\0A");
                    break;
                case '\r':
                    sb.Append("\\0D");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>Creates a child DN, e.g. <c>Child("CN", "Jan Kowalski")</c>.</summary>
    public DistinguishedName Child(string type, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(value);
        return new DistinguishedName([new Rdn(type, value), .. _rdns]);
    }

    /// <summary>Returns a DN with the leaf RDN value replaced (rename).</summary>
    public DistinguishedName WithLeafValue(string value)
    {
        var copy = (Rdn[])_rdns.Clone();
        copy[0] = copy[0] with { Value = value };
        return new DistinguishedName(copy);
    }

    /// <summary>Returns the same leaf under a different parent (move).</summary>
    public DistinguishedName WithParent(DistinguishedName parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return new DistinguishedName([Leaf, .. parent._rdns]);
    }

    /// <summary>True when this DN is strictly below <paramref name="ancestor"/>.</summary>
    public bool IsDescendantOf(DistinguishedName ancestor)
    {
        ArgumentNullException.ThrowIfNull(ancestor);
        if (ancestor._rdns.Length >= _rdns.Length)
        {
            return false;
        }

        var offset = _rdns.Length - ancestor._rdns.Length;
        for (var i = 0; i < ancestor._rdns.Length; i++)
        {
            if (!RdnEquals(_rdns[offset + i], ancestor._rdns[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Canonical name like ADSI <c>canonicalName</c>: <c>contoso.com/Sales/Jan Kowalski</c>.
    /// Slashes inside names are escaped as <c>\/</c>.
    /// </summary>
    public string ToCanonicalName()
    {
        var domain = DnsDomain;
        var parts = _rdns
            .Take(_rdns.Length - DomainComponent._rdns.Length)
            .Reverse()
            .Select(r => r.Value.Replace("/", "\\/", StringComparison.Ordinal));
        var path = string.Join('/', parts);
        return path.Length == 0 ? domain + "/" : $"{domain}/{path}";
    }

    /// <summary>RFC 4514 string with escaped values (no spaces after commas, AD style).</summary>
    public override string ToString() => _text ??= string.Join(',', _rdns.Select(r => r.ToString()));

    public bool Equals(DistinguishedName? other)
    {
        if (other is null || other._rdns.Length != _rdns.Length)
        {
            return false;
        }

        for (var i = 0; i < _rdns.Length; i++)
        {
            if (!RdnEquals(_rdns[i], other._rdns[i]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as DistinguishedName);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var rdn in _rdns)
        {
            hash.Add(rdn.Type, StringComparer.OrdinalIgnoreCase);
            hash.Add(rdn.Value, StringComparer.OrdinalIgnoreCase);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// Replaces the naming context suffix of a DN with <see cref="DomainPlaceholder"/>: <c>CN=Jan Kowalski,OU=IT,{domain}</c>
    /// (the naming context itself becomes <c>{domain}</c>). The relative part is written with normalized escaping. A DN
    /// outside the naming context (another partition or domain) or one that does not parse is returned unchanged.
    /// </summary>
    /// <param name="distinguishedName">The DN.</param>
    /// <param name="namingContext">The default naming context of the DN's domain (<c>DC=contoso,DC=test</c>).</param>
    public static string ToPortable(string distinguishedName, string namingContext)
    {
        ArgumentNullException.ThrowIfNull(distinguishedName);
        ArgumentNullException.ThrowIfNull(namingContext);
        if (!TryParse(distinguishedName, out var dn) || !TryParse(namingContext, out var domain))
        {
            return distinguishedName;
        }

        if (dn.Equals(domain))
        {
            return DomainPlaceholder;
        }

        if (!dn.IsDescendantOf(domain))
        {
            return distinguishedName;
        }

        var relative = string.Join(',', dn._rdns.Take(dn._rdns.Length - domain._rdns.Length).Select(r => r.ToString()));
        return $"{relative},{DomainPlaceholder}";
    }

    /// <summary>The DN of a portable DN in the naming context <paramref name="namingContext"/> (the placeholder replaced).</summary>
    public static string FromPortable(string portableDn, string namingContext)
    {
        ArgumentNullException.ThrowIfNull(portableDn);
        ArgumentNullException.ThrowIfNull(namingContext);
        return portableDn.Replace(DomainPlaceholder, namingContext, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Case-insensitive comparison of two DN strings after normalization.</summary>
    public static bool AreEqual(string? a, string? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return TryParse(a, out var da) && TryParse(b, out var db) && da.Equals(db);
    }

    private static bool RdnEquals(Rdn a, Rdn b) =>
        string.Equals(a.Type, b.Type, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Value, b.Value, StringComparison.OrdinalIgnoreCase);

    public static bool operator ==(DistinguishedName? left, DistinguishedName? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(DistinguishedName? left, DistinguishedName? right) => !(left == right);
}
