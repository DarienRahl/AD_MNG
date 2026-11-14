using System.Text;
using AdMng.Core.GroupPolicy.Cleanup;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Search;
using AdMng.Presentation.GroupPolicy.Search;
using AdMng.Presentation.GroupPolicy.Settings;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.GroupPolicy.Cleanup;

/// <summary>
/// One proposal of "Porządki w GPO" as the list and the details pane show it: the kind, the GPO, the related GPOs, the
/// setting in GPMC's words, why and what to do, the notes and the actions it offers (an informational proposal offers
/// none). Texts in the UI language; directory values in memory only (CLAUDE.md rule 1).
/// </summary>
public sealed class GpoCleanupRowViewModel
{
    public required GpoCleanupProposal Proposal { get; init; }

    public GpoCleanupKind Kind => Proposal.Kind;

    public Guid GpoId => Proposal.GpoId;

    /// <summary>The index entry of the GPO (null when the GPO is no longer in the index).</summary>
    public GpoIndexEntry? Entry { get; init; }

    /// <summary>The index entries of <see cref="GpoCleanupProposal.RelatedGpoIds"/> still in the index (in their order).</summary>
    public IReadOnlyList<GpoIndexEntry> Related { get; init; } = [];

    /// <summary>"Pusty obiekt GPO".</summary>
    public required string Title { get; init; }

    public required string GpoName { get; init; }

    /// <summary>The related GPOs' names, comma-separated (empty when none).</summary>
    public required string RelatedText { get; init; }

    /// <summary>"Powiązane: …" (empty when none).</summary>
    public string RelatedLine { get; init; } = string.Empty;

    /// <summary>The setting's name in GPMC's words (null: the proposal is about the whole GPO).</summary>
    public string? SettingName { get; init; }

    /// <summary>"Konfiguracja komputera › Zasady › …" of the setting (null with <see cref="SettingName"/>).</summary>
    public string? SettingPath { get; init; }

    /// <summary>The setting's value in the GPO (null with <see cref="SettingName"/>).</summary>
    public string? SettingValue { get; init; }

    /// <summary>What was found, with the GPO, related GPOs and setting.</summary>
    public required string Message { get; init; }

    /// <summary>What to do.</summary>
    public required string Recommendation { get; init; }

    /// <summary>The proposal's remarks (sorted as the analyzer gives them).</summary>
    public required IReadOnlyList<string> NoteTexts { get; init; }

    /// <summary>"Połączony z: …" or "Brak połączeń".</summary>
    public required string LinksText { get; init; }

    /// <summary>"Nie można skopiować: …" (null when no unit blocks).</summary>
    public string? BlockingText { get; init; }

    /// <summary>The actions in the UI language ("Usuń GPO…, Odłącz wszędzie…"), empty for an informational proposal.</summary>
    public required string ActionsText { get; init; }

    public string Key => Kind + "|" + GpoId.ToString("N") + "|" + Proposal.UnitBaseId + "|" + string.Join(",", Proposal.RelatedGpoIds);

    /// <summary>The text the search box matches (kind, GPOs, setting, message, notes).</summary>
    public string SearchText => string.Join(" ", new[] { Title, GpoName, RelatedText, SettingName, SettingPath, Message, string.Join(" ", NoteTexts) }
        .Where(s => !string.IsNullOrEmpty(s)));

    public bool HasNotes => NoteTexts.Count > 0;

    public bool HasSetting => SettingName is not null;

    public bool HasRelated => Related.Count > 0;

    public bool HasActions => Proposal.Actions.Count > 0;

    public bool HasBlocking => BlockingText is not null;

    /// <summary>The GPO is in the index ("Otwórz GPO", the actions).</summary>
    public bool GpoExists => Entry is not null;

    public bool Offers(GpoCleanupAction action) => GpoExists && Proposal.Actions.Contains(action);

