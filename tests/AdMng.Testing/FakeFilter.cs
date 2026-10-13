using System.Globalization;
using System.Text;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;

namespace AdMng.Testing;

/// <summary>Parsed RFC 4515 search filter used by <see cref="FakeDirectorySession"/>.</summary>
internal abstract record FilterNode;

internal sealed record AndNode(IReadOnlyList<FilterNode> Children) : FilterNode;

internal sealed record OrNode(IReadOnlyList<FilterNode> Children) : FilterNode;

internal sealed record NotNode(FilterNode Child) : FilterNode;

internal sealed record PresentNode(string Attribute) : FilterNode;

/// <summary>An assertion value: the raw bytes (escapes resolved) and their UTF-8 text.</summary>
internal sealed record AssertionValue(byte[] Bytes, string Text);

internal enum ComparisonOperator
{
    Equal,
    GreaterOrEqual,
    LessOrEqual,
    Approximate,
}

internal sealed record ComparisonNode(string Attribute, ComparisonOperator Operator, AssertionValue Value) : FilterNode;

internal sealed record SubstringNode(string Attribute, string? Initial, IReadOnlyList<string> Any, string? Final) : FilterNode;

internal sealed record ExtensibleNode(string Attribute, string MatchingRule, AssertionValue Value) : FilterNode;

/// <summary>Strict RFC 4515 parser; malformed filters raise the same error a DC would (LDAP 87, filter error).</summary>
internal static class FakeFilterParser
{
    public static FilterNode Parse(string filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var text = filter.Trim();
        if (text.Length > 0 && text[0] != '(')
        {
            text = "(" + text + ")";
        }

        var position = 0;
        var node = ParseFilter(text, ref position);
        if (position != text.Length)
        {
            throw Error($"unexpected text at position {position}");
        }

        return node;
    }

    private static FilterNode ParseFilter(string text, ref int position)
    {
        Expect(text, ref position, '(');
        if (position >= text.Length)
        {
            throw Error("unexpected end of filter");
        }

        FilterNode node;
        switch (text[position])
        {
            case '&':
                position++;
                node = new AndNode(ParseList(text, ref position));
                break;
            case '|':
                position++;
                node = new OrNode(ParseList(text, ref position));
                break;
            case '!':
                position++;
                node = new NotNode(ParseFilter(text, ref position));
                break;
            default:
                var end = text.IndexOf(')', position);
                if (end < 0)
                {
                    throw Error("missing ')'");
                }

                node = ParseItem(text[position..end]);
                position = end;
                break;
        }

        Expect(text, ref position, ')');
        return node;
    }

    private static List<FilterNode> ParseList(string text, ref int position)
    {
        var list = new List<FilterNode>();
        while (position < text.Length && text[position] == '(')
        {
            list.Add(ParseFilter(text, ref position));
        }

        if (list.Count == 0)
        {
            throw Error("empty filter list");
        }

        return list;
    }

    private static FilterNode ParseItem(string item)
    {
        var equals = item.IndexOf('=', StringComparison.Ordinal);
        if (equals <= 0)
        {
            throw Error($"invalid item '{item}'");
        }

        var left = item[..equals];
        var right = item[(equals + 1)..];
        if (left.EndsWith(':'))
        {
            // Extensible match: attr[:dn]:rule:=value
            var parts = left[..^1].Split(':');
            var attribute = parts[0];
            var rule = parts.Skip(1).FirstOrDefault(p => !p.Equals("dn", StringComparison.OrdinalIgnoreCase));
            if (attribute.Length == 0 || rule is null)
            {
                throw Error($"unsupported extensible match '{item}'");
            }

            return new ExtensibleNode(CheckAttribute(attribute), rule, Unescape(right));
        }

        var op = ComparisonOperator.Equal;
        var last = left[^1];
        if (last is '>' or '<' or '~')
        {
            op = last switch
            {
                '>' => ComparisonOperator.GreaterOrEqual,
                '<' => ComparisonOperator.LessOrEqual,
                _ => ComparisonOperator.Approximate,
            };
            left = left[..^1];
        }

        var name = CheckAttribute(left.Trim());
        if (op == ComparisonOperator.Equal && right == "*")
        {
            return new PresentNode(name);
        }

        if (op == ComparisonOperator.Equal && right.Contains('*', StringComparison.Ordinal))
        {
            var pieces = right.Split('*');
            var initial = pieces[0].Length > 0 ? Unescape(pieces[0]).Text : null;
            var final = pieces[^1].Length > 0 ? Unescape(pieces[^1]).Text : null;
            var any = pieces[1..^1].Where(p => p.Length > 0).Select(p => Unescape(p).Text).ToList();
            return new SubstringNode(name, initial, any, final);
        }

        return new ComparisonNode(name, op, Unescape(right));
    }

    private static string CheckAttribute(string attribute)
    {
        if (attribute.Length == 0 || !attribute.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or ';' or '.'))
        {
            throw Error($"invalid attribute description '{attribute}'");
        }

        return attribute;
    }

    private static AssertionValue Unescape(string value)
    {
        var bytes = new List<byte>(value.Length);
        var run = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\\')
            {
                if (i + 2 >= value.Length || !char.IsAsciiHexDigit(value[i + 1]) || !char.IsAsciiHexDigit(value[i + 2]))
                {
                    throw Error($"invalid escape in '{value}'");
                }

                bytes.AddRange(Encoding.UTF8.GetBytes(run.ToString()));
                run.Clear();
                bytes.Add(byte.Parse(value.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                i += 2;
                continue;
            }

            if (c is '(' or ')')
            {
                throw Error($"unescaped '{c}' in value '{value}'");
            }

            run.Append(c);
        }

        bytes.AddRange(Encoding.UTF8.GetBytes(run.ToString()));
        var array = bytes.ToArray();
        return new AssertionValue(array, Encoding.UTF8.GetString(array));
    }

    private static void Expect(string text, ref int position, char expected)
    {
        if (position >= text.Length || text[position] != expected)
        {
            throw Error($"expected '{expected}' at position {position}");
        }

        position++;
    }

    private static DirectoryException Error(string detail) =>
        new(DirectoryException.Classify(87, null), $"Bad search filter: {detail}.") { LdapResultCode = 87 };
}

/// <summary>Helpers shared by the fake session.</summary>
internal static class FakeDn
{
    public static DistinguishedName Parse(string dn)
    {
        if (!DistinguishedName.TryParse(dn, out var parsed))
        {
            throw new DirectoryException(DirectoryErrorKind.InvalidSyntax, $"Invalid DN syntax: '{dn}'.") { LdapResultCode = 34, DistinguishedName = dn };
        }

        return parsed;
    }

    /// <summary>Re-bases <paramref name="dn"/> (equal to or below <paramref name="oldBase"/>) onto <paramref name="newBase"/>.</summary>
    public static DistinguishedName Rebase(DistinguishedName dn, DistinguishedName oldBase, DistinguishedName newBase)
    {
        var relative = dn.Rdns.Count - oldBase.Rdns.Count;
        var result = newBase;
        for (var i = relative - 1; i >= 0; i--)
        {
            result = result.Child(dn.Rdns[i].Type, dn.Rdns[i].Value);
        }

        return result;
    }

    public static bool IsSameOrBelow(DistinguishedName dn, DistinguishedName ancestor) => dn == ancestor || dn.IsDescendantOf(ancestor);
}
