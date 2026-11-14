using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.GroupPolicy;
using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy.Diagnostics;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.GroupPolicy.Diagnostics;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;
using static AdMng.App.UiTests.GroupPolicy.GroupPolicyStatusUiSupport;

namespace AdMng.App.UiTests.GroupPolicy;

/// <summary>
/// The status line of "Group Policy diagnostics" (track V, stage V2b-3; docs/dev/v-interface-renewal.md "V2b-3 as
/// built"), through the real tab of the main window over the fake directory: one line under the toolbar - the number of
/// findings with when and how deep the GPOs were checked, the check with Stop, a check that was stopped or failed and
/// "Group Policy has changed since the last check" as the state of the tab with their actions -, the two checks as
/// action buttons whose button is the Cancel of the check that runs, and what the check could not evaluate as a note. A
/// slow domain controller is a read the test holds and releases, a failing one an exception of the fake session; every
/// state that is asserted lasts until the test changes it. What the tab finds and does is covered by
/// <see cref="GroupPolicyDiagnosticsUiTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GroupPolicyDiagnosticsStatusUiTests
{
    private static readonly GroupPolicyDocumentRequest Diagnostics = GroupPolicyDiagnosticsEntry.Request;

    [Fact]
    public Task TheTab_HasOneStatusLine_UnderItsToolbar_WithTheFindings_AndNoOldFeedback() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (main, _) = await OpenMainAsync(context);
        var (document, view) = await OpenAsync<GroupPolicyDiagnosticsDocumentViewModel, GroupPolicyDiagnosticsView>(main, Diagnostics);
        var line = view.PageStatus;

        // One status line, named PageStatus, over the status of the document, between the toolbar and the chips.
        AssertTheLineOf(line, view, document);
        Assert.True(TopOf(view.QuickCheckButton, view) < TopOf(line, view), "the status line is not under the toolbar");
        Assert.True(TopOf(line, view) < TopOf(view.ChipsList, view), "the status line is not above the chips");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);

        // Nothing happens: the findings and when and how deep the GPOs were checked - in the line, once, and not a second
        // time under the toolbar.
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal($"{document.LoadedText} · {document.LastRunText}", line.Message);
        Assert.StartsWith("Last check: quick, ", document.LastRunText, StringComparison.Ordinal);
        Assert.Equal(AppIcons.StatusInfo, line.IconKey);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(string.Empty, main.TabStatusText.Text);
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(view),
            text => text.IsVisible && !text.IsDescendantOf(line) && (text.Text == document.LastRunText || text.Text == document.LoadedText));
        var chipsTop = TopOf(view.ChipsList, view);

        // A selection joins the summary - and nothing under the line moves.
        view.FindingsList.SelectedItem = document.Rows[0];
        await UiWait.UntilAsync(() => line.Message.EndsWith($" · {document.SelectionText}", StringComparison.Ordinal), "the selection in the status line");
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        view.UpdateLayout();
        Assert.Equal(chipsTop, TopOf(view.ChipsList, view), 0.5);

        // The checks are action buttons at rest - the primary one in the accent colour - and the separate Cancel button is gone.
        Assert.Equal(ActionButtonState.Idle, view.QuickCheckButton.State);
        Assert.Same(document.QuickCheckCommand, view.QuickCheckButton.Command);
        Assert.Same(document.Status.CancelCommand, view.QuickCheckButton.CancelCommand);
        Assert.Same(Application.Current.FindResource("Ad.AccentActionButton"), view.QuickCheckButton.Style);
        Assert.True(view.QuickCheckButton.IsEnabled);
        Assert.Equal(ActionButtonState.Idle, view.FullCheckButton.State);
        Assert.Same(document.FullCheckCommand, view.FullCheckButton.Command);
        Assert.Same(document.Status.CancelCommand, view.FullCheckButton.CancelCommand);
        Assert.True(view.FullCheckButton.IsEnabled);
        Assert.DoesNotContain(UiWait.Descendants<Button>(view), button => button.Name == "CancelButton");

        // What a quick check does not cover is a note above the findings - not a bar, and not an event of the line.
        Assert.True(view.NotesBar.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Attention"), view.NotesBar.Style);
        Assert.Equal(document.Notes.Count, view.NotesList.Items.Count);
        Assert.Contains(UiWait.Descendants<TextBlock>(view.NotesBar), text => text.IsVisible && text.Text == document.Notes[0]);
        Assert.True(TopOf(line, view) < TopOf(view.NotesBar, view), "the note is not under the status line");
        Assert.True(TopOf(view.NotesBar, view) < TopOf(view.ChipsList, view), "the note is not above the chips");
        AssertNoNotes(view.DocumentNotes);
        AssertNoOldFeedback(view);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheFullCheck_IsWorkingWithStop_TheButtonIsItsCancel_AndAStoppedCheckIsAttentionWithRefresh() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (main, _) = await OpenMainAsync(context);
        var (document, view) = await OpenAsync<GroupPolicyDiagnosticsDocumentViewModel, GroupPolicyDiagnosticsView>(main, Diagnostics);
        var line = view.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        var chipsTop = TopOf(view.ChipsList, view);

        // "Full check": the GPOs are read, and the first read of the domain controller waits for the test.
        using (var held = new HeldRead(context.Directory.Session, IsReadOfThePolicies))
        {
            ControlHost.Press(view.FullCheckButton);
            await UiWait.UntilAsync(() => held.IsReached && document.IsLoading && line.Kind == PageStatusKind.Working, "the full check is running");

            // Working: the stage of the check - with Stop, and nothing to close. The button that started the check is its
            // Cancel; the other check waits.
            Assert.Equal("Working", line.Title);
            Assert.Equal("Reading the GPOs, links and WMI filters…", line.Message);
            Assert.True(ring.IsActive);
            Assert.Equal(Visibility.Visible, action.Visibility);
            Assert.Equal("Stop", Assert.IsType<string>(action.Content));
            Assert.Same(document.Status.CancelCommand, action.Command);
            Assert.Equal(Visibility.Collapsed, close.Visibility);
            Assert.Equal(ActionButtonState.Running, view.FullCheckButton.State);
            Assert.True(view.FullCheckButton.IsEnabled);
            Assert.Equal(ActionButtonState.Idle, view.QuickCheckButton.State);
            Assert.False(view.QuickCheckButton.IsEnabled);
            AssertNoOldFeedback(view);

            // The line was there before: nothing under it moved.
            view.UpdateLayout();
            Assert.Equal(chipsTop, TopOf(view.ChipsList, view));

            // Stop, pressed on the button that started the check: "Stopping…" at once - the read that is on its way to the
            // domain controller still has to return - on the button and in the line.
            ControlHost.Press(view.FullCheckButton);
            await UiWait.UntilAsync(() => document.Status.IsCancelling, "the check is stopping");
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.Equal("Stopping…", line.Message);
            Assert.Equal(Visibility.Collapsed, action.Visibility);
            Assert.Equal(ActionButtonState.Cancelling, view.FullCheckButton.State);
            Assert.True(document.IsLoading);
        }

        await UiWait.UntilAsync(() => !document.IsLoading && line.Kind == PageStatusKind.Attention, "the check was stopped");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Stopped: the state of the tab until it is checked again - what it may lack, and Refresh. It cannot be closed.
        Assert.Equal("Attention", line.Title);
        Assert.Equal("Reading “Group Policy diagnostics” was stopped: the tab may not show everything. Press Refresh to read it again.", line.Message);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.False(ring.IsActive);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Refresh", Assert.IsType<string>(action.Content));
        Assert.Same(document.RefreshCommand, action.Command);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.Equal(ActionButtonState.Idle, view.FullCheckButton.State);
        Assert.True(view.FullCheckButton.IsEnabled);
        Assert.Equal(GpoDiagnosticsDepth.Quick, document.LastDepth);
        view.UpdateLayout();
        Assert.Equal(chipsTop + GrowthOf(line), TopOf(view.ChipsList, view), 0.5);

        // Refresh, pressed in the line, runs the check that was stopped - the full one - again.
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !document.IsLoading && line.Kind == PageStatusKind.Idle, "the check ran again");
        Assert.Equal(GpoDiagnosticsDepth.Full, document.LastDepth);
        Assert.StartsWith("Last check: full, ", document.LastRunText, StringComparison.Ordinal);
        view.UpdateLayout();
        Assert.Equal(chipsTop, TopOf(view.ChipsList, view), 0.5);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task ACheckThatFails_IsAnErrorWithRetry_UntilTheGposAreCheckedAgain() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (main, _) = await OpenMainAsync(context);
        var (document, view) = await OpenAsync<GroupPolicyDiagnosticsDocumentViewModel, GroupPolicyDiagnosticsView>(main, Diagnostics);
        var line = view.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var session = context.Directory.Session;
        var shown = document.Rows;

        // The domain controller does not answer the list of GPOs any more.
        var fail = 1;
        session.BeforeOperation = operation =>
        {
            if (Volatile.Read(ref fail) == 1 && IsReadOfThePolicies(operation))
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "the domain controller does not answer");
            }
        };
        ControlHost.Press(view.FullCheckButton);
        await UiWait.UntilAsync(() => !document.IsLoading && line.Kind == PageStatusKind.Error, "the check failed");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // The line says why the GPOs were not checked: what was tried, the reason, what to do, the code - with Retry, and it
        // cannot be closed. The findings of before stay on the screen.
        Assert.Equal("Error", line.Title);
        Assert.NotNull(document.Error);
        Assert.Equal($"Could not read “Group Policy diagnostics”: {document.Error} Press Retry to try again. (ServerUnavailable)", line.Message);
        Assert.Equal(AppIcons.StatusError, line.IconKey);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Retry", Assert.IsType<string>(action.Content));
        Assert.Same(document.RefreshCommand, action.Command);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.Same(shown, document.Rows);
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(view),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text == document.Error);
        AssertNoOldFeedback(view);

        // Retry, pressed in the line, runs the check that failed - the full one.
        Volatile.Write(ref fail, 0);
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !document.IsLoading && line.Kind == PageStatusKind.Idle, "the GPOs were checked");
        session.BeforeOperation = null;
        Assert.Equal(GpoDiagnosticsDepth.Full, document.LastDepth);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task GroupPolicyChangedSinceTheLastCheck_IsALastingAttentionWithCheckAgain_NotABar() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (main, _) = await OpenMainAsync(context);
        var (document, view) = await OpenAsync<GroupPolicyDiagnosticsDocumentViewModel, GroupPolicyDiagnosticsView>(main, Diagnostics);
        var line = view.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var shown = document.Rows;
        var chipsTop = TopOf(view.ChipsList, view);

        // A change elsewhere in this window: the findings stay, and the line says that they may be out of date.
        context.Services.GetRequiredService<GroupPolicyServices>().NotifyChanged();
        await UiWait.UntilAsync(() => document.IsStale && !document.IsLoading && line.Kind == PageStatusKind.Attention, "the findings are marked");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal("Group Policy has changed since the last check – the results may be out of date.", line.Message);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Check again", Assert.IsType<string>(action.Content));
        Assert.Same(document.CheckAgainCommand, action.Command);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.Same(shown, document.Rows);
        AssertNoOldFeedback(view);
        view.UpdateLayout();
        Assert.Equal(chipsTop + GrowthOf(line), TopOf(view.ChipsList, view), 0.5);

        // The action of the line checks the GPOs again: the summary, and nothing is left to say.
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !document.IsStale && !document.IsLoading && line.Kind == PageStatusKind.Idle, "the findings are current");
        Assert.Equal($"{document.LoadedText} · {document.LastRunText}", line.Message);
        view.UpdateLayout();
        Assert.Equal(chipsTop, TopOf(view.ChipsList, view), 0.5);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    /// <summary>The read of the GPOs of the domain: the one-level search under CN=Policies,CN=System.</summary>
    private static bool IsReadOfThePolicies(FakeOperation operation) =>
        operation is { Kind: FakeOperationKind.Search, Search.Scope: SearchScope.OneLevel }
        && operation.DistinguishedName.StartsWith("CN=Policies,CN=System,", StringComparison.OrdinalIgnoreCase);
}
