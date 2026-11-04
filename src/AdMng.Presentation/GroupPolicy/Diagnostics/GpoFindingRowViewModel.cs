using AdMng.Core.GroupPolicy.Diagnostics;

namespace AdMng.Presentation.GroupPolicy.Diagnostics;

/// <summary>
/// One finding as "Diagnostyka zasad grupy" and the GPO document's "Stan" tab show it: the list columns (severity, area,
/// GPO, container or setting, message) and the details (title, message with its arguments, why it matters, what to do,
/// what it is about, the repair). Texts in the UI language; directory values in memory only (CLAUDE.md rule 1).
/// </summary>
public sealed class GpoFindingRowViewModel
{
    public required GpoFinding Finding { get; init; }

    public GpoFindingSeverity Severity => Finding.Severity;

    public GpoFindingArea Area => Finding.Area;

    public string Code => Finding.Code;

    /// <summary>"Wysoka".</summary>
    public required string SeverityText { get; init; }

    /// <summary>"Bezpieczeństwo".</summary>
    public required string AreaText { get; init; }

    /// <summary>The GPO's name ("–" for a container or WMI filter finding).</summary>
    public required string GpoText { get; init; }

    /// <summary>The setting, else the container, else the WMI filter ("–" when none).</summary>
    public required string WhereText { get; init; }

    /// <summary>"Obiekt GPO nie jest połączony".</summary>
    public required string Title { get; init; }

    /// <summary>The message with the finding's arguments (names instead of SIDs).</summary>
    public required string Message { get; init; }

    /// <summary>Why it matters.</summary>
    public required string Explanation { get; init; }

    /// <summary>What to do.</summary>
    public required string Recommendation { get; init; }

    /// <summary>What the finding is about: the GPO, the container, the other GPOs, the setting (one line each).</summary>
    public required IReadOnlyList<string> AffectedLines { get; init; }

    /// <summary>"Naprawa: Wyrównaj wersje", or "Brak automatycznej naprawy – …".</summary>
    public required string FixText { get; init; }

    /// <summary>The setting the finding is about ("Edytuj to ustawienie"), null when none.</summary>
    public GroupPolicySettingFocus? Focus { get; init; }

    /// <summary>The GPO exists (it can be opened and its settings edited).</summary>
    public bool GpoExists { get; init; }

    /// <summary>The domain head or OU "Pokaż w modelu" models (the finding's container, else the GPO's first link), null when none.</summary>
    public string? ModelContainerDn { get; init; }

    /// <summary>A repair through the change plan is offered ("Napraw…").</summary>
    public bool HasFix => Finding.Fix is not null;

    /// <summary>"Edytuj to ustawienie" is offered: an editor of AD Manager edits the setting of an existing GPO.</summary>
    public bool CanEditSetting => GpoExists && Finding.GpoId is not null && GroupPolicyActions.CanEditSetting(Focus);

    /// <summary>The finding's identity (code, GPO, container, setting, arguments): the selection is kept across a re-check by it.</summary>
    public required string Key { get; init; }

    /// <summary>The texts the search looks in (title, message, GPO, container or setting, area, severity, code).</summary>
    public required string SearchText { get; init; }

    /// <summary>The details as plain text ("Kopiuj").</summary>
    public string DetailsText(string whyHeading, string whatHeading, string affectedHeading) => string.Join(Environment.NewLine,
    [
        $"{SeverityText} · {AreaText} · {Title}",
        Message,
        string.Empty,
        whyHeading,
        Explanation,
        string.Empty,
        whatHeading,
        Recommendation,
        string.Empty,
        affectedHeading,
        .. AffectedLines,
        FixText,
    ]);

    /// <summary>For diagnostics: the code and severity only (no directory values).</summary>
    public override string ToString() => Finding.ToString();
}
