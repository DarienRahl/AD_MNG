using System.Globalization;
using System.Text;
using AdMng.Core.Model;

namespace AdMng.Presentation.Tests.TestDoubles;

/// <summary>
/// Minimal RFC 4515 filter evaluator for the in-memory directory: and/or/not, equality (case-insensitive, any
/// value), presence, substrings, &gt;= / &lt;= (numeric when both sides are numbers), the AD bit matching rules
/// (803/804) and a simplified ANR (prefix match on name, displayName, sAMAccountName, givenName, sn).
/// </summary>
internal static class LdapFilterEvaluator
{
    private static readonly string[] AnrAttributes = ["name", "displayName", "sAMAccountName", "givenName", "sn"];

    public static bool Matches(string filter, DirectoryObject obj)
    {
        var position = 0;
        var node = Parse(filter.Trim(), ref position);
        return node(obj);
    }

    private static Func<DirectoryObject, bool> Parse(string f, ref int i)
    {
        Expect(f, ref i, '(');
        Func<DirectoryObject, bool> node;
        switch (f[i])
        {
            case '&':
                i++;
                var all = ParseList(f, ref i);
                node = o => all.All(n => n(o));
                break;
            case '|':
                i++;
                var any = ParseList(f, ref i);
                node = o => any.Any(n => n(o));
                break;
            case '!':
                i++;
                var inner = Parse(f, ref i);
                node = o => !inner(o);
                break;
            default:
                node = ParseItem(f, ref i);
                break;
        }

        Expect(f, ref i, ')');
        return node;
    }

    private static List<Func<DirectoryObject, bool>> ParseList(string f, ref int i)
    {
        var list = new List<Func<DirectoryObject, bool>>();
        while (i < f.Length && f[i] == '(')
        {
            list.Add(Parse(f, ref i));
        }

        return list;
    }

    private static Func<DirectoryObject, bool> ParseItem(string f, ref int i)
    {
        var start = i;
        var depth = 0;
        while (i < f.Length && !(f[i] == ')' && depth == 0))
        {
            if (f[i] == '\\')
            {
                i += 3;
                continue;
            }

            i++;
        }

        var item = f[start..i];
        if (item.Contains(":=", StringComparison.Ordinal))
        {
            var parts = item.Split(":=", 2);
            var left = parts[0].Split(':');
            var attribute = left[0];
            var rule = left.Length > 1 ? left[1] : string.Empty;
            var value = Unescape(parts[1]);
            return rule switch
            {
                "1.2.840.113556.1.4.803" => o => Numbers(o, attribute).Any(n => (n & long.Parse(value, CultureInfo.InvariantCulture)) == long.Parse(value, CultureInfo.InvariantCulture)),
                "1.2.840.113556.1.4.804" => o => Numbers(o, attribute).Any(n => (n & long.Parse(value, CultureInfo.InvariantCulture)) != 0),
                _ => _ => false,
            };
        }

        if (item.Contains(">=", StringComparison.Ordinal))
        {
            var parts = item.Split(">=", 2);
            var value = Unescape(parts[1]);
            return o => o.GetStrings(parts[0]).Any(v => Compare(v, value) >= 0);
        }

        if (item.Contains("<=", StringComparison.Ordinal))
        {
            var parts = item.Split("<=", 2);
            var value = Unescape(parts[1]);
            return o => o.GetStrings(parts[0]).Any(v => Compare(v, value) <= 0);
        }

        var eq = item.IndexOf('=', StringComparison.Ordinal);
        var attr = item[..eq];
        var raw = item[(eq + 1)..];
        if (string.Equals(attr, "anr", StringComparison.OrdinalIgnoreCase))
        {
            var text = Unescape(raw);
            return o => AnrAttributes.Any(a => o.GetStrings(a).Any(v => v.StartsWith(text, StringComparison.OrdinalIgnoreCase)));
        }

        if (raw == "*")
        {
            return o => o.GetStrings(attr).Count > 0;
        }

        if (raw.Contains('*', StringComparison.Ordinal))
        {
            var pieces = raw.Split('*').Select(Unescape).ToArray();
            return o => o.GetStrings(attr).Any(v => MatchesSubstring(v, pieces));
        }

        var expected = Unescape(raw);
        return o => o.GetStrings(attr).Any(v => string.Equals(v, expected, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<long> Numbers(DirectoryObject o, string attribute) =>
        o.GetStrings(attribute).Select(v => long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? (long?)n : null)
            .Where(n => n is not null).Select(n => n!.Value);

    private static int Compare(string actual, string expected) =>
        long.TryParse(actual, NumberStyles.Integer, CultureInfo.InvariantCulture, out var a)
        && long.TryParse(expected, NumberStyles.Integer, CultureInfo.InvariantCulture, out var e)
            ? a.CompareTo(e)
            : string.Compare(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesSubstring(string value, string[] pieces)
    {
        var index = 0;
        for (var p = 0; p < pieces.Length; p++)
        {
            var piece = pieces[p];
            if (piece.Length == 0)
            {
                continue;
            }

            var found = value.IndexOf(piece, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0 || (p == 0 && found != 0))
            {
                return false;
            }

            index = found + piece.Length;
        }

        return pieces[^1].Length == 0 || value.EndsWith(pieces[^1], StringComparison.OrdinalIgnoreCase);
    }

    private static string Unescape(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return value;
        }

        var bytes = new List<byte>();
        var sb = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 2 < value.Length)
            {
                bytes.Add(byte.Parse(value.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                i += 2;
                continue;
            }

            if (bytes.Count > 0)
            {
                sb.Append(Encoding.UTF8.GetString([.. bytes]));
                bytes.Clear();
            }

            sb.Append(value[i]);
        }

        if (bytes.Count > 0)
        {
            sb.Append(Encoding.UTF8.GetString([.. bytes]));
        }

        return sb.ToString();
    }

    private static void Expect(string f, ref int i, char c)
    {
        if (i >= f.Length || f[i] != c)
        {
            throw new FormatException($"Expected '{c}' at {i} in {f}");
        }

        i++;
    }
}
