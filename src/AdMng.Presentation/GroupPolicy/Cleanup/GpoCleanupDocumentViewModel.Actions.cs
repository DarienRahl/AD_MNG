using System.Text;
using AdMng.Core.GroupPolicy.Cleanup;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.GroupPolicy.Copy;
using AdMng.Presentation.GroupPolicy.Search;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy.Cleanup;

/// <summary>The actions of a proposal (each a plan with preview and undo), "Otwórz GPO", "Porównaj", "Pokaż w wyszukiwarce", "Kopiuj" and the exports.</summary>
public sealed partial class GpoCleanupDocumentViewModel
{
    /// <summary>"Usuń GPO…": refused when the GPO changed since the analysis (the index is read again), else the backup offer and the typed confirmation.</summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        if (SelectedRow is not { Entry: { } entry } row || row.ExpectedState() is not { } expected)
        {
            return;
        }

        var result = await GpoSettingsActions.DeleteCheckedCoreAsync(Actions, entry.Gpo, expected).ConfigureAwait(true);
        if (result != GpoCheckedDeleteResult.NotDeleted)
        {
            await AfterActionAsync(row).ConfigureAwait(true);
        }
    }

    private bool CanDelete() => CanAct(GpoCleanupAction.Delete);

    /// <summary>"Odłącz wszędzie…".</summary>
    [RelayCommand(CanExecute = nameof(CanUnlink))]
    private Task UnlinkAsync() => RunActionAsync(row => Actions.UnlinkAllAsync(row.Entry!.Gpo));

    private bool CanUnlink() => CanAct(GpoCleanupAction.Unlink);

    /// <summary>"Wyłącz GPO": all settings disabled (reversible).</summary>
    [RelayCommand(CanExecute = nameof(CanDisable))]
    private Task DisableAsync() => RunActionAsync(row => Actions.SetStatusAsync(row.Entry!.Gpo, GpoStatus.AllSettingsDisabled));

    private bool CanDisable() => CanAct(GpoCleanupAction.Disable);

    /// <summary>"Usuń duplikat z tego GPO": the duplicated setting removed from the covered GPO (the keeper still sets it).</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveSetting))]
    private Task RemoveSettingAsync() => RunActionAsync(row => row.Proposal.UnitBaseId is { } baseId && Texts is { } texts
        ? Actions.RemoveSettingsAsync(row.Entry!.Gpo, GpoCopyItems.OfBaseId(texts, row.Entry, baseId), Definitions)
        : Task.FromResult(false));

    private bool CanRemoveSetting() => CanAct(GpoCleanupAction.RemoveSetting) && SelectedRow?.Proposal.UnitBaseId is not null;

    /// <summary>"Kopiuj do innego GPO…": the GPO's settings into a GPO the operator picks (then "Wyłącz GPO" as a separate step).</summary>
    [RelayCommand(CanExecute = nameof(CanCopyTo))]
    private Task CopyToAsync() => RunActionAsync(row => Texts is { } texts
        ? Actions.CopySettingsAsync(GpoCopyItems.OfGpo(texts, row.Entry!), null, Definitions)
        : Task.FromResult(false));

    private bool CanCopyTo() => CanAct(GpoCleanupAction.CopyTo);

    /// <summary>"Scal z…": the GPO merged into the proposal's target (copy, relink, disable – one plan).</summary>
    [RelayCommand(CanExecute = nameof(CanMerge))]
    private Task MergeAsync() => RunActionAsync(row => Texts is { } texts && row.Related is [var target, ..]
        ? Actions.MergeAsync(row.Entry!.Gpo, target.Gpo, GpoCopyItems.OfGpo(texts, row.Entry), Definitions)
        : Task.FromResult(false));

    private bool CanMerge() => CanAct(GpoCleanupAction.Merge) && SelectedRow is { HasRelated: true };

    /// <summary>"Otwórz GPO": the GPO's document (the "Ustawienia" tab when the proposal is about a setting).</summary>
    [RelayCommand(CanExecute = nameof(CanOpenGpo))]
    private void OpenGpo()
    {
        if (SelectedRow is { Entry: { } entry } row)
        {
            Actions.OpenGpo(entry.Gpo.Id, entry.Gpo.DisplayName, row.HasSetting ? GpoDocumentTab.Settings : GpoDocumentTab.Status);
        }
    }

    private bool CanOpenGpo() => SelectedRow is { GpoExists: true };

    /// <summary>"Porównaj": the GPO with the first related GPO (or one picked).</summary>
    [RelayCommand(CanExecute = nameof(CanOpenGpo))]
    private Task CompareAsync() => SelectedRow is { Entry: { } entry } row
        ? Actions.CompareAsync(entry.Gpo, row.Related is [var other, ..] ? other.Gpo : null)
        : Task.CompletedTask;

    /// <summary>"Pokaż w wyszukiwarce": "Wyszukiwarka ustawień GPO" with the setting's name.</summary>
    [RelayCommand(CanExecute = nameof(CanShowInSearch))]
    private void ShowInSearch()
    {
        if (SelectedRow is { SettingName: { } name })
        {
            Actions.OpenSettingsSearch(name);
        }
    }

    private bool CanShowInSearch() => SelectedRow is { HasSetting: true };

    /// <summary>"Kopiuj": the proposal's details as text.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Copy()
    {
        if (SelectedRow is { } row && Gp.Ui.Clipboard.SetText(DetailsText(row)))
        {
            Gp.Ui.Notifications.Show(NotificationSeverity.Info, Localizer[L.GpoCleanup.Copied]);
        }
    }

    /// <summary>The details of a proposal as the "Kopiuj" text.</summary>
    public string DetailsText(GpoCleanupRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.DetailsText(Localizer[L.GpoCleanup.WhyHeading], Localizer[L.GpoCleanup.WhatHeading], Localizer[L.GpoCleanup.NotesHeading]);
    }

    /// <summary>The texts of the shown index view (null before the first analysis).</summary>
    private GpoIndexTexts? Texts => _shown is { } shown ? GpoIndexTexts.For(Localizer, shown.View) : null;

    /// <summary>The definition set the shown units were built with (copy, remove and merge plan with it).</summary>
    private Core.GroupPolicy.AdministrativeTemplates.PolicyDefinitionSet? Definitions => _shown?.View.Index.Definitions;

    private bool CanAct(GpoCleanupAction action) => SelectedRow?.Offers(action) == true && HasSession && !IsLoading && _shown is not null;

    /// <summary>Runs an action of the selected proposal; after it wrote something the index is refreshed and analysed again.</summary>
    private async Task RunActionAsync(Func<GpoCleanupRowViewModel, Task<bool>> action)
    {
        if (SelectedRow is not { GpoExists: true } row)
        {
            return;
        }

        if (await action(row).ConfigureAwait(true))
        {
            await AfterActionAsync(row).ConfigureAwait(true);
        }
    }

    /// <summary>The GPOs that changed are read again and analysed; the selection stays near the proposal's place.</summary>
    private Task AfterActionAsync(GpoCleanupRowViewModel row)
    {
        _restoreSelection = (row.Key, IndexOf(Rows, row));
        return AnalyzeAsync(GpoIndexRefresh.Incremental);
    }

    // ---- Export ------------------------------------------------------------------------------------------------------------

    /// <summary>"Eksportuj do CSV…": the proposals the list shows (with the filters) to a file the operator picks (rule 1).</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportCsvAsync()
    {
        var title = Localizer[L.GpoCleanup.ExportCsv];
        var path = await Gp.Ui.Files.SaveFileAsync(title, SuggestedFileName(".csv"), [new FileFilter(Localizer[L.Files.CsvFilter], "*.csv")])
            .ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        var rows = Rows.Select(r => (IReadOnlyList<string>)
            [r.Title, r.GpoName, r.RelatedText, r.SettingName ?? string.Empty, r.SettingPath ?? string.Empty, r.Message, r.Recommendation,
                string.Join(" | ", r.NoteTexts), r.ActionsText]).ToList();
        var headers = new[]
        {
            L.GpoCleanup.ColumnKind, L.GroupPolicy.ColumnGpo, L.GpoCleanup.ColumnRelated, L.GpoCleanup.SettingHeading, L.GpoCleanup.ColumnPath,
            L.GpoCleanup.WhyHeading, L.GpoCleanup.WhatHeading, L.GpoCleanup.NotesHeading, L.GpoCleanup.ColumnActions,
        }.Select(k => Localizer[k]).ToList();
        var delimiter = CsvFiles.ListSeparator(Gp.Ui.Regional.Culture);
        await WriteAsync(title, path, rows.Count, writer => _csv.Write(writer, headers, rows, delimiter, protectFormulas: true)).ConfigureAwait(true);
    }

    /// <summary>"Eksportuj do HTML…": a self-contained report (every value escaped, no scripts, no external resources).</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportHtmlAsync()
    {
        var title = Localizer[L.GpoCleanup.ExportHtml];
        var path = await Gp.Ui.Files.SaveFileAsync(title, SuggestedFileName(".html"), [new FileFilter(Localizer[L.GpoDiagnostics.HtmlFilter], "*.html")])
            .ConfigureAwait(true);
        if (path is null || _shown is not { } shown)
        {
            return;
        }

        var html = HtmlReport(shown, Rows);
        await WriteAsync(title, path, Rows.Count, writer => writer.Write(html)).ConfigureAwait(true);
    }

    private bool CanExport() => _shown is not null && Rows.Count > 0 && !IsLoading;

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
        var name = ListExportSnapshot.SuggestFileName(Localizer.Format(L.GpoCleanup.ReportTitle, domain), Gp.Time.GetLocalNow());
        return Path.ChangeExtension(name, extension);
    }

    /// <summary>The HTML report of <paramref name="rows"/>: one self-contained page, every text HTML-encoded.</summary>
    private string HtmlReport(Analysis shown, IReadOnlyList<GpoCleanupRowViewModel> rows)
    {
        var index = shown.View.Index;
        var title = Localizer.Format(L.GpoCleanup.ReportTitle, index.DomainDnsName);
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\n<html lang=\"").Append(E(Localizer.Culture.TwoLetterISOLanguageName)).Append("\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">\n")
            .Append("<title>").Append(E(title)).Append("</title>\n<style>\n")
            .Append("body{font-family:'Segoe UI',sans-serif;margin:24px;color:#1b1b1b}h1{font-size:20px;margin:0 0 4px}")
            .Append(".sub{color:#5f5f5f;margin:0 0 16px}.note{background:#fff4ce;border-left:4px solid #9d5d00;padding:6px 10px;margin:4px 0}")
            .Append("table{border-collapse:collapse;width:100%}th,td{border:1px solid #d1d1d1;padding:6px 8px;text-align:left;vertical-align:top}")
            .Append("th{background:#f3f3f3}td p{margin:0 0 6px}td ul{margin:0;padding-left:18px}.kind{font-weight:600;white-space:nowrap}\n</style>\n</head>\n<body>\n");
        html.Append("<h1>").Append(E(title)).Append("</h1>\n<p class=\"sub\">")
            .Append(E(Localizer.Format(L.GpoCleanup.ReportSubtitle, index.Server, index.BuiltAt.ToLocalTime().ToString("g", Localizer.Culture))))
            .Append("<br>").Append(E(string.Join(" · ", Chips.Where(c => c.HasProposals).Select(c => c.Text))));
        if (IsFiltered)
        {
            html.Append("<br>").Append(E(Localizer.Format(L.GpoCleanup.ReportFilters, rows.Count, _all.Count)));
        }

        html.Append("</p>\n");
        foreach (var note in Notes)
        {
            html.Append("<p class=\"note\">").Append(E(note)).Append("</p>\n");
        }

        html.Append("<table>\n<thead><tr><th>").Append(E(Localizer[L.GpoCleanup.ColumnKind])).Append("</th><th>")
            .Append(E(Localizer[L.GroupPolicy.ColumnGpo])).Append("</th><th>").Append(E(Localizer[L.GpoCleanup.ColumnProposal])).Append("</th><th>")
            .Append(E(Localizer[L.GpoCleanup.ColumnActions])).Append("</th></tr></thead>\n<tbody>\n");
        foreach (var row in rows)
        {
            html.Append("<tr><td class=\"kind\">").Append(E(row.Title)).Append("</td><td>").Append(E(row.GpoName));
            if (row.RelatedText.Length > 0)
            {
                html.Append("<br>").Append(E(Localizer.Format(L.GpoCleanup.RelatedLine, row.RelatedText)));
            }

            html.Append("</td><td>");
            if (row.SettingName is not null)
            {
                html.Append("<p><strong>").Append(E(row.SettingName)).Append("</strong> – ").Append(E(row.SettingPath)).Append("</p>");
            }

            html.Append("<p>").Append(E(row.Message)).Append("</p><p><em>").Append(E(Localizer[L.GpoCleanup.WhatHeading])).Append("</em> ")
                .Append(E(row.Recommendation)).Append("</p>");
            if (row.NoteTexts.Count > 0)
            {
                html.Append("<ul>");
                foreach (var note in row.NoteTexts)
                {
                    html.Append("<li>").Append(E(note)).Append("</li>");
                }

                html.Append("</ul>");
            }

            html.Append("</td><td>").Append(E(row.ActionsText)).Append("</td></tr>\n");
        }

        html.Append("</tbody>\n</table>\n<p class=\"sub\">").Append(E(Localizer[L.GpoCleanup.ReportPrivacy])).Append("</p>\n</body>\n</html>\n");
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
