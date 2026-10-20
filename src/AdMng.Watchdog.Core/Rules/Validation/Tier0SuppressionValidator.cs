using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Rules.Validation;

/// <summary>
/// <c>tier0Suppression</c> (8.3, 12.6): <c>ignoredAttributes</c> with a Tier-0 attribute, <c>disabledKinds</c> with a
/// Tier-0 kind, or an <c>excludedSubtrees</c> entry that equals or contains a <c>criticalObjects</c> entry, a
/// <c>privilegedGroups.additionalCriticalDns</c> / <c>additionalHighDns</c> group or one of
/// <see cref="Tier0Catalog.ProtectedSubtrees"/> (<c>{domain}</c>, <c>OU=Domain Controllers</c>, <c>CN=System</c>,
/// <c>CN=Users</c>, <c>CN=Builtin</c>) – allowed only with the bootstrap
/// flag <c>Authorization.AllowTier0Suppression</c>. Health kinds can never be disabled (the detector records them
/// regardless), so listing one is an error even with the flag. The same flag guards the stage B rules
/// (<see cref="Tier0Catalog.SuppressionProtectedRules"/>, design 9.1–9.3): compared with the shipped rule they cannot be
/// disabled, their matching cannot be narrowed (kinds, object kinds, scope, attributes, tags, group filter, minimum
/// severity, actor exclusions, exclusions), the <c>watchdog-self</c> override cannot leave Critical, and their delivery
/// cannot be weakened (the checks of the locked rules).
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

        ProtectedRules(run);
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

    private static void ProtectedRules(ValidationRun run)
    {
        var rules = run.Configuration.Rules;
        for (var i = 0; i < rules.Count; i++)
        {
            if (rules[i] is not { } rule || !Tier0Catalog.IsSuppressionProtectedRule(rule.Id) || DefaultRules.Find(rule.Id) is not { } shipped)
            {
                continue;
            }

            var path = ValidationRun.Item("rules", i);
            void Suppressed(string property, string what) =>
                run.Error(path + "." + property, ConfigValidationCodes.Tier0Suppression,
                    $"The {what} of the built-in rule \"{shipped.Id}\" can be weakened only when the bootstrap setting Authorization.AllowTier0Suppression is true.",
                    shipped.Id);

            if (!rule.Enabled)
            {
                run.Error(path + ".enabled", ConfigValidationCodes.Tier0Suppression,
                    $"The built-in rule \"{shipped.Id}\" can be disabled only when the bootstrap setting Authorization.AllowTier0Suppression is true.",
                    shipped.Id);
            }

            Matching(rule, shipped, Suppressed);
            RuleValidator.DeliveryWeakenings(run.Configuration.Notifications, rule, shipped, Suppressed);
        }
    }

    /// <summary>Narrowed matching of a protected rule compared with the shipped one (an empty list = any).</summary>
    private static void Matching(RuleSettings rule, RuleSettings shipped, Action<string, string> suppressed)
    {
        if (Narrows(rule.Kinds, shipped.Kinds))
        {
            suppressed("kinds", "event kinds");
        }

        if (Narrows(rule.ObjectKinds, shipped.ObjectKinds))
        {
            suppressed("objectKinds", "object kinds");
        }

        if (rule.Scope.IncludeSubtrees.Count > shipped.Scope.IncludeSubtrees.Count || rule.Scope.ExcludeSubtrees.Count > shipped.Scope.ExcludeSubtrees.Count)
        {
            suppressed("scope", "scope");
        }

        if (rule.Attributes.Count > 0 && shipped.Attributes.Count == 0)
        {
            suppressed("attributes", "attribute filter");
        }

        if (rule.RequiredTags.Any(t => !shipped.RequiredTags.Contains(t, StringComparer.OrdinalIgnoreCase))
            || (rule.AnyTags.Count > 0 && shipped.AnyTags.Count == 0))
        {
            suppressed("requiredTags", "tag filter");
        }

        if (rule.Groups != shipped.Groups)
        {
            suppressed("groups", "group filter");
        }

        if ((rule.MinSeverity ?? Severity.Info) > (shipped.MinSeverity ?? Severity.Info))
        {
            suppressed("minSeverity", "minimum severity");
        }

        if (shipped.Severity is { } shippedOverride && rule.Severity != shippedOverride)
        {
            suppressed("severity", "notification severity");
        }

        if (rule.Actors.ExcludeActors.Count > 0 || (rule.Actors.ExcludeSelfChanges && !shipped.Actors.ExcludeSelfChanges))
        {
            suppressed("actors", "actor filter");
        }

        if (rule.Exclusions.Count > 0)
        {
            suppressed("exclusions", "exclusion list");
        }
    }

    /// <summary>True when <paramref name="configured"/> matches less than <paramref name="shipped"/> (empty = any).</summary>
    private static bool Narrows<T>(IReadOnlyList<T> configured, IReadOnlyList<T> shipped) =>
        configured.Count > 0 && (shipped.Count == 0 || shipped.Any(k => !configured.Contains(k)));

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
