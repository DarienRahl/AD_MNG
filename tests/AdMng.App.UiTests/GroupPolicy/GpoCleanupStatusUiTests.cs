using System.ComponentModel;
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
using AdMng.Presentation.GroupPolicy.Cleanup;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;
using static AdMng.App.UiTests.GroupPolicy.GroupPolicyStatusUiSupport;

namespace AdMng.App.UiTests.GroupPolicy;

/// <summary>
/// The status line of "GPO cleanup" (track V, stage V2b-3; docs/dev/v-interface-renewal.md "V2b-3 as built"), through
/// the real tab of the main window over the fake directory: one line under the toolbar - the number of proposals with
/// when the GPOs were read, the read and the analysis with Stop, a read that was stopped or failed and "Group Policy
/// changed after the analysis" as the state of the tab with their actions -, the two reads as action buttons whose
/// button is the Cancel of the read that runs, and what the analysis could not see as a note. A slow domain controller is
/// a read the test holds and releases, a failing one an exception of the fake session; every state that is asserted
/// lasts until the test changes it. What the tab proposes and does is covered by <see cref="GpoCleanupUiTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GpoCleanupStatusUiTests
{
    private static readonly GroupPolicyDocumentRequest Cleanup = GpoCleanupEntry.Request;

    [Fact]
    public Task TheTab_HasOneStatusLine_UnderItsToolbar_WithTheProposals_AndNoOldFeedback() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (main, _) = await OpenAnalysableMainAsync(context);
        var (document, view) = await OpenAsync<GpoCleanupDocumentViewModel, GpoCleanupView>(main, Cleanup);
        var line = view.PageStatus;

        // One status line, named PageStatus, over the status of the document, between the toolbar and the chips.
        AssertTheLineOf(line, view, document);
        Assert.True(TopOf(view.AnalyzeButton, view) < TopOf(line, view), "the status line is not under the toolbar");
        Assert.True(TopOf(line, view) < TopOf(view.ChipsList, view), "the status line is not above the chips");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);

        // Nothing happens: the proposals and when the GPOs were read - in the line, once, and not a second time under the toolbar.
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal($"{document.LoadedText} · {document.LastRunText}", line.Message);
        Assert.StartsWith("Analysed GPOs: 16, settings read ", document.LastRunText, StringComparison.Ordinal);
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
        view.ProposalsList.SelectedItem = document.Rows[0];
        await UiWait.UntilAsync(() => line.Message.EndsWith($" · {document.SelectionText}", StringComparison.Ordinal), "the selection in the status line");
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        view.UpdateLayout();
        Assert.Equal(chipsTop, TopOf(view.ChipsList, view), 0.5);

        // The reads are action buttons at rest - the primary one in the accent colour - and the separate Cancel button is gone.
        Assert.Equal(ActionButtonState.Idle, view.AnalyzeButton.State);
        Assert.Same(document.AnalyzeCommand, view.AnalyzeButton.Command);
        Assert.Same(document.Status.CancelCommand, view.AnalyzeButton.CancelCommand);
        Assert.Same(Application.Current.FindResource("Ad.AccentActionButton"), view.AnalyzeButton.Style);
        Assert.True(view.AnalyzeButton.IsEnabled);
        Assert.Equal(ActionButtonState.Idle, view.ReadAllButton.State);
        Assert.Same(document.ReadAllCommand, view.ReadAllButton.Command);
        Assert.Same(document.Status.CancelCommand, view.ReadAllButton.CancelCommand);
        Assert.True(view.ReadAllButton.IsEnabled);
        Assert.DoesNotContain(UiWait.Descendants<Button>(view), button => button.Name == "CancelButton");

        // Every GPO was read: no note, no banner, no bar.
        AssertNoNotes(view.DocumentNotes);
        Assert.False(view.NotesBar.IsVisible);
        AssertNoOldFeedback(view);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task Analysing_IsWorkingWithStop_TheButtonIsItsCancel_AndAStoppedReadIsAttentionWithRefresh() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (main, _) = await OpenAnalysableMainAsync(context);
        var (document, view) = await OpenAsync<GpoCleanupDocumentViewModel, GpoCleanupView>(main, Cleanup);
        var line = view.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        var chipsTop = TopOf(view.ChipsList, view);
        var said = new List<(PageStatusKind Kind, string Message)>();
        PropertyChangedEventHandler record = (_, e) =>
        {
            if (e.PropertyName == nameof(PageStatusHolder.Current))
            {
                said.Add((document.Status.Current.Kind, document.Status.Current.Message));
            }
        };

        // "Refresh": the GPOs are read and analysed again, and the first read of the domain controller waits for the test.
        using (var held = new HeldRead(context.Directory.Session, IsReadOfThePolicies))
        {
            ControlHost.Press(view.AnalyzeButton);
            await UiWait.UntilAsync(() => held.IsReached && document.IsLoading && line.Kind == PageStatusKind.Working, "the GPOs are being read");

            // Working: the stage of the read - with Stop, and nothing to close. The button that started the read is its
            // Cancel; the other read waits.
            Assert.Equal("Working", line.Title);
            Assert.Equal("Reading GPOs and links…", line.Message);
            Assert.True(ring.IsActive);
            Assert.Equal(Visibility.Visible, action.Visibility);
            Assert.Equal("Stop", Assert.IsType<string>(action.Content));
            Assert.Same(document.Status.CancelCommand, action.Command);
            Assert.Equal(Visibility.Collapsed, close.Visibility);
            Assert.Equal(ActionButtonState.Running, view.AnalyzeButton.State);
            Assert.True(view.AnalyzeButton.IsEnabled);
            Assert.Equal(ActionButtonState.Idle, view.ReadAllButton.State);
            Assert.False(view.ReadAllButton.IsEnabled);
            AssertNoOldFeedback(view);

            // The line was there before: nothing under it moved.
            view.UpdateLayout();
            Assert.Equal(chipsTop, TopOf(view.ChipsList, view));

            // Stop, pressed on the button that started the read: "Stopping…" was said at once, and the tab is back in the
            // hands of the operator.
            document.Status.PropertyChanged += record;
            ControlHost.Press(view.AnalyzeButton);
            await UiWait.UntilAsync(() => !document.IsLoading && line.Kind == PageStatusKind.Attention, "the read was stopped");
            document.Status.PropertyChanged -= record;
        }

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Contains((PageStatusKind.Working, "Stopping…"), said);

        // Stopped: the state of the tab until it is read again - what it may lack, and Refresh. It cannot be closed.
        Assert.Equal("Attention", line.Title);
        Assert.Equal("Reading “GPO cleanup” was stopped: the tab may not show everything. Press Refresh to read it again.", line.Message);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.False(ring.IsActive);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Refresh", Assert.IsType<string>(action.Content));
        Assert.Same(document.RefreshCommand, action.Command);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.Equal(ActionButtonState.Idle, view.AnalyzeButton.State);
        Assert.True(view.AnalyzeButton.IsEnabled);
        view.UpdateLayout();
        Assert.Equal(chipsTop + GrowthOf(line), TopOf(view.ChipsList, view), 0.5);

        // Refresh, pressed in the line, reads and analyses the GPOs again: the summary, and the chips are where they were.
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !document.IsLoading && line.Kind == PageStatusKind.Idle, "the GPOs were analysed again");
        view.UpdateLayout();
        Assert.Equal(chipsTop, TopOf(view.ChipsList, view), 0.5);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task AReadThatFails_IsAnErrorWithRetry_UntilTheGposAreReadAgain() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (main, _) = await OpenAnalysableMainAsync(context);
        var (document, view) = await OpenAsync<GpoCleanupDocumentViewModel, GpoCleanupView>(main, Cleanup);
        var line = view.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var session = context.Directory.Session;
        var shown = document.View;

        // The domain controller does not answer the list of GPOs any more.
        var fail = 1;
        session.BeforeOperation = operation =>
        {
            if (Volatile.Read(ref fail) == 1 && IsReadOfThePolicies(operation))
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "the domain controller does not answer");
            }
        };
        ControlHost.Press(view.AnalyzeButton);
        await UiWait.UntilAsync(() => !document.IsLoading && line.Kind == PageStatusKind.Error, "the read failed");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // The line says why the GPOs were not read: what was tried, the reason, what to do, the code - with Retry, and it
        // cannot be closed. The proposals of before stay on the screen.
        Assert.Equal("Error", line.Title);
        Assert.NotNull(document.Error);
        Assert.Equal($"Could not read “GPO cleanup”: {document.Error} Press Retry to try again. (ServerUnavailable)", line.Message);
        Assert.Equal(AppIcons.StatusError, line.IconKey);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Retry", Assert.IsType<string>(action.Content));
        Assert.Same(document.RefreshCommand, action.Command);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.Same(shown, document.View);
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(view),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text == document.Error);
        AssertNoOldFeedback(view);

        // Retry, pressed in the line, reads the GPOs - the read that failed, not a look at the old proposals.
        Volatile.Write(ref fail, 0);
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !document.IsLoading && line.Kind == PageStatusKind.Idle, "the GPOs were read");
        session.BeforeOperation = null;
        Assert.NotSame(shown, document.View);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task GroupPolicyChangedAfterTheAnalysis_IsALastingAttentionWithRefresh_NotABar() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (main, _) = await OpenAnalysableMainAsync(context);
        var (document, view) = await OpenAsync<GpoCleanupDocumentViewModel, GpoCleanupView>(main, Cleanup);
        var line = view.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var shown = document.View;
        var chipsTop = TopOf(view.ChipsList, view);

        // A change elsewhere in this window: the proposals stay, and the line says that they may be out of date.
        context.Services.GetRequiredService<GroupPolicyServices>().NotifyChanged();
        await UiWait.UntilAsync(() => document.IsStale && !document.IsLoading && line.Kind == PageStatusKind.Attention, "the proposals are marked");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal("Group Policy changed after the analysis – the proposals may be out of date.", line.Message);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Refresh", Assert.IsType<string>(action.Content));
        Assert.Same(document.AnalyzeCommand, action.Command);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.Same(shown, document.View);
        AssertNoOldFeedback(view);
        view.UpdateLayout();
        Assert.Equal(chipsTop + GrowthOf(line), TopOf(view.ChipsList, view), 0.5);

        // The action of the line reads the GPOs that changed and analyses them: the summary, and nothing is left to say.
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !document.IsStale && !document.IsLoading && line.Kind == PageStatusKind.Idle, "the proposals are current");
        Assert.Equal($"{document.LoadedText} · {document.LastRunText}", line.Message);
        view.UpdateLayout();
        Assert.Equal(chipsTop, TopOf(view.ChipsList, view), 0.5);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task WhatTheAnalysisCouldNotSee_IsANoteAboveTheProposals_WhileTheLineKeepsTheSummary() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (main, _) = await OpenAnalysableMainAsync(context);
        context.Sysvol.Unavailable = SysvolUnavailableReason.Unreachable;
        var (document, view) = await OpenAsync<GpoCleanupDocumentViewModel, GpoCleanupView>(main, Cleanup);
        var line = view.PageStatus;

        // The line keeps the summary: what the analysis could not see is a sentence about the proposals, not an event.
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.StartsWith(document.LoadedText, line.Message, StringComparison.Ordinal);
        Assert.Null(document.Status.Lasting);

        // The note: an attention note under the notes of the document, above the chips, with the note style - no bar.
        Assert.True(document.HasNotes);
        Assert.True(view.NotesBar.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Attention"), view.NotesBar.Style);
        Assert.Equal(document.Notes.Count, view.NotesList.Items.Count);
        Assert.Contains(UiWait.Descendants<TextBlock>(view.NotesBar), text => text.IsVisible && text.Text == document.Notes[0]);
        Assert.True(view.DocumentNotes.SysvolNote.IsVisible);
        Assert.True(TopOf(line, view) < TopOf(view.DocumentNotes.SysvolNote, view), "the SYSVOL note is not under the status line");
        Assert.True(TopOf(view.DocumentNotes.SysvolNote, view) < TopOf(view.NotesBar, view), "the notes of the analysis are not under the SYSVOL note");
        Assert.True(TopOf(view.NotesBar, view) < TopOf(view.ChipsList, view), "the notes of the analysis are not above the chips");
        AssertNoOldFeedback(view);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    /// <summary>The main window over the GPO management sample and the cleanup sample (16 GPOs).</summary>
    private static Task<(AdMng.App.Views.MainWindow Main, GroupPolicyManagementSample Sample)> OpenAnalysableMainAsync(UiTestContext context) =>
        OpenMainAsync(context, _ => context.Directory.AddGroupPolicyCleanupSample(context.Company, context.Sysvol));

    /// <summary>The read of the GPOs of the domain: the one-level search under CN=Policies,CN=System.</summary>
    private static bool IsReadOfThePolicies(FakeOperation operation) =>
        operation is { Kind: FakeOperationKind.Search, Search.Scope: SearchScope.OneLevel }
        && operation.DistinguishedName.StartsWith("CN=Policies,CN=System,", StringComparison.OrdinalIgnoreCase);
}
