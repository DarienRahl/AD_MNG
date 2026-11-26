using System.Text;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Pki;

/// <summary>
/// The actions of a finding (<i>Otwórz szablon</i>, <i>Otwórz urząd</i>, <i>Otwórz konto / komputer / obiekt</i>,
/// <i>Kopiuj</i>) and the exports of the AD CS security review. No <i>Napraw</i> in P1 (rule 5; repairs are P3).
/// </summary>
public sealed partial class PkiSecurityReviewDocumentViewModel
{
    /// <summary><i>Otwórz szablon</i>: <i>Szablony certyfikatów</i> at the finding's template (the shell opens or selects the tab).</summary>
    [RelayCommand(CanExecute = nameof(CanOpenTemplate))]
    private void OpenTemplate()
    {
        if (SelectedRow is { TemplateCn: { } cn })
        {
            Pki.RequestDocument(new PkiDocumentRequest(PkiDocumentKind.Templates) { TemplateCn = cn });
        }
    }

    private bool CanOpenTemplate() => SelectedRow is { CanOpenTemplate: true };

    /// <summary><i>Otwórz urząd</i>: the overview at the finding's CA (its own, or the first CA that publishes the template).</summary>
    [RelayCommand(CanExecute = nameof(CanOpenCa))]
    private void OpenCa()
    {
        if (SelectedRow is { CaDn: { } dn })
        {
            Pki.RequestDocument(new PkiDocumentRequest(PkiDocumentKind.Overview) { CaDn = dn });
        }
    }

    private bool CanOpenCa() => SelectedRow is { CanOpenCa: true };

    /// <summary><i>Otwórz konto / komputer / obiekt</i>: the property sheet of the account, CA computer or PKI object.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenObject))]
    private Task OpenObjectAsync() =>
        SelectedRow is { OpenDn: { } dn } ? Pki.Ui.Dialogs.ShowPropertiesAsync(dn) : Task.CompletedTask;

    private bool CanOpenObject() => SelectedRow is { CanOpenObject: true } && HasSession;

    /// <summary><i>Kopiuj</i>: the finding's details as text.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Copy()
    {
        if (SelectedRow is { } row && Pki.Ui.Clipboard.SetText(DetailsText(row)))
        {
            Pki.Ui.Notifications.Show(NotificationSeverity.Info, Localizer[L.PkiSecurity.Copied]);
        }
    }

    /// <summary>The details of a finding as the <i>Kopiuj</i> text.</summary>
    public string DetailsText(PkiFindingRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.DetailsText(Headings());
    }

    private PkiFindingHeadings Headings() => (_review?.Texts ?? new PkiFindingRows(Localizer, Core.Pki.PkiSnapshot.Empty(string.Empty, default))).Headings;

    // ---- Export ------------------------------------------------------------------------------------------------------------

    /// <summary><i>Eksportuj do CSV…</i>: the findings the list shows (with the filters) to a file the operator picks (rule 1).</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportCsvAsync()
    {
        var title = Localizer[L.PkiSecurity.ExportCsv];
        var path = await Pki.Ui.Files.SaveFileAsync(title, SuggestedFileName(".csv"), [new FileFilter(Localizer[L.Files.CsvFilter], "*.csv")])
            .ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        var rows = Rows.Select(r => (IReadOnlyList<string>)
            [r.SeverityText, r.Esc, r.AreaText, r.ObjectText, r.Title, r.Message, r.Explanation, r.Recommendation,
                string.Join(" | ", r.PrincipalLines), string.Join(" | ", r.NoteLines), string.Join(" | ", r.RelatedLines),
                r.Definition.Esc, r.Definition.Locksmith ?? string.Empty, r.Definition.Mdi ?? string.Empty, r.Code]).ToList();
        var headers = new[]
        {
            L.PkiSecurity.ColumnSeverity, L.PkiSecurity.ColumnEsc, L.PkiSecurity.ColumnArea, L.PkiSecurity.ColumnObject, L.PkiSecurity.ColumnFinding,
            L.PkiSecurity.ConditionHeading, L.PkiSecurity.AttackPathHeading, L.PkiSecurity.WhatHeading, L.PkiSecurity.PrincipalsHeading,
            L.PkiSecurity.NotesHeading, L.PkiSecurity.RelatedHeading,
        }.Select(k => Localizer[k]).Concat(["Certipy", "Locksmith", "MDI", "Code"]).ToList();
        var delimiter = CsvFiles.ListSeparator(Pki.Ui.Regional.Culture);
        await WriteAsync(title, path, rows.Count, writer => Pki.Csv.Write(writer, headers, rows, delimiter, protectFormulas: true)).ConfigureAwait(true);
    }

    /// <summary><i>Eksportuj do HTML…</i>: a self-contained report (every value escaped, no scripts, no external resources).</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportHtmlAsync()
    {
        var title = Localizer[L.PkiSecurity.ExportHtml];
        var path = await Pki.Ui.Files.SaveFileAsync(title, SuggestedFileName(".html"), [new FileFilter(Localizer[L.PkiSecurity.HtmlFilter], "*.html")])
            .ConfigureAwait(true);
        if (path is null || _review is not { } review)
        {
            return;
        }

        var html = HtmlReport(review, Rows);
        await WriteAsync(title, path, Rows.Count, writer => writer.Write(html)).ConfigureAwait(true);
    }

