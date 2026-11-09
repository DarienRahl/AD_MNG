using System.Text;
using AdMng.Presentation.GroupPolicy.Copy;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy.Search;

/// <summary>
/// The row actions of "Wyszukiwarka ustawień GPO" ("Otwórz GPO", "Edytuj to ustawienie", "Porównaj GPO", "Kopiuj do innego
/// GPO…", "Kopiuj") and the exports of the results.
/// </summary>
public sealed partial class GpoSettingsSearchDocumentViewModel
{
    /// <summary>The selected rows, or the current row when the list reports no selection.</summary>
    public IReadOnlyList<GpoSearchRowViewModel> ActionRows => SelectedRows.Count > 0 ? SelectedRows : SelectedRow is { } row ? [row] : [];

    /// <summary>"Otwórz GPO": the GPO's document at its "Ustawienia" tab.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenGpo()
    {
        if (SelectedRow is { } row)
        {
            Actions.OpenGpo(row.GpoId, row.GpoName, GpoDocumentTab.Settings);
        }
    }

    /// <summary>"Edytuj to ustawienie": the editor of the setting (K3 or K4) at that setting.</summary>
    [RelayCommand(CanExecute = nameof(CanEditSetting))]
    private void EditSetting()
    {
        if (SelectedRow is { CanEdit: true, Text.Focus: { } focus } row)
        {
            Actions.EditSetting(row.GpoId, row.GpoName, focus);
        }
    }

    private bool CanEditSetting() => SelectedRow is { CanEdit: true } && HasSession;

    /// <summary>"Porównaj GPO": the GPOs of two selected rows, or the GPO of one row with a GPO picked in the window.</summary>
    [RelayCommand(CanExecute = nameof(CanCompare))]
    private Task CompareAsync()
    {
        var gpos = ActionRows.Select(r => r.Gpo).DistinctBy(g => g.Id).ToList();
        return gpos.Count switch
        {
            1 => Actions.CompareAsync(gpos[0]),
            2 => Actions.CompareAsync(gpos[0], gpos[1]),
            _ => Task.CompletedTask,
        };
    }

    private bool CanCompare() => HasSession && ActionRows.Select(r => r.GpoId).Distinct().Count() is 1 or 2;

    /// <summary>
    /// "Kopiuj do innego GPO…": the selected occurrences into a GPO picked in the copy window, through the change plan preview
    /// (K7 W5; units that cannot be copied are shown there with the reason). After a copy the changed GPOs are read again.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCopyTo))]
    private async Task CopyToAsync()
    {
        var items = ActionRows.Select(r => r.CopyItem).ToList();
        if (items.Count > 0 && await Actions.CopySettingsAsync(items).ConfigureAwait(true))
        {
            await ReadAsync(GpoIndexRefresh.Incremental).ConfigureAwait(true);
        }
    }

    private bool CanCopyTo() => HasSession && !IsLoading && ActionRows.Count > 0;

    /// <summary>"Kopiuj": the selected occurrences as text (GPO, setting, value, parts, path).</summary>
    [RelayCommand(CanExecute = nameof(CanCopy))]
    private void Copy()
    {
        var rows = ActionRows;
        if (rows.Count > 0)
        {
            ClipboardCopy.Text(Gp.Ui.Clipboard, Gp.Ui.Notifications, string.Join(Environment.NewLine, rows.Select(r => r.CopyText)), Localizer[L.GpoSearch.Copied]);
        }
    }

    private bool CanCopy() => ActionRows.Count > 0;

    // ---- Export -------------------------------------------------------------------------------------------------------------

    /// <summary>"Eksportuj do CSV…": every shown occurrence (one line each) to a file the operator picks (rule 1).</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportCsvAsync()
    {
        var title = Localizer[L.GpoSearch.ExportCsv];
        var path = await Gp.Ui.Files.SaveFileAsync(title, SuggestedFileName(".csv"), [new FileFilter(Localizer[L.Files.CsvFilter], "*.csv")])
            .ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        var rows = Rows.Select(r => (IReadOnlyList<string>)
            [r.Name, r.GroupPath, r.Group.SummaryText, r.GpoName, r.ValueText, string.Join(" | ", r.Elements), r.LinksText, r.MarksText, r.Technical]).ToList();
        var headers = new[]
        {
            L.GroupPolicy.ColumnSetting, L.GpoSearch.ColumnPath, L.GpoSearch.ColumnAgreement, L.GroupPolicy.ColumnGpo, L.GroupPolicy.ColumnValue,
            L.GpoSearch.ColumnElements, L.GroupPolicy.ColumnLinks, L.GpoSearch.ColumnMarks, L.GpoSearch.DetailsTechnical,
        }.Select(k => Localizer[k]).ToList();
        var delimiter = CsvFiles.ListSeparator(Gp.Ui.Regional.Culture);
        await WriteAsync(title, path, rows.Count, writer => _csv.Write(writer, headers, rows, delimiter, protectFormulas: true)).ConfigureAwait(true);
    }

    /// <summary>"Eksportuj do HTML…": a self-contained report of the shown groups (every value escaped, no scripts, no external resources).</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportHtmlAsync()
    {
        var title = Localizer[L.GpoSearch.ExportHtml];
        var path = await Gp.Ui.Files.SaveFileAsync(title, SuggestedFileName(".html"), [new FileFilter(Localizer[L.GpoSearch.HtmlFilter], "*.html")])
            .ConfigureAwait(true);
        if (path is null || _corpus is not { } corpus)
        {
            return;
        }

        var html = HtmlReport(corpus, Groups);
        await WriteAsync(title, path, Rows.Count, writer => writer.Write(html)).ConfigureAwait(true);
    }

