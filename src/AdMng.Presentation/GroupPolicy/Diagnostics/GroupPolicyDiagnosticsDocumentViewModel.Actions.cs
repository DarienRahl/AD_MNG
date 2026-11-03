using System.Text;
using AdMng.Core.GroupPolicy.Diagnostics;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy.Diagnostics;

/// <summary>The actions of a finding ("Napraw…", "Otwórz GPO", "Edytuj to ustawienie", "Pokaż w modelu", "Kopiuj") and the exports.</summary>
public sealed partial class GroupPolicyDiagnosticsDocumentViewModel
{
    /// <summary>
    /// "Napraw…": the finding's repair through the change plan preview (rule 5); after a successful run the domain is checked
    /// again at the same depth and the selection stays on the finding (or near its place when it is gone).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanFix))]
    private async Task FixAsync()
    {
        if (SelectedRow is not { Finding.Fix: { } fix } row || _check is null)
        {
            return;
        }

        var rows = new GpoFindingRows(Localizer, _check.Snapshot, _check.Names);
        _restoreSelection = (row.Key, IndexOf(Rows, row));
        if (await _fixes.RunAsync(fix, GpoQuickFixRunner.SubjectOf(row), rows.NameOf).ConfigureAwait(true))
        {
            await CheckAsync(LastDepth ?? GpoDiagnosticsDepth.Quick).ConfigureAwait(true);
        }
        else
        {
            _restoreSelection = null;
        }
    }

    private bool CanFix() => SelectedRow is { HasFix: true } && HasSession && !IsLoading;

    /// <summary>"Otwórz GPO": the GPO's document at the tab of the finding's area.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenGpo))]
    private void OpenGpo()
    {
        if (SelectedRow is { GpoExists: true, Finding.GpoId: { } id } row)
        {
            Actions.OpenGpo(id, row.GpoText, TabOf(row.Finding));
        }
    }

    private bool CanOpenGpo() => SelectedRow is { GpoExists: true };

    /// <summary>"Edytuj to ustawienie": the editor of the finding's setting (K3 or K4) at that setting.</summary>
    [RelayCommand(CanExecute = nameof(CanEditSetting))]
    private void EditSetting()
    {
        if (SelectedRow is { CanEditSetting: true, Finding.GpoId: { } id, Focus: { } focus } row)
        {
            Actions.EditSetting(id, row.GpoText, focus);
        }
    }

    private bool CanEditSetting() => SelectedRow is { CanEditSetting: true };

