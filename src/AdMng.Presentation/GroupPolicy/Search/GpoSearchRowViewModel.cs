using System.Text;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Search;
using AdMng.Presentation.GroupPolicy.Copy;
using AdMng.Presentation.GroupPolicy.Settings;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.GroupPolicy.Search;

/// <summary>
/// One row of "Wyszukiwarka ustawień GPO": the setting as one GPO sets it – the GPO, the value (masked values stay hidden),
/// where the GPO is linked and the marks (conditional, disabled item, hidden value, GPO read in part). In memory only;
/// <see cref="ToString"/> prints the unit's kind and the GPO GUID only.
/// </summary>
public sealed class GpoSearchRowViewModel
{
    internal GpoSearchRowViewModel(ILocalizer localizer, GpoSearchGroupViewModel group, GpoSearchRecord record)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        Group = group ?? throw new ArgumentNullException(nameof(group));
        Record = record ?? throw new ArgumentNullException(nameof(record));
        var entry = record.Entry;
        LinksText = entry.Links.Count == 0
            ? localizer[L.GroupPolicy.NotLinked]
            : string.Join(", ", entry.Links.OrderBy(l => l.Kind).ThenBy(l => l.ContainerName, StringComparer.Create(localizer.Culture, ignoreCase: true))
                .Select(l => l.Enabled ? l.ContainerName : localizer.Format(L.GpoSearch.LinkDisabled, l.ContainerName)));
        var marks = new List<string>();
        if (record.Unit.Comparability == GpoUnitComparability.Conditional)
        {
            marks.Add(localizer[L.GpoSearch.MarkConditional]);
        }

        if (record.Unit.Disabled)
        {
            marks.Add(localizer[L.GpoSearch.MarkDisabled]);
        }

        if (record.IsMasked)
        {
            marks.Add(localizer[L.GpoSearch.MarkMasked]);
        }

        if (entry.Status == GpoIndexStatus.Incomplete)
        {
            marks.Add(localizer[L.GpoSearch.MarkIncomplete]);
        }

        Marks = marks;
        MarksText = string.Join(" · ", marks);
        RefusalText = record.Unit.Refusal is { } refusal ? GpoUnitRefusalText.Text(localizer, refusal) : null;
        CanEdit = GroupPolicyActions.CanEditSetting(record.Text.Focus);
    }

    /// <summary>The group (the setting) the row belongs to – the list groups the rows by it.</summary>
    public GpoSearchGroupViewModel Group { get; }

    /// <summary>The occurrence behind the row.</summary>
    public GpoSearchRecord Record { get; }

    public GpoInfo Gpo => Record.Entry.Gpo;

    public Guid GpoId => Record.Entry.Gpo.Id;

    public string GpoName => Record.Entry.Gpo.DisplayName;

    public GpoSettingUnit Unit => Record.Unit;

    /// <summary>What the GPO's presenter makes of the setting.</summary>
    public GpoSettingText Text => Record.Text;

    public string Name => Record.Text.Name;

    public string GroupPath => Record.Text.GroupPath;

    /// <summary>The value in GPMC's words ("(ustawione – ukryte)" for a secret).</summary>
    public string ValueText => Record.Text.ValueText;

    /// <summary>The parts of the value, one per line (list elements, a policy's options, a preference item's properties).</summary>
    public IReadOnlyList<string> Elements => Record.Text.Elements;

    public bool HasElements => Record.Text.Elements.Count > 0;

    /// <summary>The key behind the setting (tool tip, export): never a value.</summary>
    public string Technical => Record.Text.Technical;

    /// <summary>"Firma, Komputery (wyłączone)" or "(niepołączone)".</summary>
    public string LinksText { get; }

    public IReadOnlyList<string> Marks { get; }

    /// <summary>"warunkowe · element wyłączony" (empty without marks).</summary>
    public string MarksText { get; }

    public bool HasMarks => Marks.Count > 0;

    /// <summary>Why the setting cannot be copied or removed (null: it can).</summary>
    public string? RefusalText { get; }

    public bool IsMasked => Record.IsMasked;

    /// <summary>An editor of AD Manager edits this setting ("Edytuj to ustawienie").</summary>
    public bool CanEdit { get; }

    /// <summary>The item the copy window shows for this occurrence (selection with the unit's identity and index-time value).</summary>
    public GpoCopyItem CopyItem => new(new GpoUnitSelection(GpoId, Unit.Id, Unit.ItemId, Unit.ValueFingerprint), GpoName, Name, GroupPath, ValueText, Unit.Refusal);

    /// <summary>"GPO: name = value", the parts and the path – one occurrence of "Kopiuj".</summary>
    public string CopyText
    {
        get
        {
            var text = new StringBuilder();
            text.Append(GpoName).Append(": ").Append(Name).Append(" = ").Append(ValueText);
            foreach (var element in Elements.Where(e => !string.Equals(e, ValueText, StringComparison.Ordinal)))
            {
                text.AppendLine().Append("  ").Append(element);
            }

            text.AppendLine().Append("  ").Append(GroupPath);
            return text.ToString();
        }
    }

    /// <summary>For diagnostics: the kind and the GPO GUID only (names and values are GPO content).</summary>
    public override string ToString() => Unit + " " + SysvolPaths.FolderName(GpoId);
}
