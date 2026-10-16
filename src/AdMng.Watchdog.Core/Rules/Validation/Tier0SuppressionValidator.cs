using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Rules.Validation;

/// <summary>
/// <c>tier0Suppression</c> (8.3, 12.6): <c>ignoredAttributes</c> with a Tier-0 attribute, <c>disabledKinds</c> with a
/// Tier-0 kind, or an <c>excludedSubtrees</c> entry that equals or contains a <c>criticalObjects</c> entry,
/// <c>{domain}</c>, <c>OU=Domain Controllers,{domain}</c> or <c>CN=System,{domain}</c> – allowed only with the bootstrap
/// flag <c>Authorization.AllowTier0Suppression</c>. Health kinds can never be disabled (the detector records them
/// regardless), so listing one is an error even with the flag.
/// </summary>
internal static class Tier0SuppressionValidator
{
    public static void Validate(ValidationRun run)
    {
        var directory = run.Configuration.Directory;
        var allowed = run.Context.AllowTier0Suppression;

        for (var i = 0; i < directory.DisabledKinds.Count; i++)
        {
            var kind = directory.DisabledKinds[i];
            var path = ValidationRun.Item("directory.disabledKinds", i);
            if (Enum.IsDefined(kind) && EventKindCatalog.Get(kind).IsHealth)
            {
                run.Error(path, ConfigValidationCodes.Tier0Suppression, "Health kinds cannot be disabled.", kind.ToString());
            }
            else if (!allowed && Tier0Catalog.IsTier0Kind(kind))
            {
                run.Error(path, ConfigValidationCodes.Tier0Suppression,
                    "Tier-0 kinds can be disabled only when the bootstrap setting Authorization.AllowTier0Suppression is true.", kind.ToString());
            }
        }

        if (allowed)
        {
            return;
        }

        for (var i = 0; i < directory.IgnoredAttributes.Count; i++)
        {
            if (Tier0Catalog.IsTier0Attribute(directory.IgnoredAttributes[i]))
            {
                run.Error(ValidationRun.Item("directory.ignoredAttributes", i), ConfigValidationCodes.Tier0Suppression,
                    "Tier-0 attributes can be ignored only when the bootstrap setting Authorization.AllowTier0Suppression is true.",
                    directory.IgnoredAttributes[i]);
            }
        }

        var protectedEntries = directory.CriticalObjects.Concat(Tier0Catalog.ProtectedSubtrees).ToList();
        for (var i = 0; i < directory.ExcludedSubtrees.Count; i++)
        {
            var value = directory.ExcludedSubtrees[i];
            if (Parse(value, ConfigTokens.Placeholder) is not { } excluded)
            {
                continue;
            }

            // "{domain}" in the exclusion: both sides expand with the placeholder domain. A literal exclusion
            // (DC=contoso,DC=com) is compared with the protected entries expanded for its own domain components – the
            // validator does not need to know the real domain.
            var tokens = ConfigTokens.ContainsToken(value)
                ? ConfigTokens.Placeholder
                : ConfigTokens.Placeholder with { DomainDn = excluded.DomainComponent.ToString() };
            if (protectedEntries.Select(p => Parse(p, tokens)).Any(p => p is not null && (p.Equals(excluded) || p.IsDescendantOf(excluded))))
            {
                run.Error(ValidationRun.Item("directory.excludedSubtrees", i), ConfigValidationCodes.Tier0Suppression,
                    "The subtree contains a critical object (domain head, Domain Controllers, CN=System or a critical object); excluding it needs Authorization.AllowTier0Suppression.");
            }
        }
    }

    private static DistinguishedName? Parse(string? value, ConfigTokens tokens) =>
        tokens.ExpandDn(value) is { } expanded && DistinguishedName.TryParse(expanded, out var dn) && !dn.IsRoot
            ? dn
            : null;
}
