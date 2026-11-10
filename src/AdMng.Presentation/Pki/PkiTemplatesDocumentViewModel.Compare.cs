using System.Collections.ObjectModel;
using System.Text;
using AdMng.Core.Pki;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Pki;

/// <summary>The compare pane (two templates side by side, inside the document), <i>Kopiuj szczegóły</i> and the exports.</summary>
public sealed partial class PkiTemplatesDocumentViewModel
{
    // ---- Compare ----------------------------------------------------------------------------------------------------------

    /// <summary>The compare pane is open.</summary>
    [ObservableProperty]
    public partial bool IsComparing { get; private set; }

    /// <summary>The template on the left (the selected one when the pane opened).</summary>
    [ObservableProperty]
    public partial PkiTemplateRowViewModel? CompareLeft { get; private set; }

    /// <summary>The template on the right (the second selected row, or the one picked in the pane).</summary>
    [ObservableProperty]
    public partial PkiTemplateRowViewModel? CompareRight { get; set; }

    /// <summary>The templates the right side can be (every template but the left one).</summary>
    public ObservableCollection<PkiTemplateRowViewModel> CompareCandidates { get; } = [];

    /// <summary><i>Tylko różnice</i>.</summary>
    [ObservableProperty]
    public partial bool CompareOnlyDifferences { get; set; }

    /// <summary>The compared properties (all, or the differing ones with <see cref="CompareOnlyDifferences"/>).</summary>
    public ObservableCollection<PkiCompareRowViewModel> CompareRows { get; } = [];

    /// <summary>"Różnice: 4" or "Szablony nie różnią się…".</summary>
    [ObservableProperty]
    public partial string CompareSummary { get; private set; } = string.Empty;

    public string CompareHint => Localizer[L.PkiTemplates.CompareHint];

    /// <summary><i>Porównaj…</i>: the two selected templates, else the selected one with a template picked in the pane.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Compare()
    {
        var (left, right) = SelectedRows is [var first, var second] ? (first, second) : (SelectedRow, (PkiTemplateRowViewModel?)null);
        if (left is null)
        {
            return;
        }

        CompareLeft = left;
        CompareCandidates.Clear();
        foreach (var row in _all.Where(r => !ReferenceEquals(r, left)))
        {
            CompareCandidates.Add(row);
        }

        CompareRight = right ?? CompareCandidates.FirstOrDefault();
        IsComparing = true;
        RefreshCompare();
    }

    /// <summary><i>Zamknij porównanie</i>.</summary>
    [RelayCommand]
    private void CloseCompare()
    {
        IsComparing = false;
        CompareLeft = null;
        CompareRight = null;
        CompareCandidates.Clear();
        CompareRows.Clear();
        CompareSummary = string.Empty;
    }

    partial void OnCompareRightChanged(PkiTemplateRowViewModel? value) => RefreshCompare();

    partial void OnCompareOnlyDifferencesChanged(bool value) => RefreshCompare();

    /// <summary>The pane's rows from the shown rows (a reload or the expansion keeps the pair by cn).</summary>
    private void RefreshCompare()
    {
        if (!IsComparing)
        {
            return;
        }

        CompareLeft = Same(CompareLeft);
        var right = Same(CompareRight);
        if (!ReferenceEquals(right, CompareRight))
        {
            CompareRight = right;
            return;
        }

        CompareRows.Clear();
        if (CompareLeft is not { } left || right is null)
        {
            CompareSummary = string.Empty;
            return;
        }

        var all = CompareProperties(left, right);
        var differences = all.Count(r => r.IsDifferent);
        foreach (var row in CompareOnlyDifferences ? all.Where(r => r.IsDifferent) : all)
        {
            CompareRows.Add(row);
        }

        CompareSummary = differences == 0 ? Localizer[L.PkiTemplates.CompareSame] : Localizer.Format(L.PkiTemplates.CompareDifferences, differences);
    }

    private PkiTemplateRowViewModel? Same(PkiTemplateRowViewModel? row) =>
        row is null ? null : _all.FirstOrDefault(r => string.Equals(r.Cn, row.Cn, StringComparison.OrdinalIgnoreCase));

