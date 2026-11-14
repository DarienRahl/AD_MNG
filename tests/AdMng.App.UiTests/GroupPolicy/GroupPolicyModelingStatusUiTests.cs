using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.GroupPolicy;
using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Modeling;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.GroupPolicy.Modeling;
using AdMng.Testing;
using static AdMng.App.UiTests.GroupPolicy.GroupPolicyStatusUiSupport;

namespace AdMng.App.UiTests.GroupPolicy;

/// <summary>
/// The status line of "What the computer gets" (track V, stage V2b-3; docs/dev/v-interface-renewal.md "V2b-3 as built"),
/// through the real tab of the main window over the fake directory: one line under the header - what the computer and
/// the user get, in numbers; the calculation with Stop; a calculation that was stopped or failed as the state of the tab,
/// with its action - and what the model could not use as a note above the results. A slow domain controller is a read
/// the test holds and releases, a failing one an exception of the fake session; every state that is asserted lasts until
/// the test changes it. What the model shows and does is covered by <see cref="GroupPolicyWindowTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GroupPolicyModelingStatusUiTests
{
    [Fact]
    public Task TheTab_HasOneStatusLine_UnderItsHeader_WithWhatTheComputerGets_AndNoOldFeedback() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        context.Directory.Session.ComputesTokenGroups = true;
        var (main, _) = await OpenMainAsync(context);
        var (model, view) = await OpenAsync<GroupPolicyModelingDocumentViewModel, GroupPolicyModelingView>(main, Request(context));
        var line = view.PageStatus;

        // One status line, named PageStatus, over the status of the document, between the header and the inputs.
        AssertTheLineOf(line, view, model);
        Assert.True(TopOf(view.TitleText, view) < TopOf(line, view), "the status line is not under the header");
        Assert.True(TopOf(line, view) < TopOf(view.ComputerText, view), "the status line is not above the inputs");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);

        // Nothing happens: what the computer gets - in the line, once, and not a second time above the results.
        Assert.True(model.HasModel);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.StartsWith("Computer: 3 GPO, ", line.Message, StringComparison.Ordinal);
        Assert.Equal(model.SummaryText, line.Message);
        Assert.Equal(AppIcons.StatusInfo, line.IconKey);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(string.Empty, main.TabStatusText.Text);
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(view),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text == model.SummaryText);
        Assert.True(view.LoopbackText.IsVisible);
        Assert.Equal(model.LoopbackText, view.LoopbackText.Text);

        // The inputs changed: a sentence next to the inputs - not an event of the line - and "Przelicz" stays a button.
        view.WmiBox.SelectedValue = WmiFilterAssumption.AssumeFalse;
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(Visibility.Visible, view.StaleText.Visibility);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(model.SummaryText, line.Message);
        Assert.IsNotType<ActionButton>(view.RecalculateButton);

        // Nothing could not be used: no note, no banner, no bar.
        AssertNoNotes(view.DocumentNotes);
        Assert.False(view.NotesBar.IsVisible);
        AssertNoOldFeedback(view);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheCalculation_IsWorkingWithStop_AndAStoppedOneIsAttentionWithRefresh() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        context.Directory.Session.ComputesTokenGroups = true;
        var (main, _) = await OpenMainAsync(context);
        var (model, view) = await OpenAsync<GroupPolicyModelingDocumentViewModel, GroupPolicyModelingView>(main, Request(context));
        var line = view.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        var tabsTop = TopOf(view.PartTabs, view);

        // "Przelicz": the model is calculated again, and the first read of the domain controller waits for the test.
        using (var held = new HeldRead(context.Directory.Session, IsReadOfThePolicies))
        {
            ControlHost.Press(view.RecalculateButton);
            await UiWait.UntilAsync(() => held.IsReached && model.IsLoading && line.Kind == PageStatusKind.Working, "the model is being calculated");

            // Working: what is calculated - with Stop, and nothing to close.
            Assert.Equal("Working", line.Title);
            Assert.Equal("Reading “What the computer gets – WAW-PC001”…", line.Message);
            Assert.True(ring.IsActive);
            Assert.Equal(Visibility.Visible, action.Visibility);
            Assert.Equal("Stop", Assert.IsType<string>(action.Content));
            Assert.Same(model.Status.CancelCommand, action.Command);
            Assert.Equal(Visibility.Collapsed, close.Visibility);
            AssertNoOldFeedback(view);

            // The line was there before: nothing under it moved.
            view.UpdateLayout();
            Assert.Equal(tabsTop, TopOf(view.PartTabs, view));

            // Stop, pressed in the line: "Stopping…" at once - the read that is on its way to the domain controller still
            // has to return.
            ControlHost.Press(action);
            await UiWait.UntilAsync(() => model.Status.IsCancelling, "the calculation is stopping");
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.Equal("Stopping…", line.Message);
            Assert.Equal(Visibility.Collapsed, action.Visibility);
            Assert.True(model.IsLoading);
        }

        await UiWait.UntilAsync(() => !model.IsLoading && line.Kind == PageStatusKind.Attention, "the calculation was stopped");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Stopped: the state of the tab until it is calculated again - what it may lack, and Refresh. It cannot be closed.
        Assert.Equal("Attention", line.Title);
        Assert.Equal("Reading “What the computer gets – WAW-PC001” was stopped: the tab may not show everything. Press Refresh to read it again.", line.Message);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.False(ring.IsActive);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Refresh", Assert.IsType<string>(action.Content));
        Assert.Same(model.RefreshCommand, action.Command);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        view.UpdateLayout();
        Assert.Equal(tabsTop + GrowthOf(line), TopOf(view.PartTabs, view), 0.5);

        // Refresh, pressed in the line, calculates the model: the summary, and the tabs are where they were.
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !model.IsLoading && line.Kind == PageStatusKind.Idle, "the model was calculated again");
        Assert.Equal(model.SummaryText, line.Message);
        view.UpdateLayout();
        Assert.Equal(tabsTop, TopOf(view.PartTabs, view), 0.5);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task ACalculationThatFails_IsAnErrorWithRetry_UntilTheModelIsCalculatedAgain() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        context.Directory.Session.ComputesTokenGroups = true;
        var (main, _) = await OpenMainAsync(context);
        var (model, view) = await OpenAsync<GroupPolicyModelingDocumentViewModel, GroupPolicyModelingView>(main, Request(context));
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
        ControlHost.Press(view.RecalculateButton);
        await UiWait.UntilAsync(() => !model.IsLoading && line.Kind == PageStatusKind.Error, "the calculation failed");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // The line says why the tab is empty: what was tried, the reason, what to do, the code - with Retry, and it cannot be closed.
        Assert.Equal("Error", line.Title);
        Assert.NotNull(model.Error);
        Assert.Equal($"Could not read “What the computer gets – WAW-PC001”: {model.Error} Press Retry to try again. (ServerUnavailable)", line.Message);
        Assert.Equal(AppIcons.StatusError, line.IconKey);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Retry", Assert.IsType<string>(action.Content));
        Assert.Same(model.RefreshCommand, action.Command);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.False(model.HasModel);
        Assert.False(view.ExportCsvButton.IsEnabled);
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(view),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text == model.Error);
        AssertNoOldFeedback(view);

        // Retry, pressed in the line, calculates the model.
        Volatile.Write(ref fail, 0);
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !model.IsLoading && line.Kind == PageStatusKind.Idle, "the model was calculated");
        session.BeforeOperation = null;
        Assert.True(model.HasModel);
        Assert.Equal(model.SummaryText, line.Message);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task WhatTheModelCouldNotUse_IsANoteAboveTheResults_WhileTheLineKeepsTheSummary() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        context.Directory.Session.ComputesTokenGroups = true;
        var (main, _) = await OpenMainAsync(context);
        context.Sysvol.Unavailable = SysvolUnavailableReason.Unreachable;
        var (model, view) = await OpenAsync<GroupPolicyModelingDocumentViewModel, GroupPolicyModelingView>(main, Request(context));
        var line = view.PageStatus;

        // The line keeps the summary of the model: that the settings could not be read is a sentence about it, not an event.
        Assert.True(model.HasModel);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(model.SummaryText, line.Message);
        Assert.Null(model.Status.Lasting);

        // The note: an attention note under the notes of the document, above the tabs, with the note style - no bar.
        Assert.True(model.HasNotes);
        Assert.True(view.NotesBar.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Attention"), view.NotesBar.Style);
        Assert.Equal(model.Notes.Count, view.NotesList.Items.Count);
        Assert.Contains(UiWait.Descendants<TextBlock>(view.NotesBar), text => text.IsVisible && text.Text == model.Notes[0]);
        Assert.True(view.DocumentNotes.SysvolNote.IsVisible);
        Assert.True(TopOf(line, view) < TopOf(view.DocumentNotes.SysvolNote, view), "the SYSVOL note is not under the status line");
        Assert.True(TopOf(view.NotesBar, view) < TopOf(view.PartTabs, view), "the note is not above the tabs");
        AssertNoOldFeedback(view);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    private static GroupPolicyDocumentRequest Request(UiTestContext context) =>
        new(GroupPolicyDocumentKind.Modeling) { ComputerDn = context.Company.Computer1, Name = "WAW-PC001" };

    /// <summary>The read of the GPOs of the domain: the one-level search under CN=Policies,CN=System.</summary>
    private static bool IsReadOfThePolicies(FakeOperation operation) =>
        operation is { Kind: FakeOperationKind.Search, Search.Scope: SearchScope.OneLevel }
        && operation.DistinguishedName.StartsWith("CN=Policies,CN=System,", StringComparison.OrdinalIgnoreCase);
}
