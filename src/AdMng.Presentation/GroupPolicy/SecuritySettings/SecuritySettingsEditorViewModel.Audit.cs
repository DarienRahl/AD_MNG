using System.Globalization;
using AdMng.Core.GroupPolicy.Security;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.GroupPolicy.SecuritySettings;

/// <summary>"Inspekcja": the legacy categories ([Event Audit] of GptTmpl.inf) and the advanced subcategories (audit.csv).</summary>
public sealed partial class SecuritySettingsEditorViewModel
{
    private List<AuditRowViewModel> _advancedRows = [];

    /// <summary>The nine legacy categories ("Zasady inspekcji").</summary>
    [ObservableProperty]
    public partial IReadOnlyList<AuditRowViewModel> LegacyAudit { get; private set; } = [];

    /// <summary>The advanced audit categories with their subcategories ("Zaawansowana konfiguracja zasad inspekcji").</summary>
    [ObservableProperty]
    public partial IReadOnlyList<AuditCategoryViewModel> AdvancedAuditCategories { get; private set; } = [];

    /// <summary>Both legacy and advanced audit are configured but the subcategories are not forced: a warning, else null.</summary>
    [ObservableProperty]
    public partial string? AuditNote { get; private set; }

    private void BuildAuditRows()
    {
        IReadOnlyList<AuditStateChoice> states =
        [
            new AuditStateChoice(null, Localizer[L.SecuritySettings.AuditNotConfigured]),
            new AuditStateChoice(0, Localizer[L.SecuritySettings.AuditNone]),
            new AuditStateChoice(1, Localizer[L.SecuritySettings.AuditSuccess]),
            new AuditStateChoice(2, Localizer[L.SecuritySettings.AuditFailure]),
            new AuditStateChoice(3, Localizer[L.SecuritySettings.AuditBoth]),
        ];
        LegacyAudit = [.. SecuritySettingsCatalog.AuditCategories.Select(k => new AuditRowViewModel(this, k, Localizer[SecuritySettingsText.EventAuditKey(k)], states, null))];
        _advancedRows = [.. SecuritySettingsCatalog.AuditSubcategories.Select(s => new AuditRowViewModel(this, s.Id, Localizer[SecuritySettingsText.AuditSubcategoryKey(s.Id)], states, s))];
        AdvancedAuditCategories = [.. SecuritySettingsCatalog.AdvancedAuditCategories
            .Select(c => new AuditCategoryViewModel(c, Localizer[SecuritySettingsText.AuditCategoryKey(c)], [.. _advancedRows.Where(r => r.Subcategory!.Category == c)]))
            .Where(c => c.Rows.Count > 0)];
    }

    private void RebuildAudit()
    {
        var template = _template?.Document;
        var originalTemplate = _template?.OriginalDocument;
        foreach (var row in LegacyAudit)
        {
            var value = Level(template?.Get(SecurityTemplateDocument.EventAudit, row.Id));
            row.Update(() => row.SelectedState = row.States.FirstOrDefault(s => s.Value == value) ?? row.States[0]);
            row.IsChanged = value != Level(originalTemplate?.Get(SecurityTemplateDocument.EventAudit, row.Id));
        }

        var current = _audit?.Document.GetSubcategories() ?? new Dictionary<Guid, int>();
        var original = _audit?.OriginalDocument.GetSubcategories() ?? new Dictionary<Guid, int>();
        foreach (var row in _advancedRows)
        {
            var guid = row.Subcategory!.Guid;
            int? value = current.TryGetValue(guid, out var v) ? v : null;
            row.Update(() => row.SelectedState = row.States.FirstOrDefault(s => s.Value == value) ?? row.States[0]);
            row.IsChanged = value != (original.TryGetValue(guid, out var before) ? before : null);
        }

        var legacy = LegacyAudit.Any(r => r.SelectedState?.Value is not null);
        var advanced = _advancedRows.Any(r => r.SelectedState?.Value is not null);
        var forced = template?.Get(SecurityTemplateDocument.RegistryValues, SecuritySettingsCatalog.ForceAuditSubcategories.Key) is { } raw
            && Number(SecuritySettingsCatalog.ForceAuditSubcategories, raw) == 1;
        AuditNote = legacy && advanced && !forced ? Localizer[L.SecuritySettings.AuditBothNote] : null;
    }

    internal void OnAuditEdited(AuditRowViewModel row)
    {
        var value = row.SelectedState?.Value;
        if (row.Subcategory is { } subcategory)
        {
            if (CanEditAudit && _audit is { } audit)
            {
                audit.Document.SetSubcategory(subcategory.Guid, subcategory.CsvName, value);
            }
        }
        else if (CanEditTemplate && _template is { } template)
        {
            if (value is { } level)
            {
                template.Document.Set(SecurityTemplateDocument.EventAudit, row.Id, level.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                template.Document.Remove(SecurityTemplateDocument.EventAudit, row.Id);
            }
        }

        AfterEdit();
    }

    /// <summary>0–3 of a legacy category, null when not defined (or not a level).</summary>
    private static int? Level(string? raw) =>
        int.TryParse(raw?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 3 ? value : null;
}