    /// <summary>The README columns, the flags of each attribute, the policies and the rights of both templates.</summary>
    private List<PkiCompareRowViewModel> CompareProperties(PkiTemplateRowViewModel left, PkiTemplateRowViewModel right)
    {
        var rows = new List<PkiCompareRowViewModel>();
        void Add(string labelKey, Func<PkiTemplateRowViewModel, string> value) => rows.Add(new(Localizer[labelKey], value(left), value(right)));
        void AddText(string label, Func<PkiTemplateRowViewModel, string> value) => rows.Add(new(label, value(left), value(right)));

        Add(L.PkiTemplates.ColumnName, r => r.DisplayName);
        Add(L.PkiTemplates.ColumnCn, r => r.Cn);
        Add(L.PkiTemplates.Oid, r => r.Template.Oid ?? string.Empty);
        Add(L.PkiTemplates.ColumnSchema, r => r.SchemaText);
        Add(L.PkiTemplates.ColumnVersion, r => r.VersionText);
        Add(L.PkiTemplates.ColumnPurpose, r => r.PurposeText);
        Add(L.PkiTemplates.Ekus, r => PkiDisplay.List(Localizer, r.Template.Ekus.Select(PkiDisplay.Purpose)));
        Add(L.PkiTemplates.ColumnValidity, r => r.ValidityText);
        Add(L.PkiTemplates.ColumnRenewal, r => r.RenewalText);
        Add(L.PkiTemplates.ColumnKey, r => r.KeyText);
        Add(L.PkiTemplates.ColumnSubject, r => r.SubjectText);
        Add(L.PkiTemplates.ColumnApproval, r => r.ApprovalText);
        Add(L.PkiTemplates.ColumnSignatures, r => r.SignaturesText);
        Add(L.PkiTemplates.RaPolicies, r => PkiDisplay.List(Localizer, r.Template.RaApplicationPolicies.Concat(r.Template.RaIssuancePolicies).Select(PkiDisplay.Purpose)));
        Add(L.PkiTemplates.ColumnPublish, r => r.PublishText);
        Add(L.PkiTemplates.ColumnSuperseded, r => r.SupersededText);
        Add(L.PkiTemplates.ColumnIssuancePolicies, r => r.IssuancePoliciesText);
        Add(L.PkiTemplates.ColumnCas, r => r.CasText);
        foreach (var attribute in Enum.GetValues<PkiFlagAttribute>())
        {
            AddText(Localizer[L.PkiTemplates.FlagGroupKey(attribute)], r => FlagText(r.Template, attribute));
        }

        Add(L.PkiTemplates.EnrollHeading, r => RightsText(r, enroll: true));
        Add(L.PkiTemplates.ModifyHeading, r => RightsText(r, enroll: false));
        return rows;
    }

    /// <summary>The set flags of one attribute as their sentences (one per line).</summary>
    private string FlagText(CertificateTemplate template, PkiFlagAttribute attribute)
    {
        var decoded = template.DecodeFlags(attribute);
        var lines = decoded.Set.Select(e => Localizer[L.PkiTemplates.FlagKey(attribute, e.Name)]).ToList();
        lines.AddRange(decoded.Fields.Where(f => f.Value != 0).Select(f => Localizer.Format(L.PkiTemplates.FieldKey(f.Field.Name), f.Value)));
        if (decoded.UnknownBitNames.Count > 0)
        {
            lines.Add(Localizer.Format(L.PkiTemplates.UnknownBits, string.Join(", ", decoded.UnknownBitNames)));
        }

        return lines.Count == 0 ? Localizer[L.PkiTemplates.NoFlags] : string.Join("\n", lines);
    }

