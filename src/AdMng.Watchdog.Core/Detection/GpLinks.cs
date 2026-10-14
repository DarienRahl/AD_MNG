using System.Globalization;
using AdMng.Core.Ldap;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>Parser of <c>gPLink</c> values: <c>[LDAP://&lt;GPO DN&gt;;&lt;options&gt;]…</c> (options 1 = disabled, 2 = enforced).</summary>
internal static class GpLinks
{
    /// <summary>One linked GPO.</summary>
    public readonly record struct Link(string Dn, int Options)
    {
        public bool Disabled => (Options & 1) != 0;

        public bool Enforced => (Options & 2) != 0;
    }

    /// <summary>The links of a gPLink value in order; malformed parts are skipped.</summary>
    public static IReadOnlyList<Link> Parse(string? value)
    {
        var result = new List<Link>();
        if (string.IsNullOrEmpty(value))
        {
            return result;
        }

        var position = 0;
        while (position < value.Length)
        {
            var open = value.IndexOf('[', position);
            if (open < 0)
            {
                break;
            }

            var close = value.IndexOf(']', open + 1);
            if (close < 0)
            {
                break;
            }

            var body = value[(open + 1)..close];
            position = close + 1;
            var separator = body.LastIndexOf(';');
            var path = separator < 0 ? body : body[..separator];
            var options = separator >= 0 && int.TryParse(body.AsSpan(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var o) ? o : 0;
            if (path.StartsWith("LDAP://", StringComparison.OrdinalIgnoreCase))
            {
                path = path[7..];
            }

            if (DistinguishedName.TryParse(path, out var dn) && !dn.IsRoot)
            {
                result.Add(new Link(dn.ToString(), options));
            }
        }

        return result;
    }
}
