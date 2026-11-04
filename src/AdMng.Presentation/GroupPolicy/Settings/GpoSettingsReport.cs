using System.Text;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.GroupPolicy.Settings;

/// <summary>
/// "Raport HTML…" of the GPO's "Ustawienia" tab (design note docs/dev/k-computers-and-gpo.md 20.1): one self-contained page
/// (inline style, no script, no external resource) grouped like GPMC's settings report – configuration, then GPMC's folder
/// path, then the settings with their values, parts, notes and technical keys. Every text is HTML-escaped; masked values
/// are already masked by the presenter. Written only to a file the operator picks (CLAUDE.md rule 1).
/// </summary>
public static class GpoSettingsReport
{
    private const string Style = """
        body{font-family:"Segoe UI",system-ui,sans-serif;font-size:14px;color:#1b1b1b;background:#fff;margin:24px 32px}
        h1{font-size:22px;font-weight:600;margin:0 0 4px}
        .meta{color:#5c5c5c;margin:0 0 20px}
        h2{font-size:18px;font-weight:600;border-bottom:2px solid #0f6cbd;padding-bottom:4px;margin:28px 0 8px}
        h3{font-size:14px;font-weight:600;color:#0f6cbd;margin:18px 0 6px}
        table{border-collapse:collapse;width:100%;table-layout:fixed}
        th,td{border:1px solid #d6d6d6;padding:6px 8px;text-align:left;vertical-align:top;overflow-wrap:anywhere}
        th{background:#f3f3f3;font-weight:600}
        th:nth-child(1){width:34%}th:nth-child(3){width:26%}
        ul{margin:4px 0 0 18px;padding:0}
        .note{color:#5c5c5c;font-size:12px;margin-top:4px}
        .tech{font-family:Consolas,monospace;font-size:12px;color:#5c5c5c}
        footer{color:#5c5c5c;font-size:12px;margin-top:28px}
        """;

    /// <summary>The page for <paramref name="rows"/> (configurations and folders in the order the rows come).</summary>
    /// <param name="localizer">The window's language (headings, columns).</param>
    /// <param name="title">"Ustawienia obiektu GPO „…”".</param>
    /// <param name="meta">Domain, GUID, DC and time of the read.</param>
    /// <param name="rows">The rows as the tab shows them (sorted).</param>
    public static string Build(ILocalizer localizer, string title, string meta, IReadOnlyList<GpoSettingText> rows)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(rows);
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\n<html lang=\"").Append(Encode(localizer.Culture.TwoLetterISOLanguageName)).Append("\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append("<title>").Append(Encode(title)).Append("</title>\n<style>\n").Append(Style).Append("</style>\n</head>\n<body>\n")
            .Append("<h1>").Append(Encode(title)).Append("</h1>\n<p class=\"meta\">").Append(Encode(meta)).Append("</p>\n");

        foreach (var configuration in rows.GroupBy(r => r.Configuration))
        {
            html.Append("<h2>").Append(Encode(configuration.Key)).Append("</h2>\n");
            foreach (var folder in configuration.GroupBy(r => r.Path))
            {
                if (folder.Key.Length > 0)
                {
                    html.Append("<h3>").Append(Encode(folder.Key)).Append("</h3>\n");
                }

                html.Append("<table>\n<thead><tr><th>").Append(Encode(localizer[L.GroupPolicy.ColumnSetting])).Append("</th><th>")
                    .Append(Encode(localizer[L.GroupPolicy.ColumnValue])).Append("</th><th>").Append(Encode(localizer[L.GpoSettings.ColumnTechnical]))
                    .Append("</th></tr></thead>\n<tbody>\n");
                foreach (var row in folder)
                {
                    Row(html, row);
                }

                html.Append("</tbody>\n</table>\n");
            }
        }

        html.Append("<footer>").Append(Encode(localizer[L.GpoSettings.ReportNote])).Append("</footer>\n</body>\n</html>\n");
        return html.ToString();
    }

    private static void Row(StringBuilder html, GpoSettingText row)
    {
        html.Append("<tr><td>").Append(Encode(row.Name));
        if (row.Explanation is { Length: > 0 } explanation)
        {
            html.Append("<div class=\"note\">").Append(Encode(explanation)).Append("</div>");
        }

        html.Append("</td><td>").Append(Encode(row.ValueText));
        var elements = row.Elements.Where(e => e.Length > 0).ToList();
        if (elements.Count > 1 || (elements.Count == 1 && !string.Equals(elements[0], row.ValueText, StringComparison.Ordinal)))
        {
            html.Append("<ul>");
            foreach (var element in elements)
            {
                html.Append("<li>").Append(Encode(element)).Append("</li>");
            }

            html.Append("</ul>");
        }

        html.Append("</td><td class=\"tech\">").Append(Encode(row.Technical)).Append("</td></tr>\n");
    }

    /// <summary>The text with &amp;, &lt;, &gt; and quotes escaped (letters such as „ó” stay letters: the page is UTF-8).</summary>
    private static string Encode(string text)
    {
        var html = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            html.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&#39;",
                _ when char.IsControl(c) && c is not '\n' and not '\t' => " ",
                _ => c.ToString(),
            });
        }

        return html.ToString();
    }
}
