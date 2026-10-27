using System.Globalization;
using AdMng.Core.Ldap;

namespace AdMng.Core.GroupPolicy;

/// <summary>
/// One GPO link of a <c>gPLink</c> value ([MS-GPOL] 2.2.2): the GPO's DN, the options (1 = disabled, 2 = enforced) and the
/// link order as GPMC shows it - 1 is the highest precedence, the last link of the value (new links are prepended).
/// </summary>
/// <param name="GpoDn">DN of the group policy container (normalized).</param>
/// <param name="Options">Link options.</param>
/// <param name="Order">Link order within the container: 1 = last in the value = applied last (wins).</param>
public sealed record GroupPolicyLink(string GpoDn, int Options, int Order)
{
    /// <summary>Link option: the link is disabled (the GPO is not applied through it).</summary>
    public const int DisabledOption = 1;

    /// <summary>Link option: enforced (No Override) - block inheritance does not stop it and it wins over child links.</summary>
    public const int EnforcedOption = 2;

    public bool Disabled => (Options & DisabledOption) != 0;

    public bool Enforced => (Options & EnforcedOption) != 0;

    /// <summary>The GPO's GUID from the <c>cn</c> of <see cref="GpoDn"/>, null when the cn is not <c>{GUID}</c>.</summary>
    public Guid? GpoId => DistinguishedName.TryParse(GpoDn, out var dn) && !dn.IsRoot && GroupPolicyLinks.TryParseGpoId(dn.Name, out var id) ? id : null;
}

/// <summary>Parser of <c>gPLink</c> (shared by the privileged access review and AdMng Watchdog).</summary>
public static class GroupPolicyLinks
{
    /// <summary><c>gPOptions</c> bit of a domain head or OU that blocks inheritance from the containers above it.</summary>
    public const int BlockInheritanceOption = 1;

    /// <summary>
    /// The links of a gPLink value <c>[LDAP://&lt;GPO DN&gt;;&lt;options&gt;]…</c> in the order of the value (lowest
    /// precedence first); malformed parts are skipped.
    /// </summary>
    public static IReadOnlyList<GroupPolicyLink> Parse(string? value)
    {
        var parsed = new List<(string Dn, int Options)>();
        if (string.IsNullOrEmpty(value))
        {
            return [];
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
                parsed.Add((dn.ToString(), options));
            }
        }

        return [.. parsed.Select((link, index) => new GroupPolicyLink(link.Dn, link.Options, parsed.Count - index))];
    }

    /// <summary>The GUID of a group policy container's <c>cn</c>: exactly <c>{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}</c>.</summary>
    public static bool TryParseGpoId(string? cn, out Guid guid)
    {
        guid = Guid.Empty;
        return cn is { Length: 38 } && Guid.TryParseExact(cn, "B", out guid) && guid != Guid.Empty;
    }
}
