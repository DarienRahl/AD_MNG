using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Session;
using AdMng.Presentation.Settings;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

/// <summary>
/// The status line of the change plan preview (track V, stage V2b; docs/dev/v-interface-renewal.md "V2b-1 as built"),
/// through the real window over the fake directory: one line under the title - the summary of the plan, the execution
/// with the part done, its result with the domain and Undo as its action - and Execute as an action button that is the
/// Cancel of the execution it started. The plan is one the fake directory can execute (two accounts get a new title);
/// a slow domain controller is a write the test holds and releases, a failing one an injected failure; every state that
/// is asserted lasts until the test changes it.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ChangePlanPreviewStatusUiTests
{
    private const string Title = "Zmiana stanowiska – dział IT";

    /// <summary>How long a held write waits for the test at most (a failing test must not keep a pool thread for good).</summary>
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);

    [Fact]
    public Task ThePreview_HasOneStatusLine_UnderTheTitle_WithTheSummaryOfThePlan() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (window, preview) = await OpenAsync(context);
        var line = window.PageStatus;

        // One status line, named PageStatus, over the status of the window, between the title and the tabs, of the
        // fixed height.
        Assert.Same(line, Assert.Single(UiWait.Descendants<StatusLine>(window)));
        Assert.True(line.IsVisible);
        Assert.IsAssignableFrom<IHasPageStatus>(preview);
        Assert.Same(preview.Status.Current, line.Status);
        Assert.Same(preview.Status.CloseCommand, line.CloseCommand);
        Assert.Equal(Title, window.TitleText.Text);
        Assert.True(TopOf(window.TitleText, window) < TopOf(line, window), "the status line is not under the title");
        Assert.True(TopOf(line, window) < TopOf(window.PreviewTabs, window), "the status line is not above the tabs");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);

        // Nothing happens: the summary of the plan - in the line, and not a second time in the footer.
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.NotEqual(string.Empty, preview.Summary);
        Assert.Equal(preview.Summary, line.Message);
        Assert.Contains(context.Directory.Session.Info.Server, line.Message, StringComparison.Ordinal);
        Assert.Equal(AppIcons.StatusInfo, line.IconKey);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(line));
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(window),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text == preview.Summary);

        // Execute is an action button at rest - the default button of the window - over the command of before and the
        // Cancel of the window's operation. Nothing was executed: no Undo, no note about an undo plan.
        Assert.True(window.ExecuteButton.IsVisible);
        Assert.True(window.ExecuteButton.IsDefault);
        Assert.Equal(ActionButtonState.Idle, window.ExecuteButton.State);
        Assert.Same(preview.ExecuteCommand, window.ExecuteButton.Command);
        Assert.Same(preview.Status.CancelCommand, window.ExecuteButton.CancelCommand);
        Assert.True(window.ExecuteButton.IsEnabled);
        Assert.False(window.UndoButton.IsVisible);
        Assert.False(window.RollbackNote.IsVisible);
        Assert.True(window.CloseButton.IsEnabled);
        Assert.NotEmpty(preview.Rows);
        Assert.Equal(preview.Rows.Count, window.RowsList.Items.Count);
        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    [Fact]
    public Task Execute_SaysHowFarItIs_ThenWhatHappenedAndWhere_AndUndoShowsItsPlanWithANote() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (window, preview) = await OpenAsync(context);
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        var session = context.Directory.Session;
        var domain = session.Info.DomainDnsName;
        var jan = context.Company.JanKowalski;
        var before = session.GetValues(jan, Attr.Title).ToList();
        var tabsTop = TopOf(window.PreviewTabs, window);

        // One object after another, and the write of the second one waits for the test.
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var writes = 0;
        session.BeforeOperation = operation =>
        {
            if (operation.IsWrite && Interlocked.Increment(ref writes) == 2)
            {
                held.Set();
                release.Wait(HoldLimit);
            }
        };
        try
        {
            ControlHost.Press(window.ExecuteButton);
            await UiWait.UntilAsync(
                () => held.IsSet && preview.IsExecutingStep && !double.IsNaN(line.ProgressFraction),
                "the first object was executed and the second one waits");

            // Working: what is executed, in which domain, and the part done - the ring shows it and stands still.
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.Equal("Working", line.Title);
            Assert.Equal($"Executing “{Title}” in {domain}: 1 of 2…", line.Message);
            Assert.Equal(0.5, line.ProgressFraction, 5);
            Assert.Equal(0.5, ring.Fraction, 5);
            Assert.True(ring.IsActive);
            Assert.False(ring.IsTurning, "a ring with a known part done turns");
            Assert.Equal(Visibility.Visible, action.Visibility);
            Assert.Equal("Stop", Assert.IsType<string>(action.Content));
            Assert.Equal(Visibility.Collapsed, close.Visibility);

            // The Execute button stays and is the Cancel of the execution: enabled although its command cannot
            // execute. There is no separate Cancel button and no progress bar, and the window cannot be closed.
            Assert.True(window.ExecuteButton.IsVisible);
            Assert.Equal(ActionButtonState.Running, window.ExecuteButton.State);
            Assert.True(window.ExecuteButton.IsEnabled);
            Assert.False(preview.ExecuteCommand.CanExecute(null));
            Assert.False(window.CloseButton.IsEnabled);
            Assert.DoesNotContain(
                UiWait.Descendants<Button>(window),
                other => other.IsVisible && other is not ActionButton && !other.IsDescendantOf(line)
                    && other.Content is string text && (text == "Stop" || text == "Cancel"));
            AssertNoOldFeedback(window);

            // The line was there before: the tabs did not move.
            window.UpdateLayout();
            Assert.Equal(tabsTop, TopOf(window.PreviewTabs, window));
        }
        finally
        {
            release.Set();
        }

        await UiWait.UntilAsync(() => preview.IsResultsStep && line.Kind == PageStatusKind.Done, "the execution ended");
        session.BeforeOperation = null;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Done: what happened, how many, in which domain and where the report is - said once, in the line. The results
        // are shown, Undo takes the place of Execute.
        Assert.Equal("Done", line.Title);
        Assert.Equal(
            $"“{Title}” was executed in {domain}: 2 objects changed. The report is in the list of background operations.",
            line.Message);
        Assert.Equal(AppIcons.StatusOk, line.IconKey);
        Assert.False(ring.IsActive);
        Assert.Equal(Visibility.Collapsed, action.Visibility);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.NotEqual(string.Empty, preview.ResultText);
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(window),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text == preview.ResultText);
        Assert.Same(window.ResultsTab, window.PreviewTabs.SelectedItem);
        Assert.False(window.ExecuteButton.IsVisible);
        Assert.True(window.UndoButton.IsVisible);
        Assert.True(window.UndoButton.IsEnabled);
        Assert.True(window.CloseButton.IsEnabled);
        Assert.Equal(["Koordynator V2b"], session.GetValues(jan, Attr.Title));
        window.UpdateLayout();
        Assert.Equal(tabsTop + GrowthOf(line), TopOf(window.PreviewTabs, window), 0.5);

        // Undo shows the undo plan: the line says its summary, and that it is the undo plan is a note above the tabs
        // for as long as that plan waits to be executed.
        ControlHost.Press(window.UndoButton);
        await UiWait.UntilAsync(() => preview.IsRollbackPreview && preview.IsPreviewStep, "the undo plan is shown");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(preview.Summary, line.Message);
        Assert.True(window.RollbackNote.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Attention"), window.RollbackNote.Style);
        Assert.True(window.RollbackNoteText.IsVisible);
        Assert.Equal(context.Localizer()[L.PlanPreview.RollbackShown], window.RollbackNoteText.Text);
        Assert.True(window.ExecuteButton.IsVisible);
        Assert.Equal(ActionButtonState.Idle, window.ExecuteButton.State);
        Assert.True(window.ExecuteButton.IsEnabled);
        Assert.Same(window.ChangesTab, window.PreviewTabs.SelectedItem);
        AssertNoOldFeedback(window);

        // The undo plan is executed like any plan: its result names the domain, and the note goes with the plan.
        await preview.ExecuteCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.True(preview.IsResultsStep);
        Assert.Equal(PageStatusKind.Done, line.Kind);
        Assert.Contains($" in {domain}: ", line.Message, StringComparison.Ordinal);
        Assert.Contains(preview.ChangeTitle, line.Message, StringComparison.Ordinal);
        Assert.False(window.RollbackNote.IsVisible);
        Assert.Equal(before, session.GetValues(jan, Attr.Title));
        context.AssertHealthy();
    });

    [Fact]
    public Task StopOfAnExecution_OnTheExecuteButton_SaysStopping_AndEndsAsAttentionWithUndo() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (window, preview) = await OpenAsync(context);
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var session = context.Directory.Session;
        var domain = session.Info.DomainDnsName;

        // The first write waits for the test.
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        session.BeforeOperation = operation =>
        {
            if (operation.IsWrite)
            {
                held.Set();
                release.Wait(HoldLimit);
            }
        };
        try
        {
            ControlHost.Press(window.ExecuteButton);
            await UiWait.UntilAsync(
                () => held.IsSet && preview.CurrentActivity is { IsRunning: true } && window.ExecuteButton.State == ActionButtonState.Running,
                "the execution runs");
            var activity = preview.CurrentActivity!;

            // Cancel where you started: "Stopping…" on the button and in the line at once - the step that is on its
            // way to the domain controller still has to end - and the activity of this execution is cancelled.
            ControlHost.Press(window.ExecuteButton);
            await UiWait.UntilAsync(() => window.ExecuteButton.State == ActionButtonState.Cancelling && preview.Status.IsCancelling, "the execution is stopping");
            Assert.True(activity.IsCancelling);
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.Equal("Stopping…", line.Message);
            Assert.Equal(Visibility.Collapsed, action.Visibility);
            Assert.True(window.ExecuteButton.IsVisible);
            Assert.True(window.ExecuteButton.IsEnabled);
            Assert.False(window.CloseButton.IsEnabled);
        }
        finally
        {
            release.Set();
        }

        await UiWait.UntilAsync(() => preview.IsResultsStep && line.Kind == PageStatusKind.Attention, "the execution was stopped");
        session.BeforeOperation = null;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Stopped: what was changed, what was not, in which domain - and Undo as the way to revert what was made.
        var result = preview.Result!;
        Assert.True(result.WasCancelled);
        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal("Attention", line.Title);
        Assert.Equal(
            $"Executing “{Title}” in {domain} was stopped: 1 object changed, failed: 0, the rest was not executed. "
            + "Press Undo to revert what was made.",
            line.Message);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Undo", Assert.IsType<string>(action.Content));
        Assert.Same(preview.UndoCommand, action.Command);
        Assert.False(window.ExecuteButton.IsVisible);
        Assert.True(window.CloseButton.IsEnabled);

        // Undo, pressed in the line, shows the undo plan.
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => preview.IsRollbackPreview && preview.IsPreviewStep && line.Kind == PageStatusKind.Idle, "the undo plan is shown");
        Assert.Equal(preview.Summary, line.Message);
        Assert.True(window.RollbackNote.IsVisible);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task AnExecutionThatFailsInPart_IsAttentionInTheLine_AndTheEndOfTheSessionLasts() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (window, preview) = await OpenAsync(context);
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var session = context.Directory.Session;
        var domain = session.Info.DomainDnsName;

        // The domain controller refuses the change of one of the two objects.
        session.FailNext(FakeOperationKind.Modify, context.Company.LukaszWisniewski, DirectoryErrorKind.AccessDenied);
        await preview.ExecuteCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // One of two: what was changed, what was not, where the reasons are and what to do - in the line, and not a
        // second time in the window.
        Assert.True(preview.IsResultsStep);
        Assert.True(preview.ResultHasFailures);
        Assert.Equal(PageStatusKind.Attention, line.Kind);
        Assert.Equal(
            $"“{Title}” was executed only in part in {domain}: 1 object changed, failed: 1. "
            + "The reasons are in the list of results. Press Undo to revert what was made.",
            line.Message);
        Assert.Equal("Undo", Assert.IsType<string>(action.Content));
        Assert.Same(preview.UndoCommand, action.Command);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Same(window.ResultsTab, window.PreviewTabs.SelectedItem);
        Assert.Equal(2, preview.ResultRows.Count);
        Assert.Equal(preview.ResultRows.Count, window.ResultsList.Items.Count);
        Assert.NotEqual(string.Empty, preview.ResultText);
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(window),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text == preview.ResultText);
        AssertNoOldFeedback(window);

        // The session of the window ends: an error that cannot be closed takes the line, and nothing can be undone or
        // executed any more.
        await context.Services.GetRequiredService<ISessionManager>().DisconnectAsync();
        await UiWait.UntilAsync(() => preview.IsSessionEnded && line.Kind == PageStatusKind.Error, "the end of the session in the line");
        Assert.Equal(context.Localizer()[L.WindowSession.Ended], line.Message);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.False(window.UndoButton.IsEnabled);
        Assert.True(window.CloseButton.IsEnabled);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The preview window over a plan the fake directory can execute: two accounts of the sample company get a new
    /// title, one object after the other (so "1 of 2" is a state a test can hold).
    /// </summary>
    private static async Task<(ChangePlanPreviewWindow Window, ChangePlanPreviewViewModel Preview)> OpenAsync(UiTestContext context)
    {
        var session = await context.ConnectAsync();
        await context.Services.GetRequiredService<ISettingsStore>().UpdateAsync(s => s.BatchMaxParallelism = 1);
        var gp = context.Services.GetRequiredService<GroupPolicyServices>();
        var request = new BatchRequest(
            [context.Company.JanKowalski, context.Company.LukaszWisniewski],
            [new SetAttributeOperation(Attr.Title, "Koordynator V2b")])
        {
            Title = Title,
        };
        var plan = await gp.Engines.Planner.PlanAsync(session, request, new PlanningOptions { MaxDegreeOfParallelism = 1 }, null, CancellationToken.None);
        Assert.Equal(2, plan.ExecutableCount);

        var preview = new ChangePlanPreviewViewModel(gp.Ui, gp.Engines, new ChangePlanPreviewRequest(plan, Title));
        var window = (ChangePlanPreviewWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(preview);
        UiTestSupport.ShowOffScreen(window);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        return (window, preview);
    }

    /// <summary>The old places of feedback are gone from the window: no progress bar and no banner of the styles of before.</summary>
    private static void AssertNoOldFeedback(ChangePlanPreviewWindow window)
    {
        var errorBar = Application.Current.FindResource("Ad.ErrorBar");
        var warningBar = Application.Current.FindResource("Ad.WarningBar");
        Assert.Empty(UiWait.Descendants<ProgressBar>(window));
        Assert.DoesNotContain(
            UiWait.Descendants<Border>(window),
            border => ReferenceEquals(border.Style, errorBar) || ReferenceEquals(border.Style, warningBar));
        Assert.Same(window.PageStatus, Assert.Single(UiWait.Descendants<StatusLine>(window)));
    }

    /// <summary>
    /// How much higher than one line the status line is right now: a result that does not fit one line wraps (to three
    /// lines at most), and what lies under the line moves down by as much - by nothing when the result fits.
    /// </summary>
    private static double GrowthOf(StatusLine line)
    {
        var growth = line.ActualHeight - (double)Application.Current.FindResource("Ad.Size.StatusLine");
        Assert.InRange(growth, -0.5, 40.5);
        return growth;
    }

    private static double TopOf(FrameworkElement element, UIElement relativeTo) =>
        element.TranslatePoint(new Point(0, 0), relativeTo).Y;
}
