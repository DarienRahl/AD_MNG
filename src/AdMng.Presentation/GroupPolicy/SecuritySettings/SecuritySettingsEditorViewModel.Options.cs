using System.Globalization;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Security;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy.SecuritySettings;

/// <summary>"Zasady kont i opcje zabezpieczeń": [System Access], [Kerberos Policy] and [Registry Values] of GptTmpl.inf.</summary>
public sealed partial class SecuritySettingsEditorViewModel
{
    private List<SecurityOptionRowViewModel> _optionRows = [];

    /// <summary>The raw entry rows by section, key and value (reused, so a selection and the typed raw value survive a refresh).</summary>
    private readonly Dictionary<(string Section, string Key, string Value), RawTemplateEntryViewModel> _rawRows = [];

    /// <summary>The row whose input is being applied (its text is not overwritten by the refresh that follows).</summary>
    private SecurityOptionRowViewModel? _editingOption;

    /// <summary>The groups as GPMC lists them, then "Inne wpisy szablonu" (the keys the catalog does not know).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SecurityOptionGroupViewModel> OptionGroups { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRawGroupSelected), nameof(ShowsOptionGroupColumn))]
    public partial SecurityOptionGroupViewModel? SelectedOptionGroup { get; set; }

    /// <summary>The selected option (its editor is shown below the list).</summary>
    [ObservableProperty]
    public partial SecurityOptionRowViewModel? SelectedOption { get; set; }

    /// <summary>"Inne wpisy szablonu" is selected: the raw entries are shown instead of options.</summary>
    public bool IsRawGroupSelected => SelectedOptionGroup?.IsRaw == true;

    /// <summary>Keys of the three sections the catalog does not know, as written (removed or replaced raw, never dropped).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<RawTemplateEntryViewModel> RawEntries { get; private set; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveRawEntryCommand), nameof(ReplaceRawValueCommand))]
    public partial RawTemplateEntryViewModel? SelectedRawEntry { get; set; }

    /// <summary>The new raw value of the selected entry ("Zastąp wartość").</summary>
    [ObservableProperty]
    public partial string RawValueText { get; set; } = string.Empty;

    partial void OnSelectedOptionGroupChanged(SecurityOptionGroupViewModel? value)
    {
        ApplyFilters();
        SelectedOption = value is not null && SelectedOption is { } selected && Options.Contains(selected) ? selected : null;
    }

    partial void OnSelectedRawEntryChanged(RawTemplateEntryViewModel? oldValue, RawTemplateEntryViewModel? newValue)
    {
        // The same entry again (its row was rebuilt after its value was replaced): the typed value stays.
        if (newValue is null || oldValue is null || !oldValue.Section.Equals(newValue.Section, StringComparison.OrdinalIgnoreCase)
            || !oldValue.Key.Equals(newValue.Key, StringComparison.OrdinalIgnoreCase))
        {
            RawValueText = newValue?.Value ?? string.Empty;
        }
    }

    private void BuildOptionRows()
    {
        var enabled = new SecurityChoice(1, Localizer[L.SecuritySettings.Enabled]);
        var disabled = new SecurityChoice(0, Localizer[L.SecuritySettings.Disabled]);
        _optionRows = [.. SecuritySettingsCatalog.Options.Select(o => new SecurityOptionRowViewModel(this, o, Localizer[SecuritySettingsText.OptionKey(o.Id)],
            Localizer[SecuritySettingsText.OptionGroupKey(o.Group)],
            o.Kind switch
            {
                SecurityValueKind.Boolean => [enabled, disabled],
                SecurityValueKind.Choice => [.. (o.Choices ?? []).Select(c => new SecurityChoice(c, Localizer[SecuritySettingsText.OptionChoiceKey(o.Id, c)]))],
                _ => [],
            },
            o.Kind == SecurityValueKind.Number ? Localizer.Format(L.SecuritySettings.Range, o.Minimum.ToString(Localizer.Culture), o.Maximum.ToString(Localizer.Culture)) : null))];
        var accountNote = Localizer[L.SecuritySettings.DomainAccountPolicyNote];
        var groups = Enum.GetValues<SecurityOptionGroup>()
            .Select(g => new SecurityOptionGroupViewModel(g, Localizer[SecuritySettingsText.OptionGroupKey(g)], [.. _optionRows.Where(r => r.Definition.Group == g)],
                g is SecurityOptionGroup.PasswordPolicy or SecurityOptionGroup.AccountLockout or SecurityOptionGroup.Kerberos ? accountNote : null))
            .Where(g => g.Rows.Count > 0)
            .ToList();
        groups.Add(new SecurityOptionGroupViewModel(null, Localizer[L.SecuritySettings.RawGroup], [], Localizer[L.SecuritySettings.RawHint]));
        OptionGroups = groups;
        SelectedOptionGroup = groups[0];
        SelectedOption = null;
    }

