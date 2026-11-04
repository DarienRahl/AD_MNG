using AdMng.Core.GroupPolicy.Content;

namespace AdMng.Core.GroupPolicy.AdministrativeTemplates;

/// <summary>
/// The registry values the loaded templates describe (K3 editor, K5 modelling): per normalized key the value names some
/// policy owns (<see cref="PolicyEngine.OwnValueNames"/>) and the keys of list elements (<see cref="PolicyEngine.ListKeys"/>,
/// whose every value belongs to the list). A <c>Registry.pol</c> entry outside both is an extra value no template
/// describes. Built once per definition set.
/// </summary>
public sealed class PolicyOwnershipIndex
{
    private readonly Dictionary<string, HashSet<string>> _ownedNames;
    private readonly HashSet<string> _listKeys;

    private PolicyOwnershipIndex(Dictionary<string, HashSet<string>> ownedNames, HashSet<string> listKeys)
    {
        _ownedNames = ownedNames;
        _listKeys = listKeys;
    }

    /// <summary>No templates: nothing is owned.</summary>
    public static PolicyOwnershipIndex Empty { get; } = new(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase));

    /// <summary>The value names every loaded policy owns per normalized key (the <c>ownedByAny</c> map of <see cref="PolicyEngine.Read"/>).</summary>
    public IReadOnlyDictionary<string, HashSet<string>> OwnedNames => _ownedNames;

    /// <summary>The keys of list elements.</summary>
    public IReadOnlySet<string> ListKeys => _listKeys;

    /// <summary>The index of <paramref name="set"/>.</summary>
    public static PolicyOwnershipIndex Build(PolicyDefinitionSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        var owned = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var listKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in set.Policies)
        {
            foreach (var (key, names) in PolicyEngine.OwnValueNames(definition.Policy))
            {
                if (!owned.TryGetValue(key, out var existing))
                {
                    existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    owned[key] = existing;
                }

                existing.UnionWith(names);
            }

            listKeys.UnionWith(PolicyEngine.ListKeys(definition.Policy));
        }

        return new PolicyOwnershipIndex(owned, listKeys);
    }

    /// <summary>A template describes the entry (its value, a deletion of it or a soft value; any value of a list key).</summary>
    public bool IsOwned(RegistryPolEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return IsOwned(entry.Key, entry.ValueName);
    }

    /// <summary>A template describes the value <paramref name="valueName"/> of <paramref name="key"/> (<c>**del.</c> and <c>**soft.</c> prefixes ignored).</summary>
    public bool IsOwned(string key, string valueName)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(valueName);
        var normalized = RegistryPolicyDocument.NormalizeKey(key);
        if (_listKeys.Contains(normalized))
        {
            return true;
        }

        var name = valueName.StartsWith(RegistryPolicyDocument.DeletePrefix, StringComparison.OrdinalIgnoreCase) ? valueName[RegistryPolicyDocument.DeletePrefix.Length..]
            : valueName.StartsWith(RegistryPolicyDocument.SoftPrefix, StringComparison.OrdinalIgnoreCase) ? valueName[RegistryPolicyDocument.SoftPrefix.Length..]
            : valueName;
        return _ownedNames.TryGetValue(normalized, out var names) && names.Contains(name);
    }
}