    /// <summary>The details as the "Kopiuj" text.</summary>
    public string DetailsText(string why, string what, string notes)
    {
        var text = new StringBuilder();
        text.AppendLine(Title).AppendLine(GpoName);
        if (RelatedText.Length > 0)
        {
            text.AppendLine(RelatedText);
        }

        if (SettingName is not null)
        {
            text.AppendLine(SettingPath + GpoSettingText.Separator + SettingName);
        }

        text.AppendLine().AppendLine(why).AppendLine(Message).AppendLine().AppendLine(what).AppendLine(Recommendation);
        if (NoteTexts.Count > 0)
        {
            text.AppendLine().AppendLine(notes);
            foreach (var note in NoteTexts)
            {
                text.Append("• ").AppendLine(note);
            }
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>For diagnostics: the kind and the GUID only.</summary>
    public override string ToString() => Proposal.ToString();

    /// <summary>The row of <paramref name="proposal"/> (texts of the index view <paramref name="texts"/>).</summary>
    internal static GpoCleanupRowViewModel Create(GpoCleanupProposal proposal, GpoIndexTexts texts)
    {
        var localizer = texts.Localizer;
        var index = texts.View.Index;
        var entry = index.Find(proposal.GpoId);
        var related = proposal.RelatedGpoIds.Select(index.Find).OfType<GpoIndexEntry>().ToList();
        var gpoName = entry?.Gpo.DisplayName ?? proposal.GpoId.ToString("B");
        var relatedText = string.Join(", ", related.Select(e => e.Gpo.DisplayName));
        GpoSettingText? setting = null;
        if (proposal.UnitBaseId is { } baseId && entry?.Units.FirstOrDefault(u => string.Equals(u.BaseId, baseId, StringComparison.OrdinalIgnoreCase)) is { } unit)
        {
            setting = texts.Describe(entry, unit);
        }

        var settingName = setting?.Name ?? string.Empty;
        var links = entry?.Links.Select(l => l.ContainerName).Distinct().ToList() ?? [];
        List<string> blocking = entry is null ? [] : proposal.BlockingUnitIds
            .Select(id => entry.Units.FirstOrDefault(u => string.Equals(u.Id, id, StringComparison.OrdinalIgnoreCase)))
            .OfType<GpoSettingUnit>()
            .Select(u => texts.Describe(entry, u).Name)
            .ToList();
        return new GpoCleanupRowViewModel
        {
            Proposal = proposal,
            Entry = entry,
            Related = related,
            Title = localizer[L.GpoCleanup.KindTitleKey(proposal.Kind)],
            GpoName = gpoName,
            RelatedText = relatedText,
            RelatedLine = relatedText.Length == 0 ? string.Empty : localizer.Format(L.GpoCleanup.RelatedLine, relatedText),
            SettingName = setting?.Name,
            SettingPath = setting?.GroupPath,
            SettingValue = setting?.ValueText,
            Message = localizer.Format(L.GpoCleanup.KindMessageKey(proposal.Kind), gpoName, relatedText, settingName),
            Recommendation = localizer.Format(L.GpoCleanup.KindRecommendationKey(proposal.Kind), gpoName, relatedText, settingName),
            NoteTexts = [.. proposal.Notes.Select(n => localizer[L.GpoCleanup.NoteKey(n)])],
            LinksText = links.Count == 0 ? localizer[L.GpoCleanup.NoLinks] : localizer.Format(L.GpoCleanup.LinksLine, string.Join(", ", links)),
            BlockingText = blocking.Count == 0 ? null : localizer.Format(L.GpoCleanup.BlockingLine, string.Join(", ", blocking)),
            ActionsText = string.Join(", ", proposal.Actions.Select(a => localizer[L.GpoCleanup.ActionKey(a)])),
        };
    }

    /// <summary>What the advisor saw of the GPO (its delete is refused when the GPO moved since).</summary>
    internal GpoExpectedState? ExpectedState() => Entry is { } entry
        ? GpoExpectedState.Of(entry.Gpo, entry.Links)
        : null;
}
