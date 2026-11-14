using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.GroupPolicy;
using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.GroupPolicy.AdministrativeTemplates;
using AdMng.Testing;
using static AdMng.App.UiTests.GroupPolicy.GroupPolicyStatusUiSupport;

namespace AdMng.App.UiTests.GroupPolicy;

/// <summary>
/// The status line of "Group Policy Objects" (track V, stage V2b-3; docs/dev/v-interface-renewal.md "V2b-3 as built"),
/// through the real tab of the main window over the fake directory: one line under the toolbar - the summary of the
/// list with the selection, the read with Stop, a read that was stopped or failed as the state of the tab with its
/// action - the status bar of the window that does not repeat it, and the two notes that are no status: why SYSVOL is
/// not available and where changes are made. A slow domain controller is a read the test holds and releases, a failing
/// one an exception of the fake session; every state that is asserted lasts until the test changes it. What the list
/// shows and does is covered by <see cref="GroupPolicyWindowTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GroupPolicyObjectsStatusUiTests
{
    private static readonly GroupPolicyDocumentRequest Objects = new(GroupPolicyDocumentKind.Objects);

    [Fact]
    public Task TheTab_HasOneStatusLine_UnderItsToolbar_WithTheSummary_AndTheStatusBarDoesNotRepeatIt() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (main, _) = await OpenMainAsync(context);
        var (document, view) = await OpenAsync<GroupPolicyObjectsDocumentViewModel, GroupPolicyObjectsView>(main, Objects);
        var line = view.PageStatus;

        // One status line, named PageStatus, over the status of the document, between the toolbar and the list.
        AssertTheLineOf(line, view, document);
        Assert.IsAssignableFrom<IHasPageStatus>(document);
        Assert.True(TopOf(view.RefreshButton, view) < TopOf(line, view), "the status line is not under the toolbar");
        Assert.True(TopOf(line, view) < TopOf(view.GpoList, view), "the status line is not above the list");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);

        // Nothing happens: what was read - in the line, and not in the status bar of the window.
        Assert.Equal(7, document.Rows.Count);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal("7 group policy objects, 1 with different versions in AD and SYSVOL", line.Message);
        Assert.Equal(document.StatusText, line.Message);
        Assert.Equal(AppIcons.StatusInfo, line.IconKey);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(document.StatusText, main.Shell.StatusText);
        Assert.Equal(string.Empty, main.TabStatusText.Text);
        Assert.Equal(string.Empty, main.TabSelectionText.Text);

        // A selection joins the summary - in the line, not in the status bar.
        view.GpoList.SelectedItems.Add(document.Rows[0]);
        view.GpoList.SelectedItems.Add(document.Rows[1]);
        await UiWait.UntilAsync(() => line.Message.EndsWith(" · 2 selected", StringComparison.Ordinal), "the selection in the status line");
        Assert.StartsWith(document.StatusText, line.Message, StringComparison.Ordinal);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal("2 selected", main.Shell.SelectionText);
        Assert.Equal(string.Empty, main.TabSelectionText.Text);
        Assert.Equal(string.Empty, main.TabStatusText.Text);

        // SYSVOL is there and the session is on the PDC emulator: the notes under the line say nothing. No banner and
        // no progress bar is left, also not in the control the view hosts.
        Assert.True(TopOf(line, view) < TopOf(view.GpoList, view));
        AssertNoNotes(view.DocumentNotes);
        AssertNoOldFeedback(view);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task ReadingTheList_IsWorkingWithStop_AndAStoppedReadIsTheStateOfTheTab_WithRefresh() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (main, _) = await OpenMainAsync(context);
        var (document, view) = await OpenAsync<GroupPolicyObjectsDocumentViewModel, GroupPolicyObjectsView>(main, Objects);
        var line = view.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        var listTop = TopOf(view.GpoList, view);

        // The first read of the list - the GPOs under CN=Policies - waits for the test.
        using (var held = new HeldRead(context.Directory.Session, IsReadOfThePolicies))
        {
            ControlHost.Press(view.RefreshButton);
            await UiWait.UntilAsync(() => held.IsReached && document.IsLoading && line.Kind == PageStatusKind.Working, "the list is being read");

            // Working: what is read - with Stop, and nothing to close.
            Assert.Equal("Working", line.Title);
            Assert.Equal("Reading “Group Policy Objects”…", line.Message);
            Assert.True(ring.IsActive);
            Assert.Equal(Visibility.Visible, action.Visibility);
            Assert.Equal("Stop", Assert.IsType<string>(action.Content));
            Assert.Same(document.Status.CancelCommand, action.Command);
            Assert.Equal(Visibility.Collapsed, close.Visibility);
            AssertNoOldFeedback(view);

            // The line was there before: the list did not move. The status bar still says nothing about the tab.
            view.UpdateLayout();
            Assert.Equal(listTop, TopOf(view.GpoList, view));
            Assert.Equal(string.Empty, main.TabStatusText.Text);

            // Stop, pressed in the line: "Stopping…" at once - the read that is on its way to the domain controller
            // still has to return.
            ControlHost.Press(action);
            await UiWait.UntilAsync(() => document.Status.IsCancelling, "the read is stopping");
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.Equal("Stopping…", line.Message);
            Assert.Equal(Visibility.Collapsed, action.Visibility);
            Assert.True(document.IsLoading);
        }

        await UiWait.UntilAsync(() => !document.IsLoading && line.Kind == PageStatusKind.Attention, "the read was stopped");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Stopped: the state of the tab until it is read again - what it may lack, and Refresh. It cannot be closed.
        Assert.Equal("Attention", line.Title);
        Assert.Equal("Reading “Group Policy Objects” was stopped: the tab may not show everything. Press Refresh to read it again.", line.Message);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.False(ring.IsActive);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Refresh", Assert.IsType<string>(action.Content));
        Assert.Same(document.RefreshCommand, action.Command);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.Equal(7, view.GpoList.Items.Count);
        view.UpdateLayout();
        Assert.Equal(listTop + GrowthOf(line), TopOf(view.GpoList, view), 0.5);

        // Refresh, pressed in the line, reads the list: the summary again, and the list is where it was.
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !document.IsLoading && line.Kind == PageStatusKind.Idle, "the list was read again");
        Assert.Equal(document.StatusText, line.Message);
        view.UpdateLayout();
        Assert.Equal(listTop, TopOf(view.GpoList, view), 0.5);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task AReadThatFails_IsAnErrorWithRetry_UntilTheListIsReadAgain() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (main, _) = await OpenMainAsync(context);
        var (document, view) = await OpenAsync<GroupPolicyObjectsDocumentViewModel, GroupPolicyObjectsView>(main, Objects);
        var line = view.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var session = context.Directory.Session;

        // The domain controller does not answer the list of GPOs any more.
        var fail = 1;
        session.BeforeOperation = operation =>
        {
            if (Volatile.Read(ref fail) == 1 && IsReadOfThePolicies(operation))
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "the domain controller does not answer");
            }
        };
        ControlHost.Press(view.RefreshButton);
        await UiWait.UntilAsync(() => !document.IsLoading && line.Kind == PageStatusKind.Error, "the read failed");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // The line says why the tab does not show the list: what was tried, the reason, what to do, the code - with
        // Retry, and it cannot be closed.
        Assert.Equal("Error", line.Title);
        Assert.NotNull(document.Error);
        Assert.Equal($"Could not read “Group Policy Objects”: {document.Error} Press Retry to try again. (ServerUnavailable)", line.Message);
        Assert.Equal(AppIcons.StatusError, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Retry", Assert.IsType<string>(action.Content));
        Assert.Same(document.RefreshCommand, action.Command);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(view),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text == document.Error);
        AssertNoOldFeedback(view);

        // Retry, pressed in the line, reads the list.
        Volatile.Write(ref fail, 0);
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !document.IsLoading && line.Kind == PageStatusKind.Idle, "the list was read");
        session.BeforeOperation = null;
        Assert.Equal(7, document.Rows.Count);
        Assert.Equal(document.StatusText, line.Message);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task ThatSysvolIsNotAvailable_AndWhereChangesAreMade_AreNotesUnderTheLine_NotStatus() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (main, _) = await OpenMainAsync(context);
        SeedAnotherPdcEmulator(context);
        context.Sysvol.Unavailable = SysvolUnavailableReason.Unreachable;
        var (document, view) = await OpenAsync<GroupPolicyObjectsDocumentViewModel, GroupPolicyObjectsView>(main, Objects);
        var line = view.PageStatus;
        var notes = view.DocumentNotes;

        // The line keeps the summary: neither sentence is something the tab did.
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(document.StatusText, line.Message);
        Assert.Null(document.Status.Lasting);

        // Why SYSVOL is not available: an attention note, under the line and above the list.
        Assert.True(notes.SysvolNote.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Attention"), notes.SysvolNote.Style);
        Assert.True(notes.SysvolText.IsVisible);
        Assert.NotNull(document.SysvolWarning);
        Assert.Equal(document.SysvolWarning, notes.SysvolText.Text);
        Assert.Contains("SYSVOL unavailable", notes.SysvolText.Text, StringComparison.Ordinal);
        Assert.True(TopOf(line, view) < TopOf(notes.SysvolNote, view), "the SYSVOL note is not under the status line");
        Assert.True(TopOf(notes.SysvolNote, view) < TopOf(view.GpoList, view), "the SYSVOL note is not above the list");
        Assert.False(view.NewButton.IsEnabled);

        // Where changes are made: an information note with "Change Domain Controller…" as a link right after it.
        Assert.True(notes.PdcPanel.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Info"), notes.PdcNote.Style);
        Assert.NotNull(document.PdcNote);
        Assert.Equal(document.PdcNote, notes.PdcText.Text);
        Assert.Contains("dc02.contoso.test", notes.PdcText.Text, StringComparison.Ordinal);
        Assert.True(notes.ChangeDcButton.IsVisible);
        Assert.True(notes.ChangeDcButton.IsEnabled);
        Assert.Same(Application.Current.FindResource("Ad.LinkButton"), notes.ChangeDcButton.Style);
        Assert.Equal("Change Domain Controller…", Assert.IsType<string>(notes.ChangeDcButton.Content));
        Assert.Same(document.ChangeDomainControllerCommand, notes.ChangeDcButton.Command);
        Assert.True(LeftOf(notes.PdcText, view) < LeftOf(notes.ChangeDcButton, view), "the link is not after the sentence");
        Assert.True(TopOf(notes.SysvolNote, view) < TopOf(notes.PdcPanel, view), "the PDC note is not under the SYSVOL note");
        Assert.True(TopOf(notes.PdcPanel, view) < TopOf(view.GpoList, view), "the PDC note is not above the list");
        AssertNoOldFeedback(view);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task EveryGroupPolicyDocument_HasItsStatusLineAndItsNotes_AndTheStatusBarSaysNothingAboutIt() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (main, sample) = await OpenMainAsync(context, _ =>
        {
            foreach (var (path, content) in SampleAdministrativeTemplates.Files())
            {
                context.Sysvol.PutPolicyDefinition(path, content);
            }
        });

        // The four views of this unit, and one of the later units, which has the line already and keeps its own bars
        // until it is converted: the status holder is the base class's, so no Group Policy tab speaks through the
        // status bar of the window any more.
        await AssertAsync<GroupPolicyObjectsDocumentViewModel, GroupPolicyObjectsView>(Objects, v => (v.PageStatus, v.DocumentNotes));
        await AssertAsync<WmiFiltersDocumentViewModel, WmiFiltersView>(new(GroupPolicyDocumentKind.WmiFilters), v => (v.PageStatus, v.DocumentNotes));
        await AssertAsync<GroupPolicyScopeDocumentViewModel, GroupPolicyScopeView>(
            new(GroupPolicyDocumentKind.Scope) { ContainerDn = context.Company.ComputersOu, Name = "Komputery" }, v => (v.PageStatus, v.DocumentNotes));
        await AssertAsync<GroupPolicyObjectDocumentViewModel, GroupPolicyObjectView>(
            new(GroupPolicyDocumentKind.Object) { GpoId = sample.WorkstationPasswords, Name = "Stacje robocze – Zasady haseł" }, v => (v.PageStatus, v.DocumentNotes));
        await AssertAsync<AdministrativeTemplatesEditorViewModel, AdministrativeTemplatesEditorView>(
            new(GroupPolicyDocumentKind.AdministrativeTemplates) { GpoId = sample.SitePrinters, Name = "Lokacja Warszawa – Drukarki" },
            v => (v.PageStatus, v.DocumentNotes));

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();

        async Task AssertAsync<TDocument, TView>(GroupPolicyDocumentRequest request, Func<TView, (StatusLine Line, GroupPolicyNotes Notes)> parts)
            where TDocument : GroupPolicyDocumentBase
            where TView : UserControl
        {
            var (document, view) = await OpenAsync<TDocument, TView>(main, request);
            var (line, notes) = parts(view);
            AssertTheLineOf(line, view, document);
            Assert.Equal(PageStatusKind.Idle, line.Kind);
            Assert.NotEqual(string.Empty, line.Message);
            Assert.StartsWith(document.LoadedText, line.Message, StringComparison.Ordinal);
            Assert.Same(notes, Assert.Single(UiWait.Descendants<GroupPolicyNotes>(view)));
            Assert.True(TopOf(line, view) < TopOf(notes, view), $"{typeof(TView).Name}: the notes are not under the status line");
            AssertNoNotes(notes);
            Assert.Equal(string.Empty, main.TabStatusText.Text);
            Assert.Equal(string.Empty, main.TabSelectionText.Text);
        }
    });

    /// <summary>The read of the GPOs of the domain: the one-level search under CN=Policies,CN=System.</summary>
    private static bool IsReadOfThePolicies(FakeOperation operation) =>
        operation is { Kind: FakeOperationKind.Search, Search.Scope: SearchScope.OneLevel }
        && operation.DistinguishedName.StartsWith("CN=Policies,CN=System,", StringComparison.OrdinalIgnoreCase);
}
