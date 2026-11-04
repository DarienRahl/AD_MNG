using System.Runtime.CompilerServices;
using AdMng.Core.GroupPolicy.Content;

namespace AdMng.Core.GroupPolicy.AdministrativeTemplates;

/// <summary>A policy one Registry.pol configures (enabled, disabled or inconsistent).</summary>
/// <param name="Definition">The policy.</param>
/// <param name="Setting">Its state and element values (<see cref="PolicyEngine.Read"/>).</param>
/// <param name="Entries">The file's entries the policy owns (its value, list and element values, their deletions; every value
/// of its list keys), in file order.</param>
public sealed record ConfiguredPolicy(PolicyDefinition Definition, PolicySetting Setting, IReadOnlyList<RegistryPolEntry> Entries)
{
    /// <summary>The policy and its state only (element values and registry data stay out of logs).</summary>
    public override string ToString() => Definition.Id + " " + Setting.State;
}

/// <summary>
/// The administrative template policies a Registry.pol configures for one class (<see cref="AdministrativeTemplatePolicies.Configured"/>)
/// and the entries none of them covers – the "extra registry settings" GPMC lists apart.
/// </summary>
public sealed class ConfiguredPolicies
{
    private readonly Dictionary<string, HashSet<string>> _names;
    private readonly HashSet<string> _listKeys;

    private ConfiguredPolicies(IReadOnlyList<ConfiguredPolicy> policies, IReadOnlyList<RegistryPolEntry> uncovered,
        Dictionary<string, HashSet<string>> names, HashSet<string> listKeys)
    {
        Policies = policies;
        Uncovered = uncovered;
        _names = names;
        _listKeys = listKeys;
    }

    /// <summary>No policy (no templates, or no file).</summary>
    public static ConfiguredPolicies None { get; } = Uncovering([]);

    /// <summary>The configured policies in the order their keys first appear in the file.</summary>
    public IReadOnlyList<ConfiguredPolicy> Policies { get; }

    /// <summary>The file's entries no configured policy covers, in file order.</summary>
    public IReadOnlyList<RegistryPolEntry> Uncovered { get; }

    /// <summary>A configured policy owns the entry's value (<c>**del.</c>/<c>**soft.</c> prefixes ignored), or the entry is
    /// in a list key of a configured policy.</summary>
    public bool Covers(RegistryPolEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var key = RegistryPolicyDocument.NormalizeKey(entry.Key);
        return _listKeys.Contains(key)
            || (_names.TryGetValue(key, out var names) && names.Contains(RegistryPolicyDocument.BaseName(entry.ValueName)));
    }

    /// <summary>
    /// A Registry.pol setting key of <see cref="RegistryPolParser"/> (<c>Computer/Registry/HKLM\key!value</c>) whose value a
    /// configured policy owns, or any value of a configured list key. The key path ends at the first <c>!</c> (template key
    /// names have none; a value name may); a key without a value (a created or deleted key), a hidden value name (<c>*</c>)
    /// and <c>**delvals.</c> outside a list key are not covered.
    /// </summary>
    public bool Covers(string settingKey)
    {
        ArgumentNullException.ThrowIfNull(settingKey);
        if (_names.Count == 0 && _listKeys.Count == 0)
        {
            return false;
        }

        var slash = settingKey.IndexOf("/Registry/", StringComparison.OrdinalIgnoreCase);
        var path = slash < 0 ? null : settingKey[(slash + "/Registry/".Length)..];
        var bang = path?.IndexOf('!', StringComparison.Ordinal) ?? -1;
        if (path is null || bang < 0)
        {
            return false; // a deleted key: not a value of a template
        }

        var name = path[(bang + 1)..];
        var hiveAndKey = path[..bang];
        var firstSlash = hiveAndKey.IndexOf('\\', StringComparison.Ordinal);
        var key = RegistryPolicyDocument.NormalizeKey(firstSlash < 0 ? string.Empty : hiveAndKey[(firstSlash + 1)..]);
        if (_listKeys.Contains(key))
        {
            return true;
        }

        return name is not ("*" or "**delvals.") && _names.TryGetValue(key, out var names) && names.Contains(name == "(Default)" ? string.Empty : name);
    }

    /// <summary>The result for <paramref name="policies"/> of <paramref name="entries"/>.</summary>
    internal static ConfiguredPolicies Of(IReadOnlyList<ConfiguredPolicy> policies, IReadOnlyList<RegistryPolEntry> entries)
    {
        var names = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var listKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var policy in policies)
        {
            foreach (var (key, owned) in PolicyEngine.OwnValueNames(policy.Definition.Policy))
            {
                var normalized = RegistryPolicyDocument.NormalizeKey(key);
                if (!names.TryGetValue(normalized, out var existing))
                {
                    existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    names[normalized] = existing;
                }

                existing.UnionWith(owned);
            }

            listKeys.UnionWith(PolicyEngine.ListKeys(policy.Definition.Policy).Select(RegistryPolicyDocument.NormalizeKey));
        }

