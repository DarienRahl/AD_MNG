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
using AdMng.Presentation.GroupPolicy.Search;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;
using static AdMng.App.UiTests.GroupPolicy.GroupPolicyStatusUiSupport;

namespace AdMng.App.UiTests.GroupPolicy;

/// <summary>
/// The status line of "GPO settings search" (track V, stage V2b-3; docs/dev/v-interface-renewal.md "V2b-3 as built"),
/// through the real tab of the main window over the fake directory: one line under the toolbar - the result of the
/// query with when the GPOs were read, the read of the index with Stop, a read that was stopped or failed and "Group
/// Policy changed in this window" as the state of the tab with their actions -, the two reads as action buttons whose
/// button is the Cancel of the read that runs, and what the index could not read as a note. A slow domain controller is
/// a read the test holds and releases, a failing one an exception of the fake session; every state that is asserted lasts
/// until the test changes it. What the tab finds and does is covered by <see cref="GpoSettingsSearchUiTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GpoSettingsSearchStatusUiTests
{
    private static readonly GroupPolicyDocumentRequest Search = GpoSettingsSearchEntry.Request;

    [Fact]
    public Task TheTab_HasOneStatusLine_UnderItsToolbar_WithTheResultOfTheQuery_AndNoOldFeedback() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (main, _) = await OpenMainAsync(context);
        var (document, view) = await OpenAsync<GpoSettingsSearchDocumentViewModel, GpoSettingsSearchView>(main, Search);
        var line = view.PageStatus;

        // One status line, named PageStatus, over the status of the document, between the toolbar and the query.
        AssertTheLineOf(line, view, document);
        Assert.True(TopOf(view.RefreshIndexButton, view) < TopOf(line, view), "the status line is not under the toolbar");
        Assert.True(TopOf(line, view) < TopOf(view.SearchBox, view), "the status line is not above the query");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);

        // No query: when the GPOs were read - in the line, once, and not a second time under the toolbar.
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.StartsWith("GPOs searched: 7 · read ", line.Message, StringComparison.Ordinal);
        Assert.Equal(document.IndexInfoText, line.Message);
        Assert.Equal(AppIcons.StatusInfo, line.IconKey);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(string.Empty, main.TabStatusText.Text);
        var searchTop = TopOf(view.SearchBox, view);

        // A query: what it found, then when the GPOs were read - the line grows by nothing, and nothing moved.
        document.SearchText = "MinimumPasswordLength";
        await document.SearchCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => line.Message.StartsWith(document.ResultText, StringComparison.Ordinal), "the result of the query in the status line");
        Assert.Equal($"{document.ResultText} · {document.IndexInfoText}", line.Message);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(view),
            text => text.IsVisible && !text.IsDescendantOf(line) && (text.Text == document.ResultText || text.Text == document.IndexInfoText));
        view.UpdateLayout();
        Assert.Equal(searchTop, TopOf(view.SearchBox, view), 0.5);

        // The selected occurrence joins it.
        view.ResultsList.SelectedItem = document.Rows[0];
        await UiWait.UntilAsync(() => line.Message.EndsWith($" · {document.SelectionText}", StringComparison.Ordinal), "the selection in the status line");
        Assert.Equal(PageStatusKind.Idle, line.Kind);

        // The reads are action buttons at rest - the commands of before, and the Cancel of the read that runs - and the
        // separate Cancel button is gone.
        Assert.Equal(ActionButtonState.Idle, view.RefreshIndexButton.State);
        Assert.Same(document.RefreshIndexCommand, view.RefreshIndexButton.Command);
        Assert.Same(document.Status.CancelCommand, view.RefreshIndexButton.CancelCommand);
        Assert.True(view.RefreshIndexButton.IsEnabled);
        Assert.Equal(ActionButtonState.Idle, view.ReadAllButton.State);
        Assert.Same(document.ReadAllCommand, view.ReadAllButton.Command);
        Assert.Same(document.Status.CancelCommand, view.ReadAllButton.CancelCommand);
        Assert.True(view.ReadAllButton.IsEnabled);
        Assert.DoesNotContain(UiWait.Descendants<Button>(view), button => button.Name == "CancelButton");

        // SYSVOL is there and the session is on the PDC emulator, and every GPO was read: no note, no banner, no bar.
        AssertNoNotes(view.DocumentNotes);
        Assert.False(view.NotesBar.IsVisible);
        Assert.False(view.CapBar.IsVisible);
        AssertNoOldFeedback(view);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task ReadingTheIndex_IsWorkingWithStop_TheButtonIsItsCancel_AndAStoppedReadIsAttentionWithRefresh() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (main, _) = await OpenMainAsync(context);
        var (document, view) = await OpenAsync<GpoSettingsSearchDocumentViewModel, GpoSettingsSearchView>(main, Search);
        var line = view.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        var listTop = TopOf(view.ResultsList, view);
        var said = new List<(PageStatusKind Kind, string Message)>();
        PropertyChangedEventHandler record = (_, e) =>
        {
            if (e.PropertyName == nameof(PageStatusHolder.Current))
            {
                said.Add((document.Status.Current.Kind, document.Status.Current.Message));
            }
        };

        // "Refresh": the GPOs are read again, and the first read of the domain controller waits for the test.
        using (var held = new HeldRead(context.Directory.Session, IsReadOfThePolicies))
        {
            ControlHost.Press(view.RefreshIndexButton);
            await UiWait.UntilAsync(() => held.IsReached && document.IsLoading && line.Kind == PageStatusKind.Working, "the index is being read");

            // Working: the stage of the read - with Stop, and nothing to close. The button that started the read is its
            // Cancel; the other read waits.
            Assert.Equal("Working", line.Title);
            Assert.Equal("Reading GPOs and links…", line.Message);
            Assert.True(ring.IsActive);
            Assert.Equal(Visibility.Visible, action.Visibility);
            Assert.Equal("Stop", Assert.IsType<string>(action.Content));
            Assert.Same(document.Status.CancelCommand, action.Command);
            Assert.Equal(Visibility.Collapsed, close.Visibility);
            Assert.Equal(ActionButtonState.Running, view.RefreshIndexButton.State);
            Assert.True(view.RefreshIndexButton.IsEnabled);
            Assert.Equal(ActionButtonState.Idle, view.ReadAllButton.State);
            Assert.False(view.ReadAllButton.IsEnabled);
            AssertNoOldFeedback(view);

            // The line was there before: nothing under it moved.
            view.UpdateLayout();
            Assert.Equal(listTop, TopOf(view.ResultsList, view));

            // Stop, pressed on the button that started the read: the index is waited for no longer, the tab is back in
            // the hands of the operator - "Stopping…" was said at once.
            document.Status.PropertyChanged += record;
            ControlHost.Press(view.RefreshIndexButton);
            await UiWait.UntilAsync(() => !document.IsLoading && line.Kind == PageStatusKind.Attention, "the read was stopped");
            document.Status.PropertyChanged -= record;
        }

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Contains((PageStatusKind.Working, "Stopping…"), said);

        // Stopped: the state of the tab until it is read again - what it may lack, and Refresh. It cannot be closed.
        Assert.Equal("Attention", line.Title);
        Assert.Equal("Reading “GPO settings search” was stopped: the tab may not show everything. Press Refresh to read it again.", line.Message);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.False(ring.IsActive);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Refresh", Assert.IsType<string>(action.Content));
        Assert.Same(document.RefreshCommand, action.Command);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.Equal(ActionButtonState.Idle, view.RefreshIndexButton.State);
        Assert.True(view.RefreshIndexButton.IsEnabled);
        view.UpdateLayout();
        Assert.Equal(listTop + GrowthOf(line), TopOf(view.ResultsList, view), 0.5);

        // Refresh, pressed in the line, reads the GPOs again: the summary, and the list is where it was.
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !document.IsLoading && line.Kind == PageStatusKind.Idle, "the GPOs were read again");
        view.UpdateLayout();
        Assert.Equal(listTop, TopOf(view.ResultsList, view), 0.5);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task AReadThatFails_IsAnErrorWithRetry_UntilTheGposAreReadAgain() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (main, _) = await OpenMainAsync(context);
        var (document, view) = await OpenAsync<GpoSettingsSearchDocumentViewModel, GpoSettingsSearchView>(main, Search);
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
        ControlHost.Press(view.RefreshIndexButton);
        await UiWait.UntilAsync(() => !document.IsLoading && line.Kind == PageStatusKind.Error, "the read failed");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // The line says why the GPOs were not read: what was tried, the reason, what to do, the code - with Retry, and
        // it cannot be closed. What was read before stays on the screen.
        Assert.Equal("Error", line.Title);
        Assert.NotNull(document.Error);
        Assert.Equal($"Could not read “GPO settings search”: {document.Error} Press Retry to try again. (ServerUnavailable)", line.Message);
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

        // Retry, pressed in the line, reads the GPOs - the read that failed, not a look at the old results.
        Volatile.Write(ref fail, 0);
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !document.IsLoading && line.Kind == PageStatusKind.Idle, "the GPOs were read");
        session.BeforeOperation = null;
        Assert.NotSame(shown, document.View);
        Assert.Equal(document.IndexInfoText, line.Message);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task GroupPolicyChangedInThisWindow_IsALastingAttentionWithRefresh_NotABar() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (main, _) = await OpenMainAsync(context);
        var (document, view) = await OpenAsync<GpoSettingsSearchDocumentViewModel, GpoSettingsSearchView>(main, Search);
        var line = view.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var shown = document.View;
        var searchTop = TopOf(view.SearchBox, view);

        // A change elsewhere in this window: the results stay, and the line says that they may be out of date.
        context.Services.GetRequiredService<GroupPolicyServices>().NotifyChanged();
        await UiWait.UntilAsync(() => document.IsStale && !document.IsLoading && line.Kind == PageStatusKind.Attention, "the results are marked");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal("Group Policy changed in this window – the results may be out of date.", line.Message);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Refresh", Assert.IsType<string>(action.Content));
        Assert.Same(document.RefreshIndexCommand, action.Command);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.Same(shown, document.View);
        AssertNoOldFeedback(view);
        view.UpdateLayout();
        Assert.Equal(searchTop + GrowthOf(line), TopOf(view.SearchBox, view), 0.5);

        // Refresh, pressed in the line, reads the GPOs that changed: the summary, and nothing is left to say.
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !document.IsStale && !document.IsLoading && line.Kind == PageStatusKind.Idle, "the results are current");
        Assert.Equal(document.IndexInfoText, line.Message);
        view.UpdateLayout();
        Assert.Equal(searchTop, TopOf(view.SearchBox, view), 0.5);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task WhatTheIndexCouldNotRead_IsANoteAboveTheResults_WhileTheLineKeepsTheSummary() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (main, _) = await OpenMainAsync(context);
        context.Sysvol.Unavailable = SysvolUnavailableReason.Unreachable;
        var (document, view) = await OpenAsync<GpoSettingsSearchDocumentViewModel, GpoSettingsSearchView>(main, Search);
        var line = view.PageStatus;

        // The line keeps the summary: what the index could not read is a sentence about the results, not an event.
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(document.IndexInfoText, line.Message);
        Assert.Null(document.Status.Lasting);

        // The note: an attention note under the notes of the document, above the query, with the note style - no bar.
        Assert.True(document.HasNotes);
        Assert.True(view.NotesBar.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Attention"), view.NotesBar.Style);
        Assert.Equal(document.Notes.Count, view.NotesList.Items.Count);
        Assert.Contains(UiWait.Descendants<TextBlock>(view.NotesBar), text => text.IsVisible && text.Text == document.Notes[0].Text);
        Assert.True(view.DocumentNotes.SysvolNote.IsVisible);
        Assert.True(TopOf(line, view) < TopOf(view.DocumentNotes.SysvolNote, view), "the SYSVOL note is not under the status line");
        Assert.True(TopOf(view.DocumentNotes.SysvolNote, view) < TopOf(view.NotesBar, view), "the notes of the index are not under the SYSVOL note");
        Assert.True(TopOf(view.NotesBar, view) < TopOf(view.SearchBox, view), "the notes of the index are not above the query");
        AssertNoOldFeedback(view);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    /// <summary>The read of the GPOs of the domain: the one-level search under CN=Policies,CN=System.</summary>
    private static bool IsReadOfThePolicies(FakeOperation operation) =>
        operation is { Kind: FakeOperationKind.Search, Search.Scope: SearchScope.OneLevel }
        && operation.DistinguishedName.StartsWith("CN=Policies,CN=System,", StringComparison.OrdinalIgnoreCase);
}
