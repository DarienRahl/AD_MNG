using AdMng.Core.GroupPolicy;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// Parser of <c>gPLink</c> values: <c>[LDAP://&lt;GPO DN&gt;;&lt;options&gt;]…</c> (options 1 = disabled, 2 = enforced) -
/// <see cref="GroupPolicyLinks"/> of AdMng.Core (shared with the privileged access review).
/// </summary>
internal static class GpLinks
{
    /// <summary>One linked GPO.</summary>
    public readonly record struct Link(string Dn, int Options)
    {
        public bool Disabled => (Options & GroupPolicyLink.DisabledOption) != 0;

        public bool Enforced => (Options & GroupPolicyLink.EnforcedOption) != 0;
    }

    /// <summary>The links of a gPLink value in order; malformed parts are skipped.</summary>
    public static IReadOnlyList<Link> Parse(string? value) => [.. GroupPolicyLinks.Parse(value).Select(l => new Link(l.GpoDn, l.Options))];
}
