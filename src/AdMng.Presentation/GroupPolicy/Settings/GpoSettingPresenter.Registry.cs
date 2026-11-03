using System.Globalization;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.GroupPolicy.Settings;

/// <summary>Registry.pol: administrative template policies, and the values no policy describes ("Dodatkowe ustawienia rejestru").</summary>
public sealed partial class GpoSettingPresenter
{
    /// <summary>The policy of <see cref="ComputerPolicies"/>/<see cref="UserPolicies"/> that owns a Registry.pol setting, or null.</summary>
    internal (ConfiguredPolicy Policy, PolicyClass Class)? PolicyOf(GpoSettingKeyInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.Area != GpoSettingArea.PolicyRegistry || info.Truncated || info.RegistryKey is not { } registryKey)
        {
            return null;
        }

        var (lookup, scope) = info.Scope == GpoScope.User ? (_userLookup, PolicyClass.User) : (_computerLookup, PolicyClass.Machine);
        return lookup?.Find(registryKey, info.ValueName) is { } policy ? (policy, scope) : null;
    }

    /// <summary>A Registry.pol value on its own: "HKLM\…\Value" with its typed data, in "Dodatkowe ustawienia rejestru".</summary>
    private GpoSettingText PolicyRegistry(GpoSettingKeyInfo info, IReadOnlyList<string> values)
    {
        if (PolicyOf(info) is { } owner)
        {
            // Part of a policy (a view that does not group rows): named after the policy, the value as written.
            var (value, elements) = RegistryValueText(Single(values));
            return Row(info, GpoSettingBranch.Policies, PolicyGroups(owner.Policy.Definition),
                Part(owner.Policy.Definition.DisplayName, RegistryPathText(info.RegistryKey!, info.ValueName)), value, elements,
                focus: GroupPolicySettingFocus.ForPolicy(owner.Class, owner.Policy.Definition.Id));
        }

        var (text, lines) = RegistryValueText(Single(values));
        return Row(info, GpoSettingBranch.Policies, [_l[L.GroupPolicyModel.AreaTemplates], _l[L.GpoSettings.ExtraRegistry]],
            RegistryPathText(info.RegistryKey ?? info.Key, info.ValueName), text, lines);
    }

    /// <summary>
    /// One row for an administrative template policy: its name, state and options, its category path. The options come from
    /// the file as it is, so what the parsers mask is masked here too: an option holding a secret reads "(ustawione – ukryte)"
    /// (<see cref="AdministrativeTemplateText.HasSecret"/>), and a masked value no option explains hides every option.
    /// </summary>
    private GpoSettingText DescribePolicy(ConfiguredPolicy policy, PolicyClass scope, IReadOnlyList<GpoSetting> settings)
    {
        var definition = policy.Definition;
        var first = settings.Count > 0 ? settings[0].Key : definition.Id;
        var registry = settings.Select(s => GpoSettingKey.Parse(s.Key)).Select(i => RegistryPathText(i.RegistryKey ?? i.Key, i.ValueName)).Distinct(StringComparer.OrdinalIgnoreCase);
        var options = AdministrativeTemplateText.Options(_l, definition, policy.Setting);
        var secret = AdministrativeTemplateText.HasSecret(definition, policy.Setting);
        var hidden = !secret && options.Count > 0 && settings.Any(s => s.Kind == GpoSettingKind.Masked);
        var explanation = definition.ExplainText.Length == 0 ? null : definition.ExplainText.Trim();
        return new GpoSettingText
        {
            Key = first,
            Keys = [.. settings.Select(s => s.Key)],
            Scope = scope == PolicyClass.User ? GpoScope.User : GpoScope.Computer,
            Kind = GpoSettingArea.PolicyRegistry,
            Configuration = Configuration(scope == PolicyClass.User ? GpoScope.User : GpoScope.Computer),
            Branch = GpoSettingBranch.Policies,
            BranchText = BranchText(GpoSettingBranch.Policies),
            Groups = PolicyGroups(definition),
            Name = definition.DisplayName,
            Technical = definition.Id + " (" + string.Join("; ", registry) + ")",
            ValueText = hidden
                ? _l.Format(L.GroupPolicyModel.ValueWithOptions, AdministrativeTemplateText.State(_l, policy.Setting.State), _l[L.GroupPolicy.MaskedValue])
                : AdministrativeTemplateText.Value(_l, definition, policy.Setting),
            Elements = hidden ? [] : options,
            IsMasked = secret || hidden,
            Explanation = secret || hidden ? Join(_l[L.GpoSettings.ExplainMasked], explanation) : explanation,
            Focus = GroupPolicySettingFocus.ForPolicy(scope, definition.Id),
            Source = settings.Count > 0 ? settings[0].Source : null,
        };
    }

    /// <summary>"Szablony administracyjne › Składniki systemu Windows › Aktualizacje".</summary>
    private string[] PolicyGroups(PolicyDefinition definition) => [_l[L.GroupPolicyModel.AreaTemplates], .. AdministrativeTemplateText.CategoryPath(definition)];

    /// <summary>"HKLM\Software\Policies\…\Value", the key alone for a key-level action, "(Domyślna)" for the default value.</summary>
    private string RegistryPathText(string registryKey, string? valueName) => valueName switch
    {
        null or "**delvals." => registryKey,
        "" => registryKey + "\\" + _l[L.GpoSettings.RegistryDefaultValue],
        "*" => registryKey + "\\" + _l[L.GpoSettings.RegistryHiddenNames],
        _ => registryKey + "\\" + valueName,
    };

    /// <summary>A registry setting value: typed data ("1 (REG_DWORD)", the lines of a REG_MULTI_SZ) or the action ("Usuń wartość").</summary>
    private (string Text, IReadOnlyList<string> Elements) RegistryValueText(string value)
    {
        if (!GpoSettingValue.TryParseRegistry(value, out var action, out var type, out var data))
        {
            return (value, []);
        }

        switch (action)
        {
            case GpoRegistryAction.DeleteValue:
                return (_l[L.GpoSettings.RegistryDeleteValue], []);
            case GpoRegistryAction.DeleteAllValues:
                return (_l[L.GpoSettings.RegistryDeleteAllValues], []);
            case GpoRegistryAction.DeleteKey:
                return (_l[L.GpoSettings.RegistryDeleteKey], []);
            case GpoRegistryAction.CreateKey:
                return (_l[L.GpoSettings.RegistryCreateKey], []);
        }

        var typeName = RegistryFormat.TypeName(type);
        string text;
        IReadOnlyList<string> elements = [];
        switch (type)
        {
            case RegistryFormat.RegMultiSz:
                elements = data.Length == 0 ? [] : data.Split(GpoSetting.ListSeparator);
                text = elements.Count == 0 ? _l[L.SecuritySettings.EmptyText] : string.Join("; ", elements);
                break;
            case RegistryFormat.RegDword or RegistryFormat.RegQword when ulong.TryParse(data, NumberStyles.None, CultureInfo.InvariantCulture, out var number):
                text = number > 9 ? number.ToString(_l.Culture) + " (0x" + number.ToString("X", CultureInfo.InvariantCulture) + ")" : number.ToString(_l.Culture);
                break;
            default:
                text = data.Length == 0 ? _l[L.SecuritySettings.EmptyText] : data;
                break;
        }

        var typed = _l.Format(L.GpoSettings.RegistryValue, text, typeName);
        return (action == GpoRegistryAction.SetIfMissing ? _l.Format(L.GpoSettings.RegistrySetIfMissing, typed) : typed, elements);
    }

    /// <summary>Which configured policy owns a registry key and value of one half (as <see cref="ConfiguredPolicies.Covers(string)"/>).</summary>
    private sealed class PolicyLookup
    {
        private readonly Dictionary<string, List<(ConfiguredPolicy Policy, HashSet<string> Names)>> _values = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ConfiguredPolicy> _lists = new(StringComparer.OrdinalIgnoreCase);

        public PolicyLookup(ConfiguredPolicies policies)
        {
            Policies = policies;
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

        public ConfiguredPolicies Policies { get; }

        /// <summary>The policy of <c>HKLM\key</c> and a value name (null: the key itself), or null.</summary>
        public ConfiguredPolicy? Find(string registryKey, string? valueName)
        {
            var slash = registryKey.IndexOf('\\', StringComparison.Ordinal);
            var key = RegistryPolicyDocument.NormalizeKey(slash < 0 ? string.Empty : registryKey[(slash + 1)..]);
            if (_lists.TryGetValue(key, out var listOwner))
            {
                return listOwner;
            }

            if (valueName is null or "*" or "**delvals." || !_values.TryGetValue(key, out var candidates))
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
}