        var uncovered = new List<RegistryPolEntry>();
        var result = new ConfiguredPolicies(policies, uncovered, names, listKeys);
        uncovered.AddRange(entries.Where(e => !result.Covers(e)));
        return result;
    }

    /// <summary>No policy: every entry is uncovered.</summary>
    internal static ConfiguredPolicies Uncovering(IReadOnlyList<RegistryPolEntry> entries) =>
        new([], entries, new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Counts only (element values and registry data stay out of logs).</summary>
    public override string ToString() =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Policies.Count} policies, {Uncovered.Count} uncovered entries");
}

/// <summary>
/// Which administrative template policies a Registry.pol configures (design note docs/dev/k-computers-and-gpo.md 18 and
/// 20.1) – the matching of the K5 modeller, shared by the GPO's settings tab and the GPO comparison so that they name
/// policies the way the model does: the candidates are the policies of the class that own a key of the file
/// (<see cref="PolicyEngine.OwnValueNames"/>, <see cref="PolicyEngine.ListKeys"/>); each is read by
/// <see cref="PolicyEngine.Read"/> with the values every loaded policy owns, and those not "not configured" are
/// configured. A value only a policy of the other class or an unconfigured one describes stays uncovered (as in GPMC).
/// The key index of a definition set is built once and kept as long as the set lives. Pure.
/// </summary>
public static class AdministrativeTemplatePolicies
{
    private static readonly ConditionalWeakTable<PolicyDefinitionSet, PolicyIndex> Indexes = new();

    /// <summary>The policies of <paramref name="scope"/> that <paramref name="document"/> configures, and the entries left over.</summary>
    public static ConfiguredPolicies Configured(RegistryPolicyDocument document, PolicyDefinitionSet set, PolicyClass scope)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(set);
        if (set.PolicyCount == 0 || document.Entries.Count == 0)
        {
            return ConfiguredPolicies.Uncovering(document.Entries);
        }

        var index = Indexes.GetValue(set, static s => new PolicyIndex(s));
        var byKey = document.Entries
            .GroupBy(e => RegistryPolicyDocument.NormalizeKey(e.Key), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var policies = new List<ConfiguredPolicy>();
        foreach (var definition in index.Candidates(document, scope))
        {
            var setting = PolicyEngine.Read(definition.Policy, document.View, index.Ownership.OwnedNames);
            if (setting.State != PolicyState.NotConfigured)
            {
                policies.Add(new ConfiguredPolicy(definition, setting, OwnedEntries(definition.Policy, document, byKey)));
            }
        }

        return ConfiguredPolicies.Of(policies, document.Entries);
    }

    /// <summary>The entries of the file <paramref name="policy"/> owns, in file order.</summary>
    private static IReadOnlyList<RegistryPolEntry> OwnedEntries(AdmxPolicy policy, RegistryPolicyDocument document, Dictionary<string, List<RegistryPolEntry>> byKey)
    {
        var listKeys = PolicyEngine.ListKeys(policy).Select(RegistryPolicyDocument.NormalizeKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var owned = new HashSet<RegistryPolEntry>(ReferenceEqualityComparer.Instance);
        foreach (var key in listKeys)
        {
            if (byKey.TryGetValue(key, out var entries))
            {
                owned.UnionWith(entries);
            }
        }

        foreach (var (key, names) in PolicyEngine.OwnValueNames(policy))
        {
            if (byKey.TryGetValue(RegistryPolicyDocument.NormalizeKey(key), out var entries))
            {
                owned.UnionWith(entries.Where(e => names.Contains(RegistryPolicyDocument.BaseName(e.ValueName))));
            }
        }

        return [.. document.Entries.Where(owned.Contains)];
    }

    /// <summary>The policies of a definition set by the registry keys they own (built once per set).</summary>
    private sealed class PolicyIndex
    {
        private readonly Dictionary<string, List<PolicyDefinition>> _byKey = new(StringComparer.OrdinalIgnoreCase);

        public PolicyIndex(PolicyDefinitionSet set)
        {
            Ownership = PolicyOwnershipIndex.Build(set);
            foreach (var definition in set.Policies)
            {
                var keys = PolicyEngine.OwnValueNames(definition.Policy).Keys.Concat(PolicyEngine.ListKeys(definition.Policy))
                    .Select(RegistryPolicyDocument.NormalizeKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var key in keys)
                {
                    if (!_byKey.TryGetValue(key, out var list))
                    {
                        list = [];
                        _byKey[key] = list;
                    }

                    list.Add(definition);
                }
            }
        }

        public PolicyOwnershipIndex Ownership { get; }

        /// <summary>The policies of <paramref name="policyClass"/> that own a key of <paramref name="registry"/> (each once).</summary>
        public IEnumerable<PolicyDefinition> Candidates(RegistryPolicyDocument registry, PolicyClass policyClass)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in registry.Entries.Select(e => RegistryPolicyDocument.NormalizeKey(e.Key)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!_byKey.TryGetValue(key, out var definitions))
                {
                    continue;
                }

                foreach (var definition in definitions)
                {
                    if (definition.Policy.AppliesTo(policyClass) && seen.Add(definition.Id))
                    {
                        yield return definition;
                    }
                }
            }
        }
    }
}
