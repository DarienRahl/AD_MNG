using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// The configuration-dependent inputs of <see cref="SeverityResolver"/>: the domain DN (Domain Controllers OU GPO-link
/// bump), the expanded ProtectRemovalSids and DisabledAccountsSubtrees.
/// </summary>
public sealed record SeverityContext
{
    public string? DomainDn { get; init; }
    public IReadOnlySet<string> ProtectRemovalSids { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<DistinguishedName> DisabledAccountsSubtrees { get; init; } = [];

    /// <summary>No configuration (catalog defaults and tag bumps only).</summary>
    public static SeverityContext Empty { get; } = new();

    /// <summary>Builds the context from the runtime configuration with tokens expanded for this domain.</summary>
    public static SeverityContext Create(WatchdogConfiguration configuration, DirectoryFacts facts)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(facts);
        return new SeverityContext
        {
            DomainDn = facts.DomainDn,
            ProtectRemovalSids = configuration.PrivilegedGroups.ProtectRemovalSids
                .Select(s => DetectionTokens.Expand(s, facts.DomainDn, facts.DomainSid, facts.RootDomainSid))
                .Where(s => !DetectionTokens.HasToken(s))
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            DisabledAccountsSubtrees = configuration.Directory.DisabledAccountsSubtrees
                .Select(s => DetectionTokens.Expand(s, facts.DomainDn, facts.DomainSid, facts.RootDomainSid))
                .Select(s => DistinguishedName.TryParse(s, out var dn) && !dn.IsRoot ? dn : null)
                .OfType<DistinguishedName>()
                .ToList(),
        };
    }

    /// <summary>True when <paramref name="dn"/> is equal to or below one of the disabled-accounts subtrees.</summary>
    public bool IsInDisabledSubtree(string? dn) =>
        dn is not null && DistinguishedName.TryParse(dn, out var parsed)
        && DisabledAccountsSubtrees.Any(s => parsed == s || parsed.IsDescendantOf(s));
}
