using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Readiness;

/// <summary>One check as the document shows it: the status, the title (with the host of a per-host check), what was found, what to do and the chapter to read.</summary>
public sealed class ReadinessRowViewModel
{
    public ReadinessRowViewModel(ReadinessResult result, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(localizer);
        Result = result;
        StatusText = localizer[ReadinessText.StatusKey(result.Status)];
        RemedyText = result.Remedy.Length == 0 ? string.Empty : $"{localizer[L.Readiness.RemedyHeading]} {result.Remedy}";
        DocumentationText = result.Documentation.Length == 0 ? string.Empty : $"{localizer[L.Readiness.DocumentationHeading]} {result.Documentation}";
    }

    public ReadinessResult Result { get; }

    public string Code => Result.Code;

    public ReadinessStatus Status => Result.Status;

    public string StatusText { get; }

    public string Title => Result.Title;

    /// <summary>The DC or computer of a per-host check, empty otherwise.</summary>
    public string Host => Result.Host ?? string.Empty;

    public bool HasHost => Result.Host is not null;

    public string Explanation => Result.Explanation;

    /// <summary>"Co zrobić: …", empty when there is nothing to do.</summary>
    public string RemedyText { get; }

    public bool HasRemedy => RemedyText.Length > 0;

    /// <summary>"Przeczytaj: DZIENNIKI-ZDARZEN.md, rozdział 2 „Wymagania”".</summary>
    public string DocumentationText { get; }

    /// <summary>The chapter is shown for a check that is not OK (an OK row needs no reading).</summary>
    public bool ShowsDocumentation => Status != ReadinessStatus.Ok && DocumentationText.Length > 0;

    /// <summary>The row as one line of text (copy): status, title, host, result, remedy, chapter.</summary>
    public string ToText() => string.Join(" ", new[]
    {
        $"[{StatusText}]", Title + (HasHost ? $" ({Host})" : string.Empty) + ":", Explanation, RemedyText, ShowsDocumentation ? DocumentationText : string.Empty,
    }.Where(s => s.Length > 0));

    /// <summary>The code and status only.</summary>
    public override string ToString() => Result.ToString();
}

/// <summary>An area of the check with its rows and the worst status among them.</summary>
public sealed class ReadinessGroupViewModel
{
    public ReadinessGroupViewModel(ReadinessArea area, IReadOnlyList<ReadinessRowViewModel> rows, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(localizer);
        Area = area;
        Rows = rows;
        Title = localizer[ReadinessText.AreaKey(area)];
        Status = rows.Count == 0 ? ReadinessStatus.NotChecked : rows.Select(r => r.Status).MaxBy(ReadinessResults.Rank);
        StatusText = localizer[ReadinessText.StatusKey(Status)];
        SummaryText = ReadinessDocumentViewModel.Summary(localizer, rows.Select(r => r.Status));
    }

    public ReadinessArea Area { get; }

    public string Title { get; }

    public IReadOnlyList<ReadinessRowViewModel> Rows { get; }

    /// <summary>The worst status of the rows (Error, Warning, not checked, OK).</summary>
    public ReadinessStatus Status { get; }

    public string StatusText { get; }

    /// <summary>"OK: 4 · Ostrzeżenia: 1" (only the statuses that occur).</summary>
    public string SummaryText { get; }

    public override string ToString() => $"{Area}: {Status}";
}
