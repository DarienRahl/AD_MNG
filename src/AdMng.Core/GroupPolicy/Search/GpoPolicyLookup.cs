using AdMng.Core.GroupPolicy.AdministrativeTemplates;

namespace AdMng.Core.GroupPolicy.Search;

/// <summary>
/// Which configured policy owns a registry key and value of one half – the same rule as the presenter's lookup
/// (<c>GpoSettingPresenter.PolicyOf</c>), so that a policy unit is exactly the settings the presenter collapses into one
/// policy row: a list key belongs to its policy whatever the value, a value to the first policy that owns its name.
/// </summary>
internal sealed class GpoPolicyLookup
{
    private readonly Dictionary<string, List<(ConfiguredPolicy Policy, HashSet<string> Names)>> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ConfiguredPolicy> _lists = new(StringComparer.OrdinalIgnoreCase);

    public GpoPolicyLookup(ConfiguredPolicies policies)
    {
        foreach (var policy in policies.Policies)
        {
            foreach (var (key, names) in PolicyEngine.OwnValueNames(policy.Definition.Policy))
            {
                var normalized = RegistryPolicyDocument.NormalizeKey(key);
                if (!_values.TryGetValue(normalized, out var list))
                {
                    list = [];
                    _values[normalized] = list;
                }

                list.Add((policy, new HashSet<string>(names, StringComparer.OrdinalIgnoreCase)));
            }

            foreach (var key in PolicyEngine.ListKeys(policy.Definition.Policy))
            {
                _lists.TryAdd(RegistryPolicyDocument.NormalizeKey(key), policy);
            }
        }
    }

    /// <summary>The policy of <c>HKLM\key</c> and a value name (null: the key itself), or null.</summary>
    public ConfiguredPolicy? Find(string registryKey, string? valueName)
    {
        var slash = registryKey.IndexOf('\\', StringComparison.Ordinal);
        var key = RegistryPolicyDocument.NormalizeKey(slash < 0 ? string.Empty : registryKey[(slash + 1)..]);
        if (_lists.TryGetValue(key, out var listOwner))
        {
            return listOwner;
        }

        if (valueName is null or "*" or RegistryPolicyDocument.DeleteAllValues || !_values.TryGetValue(key, out var candidates))
        {
            return null;
        }

        foreach (var (policy, names) in candidates)
        {
            if (names.Contains(valueName))
            {
                return policy;
            }
        }

        return null;
    }
}
