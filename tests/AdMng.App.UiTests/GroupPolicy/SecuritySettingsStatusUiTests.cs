using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.GroupPolicy;
using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.GroupPolicy.SecuritySettings;
using AdMng.Testing;
using static AdMng.App.UiTests.GroupPolicy.GroupPolicyStatusUiSupport;

namespace AdMng.App.UiTests.GroupPolicy;

/// <summary>
/// The status line of the security settings editor (track V, stage V2b-3; docs/dev/v-interface-renewal.md "V2b-3 as
/// built"), through the real tab of the main window over the fake directory: one line under the header - the numbers of
/// defined and changed settings, a file that changed on SYSVOL under the edits as the state of the tab with Discard as its
/// action, a save whose plan cannot be made with Retry that leads to the change plan preview - and the notes: which file
/// cannot be read (under the line) and what the audit pages leave open (on the audit page). A failing domain controller is
/// an exception of the fake session; every state that is asserted lasts until the test changes it. What the editor edits and
/// saves is covered by <see cref="GroupPolicyWindowTests"/> and <see cref="GroupPolicyEditingUiTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class SecuritySettingsStatusUiTests
{
    private const string AuditHeader = "Machine Name,Policy Target,Subcategory,Subcategory GUID,Inclusion Setting,Exclusion Setting,Setting Value";
    private const string GptTmpl = "Machine/Microsoft/Windows NT/SecEdit/GptTmpl.inf";
    private const string AuditCsv = "Machine/Microsoft/Windows NT/Audit/audit.csv";
    private const string GroupsXml = "Machine/Preferences/Groups/Groups.xml";

    [Fact]
    public Task TheEditor_HasOneStatusLine_UnderItsHeader_WithItsSummary_AndNoOldFeedback() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (main, sample) = await OpenMainAsync(context, gpo => PutTemplate(context, gpo));
        var (editor, view) = await OpenEditorAsync(main, sample);
        var line = view.PageStatus;

        // One status line, named PageStatus, over the status of the document: under the header, above the search and the pages.
        AssertTheLineOf(line, view, editor);
        Assert.True(TopOf(view.TitleText, view) < TopOf(line, view), "the status line is not under the header");
        Assert.True(TopOf(line, view) < TopOf(view.SearchBox, view), "the status line is not above the search box");
        Assert.True(TopOf(view.SearchBox, view) < TopOf(view.Pages, view), "the search box is not above the pages");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);

        // Nothing happens: how many settings are defined - in the line, and in the status bar nowhere.
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(editor.LoadedText, line.Message);
        Assert.StartsWith("Security settings: ", line.Message, StringComparison.Ordinal);
        Assert.Equal(AppIcons.StatusInfo, line.IconKey);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(string.Empty, main.TabStatusText.Text);
        Assert.Equal(string.Empty, main.TabSelectionText.Text);
        Assert.False(view.ChangedText.IsVisible);

        // Nothing could not be read, nothing is open: no note, no bar.
        Assert.False(view.FileNote.IsVisible);
        Assert.False(view.AuditNote.IsVisible);
        AssertNoNotes(view.DocumentNotes);
        AssertNoOldFeedback(view);

        // An edit joins the summary with the number of changed settings - the header says it as it did.
        editor.UserRights.Single(r => r.Constant == "SeBackupPrivilege").IsDefined = true;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal("Changed settings: 1 (not saved)", editor.SelectionText);
        Assert.Equal($"{editor.LoadedText} · {editor.SelectionText}", line.Message);
        Assert.True(view.ChangedText.IsVisible);
        Assert.Equal(editor.SelectionText, view.ChangedText.Text);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task WhatCannotBeReadAndWhatTheAuditPagesLeaveOpen_AreSaidInNotes_WhileTheLineKeepsTheSummary() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();

        // A Groups.xml that cannot be parsed, and both kinds of audit configured without forcing the subcategories.
        var (main, sample) = await OpenMainAsync(context, gpo =>
        {
            context.Sysvol.Put(gpo.SitePrinters, GptTmpl, FakeSysvolFileReader.Utf16(FakeDirectoryBuilder.Template("[Event Audit]", "AuditLogonEvents = 3")));
            context.Sysvol.PutText(gpo.SitePrinters, AuditCsv,
                AuditHeader + "\r\n,System,Audit Logon,{0cce9215-69ae-11d9-bed3-505054503030},Success and Failure,,3\r\n");
            context.Sysvol.PutText(gpo.SitePrinters, GroupsXml, "<Groups><niedomknięty>");
        });
        var (editor, view) = await OpenEditorAsync(main, sample);
        var line = view.PageStatus;

        // The line keeps the summary: that something cannot be read is a sentence about the content, not an event.
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(editor.LoadedText, line.Message);
        Assert.Null(editor.Status.Lasting);

        // The file that cannot be read: an attention note under the line, above the search and the pages - no bar.
        Assert.True(view.FileNote.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Attention"), view.FileNote.Style);
        Assert.Equal(editor.FileNote, view.FileText.Text);
        Assert.Contains("Groups.xml", view.FileText.Text, StringComparison.Ordinal);
        Assert.True(TopOf(line, view) < TopOf(view.FileNote, view), "the file note is not under the status line");
        Assert.True(TopOf(view.FileNote, view) < TopOf(view.SearchBox, view), "the file note is not above the search box");

        // The audit note stands on the audit page, above the audit rows, and only while that page is shown.
        Assert.False(view.AuditNote.IsVisible);
        view.Pages.SelectedItem = view.AuditTab;
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.NotNull(editor.AuditNote);
        Assert.True(view.AuditNote.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Attention"), view.AuditNote.Style);
        Assert.Equal(editor.AuditNote, view.AuditNoteText.Text);
        Assert.True(TopOf(view.AuditNote, view) < TopOf(view.LegacyAuditList, view), "the audit note is not above the audit rows");
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(editor.LoadedText, line.Message);
        AssertNoOldFeedback(view);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task AFileThatChangedUnderTheEdits_IsTheStateOfTheTab_WithDiscardAsItsAction_UntilTheEditsAreDiscarded() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (main, sample) = await OpenMainAsync(context, gpo => PutTemplate(context, gpo));
        var (editor, view) = await OpenEditorAsync(main, sample);
        var line = view.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var restTop = TopOf(view.Pages, view);
        editor.UserRights.Single(r => r.Constant == "SeBackupPrivilege").IsDefined = true;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // The header says how many settings are changed, which makes it a little higher than at rest.
        var pagesTop = TopOf(view.Pages, view);

        // Another administrator saved the template; the tab is read again (Refresh) on top of the edits.
        context.Sysvol.Put(sample.SitePrinters, GptTmpl, FakeSysvolFileReader.Utf16(FakeDirectoryBuilder.Template("[Privilege Rights]", "SeShutdownPrivilege = *S-1-5-32-545")));
        ControlHost.Press(view.RefreshButton);
        await UiWait.UntilAsync(() => !editor.IsLoading && line.Kind == PageStatusKind.Attention, "the conflict is said");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // The state of the tab: what happened, what it means for the save, what to do - with Discard, and not to be closed.
        Assert.Equal("Attention", line.Title);
        Assert.NotNull(editor.ConflictNote);
        Assert.StartsWith("The security settings of this GPO changed on ", line.Message, StringComparison.Ordinal);
        Assert.Equal(editor.ConflictNote, line.Message);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Discard changes", Assert.IsType<string>(action.Content));
        Assert.Same(editor.DiscardCommand, action.Command);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.True(editor.HasChanges); // the edits are kept
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(view),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text == editor.ConflictNote);
        AssertNoOldFeedback(view);
        view.UpdateLayout();
        Assert.Equal(pagesTop + GrowthOf(line), TopOf(view.Pages, view), 0.5);

        // Discard, pressed in the line: the operator is asked first; then the current file is shown and the summary is back.
        var drive = UiWait.DriveModalAsync<MessageDialogWindow>(dialog =>
        {
            dialog.ViewModel.ConfirmCommand.Execute(null);
            return Task.CompletedTask;
        });
        ControlHost.Press(action);
        await drive;
        await UiWait.UntilAsync(() => !editor.IsLoading && line.Kind == PageStatusKind.Idle, "the edits were discarded");
        Assert.Null(editor.ConflictNote);
        Assert.False(editor.HasChanges);
        Assert.True(editor.UserRights.Single(r => r.Constant == "SeShutdownPrivilege").IsDefined);
        Assert.Equal(editor.LoadedText, line.Message);
        Assert.Equal(Visibility.Collapsed, action.Visibility);
        view.UpdateLayout();
        Assert.Equal(restTop, TopOf(view.Pages, view), 0.5);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task ASaveWhosePlanCannotBeMade_SaysSoInTheLine_AndRetryLeadsToTheChangePlanPreview() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (main, sample) = await OpenMainAsync(context, gpo => PutTemplate(context, gpo));
        var (editor, view) = await OpenEditorAsync(main, sample);
        var line = view.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var session = context.Directory.Session;
        var restTop = TopOf(view.Pages, view);
        editor.UserRights.Single(r => r.Constant == "SeBackupPrivilege").IsDefined = true;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        var summary = line.Message;

        // The header says how many settings are changed, which makes it a little higher than at rest.
        var pagesTop = TopOf(view.Pages, view);
        Assert.True(view.SaveButton.IsEnabled);

        // The domain controller does not answer the read of the GPO any more: the plan of the save cannot be made.
        var gpo = sample.SitePrinters.ToString("B");
        var fail = 1;
        session.BeforeOperation = operation =>
        {
            if (Volatile.Read(ref fail) == 1 && operation.DistinguishedName.Contains(gpo, StringComparison.OrdinalIgnoreCase))
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "the domain controller does not answer");
            }
        };
        ControlHost.Press(view.SaveButton);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Error && !editor.Status.IsWorking && !editor.IsSaving, "the plan could not be made");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Said where the save was started - no dialog -: what was not done, why, what to do, the code. The editor is as it
        // was, with its edits, so the message can be closed; Retry makes the plan of the same save again.
        Assert.Equal("Error", line.Title);
        Assert.StartsWith($"Security settings of GPO {editor.GpoName} – nothing was changed: ", line.Message, StringComparison.Ordinal);
        Assert.EndsWith(" Press Retry to try again. (ServerUnavailable)", line.Message, StringComparison.Ordinal);
        Assert.Equal(AppIcons.StatusError, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Retry", Assert.IsType<string>(action.Content));
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Empty(Application.Current.Windows.OfType<MessageDialogWindow>());
        Assert.True(editor.HasChanges);
        Assert.True(view.SaveButton.IsEnabled);
        view.UpdateLayout();
        Assert.Equal(pagesTop + GrowthOf(line), TopOf(view.Pages, view), 0.5);
        AssertNoOldFeedback(view);

        // The domain controller answers again. Retry, pressed in the line: the plan is made and shown in the change plan
        // preview - while that window is open the line of the tab says its summary again - and executed there.
        Volatile.Write(ref fail, 0);
        var drive = UiWait.DriveModalAsync<ChangePlanPreviewWindow>(async preview =>
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.Equal(PageStatusKind.Idle, line.Kind);
            Assert.Equal(summary, line.Message);
            await preview.ViewModel.ExecuteCommand.ExecuteAsync(null);
            await UiWait.UntilAsync(() => preview.ViewModel.Step == PlanPreviewStep.Results, "the results of the save");
            Assert.False(preview.ViewModel.ResultHasFailures);
            preview.ViewModel.CloseCommand.Execute(null);
        });
        ControlHost.Press(action);
        await drive;
        await UiWait.UntilAsync(() => !editor.IsLoading && !editor.HasChanges && line.Kind == PageStatusKind.Idle, "the save was made and the tab read again");
        session.BeforeOperation = null;
        Assert.Contains("SeBackupPrivilege", GpoText.DecodeUnicodeText(context.Sysvol.Get(sample.SitePrinters, GptTmpl)!), StringComparison.Ordinal);
        Assert.Equal(editor.LoadedText, line.Message);
        view.UpdateLayout();
        Assert.Equal(restTop, TopOf(view.Pages, view), 0.5);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    /// <summary>A small template: a minimum password length and two rights.</summary>
    private static void PutTemplate(UiTestContext context, GroupPolicyManagementSample sample) =>
        context.Sysvol.Put(sample.SitePrinters, GptTmpl, FakeSysvolFileReader.Utf16(FakeDirectoryBuilder.Template(
            "[System Access]", "MinimumPasswordLength = 12",
            "[Privilege Rights]", "SeDebugPrivilege = *S-1-5-32-544", "SeShutdownPrivilege = *S-1-5-32-544")));

    private static Task<(SecuritySettingsEditorViewModel Editor, SecuritySettingsEditorView View)> OpenEditorAsync(MainWindow main, GroupPolicyManagementSample sample) =>
        OpenAsync<SecuritySettingsEditorViewModel, SecuritySettingsEditorView>(
            main, new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.SecuritySettings) { GpoId = sample.SitePrinters, Name = "Lokacja Warszawa – Drukarki" });
}
