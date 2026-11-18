using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.GroupPolicy;
using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.GroupPolicy;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.GroupPolicy;
using AdMng.Testing;
using static AdMng.App.UiTests.GroupPolicy.GroupPolicyStatusUiSupport;

namespace AdMng.App.UiTests.GroupPolicy;

/// <summary>
/// The status line of one GPO (track V, stage V2b-3; docs/dev/v-interface-renewal.md "V2b-3 as built"), through the
/// real tab of the main window over the fake directory: one line above the tabs of the GPO - the summary of the GPO
/// and, while the "Status" tab is shown, what that tab says: the check of the GPO with Stop, when it was checked, a
/// check that was stopped; a GPO that is gone as the state of the document, with "Delete the remaining folder" as the
/// action of the line. The health panel the view hosts has no status of its own left: what a check could not evaluate
/// is a note above the findings. A slow domain controller is a read the test holds and releases; every state that is
/// asserted lasts until the test changes it. What the tabs show is covered by <see cref="GroupPolicyWindowTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GroupPolicyObjectStatusUiTests
{
    [Fact]
    public Task TheTab_HasOneStatusLine_AboveItsTabs_AndOnTheStatusTabItSaysWhenTheGpoWasChecked() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (main, sample) = await OpenMainAsync(context);
        var (document, view) = await OpenGpoAsync(main, sample.WorkstationPasswords, "Stacje robocze – Zasady haseł");
        var line = view.PageStatus;
        var panel = view.HealthPanel;

        // One status line, named PageStatus, over the status of the document: under the header, above the tabs.
        AssertTheLineOf(line, view, document);
        Assert.True(TopOf(view.RefreshButton, view) < TopOf(line, view), "the status line is not under the header");
        Assert.True(TopOf(line, view) < TopOf(view.GpoTabs, view), "the status line is not above the tabs");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);
        var tabsTop = TopOf(view.GpoTabs, view);

        // On the first tab: the summary of the GPO - in the line, not in the status bar of the window.
        Assert.Same(view.ScopeTab, view.GpoTabs.SelectedItem);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.NotEqual(string.Empty, document.LoadedText);
        Assert.Equal(document.LoadedText, line.Message);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(string.Empty, main.TabStatusText.Text);
        AssertNoNotes(view.DocumentNotes);

        // The "Status" tab checks the GPO; afterwards the line says what the tab says: when it was checked and how many
        // findings there are. The panel has no text and no bar of its own for that any more.
        view.GpoTabs.SelectedItem = view.StatusTab;
        await UiWait.UntilAsync(() => document.Checks.Count > 0 && !document.IsCheckingHealth && line.Kind == PageStatusKind.Idle, "the check of the Status tab");
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.Same(document.HealthStatus, document.Status.Front);
        Assert.StartsWith("Last check: full, ", line.Message, StringComparison.Ordinal);
        Assert.Equal(document.HealthStatusText, line.Message);
        Assert.NotEmpty(panel.ChecksList.Items);
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(view),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text == document.HealthStatusText);
        Assert.True(panel.HealthHintText.IsVisible);
        Assert.False(panel.HealthNote.IsVisible);

        // "Check again" stays a button: it never had a Stop of its own - a running check is stopped in the line.
        Assert.IsNotType<ActionButton>(panel.CheckHealthButton);
        Assert.Same(document.CheckHealthCommand, panel.CheckHealthButton.Command);
        Assert.True(panel.CheckHealthButton.IsEnabled);
        AssertNoOldFeedback(view);

        // Back on the first tab the line is the document's again; the tabs never moved.
        view.GpoTabs.SelectedItem = view.ScopeTab;
        await UiWait.UntilAsync(() => line.Message == document.LoadedText, "the summary of the document");
        Assert.Null(document.Status.Front);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        view.UpdateLayout();
        Assert.Equal(tabsTop, TopOf(view.GpoTabs, view));

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheCheckOfTheStatusTab_IsWorkingWithStop_InTheLineOfTheTab_AndAStoppedCheckOffersCheckAgain() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (main, sample) = await OpenMainAsync(context);
        var (document, view) = await OpenGpoAsync(main, sample.DriveMappings, "Użytkownicy – Mapowanie dysków");
        var line = view.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        var tabsTop = TopOf(view.GpoTabs, view);
        Assert.Equal(PageStatusKind.Idle, line.Kind);

        // The tab is selected: it checks the GPO, and the first read of that check waits for the test.
        using (var held = new HeldRead(context.Directory.Session, IsReadUnderThePolicies))
        {
            view.GpoTabs.SelectedItem = view.StatusTab;
            await UiWait.UntilAsync(() => held.IsReached && document.IsCheckingHealth && line.Kind == PageStatusKind.Working, "the GPO is being checked");

            // Working - the operation of the tab, said by the line of the document, with Stop; the document itself does
            // not work and the tabs did not move.
            Assert.Equal("Working", line.Title);
            Assert.NotEqual(string.Empty, line.Message);
            Assert.Same(document.HealthStatus.Current, line.Status);
            Assert.StartsWith("GpoDiag.HealthChecking", document.HealthStatus.Current.Log.ToString(), StringComparison.Ordinal);
            Assert.True(document.HealthStatus.IsWorking);
            Assert.False(document.Status.IsWorking);
            Assert.False(document.IsLoading);
            Assert.True(ring.IsActive);
            Assert.Equal(Visibility.Visible, action.Visibility);
            Assert.Equal("Stop", Assert.IsType<string>(action.Content));
            Assert.Same(document.HealthStatus.CancelCommand, action.Command);
            Assert.Equal(Visibility.Collapsed, close.Visibility);
            Assert.False(view.HealthPanel.CheckHealthButton.IsEnabled);
            AssertNoOldFeedback(view);
            view.UpdateLayout();
            Assert.Equal(tabsTop, TopOf(view.GpoTabs, view));

            // Stop, pressed in the line: "Stopping…" at once - the read that is on its way still has to return.
            ControlHost.Press(action);
            await UiWait.UntilAsync(() => document.HealthStatus.IsCancelling, "the check is stopping");
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.Equal("Stopping…", line.Message);
            Assert.Equal(Visibility.Collapsed, action.Visibility);
        }

        await UiWait.UntilAsync(() => !document.IsCheckingHealth && line.Kind == PageStatusKind.Attention, "the check was stopped");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Stopped: a result that can be closed, with the button's own text as the way to check again.
        Assert.Equal("Attention", line.Title);
        Assert.Equal("Checking the GPO was stopped: the findings were not updated. Press “Check again” to check it again.", line.Message);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.False(ring.IsActive);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Check again", Assert.IsType<string>(action.Content));
        Assert.Same(document.CheckHealthCommand, action.Command);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.True(view.HealthPanel.CheckHealthButton.IsEnabled);
        view.UpdateLayout();
        Assert.Equal(tabsTop + GrowthOf(line), TopOf(view.GpoTabs, view), 0.5);

        // "Check again", pressed in the line, checks the GPO: the summary of the tab, and the tabs are where they were.
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !document.IsCheckingHealth && document.HealthFindings.Count > 0 && line.Kind == PageStatusKind.Idle, "the GPO was checked");
        Assert.StartsWith("Last check: full, ", line.Message, StringComparison.Ordinal);
        Assert.NotEmpty(view.HealthPanel.HealthFindingsList.Items);
        view.UpdateLayout();
        Assert.Equal(tabsTop, TopOf(view.GpoTabs, view), 0.5);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task AGpoThatIsGone_IsSaidByTheLine_AndTheFolderLeftBehindIsDeletedFromThere() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (main, sample) = await OpenMainAsync(context);
        var id = sample.WorkstationRemoteDesktop;
        await context.Directory.Session.DeleteAsync(GpLinkValue.GpoDn(id, context.Directory.DomainDn), subtree: true, CancellationToken.None);
        Assert.True(context.Sysvol.FolderExists(id));
        var (document, view) = await OpenGpoAsync(main, id, "Stacje robocze – Pulpit zdalny");
        var line = view.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");

        // Gone from the directory, its folder still in SYSVOL: the state of the tab, with the one thing to do as the
        // action of the line - there is no bar with a button of its own any more.
        Assert.True(document.IsMissing);
        Assert.Equal(PageStatusKind.Error, line.Kind);
        Assert.Equal("The GPO no longer exists in Active Directory, but its SYSVOL folder is still there.", line.Message);
        Assert.Equal(AppIcons.StatusError, line.IconKey);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Delete the remaining folder", Assert.IsType<string>(action.Content));
        Assert.Same(document.DeleteOrphanedFolderCommand, action.Command);
        Assert.True(action.IsEnabled);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.DoesNotContain(
            UiWait.Descendants<Button>(view),
            button => button.IsVisible && !button.IsDescendantOf(line) && button.Content as string == "Delete the remaining folder");
        AssertNoOldFeedback(view);

        // The action plans, previews and executes like every change; afterwards the GPO is just gone.
        var drive = UiWait.DriveModalAsync<ChangePlanPreviewWindow>(async preview =>
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            await preview.ViewModel.ExecuteCommand.ExecuteAsync(null);
            await UiWait.UntilAsync(() => preview.ViewModel.Step == PlanPreviewStep.Results, "the results of the change");
            Assert.False(preview.ViewModel.ResultHasFailures);
            preview.ViewModel.CloseCommand.Execute(null);
        });
        ControlHost.Press(action);
        await drive;
        await UiWait.UntilAsync(() => !document.IsLoading && !document.HasOrphanedFolder && line.Message == "The GPO no longer exists.", "the folder was deleted");
        Assert.False(context.Sysvol.FolderExists(id));
        Assert.Equal(PageStatusKind.Error, line.Kind);
        Assert.Equal(Visibility.Collapsed, action.Visibility);
        Assert.Equal(Visibility.Collapsed, close.Visibility);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task WithoutSysvol_TheTabAndItsFindingsSaySoInNotes_AndTheLineKeepsItsSummary() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (main, sample) = await OpenMainAsync(context);
        context.Sysvol.Unavailable = SysvolUnavailableReason.Unreachable;
        var (document, view) = await OpenGpoAsync(main, sample.WorkstationPasswords, "Stacje robocze – Zasady haseł");
        var line = view.PageStatus;
        var notes = view.DocumentNotes;
        var panel = view.HealthPanel;

        // Above the tabs: why SYSVOL is not available - a note, while the line says the summary of the GPO.
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(document.LoadedText, line.Message);
        Assert.True(notes.SysvolNote.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Attention"), notes.SysvolNote.Style);
        Assert.Equal(document.SysvolWarning, notes.SysvolText.Text);
        Assert.True(TopOf(line, view) < TopOf(notes.SysvolNote, view), "the SYSVOL note is not under the status line");
        Assert.True(TopOf(notes.SysvolNote, view) < TopOf(view.GpoTabs, view), "the SYSVOL note is not above the tabs");

        // On the "Status" tab: what the quick check could not evaluate - a note above the findings, not a bar; the line
        // says when the GPO was checked.
        view.GpoTabs.SelectedItem = view.StatusTab;
        await UiWait.UntilAsync(() => document.Checks.Count > 0 && !document.IsCheckingHealth && line.Kind == PageStatusKind.Idle, "the check of the Status tab");
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.StartsWith("Last check: quick, ", line.Message, StringComparison.Ordinal);
        Assert.Null(document.HealthStatus.Lasting);
        Assert.True(panel.HealthNote.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Attention"), panel.HealthNote.Style);
        Assert.True(panel.HealthNoteText.IsVisible);
        Assert.NotNull(document.HealthNote);
        Assert.Equal(document.HealthNote, panel.HealthNoteText.Text);
        Assert.Contains("SYSVOL unavailable", panel.HealthNoteText.Text, StringComparison.Ordinal);
        Assert.True(TopOf(panel.HealthNote, view) < TopOf(panel.ChecksList, view), "the note is not above the results of the check");
        AssertNoOldFeedback(view);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    /// <summary>A read of the GPOs: of the container CN=Policies,CN=System or of an object under it.</summary>
    private static bool IsReadUnderThePolicies(FakeOperation operation) =>
        operation.Kind is FakeOperationKind.Search or FakeOperationKind.Get
        && operation.DistinguishedName.Contains("CN=Policies,CN=System,", StringComparison.OrdinalIgnoreCase);

    private static Task<(GroupPolicyObjectDocumentViewModel Document, GroupPolicyObjectView View)> OpenGpoAsync(MainWindow main, Guid id, string name) =>
        OpenAsync<GroupPolicyObjectDocumentViewModel, GroupPolicyObjectView>(
            main, new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Object) { GpoId = id, Name = name });
}
