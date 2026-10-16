using System.Globalization;
using System.Text;

namespace AdMng.Core.Ldap;

/// <summary>
/// Safe construction of LDAP search filters (RFC 4515) including Active Directory
/// extensible matching rules. Every value passed to a builder method is escaped.
/// </summary>
public static class LdapFilter
{
    /// <summary>LDAP_MATCHING_RULE_BIT_AND.</summary>
    public const string MatchingRuleBitAnd = "1.2.840.113556.1.4.803";

    /// <summary>LDAP_MATCHING_RULE_BIT_OR.</summary>
    public const string MatchingRuleBitOr = "1.2.840.113556.1.4.804";

    /// <summary>LDAP_MATCHING_RULE_IN_CHAIN (transitive / nested membership).</summary>
    public const string MatchingRuleInChain = "1.2.840.113556.1.4.1941";

    /// <summary>Escapes a value for use inside a filter (RFC 4515 section 3).</summary>
    public static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '*':
                    sb.Append("\\2a");
                    break;
                case '(':
                    sb.Append("\\28");
                    break;
                case ')':
                    sb.Append("\\29");
                    break;
                case '\\':
                    sb.Append("\\5c");
                    break;
                case '\0':
                    sb.Append("\\00");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>Escapes binary data (e.g. objectGUID) as <c>\xx</c> sequences.</summary>
    public static string EscapeBinary(ReadOnlySpan<byte> value)
    {
        var sb = new StringBuilder(value.Length * 3);
        foreach (var b in value)
        {
            sb.Append('\\').Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary><c>(attr=value)</c>.</summary>
    public static string Equal(string attribute, string value) => $"({Attr(attribute)}={Escape(value)})";

    /// <summary><c>(attr=value)</c> for an integer value.</summary>
    public static string Equal(string attribute, long value) =>
        $"({Attr(attribute)}={value.ToString(CultureInfo.InvariantCulture)})";

    /// <summary><c>(objectGUID=\xx\xx...)</c>.</summary>
    public static string EqualGuid(string attribute, Guid value) =>
        $"({Attr(attribute)}={EscapeBinary(value.ToByteArray())})";

    /// <summary><c>(attr=*)</c>.</summary>
    public static string Present(string attribute) => $"({Attr(attribute)}=*)";

    /// <summary><c>(!(attr=*))</c>.</summary>
    public static string NotPresent(string attribute) => Not(Present(attribute));

    /// <summary><c>(attr=value*)</c>.</summary>
    public static string StartsWith(string attribute, string value) => $"({Attr(attribute)}={Escape(value)}*)";

    /// <summary><c>(attr=*value)</c>. Note: leading wildcards are not indexed in AD for most attributes.</summary>
    public static string EndsWith(string attribute, string value) => $"({Attr(attribute)}=*{Escape(value)})";

    /// <summary><c>(attr=*value*)</c>. Note: medial searches are expensive in AD.</summary>
    public static string Contains(string attribute, string value) => $"({Attr(attribute)}=*{Escape(value)}*)";

    /// <summary><c>(attr&gt;=value)</c>.</summary>
    public static string GreaterOrEqual(string attribute, long value) =>
        $"({Attr(attribute)}>={value.ToString(CultureInfo.InvariantCulture)})";

    /// <summary><c>(attr&lt;=value)</c>.</summary>
    public static string LessOrEqual(string attribute, long value) =>
        $"({Attr(attribute)}<={value.ToString(CultureInfo.InvariantCulture)})";

    /// <summary><c>(attr&gt;=value)</c> for string ordering (e.g. GeneralizedTime).</summary>
    public static string GreaterOrEqual(string attribute, string value) => $"({Attr(attribute)}>={Escape(value)})";

    /// <summary><c>(attr&lt;=value)</c> for string ordering (e.g. GeneralizedTime).</summary>
    public static string LessOrEqual(string attribute, string value) => $"({Attr(attribute)}<={Escape(value)})";

    /// <summary>All bits of <paramref name="mask"/> set: <c>(attr:1.2.840.113556.1.4.803:=mask)</c>.</summary>
    public static string BitAnd(string attribute, long mask) =>
        $"({Attr(attribute)}:{MatchingRuleBitAnd}:={mask.ToString(CultureInfo.InvariantCulture)})";

    /// <summary>Any bit of <paramref name="mask"/> set: <c>(attr:1.2.840.113556.1.4.804:=mask)</c>.</summary>
    public static string BitOr(string attribute, long mask) =>
        $"({Attr(attribute)}:{MatchingRuleBitOr}:={mask.ToString(CultureInfo.InvariantCulture)})";

    /// <summary>Transitive DN match, e.g. nested group membership: <c>(memberOf:1.2.840.113556.1.4.1941:=dn)</c>.</summary>
    public static string InChain(string attribute, string dn) =>
        $"({Attr(attribute)}:{MatchingRuleInChain}:={Escape(dn)})";

    /// <summary>Ambiguous name resolution - what ADUC "Find" uses: <c>(anr=value)</c>.</summary>
    public static string Anr(string value) => $"(anr={Escape(value)})";

    /// <summary>Logical AND of non-empty sub filters. Returns the single filter unchanged when only one is given.</summary>
    public static string And(params IEnumerable<string?> filters) => Combine('&', filters);

    /// <summary>Logical OR of non-empty sub filters. Returns the single filter unchanged when only one is given.</summary>
    public static string Or(params IEnumerable<string?> filters) => Combine('|', filters);

    /// <summary>Logical NOT.</summary>
    public static string Not(string filter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filter);
        return $"(!{Wrap(filter)})";
    }

    /// <summary>Ensures a filter is enclosed in parentheses (users often type <c>objectClass=user</c>).</summary>
    public static string Wrap(string filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var trimmed = filter.Trim();
        return trimmed.StartsWith('(') ? trimmed : $"({trimmed})";
    }

    /// <summary>
    /// Light-weight syntax validation of a user supplied filter: balanced parentheses,
    /// non-empty components and only valid escape sequences. Returns <c>null</c> when valid,
    /// otherwise an English diagnostic (UI maps it to a localized message).
    /// </summary>
    public static string? Validate(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return "Filter is empty.";
        }

        var f = Wrap(filter);
        var depth = 0;
        for (var i = 0; i < f.Length; i++)
        {
            var c = f[i];
            if (c == '\\')
            {
                if (i + 2 >= f.Length || !Uri.IsHexDigit(f[i + 1]) || !Uri.IsHexDigit(f[i + 2]))
                {
                    return $"Invalid escape sequence at position {i}.";
                }

                i += 2;
                continue;
            }

            if (c == '(')
            {
                depth++;
                if (i + 1 < f.Length && f[i + 1] == ')')
                {
                    return $"Empty component at position {i}.";
                }
            }
            else if (c == ')')
            {
                depth--;
                if (depth < 0)
                {
                    return $"Unbalanced ')' at position {i}.";
                }

                if (depth == 0 && i != f.Length - 1)
                {
                    return $"Unexpected text after position {i}.";
                }
            }
        }

        return depth == 0 ? null : "Unbalanced parentheses.";
    }

    private static string Combine(char op, IEnumerable<string?> filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        var parts = filters.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => Wrap(f!)).ToList();
        return parts.Count switch
        {
            0 => throw new ArgumentException("At least one filter is required.", nameof(filters)),
            1 => parts[0],
            _ => $"({op}{string.Concat(parts)})",
        };
    }

    private static string Attr(string attribute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attribute);
        foreach (var c in attribute)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or ';' or '.'))
            {
                throw new ArgumentException($"Invalid attribute name '{attribute}'.", nameof(attribute));
            }
        }

        return attribute;
    }
}