    private void RebuildOptions()
    {
        var document = _template?.Document;
        var original = _template?.OriginalDocument;
        foreach (var row in _optionRows)
        {
            var definition = row.Definition;
            var raw = document?.Get(definition.Section, definition.Key);
            row.IsChanged = !SameValue(original?.Get(definition.Section, definition.Key), raw);
            if (ReferenceEquals(row, _editingOption))
            {
                row.ValueText = row.IsDefined && raw is not null ? ValueText(definition, raw, row) : Localizer[L.SecuritySettings.NotDefined];
                continue;
            }

            row.Update(() =>
            {
                row.IsDefined = raw is not null;
                row.Text = raw is null ? string.Empty : InputText(definition, raw);
                row.SelectedChoice = raw is null ? null : row.Choices.FirstOrDefault(c => Number(definition, raw) == c.Value);
                row.Problem = null;
                row.ValueText = raw is null ? Localizer[L.SecuritySettings.NotDefined] : ValueText(definition, raw, row);
            });
        }

        // Above 14 characters the security engine needs "Złagodź limity minimalnej długości hasła" (here or in another GPO):
        // a warning, not a save blocker.
        if (_optionRows.FirstOrDefault(r => r.Definition.Id == "MinimumPasswordLength") is { } length)
        {
            var relax = SecuritySettingsCatalog.RelaxMinimumPasswordLength;
            var value = document?.Get(length.Definition.Section, length.Definition.Key) is { } raw ? Number(length.Definition, raw) : null;
            var relaxed = document?.Get(relax.Section, relax.Key) is { } relaxRaw && Number(relax, relaxRaw) == 1;
            length.Warning = value > 14 && !relaxed ? Localizer[L.SecuritySettings.PasswordLengthNeedsRelax] : null;
        }

        var entries = new List<RawTemplateEntryViewModel>();
        foreach (var section in new[] { SecurityTemplateDocument.SystemAccess, SecurityTemplateDocument.KerberosPolicy, SecurityTemplateDocument.RegistryValues })
        {
            var before = original?.Values(section).ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase) ?? [];
            foreach (var (key, value) in document?.Values(section) ?? [])
            {
                if (SecuritySettingsCatalog.FindOption(section, key) is null)
                {
                    if (!_rawRows.TryGetValue((section, key, value), out var entry))
                    {
                        entry = new RawTemplateEntryViewModel(section, key, value);
                        _rawRows[(section, key, value)] = entry;
                    }

                    entry.IsChanged = !SameValue(before.GetValueOrDefault(key), value);
                    entries.Add(entry);
                }
            }
        }

        foreach (var stale in _rawRows.Where(r => !entries.Contains(r.Value)).Select(r => r.Key).ToList())
        {
            _rawRows.Remove(stale);
        }

