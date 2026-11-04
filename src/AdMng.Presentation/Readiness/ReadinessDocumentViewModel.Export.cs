using System.Text;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Readiness;

/// <summary>
/// The exports of the readiness check: CSV and a self-contained HTML report into a file the operator picks (rule 1: the only
/// way results reach the disk). They name the domain, the DCs and the test computer – the report says so, and so does the
/// confirmation.
/// </summary>
public sealed partial class ReadinessDocumentViewModel
{
    /// <summary>"Eksportuj do CSV…": every row (area, check, status, host, result, remedy, chapter, code).</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportCsvAsync()
    {
        var title = Localizer[L.Readiness.ExportCsv];
        var path = await _services.Ui.Files.SaveFileAsync(title, SuggestedFileName(".csv"), [new FileFilter(Localizer[L.Files.CsvFilter], "*.csv")])
            .ConfigureAwait(true);
        if (path is null || _report is null)
        {
            return;
        }

        var headers = new[]
        {
            L.Readiness.ColumnArea, L.Readiness.ColumnCheck, L.Readiness.ColumnStatus, L.Readiness.ColumnHost, L.Readiness.ColumnResult,
            L.Readiness.ColumnRemedy, L.Readiness.ColumnDocumentation,
        }.Select(k => Localizer[k]).Append("Code").ToList();
        var rows = CsvRows();
        var delimiter = CsvFiles.ListSeparator(_services.Ui.Regional.Culture);
        await WriteAsync(title, path, rows.Count, writer => _services.Csv.Write(writer, headers, rows, delimiter, protectFormulas: true)).ConfigureAwait(true);
    }

    /// <summary>The rows of the CSV export, in check order.</summary>
    internal List<IReadOnlyList<string>> CsvRows() =>
    [
        .. Groups.SelectMany(g => g.Rows.Select(r => (IReadOnlyList<string>)
            [g.Title, r.Title, r.StatusText, r.Host, r.Explanation, r.Result.Remedy, r.Result.Documentation, r.Code])),
    ];

    /// <summary>"Eksportuj do HTML…": one self-contained page (every value escaped, no scripts, no external resources).</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportHtmlAsync()
    {
        var title = Localizer[L.Readiness.ExportHtml];
        var path = await _services.Ui.Files.SaveFileAsync(title, SuggestedFileName(".html"), [new FileFilter(Localizer[L.Readiness.HtmlFilter], "*.html")])
            .ConfigureAwait(true);
        if (path is null || _report is null)
        {
            return;
        }

        var html = HtmlReport();
        await WriteAsync(title, path, Groups.Sum(g => g.Rows.Count), writer => writer.Write(html)).ConfigureAwait(true);
    }

    private async Task WriteAsync(string title, string path, int count, Action<TextWriter> write)
    {
        try
        {
            await CsvFiles.WriteAsync(path, write).ConfigureAwait(true);
            _services.Ui.Notifications.Show(NotificationSeverity.Success,
                Localizer.Format(L.Files.Exported, count, Path.GetFileName(path)) + " " + Localizer[L.Readiness.ExportNote]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _services.Ui.Dialogs.ShowErrorAsync(title, _services.Ui.Errors.Translate(ex)).ConfigureAwait(true);
        }
    }

    private string SuggestedFileName(string extension)
    {
        var domain = _services.Ui.Sessions.Current?.Info.DomainDnsName ?? string.Empty;
        var name = ListExportSnapshot.SuggestFileName(Localizer.Format(L.Readiness.ReportTitle, domain), _services.Time.GetLocalNow());
        return Path.ChangeExtension(name, extension);
    }

    /// <summary>The HTML report of the shown run: the areas with their rows, the summary and the notes (names hosts; read-only).</summary>
    internal string HtmlReport()
    {
        var report = _report ?? throw new InvalidOperationException("No check was run.");
        var domain = _services.Ui.Sessions.Current?.Info.DomainDnsName ?? string.Empty;
        var title = Localizer.Format(L.Readiness.ReportTitle, domain);
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\n<html lang=\"").Append(E(Localizer.Culture.TwoLetterISOLanguageName)).Append("\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">\n")
            .Append("<title>").Append(E(title)).Append("</title>\n<style>\n")
            .Append("body{font-family:'Segoe UI',sans-serif;margin:24px;color:#1b1b1b}h1{font-size:20px;margin:0 0 4px}h2{font-size:16px;margin:20px 0 6px}")
            .Append(".sub{color:#5f5f5f;margin:0 0 12px}.note{background:#fff4ce;border-left:4px solid #9d5d00;padding:6px 10px;margin:4px 0}")
            .Append("table{border-collapse:collapse;width:100%}th,td{border:1px solid #d1d1d1;padding:6px 8px;text-align:left;vertical-align:top}")
            .Append("th{background:#f3f3f3}.st{font-weight:600;white-space:nowrap}.Error{color:#c42b1c}.Warning{color:#9d5d00}.Ok{color:#0f7b0f}")
            .Append(".NotChecked{color:#5f5f5f}td p{margin:0 0 6px}code{font-size:12px;color:#5f5f5f}\n</style>\n</head>\n<body>\n");
        html.Append("<h1>").Append(E(title)).Append("</h1>\n<p class=\"sub\">")
            .Append(E(Localizer.Format(L.Readiness.ReportSubtitle, DepthText(report.Depth), report.Server, report.At.ToString("g", Localizer.Culture))))
            .Append("<br>").Append(E(SummaryText)).Append("</p>\n");
        html.Append("<p class=\"note\">").Append(E(Localizer[L.Readiness.ExportNote])).Append("</p>\n");
        if (report.Cancelled)
        {
            html.Append("<p class=\"note\">").Append(E(Localizer[L.Readiness.Cancelled])).Append("</p>\n");
        }

        foreach (var group in Groups)
        {
            html.Append("<h2>").Append(E(group.Title)).Append(" – ").Append(E(group.StatusText)).Append("</h2>\n<table>\n<thead><tr><th>")
                .Append(E(Localizer[L.Readiness.ColumnStatus])).Append("</th><th>").Append(E(Localizer[L.Readiness.ColumnCheck])).Append("</th><th>")
                .Append(E(Localizer[L.Readiness.ColumnHost])).Append("</th><th>").Append(E(Localizer[L.Readiness.ColumnResult])).Append("</th></tr></thead>\n<tbody>\n");
            foreach (var row in group.Rows)
            {
                html.Append("<tr><td class=\"st ").Append(row.Status.ToString()).Append("\">").Append(E(row.StatusText)).Append("</td><td>")
                    .Append(E(row.Title)).Append("<br><code>").Append(E(row.Code)).Append("</code></td><td>").Append(E(row.Host)).Append("</td><td><p>")
                    .Append(E(row.Explanation)).Append("</p>");
                if (row.HasRemedy)
                {
                    html.Append("<p>").Append(E(row.RemedyText)).Append("</p>");
                }

                if (row.ShowsDocumentation)
                {
                    html.Append("<p>").Append(E(row.DocumentationText)).Append("</p>");
                }

                html.Append("</td></tr>\n");
            }

            html.Append("</tbody>\n</table>\n");
        }

        html.Append("<p class=\"sub\">").Append(E(Localizer[L.Readiness.ReadOnlyNote])).Append("</p>\n</body>\n</html>\n");
        return html.ToString();
    }

    /// <summary>Text for HTML content and attribute values: <c>&amp; &lt; &gt; " '</c> escaped, control characters dropped, Polish letters kept.</summary>
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