    /// <summary>"Pokaż w modelu": the K5 model of a new computer and user in the finding's container (or the GPO's first link).</summary>
    [RelayCommand(CanExecute = nameof(CanShowInModel))]
    private void ShowInModel()
    {
        if (SelectedRow is { ModelContainerDn: { } dn } && _check is { } check)
        {
            Gp.OpenDocument(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Modeling)
            {
                ContainerDn = dn,
                Name = new GpoFindingRows(Localizer, check.Snapshot, check.Names).ContainerText(dn),
            });
        }
    }

    private bool CanShowInModel() => SelectedRow is { ModelContainerDn: not null };

    /// <summary>"Kopiuj": the finding's details as text.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Copy()
    {
        if (SelectedRow is { } row && Gp.Ui.Clipboard.SetText(DetailsText(row)))
        {
            Gp.Ui.Notifications.Show(NotificationSeverity.Info, Localizer[L.GpoDiagnostics.Copied]);
        }
    }

    /// <summary>The details of a finding as the "Kopiuj" text.</summary>
    public string DetailsText(GpoFindingRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.DetailsText(Localizer[L.GpoDiagnostics.WhyHeading], Localizer[L.GpoDiagnostics.WhatHeading], Localizer[L.GpoDiagnostics.AffectedHeading]);
    }

    /// <summary>The GPO tab that shows what a finding is about.</summary>
    internal static GpoDocumentTab TabOf(GpoFinding finding) => finding switch
    {
        { SettingKey: not null } => GpoDocumentTab.Settings,
        { Code: GpoFindingCodes.BroadEditRights or GpoFindingCodes.OrphanedTrustee or GpoFindingCodes.DenyInSecurityFiltering } => GpoDocumentTab.Delegation,
        { Area: GpoFindingArea.Links or GpoFindingArea.Scope or GpoFindingArea.Filtering } => GpoDocumentTab.Scope,
        _ => GpoDocumentTab.Status,
    };

    // ---- Export ------------------------------------------------------------------------------------------------------------

    /// <summary>"Eksportuj do CSV…": the findings the list shows (with the filters) to a file the operator picks (rule 1).</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportCsvAsync()
    {
        var title = Localizer[L.GpoDiagnostics.ExportCsv];
        var path = await Gp.Ui.Files.SaveFileAsync(title, SuggestedFileName(".csv"), [new FileFilter(Localizer[L.Files.CsvFilter], "*.csv")])
            .ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        var rows = Rows.Select(r => (IReadOnlyList<string>)
            [r.SeverityText, r.AreaText, r.Title, r.GpoText, r.WhereText, r.Message, r.Explanation, r.Recommendation,
                string.Join(" | ", r.AffectedLines), r.FixText, r.Code]).ToList();
        var headers = new[]
        {
            L.GpoDiagnostics.ColumnSeverity, L.GpoDiagnostics.ColumnArea, L.GpoDiagnostics.ColumnFinding, L.GroupPolicy.ColumnGpo,
            L.GpoDiagnostics.ColumnWhere, L.GpoDiagnostics.DetailsHeading, L.GpoDiagnostics.WhyHeading, L.GpoDiagnostics.WhatHeading,
            L.GpoDiagnostics.AffectedHeading, L.GpoDiagnostics.Fix,
        }.Select(k => Localizer[k]).Append("Code").ToList();
        var delimiter = CsvFiles.ListSeparator(Gp.Ui.Regional.Culture);
        await WriteAsync(title, path, rows.Count, writer => _csv.Write(writer, headers, rows, delimiter, protectFormulas: true)).ConfigureAwait(true);
    }

    /// <summary>"Eksportuj do HTML…": a self-contained report (every value escaped, no scripts, no external resources).</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportHtmlAsync()
    {
        var title = Localizer[L.GpoDiagnostics.ExportHtml];
        var path = await Gp.Ui.Files.SaveFileAsync(title, SuggestedFileName(".html"), [new FileFilter(Localizer[L.GpoDiagnostics.HtmlFilter], "*.html")])
            .ConfigureAwait(true);
        if (path is null || _check is not { } check)
        {
            return;
        }

        var html = HtmlReport(check, Rows);
        await WriteAsync(title, path, Rows.Count, writer => writer.Write(html)).ConfigureAwait(true);
    }

    private bool CanExport() => _check is not null && Rows.Count > 0 && !IsLoading;

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
        var domain = Gp.Sessions.Current?.Info.DomainDnsName ?? string.Empty;
        var name = ListExportSnapshot.SuggestFileName(Localizer.Format(L.GpoDiagnostics.ReportTitle, domain), Gp.Time.GetLocalNow());
        return Path.ChangeExtension(name, extension);
    }

    /// <summary>The HTML report of <paramref name="rows"/>: one self-contained page, every text HTML-encoded.</summary>
    private string HtmlReport(CheckResult check, IReadOnlyList<GpoFindingRowViewModel> rows)
    {
        var title = Localizer.Format(L.GpoDiagnostics.ReportTitle, check.Snapshot.DomainDnsName);
        var depth = Localizer[check.Depth == GpoDiagnosticsDepth.Full ? L.GpoDiagnostics.DepthFull : L.GpoDiagnostics.DepthQuick];
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\n<html lang=\"").Append(E(Localizer.Culture.TwoLetterISOLanguageName)).Append("\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">\n")
            .Append("<title>").Append(E(title)).Append("</title>\n<style>\n")
            .Append("body{font-family:'Segoe UI',sans-serif;margin:24px;color:#1b1b1b}h1{font-size:20px;margin:0 0 4px}")
            .Append(".sub{color:#5f5f5f;margin:0 0 16px}.note{background:#fff4ce;border-left:4px solid #9d5d00;padding:6px 10px;margin:4px 0}")
            .Append("table{border-collapse:collapse;width:100%}th,td{border:1px solid #d1d1d1;padding:6px 8px;text-align:left;vertical-align:top}")
            .Append("th{background:#f3f3f3}.sev{font-weight:600;white-space:nowrap}.Critical,.High{color:#c42b1c}.Medium{color:#9d5d00}")
            .Append(".Low{color:#005fb8}.Info{color:#5f5f5f}td p{margin:0 0 6px}td ul{margin:0;padding-left:18px}\n</style>\n</head>\n<body>\n");
        html.Append("<h1>").Append(E(title)).Append("</h1>\n<p class=\"sub\">")
            .Append(E(Localizer.Format(L.GpoDiagnostics.ReportSubtitle, check.Server, depth + ", " + check.At.ToString("g", Localizer.Culture))))
            .Append("<br>").Append(E(string.Join(" · ", Chips.Select(c => c.Text))));
        if (IsFiltered)
        {
            html.Append("<br>").Append(E(Localizer.Format(L.GpoDiagnostics.ReportFilters, rows.Count, _all.Count)));
        }

        html.Append("</p>\n");
        foreach (var note in Notes)
        {
            html.Append("<p class=\"note\">").Append(E(note)).Append("</p>\n");
        }

        html.Append("<table>\n<thead><tr><th>").Append(E(Localizer[L.GpoDiagnostics.ColumnSeverity])).Append("</th><th>")
            .Append(E(Localizer[L.GpoDiagnostics.ColumnArea])).Append("</th><th>").Append(E(Localizer[L.GroupPolicy.ColumnGpo])).Append("</th><th>")
            .Append(E(Localizer[L.GpoDiagnostics.ColumnWhere])).Append("</th><th>").Append(E(Localizer[L.GpoDiagnostics.ColumnFinding]))
            .Append("</th></tr></thead>\n<tbody>\n");
        foreach (var row in rows)
        {
            html.Append("<tr><td class=\"sev ").Append(row.Severity.ToString()).Append("\">").Append(E(row.SeverityText)).Append("</td><td>")
                .Append(E(row.AreaText)).Append("</td><td>").Append(E(row.GpoText)).Append("</td><td>").Append(E(row.WhereText)).Append("</td><td>")
                .Append("<p><strong>").Append(E(row.Title)).Append("</strong></p><p>").Append(E(row.Message)).Append("</p>")
                .Append("<p><em>").Append(E(Localizer[L.GpoDiagnostics.WhyHeading])).Append("</em> ").Append(E(row.Explanation)).Append("</p>")
                .Append("<p><em>").Append(E(Localizer[L.GpoDiagnostics.WhatHeading])).Append("</em> ").Append(E(row.Recommendation)).Append("</p><ul>");
            foreach (var line in row.AffectedLines.Append(row.FixText))
            {
                html.Append("<li>").Append(E(line)).Append("</li>");
            }

            html.Append("</ul></td></tr>\n");
        }

        html.Append("</tbody>\n</table>\n<p class=\"sub\">").Append(E(Localizer[L.GpoDiagnostics.ReportPrivacy])).Append("</p>\n</body>\n</html>\n");
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
