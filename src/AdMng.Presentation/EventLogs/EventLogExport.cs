using System.Text;
using AdMng.Core.Csv;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Services;

namespace AdMng.Presentation.EventLogs;

/// <summary>One table of an export: a heading (null for the main table), the column headers and the rows.</summary>
public sealed record EventLogExportTable(string? Heading, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows)
{
    public override string ToString() => $"{Rows.Count} rows";
}

/// <summary>
/// The exports of the event log documents (operator decision, design note e-event-logs.md 1): CSV or one self-contained HTML
/// page, only into a file the operator picks (rule 1), each saying that the file contains personal data – the CSV in its first
/// line, the HTML at its top. Every value is escaped (formula protection in CSV, HTML encoding).
/// </summary>
public static class EventLogExport
{
    /// <summary>Asks for a CSV file and writes the main table (the first) with the personal-data note; true when written.</summary>
    public static async Task<bool> ExportCsvAsync(DialogServices services, ICsvCodec csv, string title, string fileTitle, EventLogExportTable table)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(csv);
        ArgumentNullException.ThrowIfNull(table);
        var localizer = services.Localizer;
        var path = await services.Files.SaveFileAsync(title, FileName(services, fileTitle, ".csv"), [new FileFilter(localizer[L.Files.CsvFilter], "*.csv")])
            .ConfigureAwait(true);
        if (path is null)
        {
            return false;
        }

        var delimiter = CsvFiles.ListSeparator(services.Regional.Culture);
        return await WriteAsync(services, title, path, table.Rows.Count, writer =>
        {
            csv.Write(writer, [localizer[L.EventLogs.PersonalDataNote]], [], delimiter, protectFormulas: true);
            csv.Write(writer, table.Headers, table.Rows, delimiter, protectFormulas: true);
        }).ConfigureAwait(true);
    }

    /// <summary>Asks for an HTML file and writes every table with the title, subtitle lines, notes and the personal-data note.</summary>
    public static async Task<bool> ExportHtmlAsync(DialogServices services, string title, string fileTitle, IReadOnlyList<string> subtitle,
        IReadOnlyList<string> notes, IReadOnlyList<EventLogExportTable> tables)
    {
        ArgumentNullException.ThrowIfNull(services);
        var localizer = services.Localizer;
        var path = await services.Files.SaveFileAsync(title, FileName(services, fileTitle, ".html"), [new FileFilter(localizer[L.EventLogs.HtmlFilter], "*.html")])
            .ConfigureAwait(true);
        if (path is null)
        {
            return false;
        }

        var html = Html(localizer, fileTitle, subtitle, notes, tables);
        return await WriteAsync(services, title, path, tables.Sum(t => t.Rows.Count), writer => writer.Write(html)).ConfigureAwait(true);
    }

    /// <summary>The HTML page: one self-contained document, every text encoded, no scripts or external resources.</summary>
    public static string Html(ILocalizer localizer, string title, IReadOnlyList<string> subtitle, IReadOnlyList<string> notes, IReadOnlyList<EventLogExportTable> tables)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(tables);
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\n<html lang=\"").Append(E(localizer.Culture.TwoLetterISOLanguageName)).Append("\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">\n")
            .Append("<title>").Append(E(title)).Append("</title>\n<style>\n")
            .Append("body{font-family:'Segoe UI',sans-serif;margin:24px;color:#1b1b1b}h1{font-size:20px;margin:0 0 4px}h2{font-size:16px;margin:18px 0 6px}")
            .Append(".sub{color:#5f5f5f;margin:0 0 12px}.privacy{background:#fde7e9;border-left:4px solid #c42b1c;padding:6px 10px;margin:0 0 12px}")
            .Append(".note{background:#fff4ce;border-left:4px solid #9d5d00;padding:6px 10px;margin:4px 0}")
            .Append("table{border-collapse:collapse;width:100%}th,td{border:1px solid #d1d1d1;padding:4px 8px;text-align:left;vertical-align:top}")
            .Append("th{background:#f3f3f3}\n</style>\n</head>\n<body>\n");
        html.Append("<h1>").Append(E(title)).Append("</h1>\n");
        html.Append("<p class=\"privacy\">").Append(E(localizer[L.EventLogs.PersonalDataNote])).Append("</p>\n");
        if (subtitle.Count > 0)
        {
            html.Append("<p class=\"sub\">").Append(string.Join("<br>", subtitle.Select(E))).Append("</p>\n");
        }

        foreach (var note in notes)
        {
            html.Append("<p class=\"note\">").Append(E(note)).Append("</p>\n");
        }

        foreach (var table in tables)
        {
            if (table.Heading is { } heading)
            {
                html.Append("<h2>").Append(E(heading)).Append("</h2>\n");
            }

            html.Append("<table>\n<thead><tr>");
            foreach (var header in table.Headers)
            {
                html.Append("<th>").Append(E(header)).Append("</th>");
            }

            html.Append("</tr></thead>\n<tbody>\n");
            foreach (var row in table.Rows)
            {
                html.Append("<tr>");
                foreach (var cell in row)
                {
                    html.Append("<td>").Append(E(cell).Replace("\n", "<br>", StringComparison.Ordinal)).Append("</td>");
                }

                html.Append("</tr>\n");
            }

            html.Append("</tbody>\n</table>\n");
        }

        html.Append("</body>\n</html>\n");
        return html.ToString();
    }

    /// <summary>Text for HTML content and attribute values: <c>&amp; &lt; &gt; " '</c> escaped, control characters dropped.</summary>
    public static string E(string? text)
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

    private static string FileName(DialogServices services, string title, string extension) =>
        Path.ChangeExtension(ListExportSnapshot.SuggestFileName(title, services.Time.GetLocalNow()), extension);

    private static async Task<bool> WriteAsync(DialogServices services, string title, string path, int count, Action<TextWriter> write)
    {
        try
        {
            await CsvFiles.WriteAsync(path, write).ConfigureAwait(true);
            services.Notifications.Show(NotificationSeverity.Success, services.Localizer.Format(L.Files.Exported, count, Path.GetFileName(path)));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await services.Dialogs.ShowErrorAsync(title, services.Errors.Translate(ex)).ConfigureAwait(true);
            return false;
        }
    }
}
