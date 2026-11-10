using System.Globalization;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Pki;

/// <summary>The status of a row of <i>Gotowość na silne mapowanie i NTAuth</i> (<c>PkiReady.Status.&lt;status&gt;</c>).</summary>
public enum PkiReadinessStatus
{
    Ok,

    /// <summary>Works today, may stop working under full enforcement or soon (<i>ostrzeżenie</i>).</summary>
    Warning,

    /// <summary>Does not work, or will not under full enforcement (<i>problem</i>).</summary>
    Problem,

    /// <summary>Not checked: the probe was not run, not available, the data could not be read, or a later stage (<i>nie sprawdzono</i>).</summary>
    NotChecked,
}

/// <summary>The areas of the readiness report, in the order shown.</summary>
public enum PkiReadinessArea
{
    /// <summary>Accounts with weak <c>altSecurityIdentities</c> (the expansion's mapped accounts, Tier 0 first).</summary>
    Mappings,

    /// <summary>Published authentication templates whose certificates carry no SID extension.</summary>
    AuthenticationTemplates,

    /// <summary>Enterprise CAs that publish logon templates and are not in NTAuth.</summary>
    NtAuth,

    /// <summary>The DC LDAPS certificates (the TLS probe, on the operator's button only).</summary>
    DomainControllerLdaps,

    /// <summary>The KDC certificates published on the DC objects.</summary>
    KdcCertificates,

    /// <summary>The KDC events 39/40/41/45/21 (P5).</summary>
    KdcEvents,
}

/// <summary>One row of the readiness report: status, item, what was found, what to do, an optional navigation.</summary>
public sealed class PkiReadinessRowViewModel
{
    public required PkiReadinessStatus Status { get; init; }

    public required string StatusText { get; init; }

    /// <summary>The account, template, CA or DC the row is about (or a summary line).</summary>
    public required string Title { get; init; }

    /// <summary>What was found.</summary>
    public required string Detail { get; init; }

    /// <summary>"Co zrobić: …", empty when there is nothing to do.</summary>
    public string RemedyText { get; init; } = string.Empty;

    public bool HasRemedy => RemedyText.Length > 0;

    /// <summary>The navigation of the row (<i>Otwórz konto / szablon / urząd</i>), null when none.</summary>
    public IRelayCommand? OpenCommand { get; init; }

    public string OpenText { get; init; } = string.Empty;

    public bool HasOpen => OpenCommand is not null;

    /// <summary>The row as one line of text (copy): status, item, what was found, what to do.</summary>
    public string ToText() => string.Join(" ", new[] { $"[{StatusText}]", Title + ":", Detail, RemedyText }.Where(s => s.Length > 0));

    /// <summary>The status only (no directory values).</summary>
    public override string ToString() => $"{nameof(PkiReadinessRowViewModel)}({Status})";
}

/// <summary>An area of the report with its rows (at most <see cref="PkiReadinessDocumentViewModel.RowsShown"/> shown) and the worst status.</summary>
public sealed class PkiReadinessGroupViewModel
{
    public PkiReadinessGroupViewModel(PkiReadinessArea area, string title, string description, IReadOnlyList<PkiReadinessRowViewModel> rows, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(localizer);
        Area = area;
        Title = title;
        Description = description;
        AllRows = rows;
        Status = rows.Count == 0 ? PkiReadinessStatus.NotChecked : rows.Select(r => r.Status).MaxBy(PkiReadinessDocumentViewModel.Rank);
        StatusText = localizer[PkiReadinessDocumentViewModel.StatusKey(Status)];
        SummaryText = PkiReadinessDocumentViewModel.Summary(localizer, rows.Select(r => r.Status));
        Rows = rows.Count <= PkiReadinessDocumentViewModel.RowsShown ? rows
            : [.. rows.Take(PkiReadinessDocumentViewModel.RowsShown), new PkiReadinessRowViewModel
            {
                Status = PkiReadinessStatus.NotChecked,
                StatusText = localizer[PkiReadinessDocumentViewModel.StatusKey(PkiReadinessStatus.NotChecked)],
                Title = localizer.Format(L.PkiReadiness.MoreRows, (rows.Count - PkiReadinessDocumentViewModel.RowsShown).ToString("N0", localizer.Culture)),
                Detail = localizer[L.PkiReadiness.MoreRowsDetail],
            }];
    }

    public PkiReadinessArea Area { get; }

    public string Title { get; }

    /// <summary>What the area checks and why (one sentence).</summary>
    public string Description { get; }

    public bool HasDescription => Description.Length > 0;

    /// <summary>The rows shown (a long list ends with "i jeszcze N").</summary>
    public IReadOnlyList<PkiReadinessRowViewModel> Rows { get; }

    /// <summary>Every row (the copy and the export).</summary>
    public IReadOnlyList<PkiReadinessRowViewModel> AllRows { get; }

    /// <summary>The worst status of the rows (problem, warning, not checked, OK).</summary>
    public PkiReadinessStatus Status { get; }

    public string StatusText { get; }

    /// <summary>"OK: 4 · Ostrzeżenia: 1" (only the statuses that occur).</summary>
    public string SummaryText { get; }

    /// <summary>The area and status only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Area}: {Status} ({AllRows.Count} rows)");
}