    private bool CanExport() => _review is not null && Rows.Count > 0 && !IsLoading;

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
        var name = ListExportSnapshot.SuggestFileName(Localizer.Format(L.PkiSecurity.ReportTitle, domain), Pki.Time.GetLocalNow());
        return Path.ChangeExtension(name, extension);
    }

    /// <summary>The HTML report of <paramref name="rows"/>: one self-contained page (CSP <c>default-src 'none'</c>), every text HTML-encoded.</summary>
    private string HtmlReport(ReviewResult review, IReadOnlyList<PkiFindingRowViewModel> rows)
    {
        var domain = Pki.Sessions.Current?.Info.DomainDnsName ?? string.Empty;
        var title = Localizer.Format(L.PkiSecurity.ReportTitle, domain);
        var headings = review.Texts.Headings;
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\n<html lang=\"").Append(E(Localizer.Culture.TwoLetterISOLanguageName)).Append("\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">\n")
            .Append("<title>").Append(E(title)).Append("</title>\n<style>\n")
            .Append("body{font-family:'Segoe UI',sans-serif;margin:24px;color:#1b1b1b}h1{font-size:20px;margin:0 0 4px}")
            .Append(".sub{color:#5f5f5f;margin:0 0 16px}.note{background:#fff4ce;border-left:4px solid #9d5d00;padding:6px 10px;margin:4px 0}")
            .Append(".scope{background:#f3f3f3;border-left:4px solid #005fb8;padding:6px 10px;margin:4px 0 12px}")
            .Append("table{border-collapse:collapse;width:100%}th,td{border:1px solid #d1d1d1;padding:6px 8px;text-align:left;vertical-align:top}")
            .Append("th{background:#f3f3f3}.sev{font-weight:600;white-space:nowrap}.Critical,.High{color:#c42b1c}.Medium{color:#9d5d00}")
            .Append(".Low{color:#005fb8}.Info{color:#5f5f5f}td p{margin:0 0 6px}td ul{margin:0 0 6px;padding-left:18px}\n</style>\n</head>\n<body>\n");
        html.Append("<h1>").Append(E(title)).Append("</h1>\n<p class=\"sub\">")
            .Append(E(Localizer.Format(L.PkiSecurity.ReportSubtitle, review.Server, review.At.ToString("g", Localizer.Culture))))
            .Append("<br>").Append(E(string.Join(" · ", Chips.Select(c => c.Text))));
        if (IsFiltered)
        {
            html.Append("<br>").Append(E(Localizer.Format(L.PkiSecurity.ReportFilters, rows.Count, _all.Count)));
        }

        html.Append("</p>\n<p class=\"scope\">").Append(E(ScopeNote)).Append("</p>\n");
        foreach (var note in Notes)
        {
            html.Append("<p class=\"note\">").Append(E(note)).Append("</p>\n");
        }

        html.Append("<table>\n<thead><tr><th>").Append(E(Localizer[L.PkiSecurity.ColumnSeverity])).Append("</th><th>")
            .Append(E(Localizer[L.PkiSecurity.ColumnEsc])).Append("</th><th>").Append(E(Localizer[L.PkiSecurity.ColumnObject])).Append("</th><th>")
            .Append(E(Localizer[L.PkiSecurity.ColumnFinding])).Append("</th></tr></thead>\n<tbody>\n");
        foreach (var row in rows)
        {
            html.Append("<tr><td class=\"sev ").Append(row.Severity.ToString()).Append("\">").Append(E(row.SeverityText)).Append("</td><td>")
                .Append(E(row.Esc)).Append("</td><td>").Append(E(row.AreaText)).Append("<br>").Append(E(row.ObjectText)).Append("</td><td>")
                .Append("<p><strong>").Append(E(row.Title)).Append("</strong></p>")
                .Append("<p><em>").Append(E(headings.Condition)).Append("</em> ").Append(E(row.Message)).Append("</p>")
                .Append("<p><em>").Append(E(headings.AttackPath)).Append("</em> ").Append(E(row.Explanation)).Append("</p>")
                .Append("<p><em>").Append(E(headings.What)).Append("</em> ").Append(E(row.Recommendation)).Append("</p>");
            List(headings.Principals, row.PrincipalLines);
            List(headings.Notes, row.NoteLines);
            List(headings.Related, row.RelatedLines);
            html.Append("<p class=\"sub\">").Append(E(row.IdsText)).Append("</p></td></tr>\n");
        }

        html.Append("</tbody>\n</table>\n<p class=\"sub\">").Append(E(Localizer[L.PkiSecurity.ReportPrivacy])).Append("</p>\n</body>\n</html>\n");
        return html.ToString();

        void List(string heading, IReadOnlyList<string> items)
        {
            if (items.Count == 0)
            {
                return;
            }

            html.Append("<p><em>").Append(E(heading)).Append("</em></p><ul>");
            foreach (var item in items)
            {
                html.Append("<li>").Append(E(item)).Append("</li>");
            }

            html.Append("</ul>");
        }
    }

    /// <summary>Text for HTML content and attribute values: <c>&amp; &lt; &gt; " '</c> escaped, everything else (Polish letters) as is.</summary>
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