    private bool CanExport() => _corpus is not null && Rows.Count > 0 && !IsLoading;

    private async Task WriteAsync(string title, string path, int count, Action<TextWriter> write)
    {
        try
        {
            await CsvFiles.WriteAsync(path, write).ConfigureAwait(true);
            Gp.Ui.Notifications.Show(NotificationSeverity.Success, Localizer.Format(L.Files.Exported, count, Path.GetFileName(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Gp.Ui.Dialogs.ShowErrorAsync(title, Gp.Errors.Translate(ex)).ConfigureAwait(true);
        }
    }

    private string SuggestedFileName(string extension)
    {
        var domain = _corpus?.View.Index.DomainDnsName ?? Gp.Sessions.Current?.Info.DomainDnsName ?? string.Empty;
        var name = ListExportSnapshot.SuggestFileName(Localizer.Format(L.GpoSearch.ReportTitle, domain), Gp.Time.GetLocalNow());
        return Path.ChangeExtension(name, extension);
    }

    /// <summary>The HTML report of <paramref name="groups"/>: one section per setting with the GPOs that set it, every text HTML-encoded.</summary>
    private string HtmlReport(GpoSearchCorpus corpus, IReadOnlyList<GpoSearchGroupViewModel> groups)
    {
        var index = corpus.View.Index;
        var title = Localizer.Format(L.GpoSearch.ReportTitle, index.DomainDnsName);
        var read = TimeZoneInfo.ConvertTime(index.BuiltAt, TimeZoneInfo.Local).ToString("g", Localizer.Culture);
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\n<html lang=\"").Append(E(Localizer.Culture.TwoLetterISOLanguageName)).Append("\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">\n")
            .Append("<title>").Append(E(title)).Append("</title>\n<style>\n")
            .Append("body{font-family:'Segoe UI',sans-serif;margin:24px;color:#1b1b1b}h1{font-size:20px;margin:0 0 4px}")
            .Append("h2{font-size:15px;margin:18px 0 2px}.sub{color:#5f5f5f;margin:0 0 12px}.path{color:#5f5f5f;margin:0 0 6px}")
            .Append(".note{background:#fff4ce;border-left:4px solid #9d5d00;padding:6px 10px;margin:4px 0}.diff{color:#9d5d00;font-weight:600}")
            .Append("table{border-collapse:collapse;width:100%}th,td{border:1px solid #d1d1d1;padding:6px 8px;text-align:left;vertical-align:top}")
            .Append("th{background:#f3f3f3}td ul{margin:0;padding-left:18px}\n</style>\n</head>\n<body>\n");
        html.Append("<h1>").Append(E(title)).Append("</h1>\n<p class=\"sub\">")
            .Append(E(Localizer.Format(L.GpoSearch.ReportSubtitle, index.Server, LastQuery?.Text.Trim() ?? string.Empty, read)));
        if (LastQuery is { } query)
        {
            html.Append("<br>").Append(E(Localizer[L.GpoSearch.FieldLabel] + ": " + Localizer[L.GpoSearch.FieldKey(query.Field)]));
        }

        html.Append("<br>").Append(E(ResultText)).Append("</p>\n");
        foreach (var note in Notes)
        {
            html.Append("<p class=\"note\">").Append(E(note.Text)).Append("</p>\n");
        }

        if (CapNote is { } cap)
        {
            html.Append("<p class=\"note\">").Append(E(cap)).Append("</p>\n");
        }

        foreach (var group in groups)
        {
            html.Append("<h2>").Append(E(group.Name)).Append("</h2>\n<p class=\"path\">").Append(E(group.GroupPath)).Append(" · <span")
                .Append(group.HasDifferentValues ? " class=\"diff\"" : string.Empty).Append('>').Append(E(group.SummaryText)).Append("</span></p>\n")
                .Append("<table>\n<thead><tr><th>").Append(E(Localizer[L.GroupPolicy.ColumnGpo])).Append("</th><th>")
                .Append(E(Localizer[L.GroupPolicy.ColumnValue])).Append("</th><th>").Append(E(Localizer[L.GroupPolicy.ColumnLinks])).Append("</th><th>")
                .Append(E(Localizer[L.GpoSearch.ColumnMarks])).Append("</th></tr></thead>\n<tbody>\n");
            foreach (var row in group.Rows)
            {
                html.Append("<tr><td>").Append(E(row.GpoName)).Append("</td><td>").Append(E(row.ValueText));
                var parts = row.Elements.Where(e => !string.Equals(e, row.ValueText, StringComparison.Ordinal)).ToList();
                if (parts.Count > 0)
                {
                    html.Append("<ul>");
                    foreach (var part in parts)
                    {
                        html.Append("<li>").Append(E(part)).Append("</li>");
                    }

                    html.Append("</ul>");
                }

                html.Append("</td><td>").Append(E(row.LinksText)).Append("</td><td>").Append(E(row.MarksText)).Append("</td></tr>\n");
            }

            html.Append("</tbody>\n</table>\n");
        }

        html.Append("<p class=\"sub\">").Append(E(Localizer[L.GpoSearch.ReportPrivacy])).Append("</p>\n</body>\n</html>\n");
        return html.ToString();
    }

    /// <summary>Text for HTML content and attribute values: <c>&amp; &lt; &gt; " '</c> escaped, everything else (Polish letters) as is.</summary>
    private static string E(string? text)
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