    /// <summary>The trustees of the enroll (or modify) rights, "right: name" per line (the counts when expanded).</summary>
    private string RightsText(PkiTemplateRowViewModel row, bool enroll)
    {
        if (row.Rights is not { IsEvaluated: true } rights)
        {
            return Localizer[L.PkiTemplates.NotEvaluated];
        }

        var lines = rights.Trustees
            .Where(t => IsEnrollRight(t.Right) == enroll)
            .Select(t => Localizer.Format(L.PkiTemplates.RightOnAttribute, Localizer[PkiTextKeys.RightKey(t.Right)], TrusteeName(t)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return lines.Count == 0 ? Localizer[L.PkiTemplates.Nobody] : string.Join("\n", lines);
    }

    // ---- Copy ---------------------------------------------------------------------------------------------------------------

    /// <summary><i>Kopiuj szczegóły</i>: the selected template's properties, flags and rights as text (the clipboard, on request).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyDetails()
    {
        if (SelectedRow is { } row && Pki.Ui.Clipboard.SetText(DetailsText(row)))
        {
            Pki.Ui.Notifications.Show(NotificationSeverity.Info, Localizer[L.PkiTemplates.Copied]);
        }
    }

    /// <summary>The details of a template as the <i>Kopiuj szczegóły</i> text.</summary>
    public string DetailsText(PkiTemplateRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var text = new StringBuilder();
        text.AppendLine(row.DisplayName);
        foreach (var line in CompareProperties(row, row))
        {
            text.Append(line.Property).Append(": ").AppendLine(line.Left.Replace("\n", "; ", StringComparison.Ordinal));
        }

        return text.ToString();
    }

    // ---- Export -------------------------------------------------------------------------------------------------------------

    /// <summary><i>Eksportuj do CSV…</i>: the templates the list shows (with the filters) to a file the operator picks (rule 1).</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportCsvAsync()
    {
        var title = Localizer[L.PkiTemplates.ExportCsv];
        var path = await Pki.Ui.Files.SaveFileAsync(title, SuggestedFileName(".csv"), [new FileFilter(Localizer[L.Files.CsvFilter], "*.csv")])
            .ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        var rows = Rows.Select(r => (IReadOnlyList<string>)[.. ExportValues(r)]).ToList();
        var headers = ExportHeaders().Select(k => Localizer[k]).ToList();
        var delimiter = CsvFiles.ListSeparator(Pki.Ui.Regional.Culture);
        await WriteAsync(title, path, rows.Count, writer => Pki.Csv.Write(writer, headers, rows, delimiter, protectFormulas: true)).ConfigureAwait(true);
    }

    /// <summary><i>Eksportuj do HTML…</i>: a self-contained page (CSP <c>default-src 'none'</c>, every value escaped, a privacy footer).</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportHtmlAsync()
    {
        var title = Localizer[L.PkiTemplates.ExportHtml];
        var path = await Pki.Ui.Files.SaveFileAsync(title, SuggestedFileName(".html"), [new FileFilter(Localizer[L.PkiTemplates.HtmlFilter], "*.html")])
            .ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        var html = HtmlReport(Rows);
        await WriteAsync(title, path, Rows.Count, writer => writer.Write(html)).ConfigureAwait(true);
    }

    private bool CanExport() => _snapshot is not null && Rows.Count > 0;

    private void RefreshExportCommands()
    {
        ExportCsvCommand.NotifyCanExecuteChanged();
        ExportHtmlCommand.NotifyCanExecuteChanged();
    }

    private static string[] ExportHeaders() =>
    [
        L.PkiTemplates.ColumnName, L.PkiTemplates.ColumnCn, L.PkiTemplates.ColumnSchema, L.PkiTemplates.ColumnVersion, L.PkiTemplates.ColumnPurpose,
        L.PkiTemplates.ColumnValidity, L.PkiTemplates.ColumnRenewal, L.PkiTemplates.ColumnKey, L.PkiTemplates.ColumnSubject, L.PkiTemplates.ColumnApproval,
        L.PkiTemplates.ColumnSignatures, L.PkiTemplates.ColumnPublish, L.PkiTemplates.ColumnSuperseded, L.PkiTemplates.ColumnIssuancePolicies,
        L.PkiTemplates.ColumnCas, L.PkiTemplates.ColumnEnroll,
    ];

    private static IEnumerable<string> ExportValues(PkiTemplateRowViewModel r) =>
    [
        r.DisplayName, r.Cn, r.SchemaText, r.VersionText, r.PurposeText, r.ValidityText, r.RenewalText, r.KeyText, r.SubjectText, r.ApprovalText,
        r.SignaturesText, r.PublishText, r.SupersededText, r.IssuancePoliciesText, r.CasText, r.EnrollText,
    ];

    private async Task WriteAsync(string title, string path, int count, Action<TextWriter> write)
    {
        try
        {
            await CsvFiles.WriteAsync(path, write).ConfigureAwait(true);
            Pki.Ui.Notifications.Show(NotificationSeverity.Success, Localizer.Format(L.Files.Exported, count, Path.GetFileName(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Pki.Ui.Dialogs.ShowErrorAsync(title, Pki.Errors.Translate(ex)).ConfigureAwait(true);
        }
    }

    private string SuggestedFileName(string extension)
    {
        var domain = Pki.Sessions.Current?.Info.DomainDnsName ?? string.Empty;
        var name = ListExportSnapshot.SuggestFileName(Localizer.Format(L.PkiTemplates.ReportTitle, domain), Pki.Time.GetLocalNow());
        return Path.ChangeExtension(name, extension);
    }

    /// <summary>The HTML report of <paramref name="rows"/>: one self-contained page, every text HTML-encoded, no scripts or external resources.</summary>
    internal string HtmlReport(IReadOnlyList<PkiTemplateRowViewModel> rows)
    {
        var domain = Pki.Sessions.Current?.Info.DomainDnsName ?? string.Empty;
        var title = Localizer.Format(L.PkiTemplates.ReportTitle, domain);
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\n<html lang=\"").Append(E(Localizer.Culture.TwoLetterISOLanguageName)).Append("\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">\n")
            .Append("<title>").Append(E(title)).Append("</title>\n<style>\n")
            .Append("body{font-family:'Segoe UI',sans-serif;margin:24px;color:#1b1b1b}h1{font-size:20px;margin:0 0 4px}")
            .Append(".sub{color:#5f5f5f;margin:0 0 16px}table{border-collapse:collapse;width:100%}")
            .Append("th,td{border:1px solid #d1d1d1;padding:6px 8px;text-align:left;vertical-align:top}th{background:#f3f3f3}")
            .Append(".broad{color:#9d2a00;font-weight:600}\n</style>\n</head>\n<body>\n");
        html.Append("<h1>").Append(E(title)).Append("</h1>\n<p class=\"sub\">")
            .Append(E(Localizer.Format(L.PkiTemplates.ReportSubtitle, _server, PkiDisplay.Date(Localizer, _snapshot?.ReadAt))))
            .Append("<br>").Append(E(SummaryText));
        if (rows.Count != _all.Count)
        {
            html.Append("<br>").Append(E(Localizer.Format(L.PkiTemplates.ReportFiltered, rows.Count, _all.Count)));
        }

        html.Append("</p>\n<table>\n<thead><tr>");
        foreach (var header in ExportHeaders())
        {
            html.Append("<th>").Append(E(Localizer[header])).Append("</th>");
        }

        html.Append("</tr></thead>\n<tbody>\n");
        foreach (var row in rows)
        {
            html.Append("<tr>");
            var values = ExportValues(row).ToList();
            for (var i = 0; i < values.Count; i++)
            {
                var broad = i == values.Count - 1 && row.IsBroadEnroll;
                html.Append(broad ? "<td class=\"broad\">" : "<td>").Append(E(values[i])).Append("</td>");
            }

            html.Append("</tr>\n");
        }

        html.Append("</tbody>\n</table>\n<p class=\"sub\">").Append(E(Localizer[L.PkiTemplates.ReportPrivacy])).Append("</p>\n</body>\n</html>\n");
        return html.ToString();
    }

    /// <summary>Text for HTML content and attribute values: <c>&amp; &lt; &gt; " '</c> escaped, control characters dropped, everything else (Polish letters) as is.</summary>
    internal static string E(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var html = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            _ = c switch
            {
                '&' => html.Append("&amp;"),
                '<' => html.Append("&lt;"),
                '>' => html.Append("&gt;"),
                '"' => html.Append("&quot;"),
                '\'' => html.Append("&#39;"),
                _ => html.Append(char.IsControl(c) && c is not ('\n' or '\t') ? ' ' : c),
            };
        }

        return html.ToString();
    }
}
