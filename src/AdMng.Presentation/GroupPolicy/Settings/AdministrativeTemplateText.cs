using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.GroupPolicy.Settings;

/// <summary>
/// How an administrative template policy reads (K5's formatting, shared by the GPO's "Ustawienia" tab, the comparison and the
/// model, design note docs/dev/k-computers-and-gpo.md 20.1): its state ("Włączone"), its options as the policy's dialog labels
/// them ("Tryb: Pobierz i zainstaluj") and its category path ("Składniki systemu Windows › Aktualizacje").
/// </summary>
public static class AdministrativeTemplateText
{
    /// <summary>"Włączone", "Wyłączone", "Niejednoznaczne", "Nie skonfigurowano".</summary>
    public static string State(ILocalizer localizer, PolicyState state)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[state switch
        {
            PolicyState.Enabled => L.AdministrativeTemplates.StateEnabled,
            PolicyState.Disabled => L.AdministrativeTemplates.StateDisabled,
            PolicyState.Inconsistent => L.AdministrativeTemplates.StateInconsistent,
            _ => L.AdministrativeTemplates.StateNotConfigured,
        }];
    }

    /// <summary>"Tryb: Pobierz i zainstaluj" – the options of an enabled policy in the order of its elements (none otherwise).</summary>
    public static IReadOnlyList<string> Options(ILocalizer localizer, PolicyDefinition definition, PolicySetting setting)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(setting);
        if (setting.State != PolicyState.Enabled || setting.Values.Count == 0)
        {
            return [];
        }

        var options = new List<string>();
        foreach (var element in definition.Policy.Elements)
        {
            if (setting.ValueOf(element.Id) is not { } value)
            {
                continue;
            }

            var control = definition.Presentation?.Controls.FirstOrDefault(c => string.Equals(c.RefId, element.Id, StringComparison.OrdinalIgnoreCase));
            var label = (control?.Label is { Length: > 0 } text ? text : element.Id).Trim().TrimEnd(':');
            options.Add(localizer.Format(L.GroupPolicyModel.OptionValue, label, ElementValue(localizer, definition, element, value)));
        }

        return options;
    }

    /// <summary>"Włączone; Tryb: Pobierz i zainstaluj, Dzień: 3" – the state and, when enabled, the options as the GPO has them.</summary>
    public static string Value(ILocalizer localizer, PolicyDefinition definition, PolicySetting setting)
    {
        var state = State(localizer, setting.State);
        var options = Options(localizer, definition, setting);
        return options.Count == 0 ? state : localizer.Format(L.GroupPolicyModel.ValueWithOptions, state, string.Join(", ", options));
    }

    /// <summary>The category path of the policy ("Składniki systemu Windows", "Aktualizacje"); empty when its category is unknown.</summary>
    public static IReadOnlyList<string> CategoryPath(PolicyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.Category is { } category ? category.PathNames() : [];
    }

    /// <summary>One element's value as the policy's dialog shows it (an enum item by its name, a yes/no, a list as "a | b").</summary>
    public static string ElementValue(ILocalizer localizer, PolicyDefinition definition, PolicyElement element, PolicyElementValue value)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(definition);
        return value switch
        {
            BooleanElementValue boolean => GroupPolicyText.YesNo(localizer, boolean.Value),
            NumberElementValue number => number.Value.ToString(localizer.Culture),
            TextElementValue text => text.Value,
            MultiTextElementValue lines => string.Join(" | ", lines.Lines),
            EnumElementValue choice when element is EnumElement list && choice.Index >= 0 && choice.Index < list.Items.Count =>
                definition.Text(list.Items[choice.Index].DisplayName),
            EnumElementValue choice => choice.Index.ToString(localizer.Culture),
            ListElementValue list => string.Join(" | ", list.Items.Select(i => i.Name.Length == 0 ? i.Value : i.Name + "=" + i.Value)),
            _ => string.Empty,
        };
    }
}