        if (!entries.SequenceEqual(RawEntries))
        {
            var selected = SelectedRawEntry;
            RawEntries = entries;
            SelectedRawEntry = selected is null ? null
                : entries.FirstOrDefault(e => e.Section == selected.Section && e.Key.Equals(selected.Key, StringComparison.OrdinalIgnoreCase));
        }
        OnPropertyChanged(nameof(HasProblems));
    }

    /// <summary>A row's input: the value is written when valid (the document keeps the last valid value otherwise).</summary>
    internal void OnOptionEdited(SecurityOptionRowViewModel row, bool definedChanged)
    {
        if (!CanEditTemplate || _template is not { } template)
        {
            RebuildOptions();
            return;
        }

        var definition = row.Definition;
        if (!row.IsDefined)
        {
            template.Document.Remove(definition.Section, definition.Key);
            row.Update(() => row.Problem = null);
        }
        else
        {
            if (definedChanged && template.Document.Get(definition.Section, definition.Key) is null)
            {
                row.Update(() => SetDefault(row));
            }

            if (!definedChanged && row.UsesChoice && row.SelectedChoice is null)
            {
                return; // a cleared combo box is not a value (the document keeps the last one)
            }

            var (value, problem) = Format(row);
            row.Update(() => row.Problem = problem);
            if (value is not null)
            {
                template.Document.Set(definition.Section, definition.Key, value);
            }
        }

        _editingOption = row;
        try
        {
            AfterEdit();
        }
        finally
        {
            _editingOption = null;
        }
    }

    /// <summary>"Usuń wpis": the key of the template is removed.</summary>
    [RelayCommand(CanExecute = nameof(CanEditRaw))]
    private void RemoveRawEntry()
    {
        if (SelectedRawEntry is { } entry && CanEditTemplate && _template is { } template)
        {
            template.Document.Remove(entry.Section, entry.Key);
            SelectedRawEntry = null;
            AfterEdit();
        }
    }

    /// <summary>"Zastąp wartość": the raw value as typed (one line).</summary>
    [RelayCommand(CanExecute = nameof(CanEditRaw))]
    private void ReplaceRawValue()
    {
        if (SelectedRawEntry is not { } entry || !CanEditTemplate || _template is not { } template)
        {
            return;
        }

        var value = RawValueText.Trim();
        try
        {
            template.Document.Set(entry.Section, entry.Key, value);
        }
        catch (ArgumentException)
        {
            ShowRefused();
            return;
        }

        AfterEdit();
    }

    private bool CanEditRaw() => CanEditTemplate && SelectedRawEntry is not null;

    /// <summary>The value a newly defined option starts with (the least it can be; booleans disabled; the first choice).</summary>
    private static void SetDefault(SecurityOptionRowViewModel row)
    {
        var definition = row.Definition;
        switch (definition.Kind)
        {
            case SecurityValueKind.Number:
                row.Text = (definition.Minimum <= 0 && definition.Maximum >= 0 ? 0 : definition.Minimum).ToString(CultureInfo.InvariantCulture);
                break;
            case SecurityValueKind.Boolean:
                row.SelectedChoice = row.Choices.FirstOrDefault(c => c.Value == 0);
                break;
            case SecurityValueKind.Choice:
                row.SelectedChoice = row.Choices.FirstOrDefault();
                break;
            default:
                row.Text = string.Empty;
                break;
        }
    }

    /// <summary>The value to write (as secedit writes it) or the reason the input is not valid.</summary>
    private (string? Value, string? Problem) Format(SecurityOptionRowViewModel row)
    {
        var definition = row.Definition;
        var registry = definition.Location == SecurityOptionLocation.RegistryValue;
        switch (definition.Kind)
        {
            case SecurityValueKind.Number:
                if (!long.TryParse(row.Text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
                {
                    return (null, Localizer[L.SecuritySettings.NotANumber]);
                }

                if (number < definition.Minimum || number > definition.Maximum)
                {
                    return (null, Localizer.Format(L.SecuritySettings.OutOfRange, definition.Minimum.ToString(Localizer.Culture), definition.Maximum.ToString(Localizer.Culture)));
                }

                return (registry ? SecurityTemplateDocument.DwordValue((uint)number) : number.ToString(CultureInfo.InvariantCulture), null);

            case SecurityValueKind.Boolean or SecurityValueKind.Choice:
                if (row.SelectedChoice is not { } choice)
                {
                    return (null, Localizer[L.SecuritySettings.ChoiceRequired]);
                }

                return (registry ? SecurityTemplateDocument.DwordValue((uint)choice.Value) : choice.Value.ToString(CultureInfo.InvariantCulture), null);

            case SecurityValueKind.Text:
                var text = row.Text.Trim();
                if (text.Contains('\r', StringComparison.Ordinal) || text.Contains('\n', StringComparison.Ordinal))
                {
                    return (null, Localizer[L.SecuritySettings.TextInvalid]);
                }

                if (registry)
                {
                    return (SecurityTemplateDocument.StringValue(text), null);
                }

                // [System Access] names (NewAdministratorName, NewGuestName) are written quoted, as secedit does.
                if (text.Length == 0)
                {
                    return (null, Localizer[L.SecuritySettings.TextRequired]);
                }

                return text.Contains('"', StringComparison.Ordinal) ? (null, Localizer[L.SecuritySettings.TextInvalid]) : ("\"" + text + "\"", null);

            default:
                var lines = row.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.TrimEnd('\r')).ToList();
                while (lines.Count > 0 && lines[^1].Length == 0)
                {
                    lines.RemoveAt(lines.Count - 1);
                }

                return (SecurityTemplateDocument.MultiStringValue(lines), null);
        }
    }

    /// <summary>The text box content of a value as written.</summary>
    private static string InputText(SecurityOptionDefinition definition, string raw)
    {
        var data = Data(definition, raw);
        return definition.Kind switch
        {
            SecurityValueKind.MultiText => data.Length == 0 ? string.Empty : string.Join(Environment.NewLine, IniReader.SplitCommaList(data)),
            SecurityValueKind.Text => definition.Location == SecurityOptionLocation.RegistryValue ? IniReader.SplitCommaList(data)[0] : IniReader.Unquote(data),
            _ => data,
        };
    }

    /// <summary>The value shown in the list.</summary>
    private string ValueText(SecurityOptionDefinition definition, string raw, SecurityOptionRowViewModel row)
    {
        switch (definition.Kind)
        {
            case SecurityValueKind.Boolean or SecurityValueKind.Choice:
                var number = Number(definition, raw);
                return row.Choices.FirstOrDefault(c => c.Value == number)?.Label ?? raw;
            case SecurityValueKind.MultiText:
                return string.Join(" / ", InputText(definition, raw).Split(Environment.NewLine));
            case SecurityValueKind.Text:
                var text = InputText(definition, raw);
                return text.Length == 0 ? Localizer[L.SecuritySettings.EmptyText] : text;
            default:
                return Data(definition, raw);
        }
    }

    /// <summary>The data of a value: a registry value without its type (<c>4,1</c> → <c>1</c>), else the value.</summary>
    private static string Data(SecurityOptionDefinition definition, string raw)
    {
        if (definition.Location != SecurityOptionLocation.RegistryValue)
        {
            return raw.Trim();
        }

        var comma = raw.IndexOf(',', StringComparison.Ordinal);
        return comma < 0 ? string.Empty : raw[(comma + 1)..].Trim();
    }

    private static long? Number(SecurityOptionDefinition definition, string raw) =>
        long.TryParse(Data(definition, raw), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ? number : null;
}
