using AdMng.Core.Ldap;

namespace AdMng.Watchdog.Core.Rules.Validation;

/// <summary><c>invalidDn</c> and <c>invalidSid</c> (8.3): every configured DN parses with
/// <see cref="DistinguishedName.TryParse(string?, out DistinguishedName)"/> and every SID matches
/// <c>^S-1-\d+(-\d+)+$</c> after token expansion with placeholder values (<see cref="ConfigTokens.Placeholder"/>).</summary>
internal static class DnAndSidValidator
{
    public static void Validate(ValidationRun run)
    {
        var c = run.Configuration;
        Dns(run, "directory.wakeUpContainers", c.Directory.WakeUpContainers);
        Dns(run, "directory.excludedSubtrees", c.Directory.ExcludedSubtrees);
        Dns(run, "directory.criticalObjects", c.Directory.CriticalObjects);
        Dns(run, "directory.serviceAccountSubtrees", c.Directory.ServiceAccountSubtrees);
        Dns(run, "directory.disabledAccountsSubtrees", c.Directory.DisabledAccountsSubtrees);
        Dns(run, "privilegedGroups.additionalCriticalDns", c.PrivilegedGroups.AdditionalCriticalDns);
        Dns(run, "privilegedGroups.additionalHighDns", c.PrivilegedGroups.AdditionalHighDns);
        Dns(run, "privilegedGroups.watchListDns", c.PrivilegedGroups.WatchListDns);
        Sids(run, "privilegedGroups.criticalSids", c.PrivilegedGroups.CriticalSids);
        Sids(run, "privilegedGroups.highSids", c.PrivilegedGroups.HighSids);
        Sids(run, "privilegedGroups.protectRemovalSids", c.PrivilegedGroups.ProtectRemovalSids);

        for (var i = 0; i < c.Rules.Count; i++)
        {
            var rule = c.Rules[i];
            if (rule is null)
            {
                continue;
            }

            var path = ValidationRun.Item("rules", i);
            Dns(run, path + ".scope.includeSubtrees", rule.Scope.IncludeSubtrees);
            Dns(run, path + ".scope.excludeSubtrees", rule.Scope.ExcludeSubtrees);
            Dns(run, path + ".groupDns", rule.GroupDns);
            Sids(run, path + ".actors.excludeActors", rule.Actors.ExcludeActors);
            for (var j = 0; j < rule.Exclusions.Count; j++)
            {
                var exclusion = rule.Exclusions[j];
                var exclusionPath = ValidationRun.Item(path + ".exclusions", j);
                if (exclusion?.SubtreeDn is { } subtree)
                {
                    Dn(run, exclusionPath + ".subtreeDn", subtree);
                }

                if (exclusion?.Actor is { } actor)
                {
                    Sid(run, exclusionPath + ".actor", actor);
                }
            }
        }
    }

    /// <summary>True when <paramref name="value"/> is a non-empty DN after placeholder expansion.</summary>
    public static bool IsValidDn(string? value) =>
        ConfigTokens.Placeholder.ExpandDn(value) is { } expanded
        && DistinguishedName.TryParse(expanded, out var dn)
        && !dn.IsRoot;

    private static void Dns(ValidationRun run, string listPath, IReadOnlyList<string> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            Dn(run, ValidationRun.Item(listPath, i), values[i]);
        }
    }

    private static void Dn(ValidationRun run, string path, string? value)
    {
        if (!IsValidDn(value))
        {
            run.Error(path, ConfigValidationCodes.InvalidDn, "Not a valid distinguished name (tokens: {domain}).");
        }
    }

    private static void Sids(ValidationRun run, string listPath, IReadOnlyList<string> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            Sid(run, ValidationRun.Item(listPath, i), values[i]);
        }
    }

    private static void Sid(ValidationRun run, string path, string? value)
    {
        if (ConfigTokens.Placeholder.ExpandSid(value) is null)
        {
            run.Error(path, ConfigValidationCodes.InvalidSid, "Not a SID (S-1-…; tokens: {domainSid}, {rootDomainSid}) – actors are identified by SID only.");
        }
    }
}
