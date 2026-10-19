using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Rules.Validation;

/// <summary>
/// <c>tier0Suppression</c> (8.3, 12.6): <c>ignoredAttributes</c> with a Tier-0 attribute, <c>disabledKinds</c> with a
/// Tier-0 kind, or an <c>excludedSubtrees</c> entry that equals or contains a <c>criticalObjects</c> entry, a
/// <c>privilegedGroups.additionalCriticalDns</c> / <c>additionalHighDns</c> group or one of
/// <see cref="Tier0Catalog.ProtectedSubtrees"/> (<c>{domain}</c>, <c>OU=Domain Controllers</c>, <c>CN=System</c>,
/// <c>CN=Users</c>, <c>CN=Builtin</c>) – allowed only with the bootstrap
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

        var groups = run.Configuration.PrivilegedGroups;
        var protectedEntries = directory.CriticalObjects
            .Concat(groups.AdditionalCriticalDns)
            .Concat(groups.AdditionalHighDns)
            .Concat(Tier0Catalog.ProtectedSubtrees)
            .ToList();
        for (var i = 0; i < directory.ExcludedSubtrees.Count; i++)
        {
            var value = directory.ExcludedSubtrees[i];
            if (Parse(value, ConfigTokens.Placeholder) is null)
            {
                continue;
            }

            if (protectedEntries.Any(p => Covers(value, p)))
            {
                run.Error(ValidationRun.Item("directory.excludedSubtrees", i), ConfigValidationCodes.Tier0Suppression,
                    "The subtree contains a critical object (domain head, Domain Controllers, CN=System, CN=Users, CN=Builtin, a critical object or a privileged group); excluding it needs Authorization.AllowTier0Suppression.");
            }
        }
    }

    /// <summary>True when the excluded subtree equals or contains the protected entry. "{domain}" on both sides: both
    /// expand with the placeholder domain. When one side is literal (DC=contoso,DC=com), the other side's token expands to
    /// the literal side's domain components – the validator does not need to know the real domain.</summary>
    private static bool Covers(string excludedValue, string? protectedValue)
    {
        var tokens = ConfigTokens.Placeholder;
        if (!ConfigTokens.ContainsToken(excludedValue) && Parse(excludedValue, tokens) is { } literalExcluded)
        {
            tokens = tokens with { DomainDn = literalExcluded.DomainComponent.ToString() };
        }
        else if (!ConfigTokens.ContainsToken(protectedValue) && Parse(protectedValue, tokens) is { } literalProtected)
        {
            tokens = tokens with { DomainDn = literalProtected.DomainComponent.ToString() };
        }

        return Parse(excludedValue, tokens) is { } excluded
            && Parse(protectedValue, tokens) is { } entry
            && (entry.Equals(excluded) || entry.IsDescendantOf(excluded));
    }

    private static DistinguishedName? Parse(string? value, ConfigTokens tokens) =>
        tokens.ExpandDn(value) is { } expanded && DistinguishedName.TryParse(expanded, out var dn) && !dn.IsRoot
            ? dn
            : null;
}
