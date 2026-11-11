using System.IO;
using System.Text;
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
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using AdMng.Presentation.Settings;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.App.UiTests;

/// <summary>
/// The status line of the batch editor (track V, stage V2b; docs/dev/v-interface-renewal.md "V2b-1 as built"), through
/// the real window over the fake directory: one line under the steps - the summary of the step shown, checking the
/// changes with Stop, executing them with the part done, the result with the domain - and the two action buttons that
/// are the Cancel of what they started. A slow domain controller is the fake session's latency (cancellable) or a
/// write the test holds and releases; every state that is asserted lasts until the test changes it.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class BatchEditorStatusUiTests
{
    /// <summary>Longer than any wait of a test: a call that is not cancelled fails the test instead of ending by itself.</summary>
    private static readonly TimeSpan SlowDomainController = TimeSpan.FromSeconds(30);

    /// <summary>How long a held write waits for the test at most (a failing test must not keep a pool thread for good).</summary>
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);

    [Fact]
    public Task TheEditor_HasOneStatusLine_UnderTheSteps_ThatSaysTheSummaryOfTheStepShown() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await CreateContextAsync();
        var company = context.Company;
        var (window, editor) = await OpenAsync(context, new BatchEditorRequest([company.JanKowalski, company.LukaszWisniewski])
        {
            Operations = [new SetAttributeOperation(Attr.Title, "Koordynator V2b")],
        });
        var line = window.PageStatus;
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");

        // One status line, named PageStatus, over the status of the window, between the steps and the content, of the
        // fixed height.
        Assert.Same(line, Assert.Single(UiWait.Descendants<StatusLine>(window)));
        Assert.True(line.IsVisible);
        Assert.IsAssignableFrom<IHasPageStatus>(editor);
        Assert.Same(editor.Status.Current, line.Status);
        Assert.Same(editor.Status.CloseCommand, line.CloseCommand);
        Assert.True(TopOf(window.StepStrip, window) < TopOf(line, window), "the status line is not under the steps");
        Assert.True(TopOf(line, window) < TopOf(window.ObjectsEditPanel, window), "the status line is not above the content");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);

        // The first step: the list of selected objects is in front and says its summary - nothing to press.
        Assert.Same(editor.SourceStatus, editor.Status.Front);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal("2 objects · User: 2", line.Message);
        Assert.Equal(AppIcons.StatusInfo, line.IconKey);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(line));
        AssertNoOldFeedback(window);

        // Preview is an action button at rest, over the command of before and the Cancel of the window's operation.
        Assert.True(window.PreviewButton.IsVisible);
        Assert.Equal(ActionButtonState.Idle, window.PreviewButton.State);
        Assert.Same(editor.PreviewCommand, window.PreviewButton.Command);
        Assert.Same(editor.Status.CancelCommand, window.PreviewButton.CancelCommand);
        Assert.True(window.PreviewButton.IsEnabled);
        Assert.False(window.ExecuteButton.IsVisible);

        // A rule the list of operations breaks is a note in its place (not a banner, and not a status): the line
        // keeps saying the summary.
        Assert.False(window.OperationsNote.IsVisible);
        editor.AddOperationCommand.Execute(OperationType.Delete);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.NotNull(editor.OperationsError);
        Assert.True(window.OperationsNote.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Attention"), window.OperationsNote.Style);
        Assert.True(window.OperationsNoteText.IsVisible);
        Assert.Equal(editor.OperationsError, window.OperationsNoteText.Text);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal("2 objects · User: 2", line.Message);
        Assert.False(window.PreviewButton.IsEnabled);
        editor.RemoveOperationCommand.Execute(editor.Operations.Single(o => o.Type == OperationType.Delete));
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.False(window.OperationsNote.IsVisible);
        Assert.True(window.PreviewButton.IsEnabled);

        // The preview: its summary is the summary of the line - said once, there - and Execute takes the place of
        // Preview.
        await editor.PreviewCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(BatchEditorStep.Preview, editor.Step);
        Assert.Null(editor.Status.Front);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.NotEqual(string.Empty, editor.PreviewSummary);
        Assert.Equal(editor.PreviewSummary, line.Message);
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(window),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text == editor.PreviewSummary);
        Assert.False(window.PreviewButton.IsVisible);
        Assert.True(window.ExecuteButton.IsVisible);
        Assert.Equal(ActionButtonState.Idle, window.ExecuteButton.State);
        Assert.Same(editor.ExecuteCommand, window.ExecuteButton.Command);
        Assert.Same(editor.Status.CancelCommand, window.ExecuteButton.CancelCommand);
        Assert.False(window.RollbackBanner.IsVisible);
        AssertNoOldFeedback(window);

        // Executed: what happened, how many, in which domain and where the report is - a result that can be closed.
        await editor.ExecuteCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(BatchEditorStep.Results, editor.Step);
        Assert.Equal(PageStatusKind.Done, line.Kind);
        Assert.Equal("Done", line.Title);
        Assert.Equal(
            $"“{editor.ChangeTitle}” was executed in {context.Directory.Session.Info.DomainDnsName}: 2 objects changed. "
            + "The report is in the list of background operations.",
            line.Message);
        Assert.Equal(AppIcons.StatusOk, line.IconKey);
        Assert.False(line.HasAction);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.False(window.ExecuteButton.IsVisible);
        AssertNoOldFeedback(window);

        // Closed: the counts of the results are the summary - in the line only.
        ControlHost.Press(close);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.NotEqual(string.Empty, editor.ResultSummary);
        Assert.Equal(editor.ResultSummary, line.Message);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(window),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text == editor.ResultSummary);

        // Undo shows its plan: that it is the undo plan is a note above the rows, and the line says its summary.
        editor.UndoCommand.Execute(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.True(editor.IsRollbackPreview);
        Assert.True(window.RollbackBanner.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Attention"), window.RollbackBanner.Style);
        Assert.Equal(context.Localizer()[L.Batch.RollbackPreview], window.RollbackBannerText.Text);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(editor.PreviewSummary, line.Message);
        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    [Fact]
    public Task ASlowCheck_IsWorkingWithStop_AndIsCancelledWhereItWasStarted() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await CreateContextAsync();
        var company = context.Company;
        var (window, editor) = await OpenAsync(context, new BatchEditorRequest([company.JanKowalski, company.LukaszWisniewski])
        {
            Operations = [new SetAttributeOperation(Attr.Title, "Koordynator V2b")],
        });
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        var contentTop = TopOf(window.ObjectsEditPanel, window);
        var session = context.Directory.Session;

        // A domain controller that takes its time: the check reads the objects and waits.
        session.Latency = SlowDomainController;
        try
        {
            ControlHost.Press(window.PreviewButton);
            await UiWait.UntilAsync(() => editor.IsBusy && line.IsWorking, "the changes are being checked");

            // Working: the action, the turning ring (nothing was checked yet), Stop - and nothing to close.
            Assert.True(editor.Status.IsWorking);
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.Equal("Working", line.Title);
            Assert.Equal("Checking the changes…", line.Message);
            Assert.True(ring.IsTurning, "the ring of the running check does not turn");
            Assert.Equal(Visibility.Visible, action.Visibility);
            Assert.Equal("Stop", Assert.IsType<string>(action.Content));
            Assert.Equal(Visibility.Collapsed, close.Visibility);

            // The Preview button is the Cancel of the check it started: it stays enabled although its command cannot
            // execute. There is no separate Stop button and no progress bar.
            Assert.True(window.PreviewButton.IsVisible);
            Assert.Equal(ActionButtonState.Running, window.PreviewButton.State);
            Assert.True(window.PreviewButton.IsEnabled);
            Assert.False(editor.PreviewCommand.CanExecute(null));
            Assert.DoesNotContain(
                UiWait.Descendants<Button>(window),
                other => other.IsVisible && !other.IsDescendantOf(line) && other.Content is string text && text == "Stop");
            AssertNoOldFeedback(window);

            // The line was there before: the content did not move.
            window.UpdateLayout();
            Assert.Equal(contentTop, TopOf(window.ObjectsEditPanel, window));

            // Cancel where you started: the check ends well before the domain controller would have answered.
            ControlHost.Press(window.PreviewButton);
            await UiWait.UntilAsync(() => window.PreviewButton.State == ActionButtonState.Idle && !editor.IsBusy, "the check stopped");
        }
        finally
        {
            session.Latency = TimeSpan.Zero;
        }

        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Stopped: control is back in the first step, nothing was planned or written, and the line says what was
        // stopped - with Retry, and it can be closed.
        Assert.Equal(BatchEditorStep.Edit, editor.Step);
        Assert.True(window.ObjectsEditPanel.IsVisible);
        Assert.Empty(editor.PreviewObjects);
        Assert.Empty(session.Writes);
        Assert.Equal(PageStatusKind.Attention, line.Kind);
        Assert.Equal("Attention", line.Title);
        Assert.Equal("Checking the changes was stopped: nothing was changed. Press Retry to try again.", line.Message);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.False(ring.IsTurning);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Retry", Assert.IsType<string>(action.Content));
        Assert.Same(editor.PreviewCommand, action.Command);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.True(window.PreviewButton.IsEnabled);
        window.UpdateLayout();
        Assert.Equal(contentTop, TopOf(window.ObjectsEditPanel, window));

        // Retry, pressed in the line, checks again - the domain controller answers now: the preview and its summary.
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => editor.Step == BatchEditorStep.Preview && !editor.IsBusy && line.Kind == PageStatusKind.Idle, "the preview after Retry");
        Assert.Equal(editor.PreviewSummary, line.Message);
        Assert.Equal(Visibility.Collapsed, action.Visibility);
        Assert.Equal(2, editor.ChangeCount);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task ACheckThatFails_IsAnErrorInTheLine_WithRetry_AndTheEndOfTheSessionLasts() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await CreateContextAsync();
        var (window, editor) = await OpenAsync(context, new BatchEditorRequest([context.Company.JanKowalski])
        {
            Operations = [new SetAttributeOperation(Attr.Title, "Koordynator V2b")],
        });
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var session = context.Directory.Session;

        // The check cannot read the object.
        var fail = true;
        session.BeforeOperation = operation =>
        {
            if (fail && operation.Kind == FakeOperationKind.Get)
            {
                throw new InvalidOperationException("the planner cannot read the object");
            }
        };
        await editor.PreviewCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // What we tried: why it failed. What to do next. (code) - in the line, and nowhere else in the window.
        var reason = Assert.IsType<string>(editor.Error);
        Assert.Equal(BatchEditorStep.Edit, editor.Step);
        Assert.Equal(PageStatusKind.Error, line.Kind);
        Assert.Equal("Error", line.Title);
        Assert.Equal(
            PageStatusText.Failure("Could not check the changes", reason, "Press Retry to try again.", ResultCode.Of(new InvalidOperationException())),
            line.Message);
        Assert.Equal(AppIcons.StatusError, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.Equal("Retry", Assert.IsType<string>(action.Content));
        Assert.Same(editor.PreviewCommand, action.Command);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Equal(ActionButtonState.Idle, window.PreviewButton.State);
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(window),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text.Contains(reason, StringComparison.Ordinal));
        AssertNoOldFeedback(window);

        // Retry, pressed in the line, once the object can be read: the preview.
        fail = false;
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => editor.Step == BatchEditorStep.Preview && line.Kind == PageStatusKind.Idle, "the preview after Retry");
        session.BeforeOperation = null;
        Assert.Null(editor.Error);
        Assert.Equal(editor.PreviewSummary, line.Message);
        Assert.True(window.ExecuteButton.IsEnabled);

        // The session of the window ends: an error that cannot be closed, for as long as the window is open - and
        // nothing can be executed any more.
        await context.Services.GetRequiredService<ISessionManager>().DisconnectAsync();
        await UiWait.UntilAsync(() => editor.IsSessionEnded && line.Kind == PageStatusKind.Error, "the end of the session in the line");
        Assert.Equal(context.Localizer()[L.WindowSession.Ended], line.Message);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.False(window.ExecuteButton.IsEnabled);
        Assert.Equal(ActionButtonState.Idle, window.ExecuteButton.State);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task AnExecution_SaysHowFarItIs_OnTheLine_AndTheExecuteButtonIsItsCancel() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await CreateContextAsync();
        var company = context.Company;
        var (window, editor) = await OpenAsync(context, new BatchEditorRequest([company.JanKowalski, company.LukaszWisniewski])
        {
            Operations = [new SetAttributeOperation(Attr.Title, "Koordynator V2b")],
        });
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        var session = context.Directory.Session;
        var domain = session.Info.DomainDnsName;
        await editor.PreviewCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(BatchEditorStep.Preview, editor.Step);

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
                () => held.IsSet && editor.Step == BatchEditorStep.Executing && !double.IsNaN(line.ProgressFraction),
                "the first object was executed and the second one waits");

            // Working: what is executed, in which domain, and the part done - the ring shows it and stands still.
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.Equal($"Executing “{editor.ChangeTitle}” in {domain}: 1 of 2…", line.Message);
            Assert.Equal(0.5, line.ProgressFraction, 5);
            Assert.Equal(0.5, ring.Fraction, 5);
            Assert.True(ring.IsActive);
            Assert.False(ring.IsTurning, "a ring with a known part done turns");
            Assert.Equal("Stop", Assert.IsType<string>(action.Content));
            Assert.Equal(Visibility.Collapsed, close.Visibility);

            // The Execute button stays and is the Cancel of the execution; the step shows the object done last. There
            // is no separate Stop button and no progress bar.
            Assert.True(window.ExecutingPanel.IsVisible);
            Assert.True(window.ExecuteButton.IsVisible);
            Assert.Equal(ActionButtonState.Running, window.ExecuteButton.State);
            Assert.True(window.ExecuteButton.IsEnabled);
            Assert.False(editor.ExecuteCommand.CanExecute(null));
            Assert.NotNull(editor.CurrentActivity);
            Assert.True(window.CurrentObjectText.IsVisible);
            Assert.False(string.IsNullOrEmpty(window.CurrentObjectText.Text));
            Assert.Equal(editor.CurrentActivity!.CurrentObject, window.CurrentObjectText.Text);
            Assert.DoesNotContain(
                UiWait.Descendants<Button>(window),
                other => other.IsVisible && !other.IsDescendantOf(line) && other.Content is string text && text == "Stop");
            AssertNoOldFeedback(window);
        }
        finally
        {
            release.Set();
        }

        await UiWait.UntilAsync(() => editor.Step == BatchEditorStep.Results && line.Kind == PageStatusKind.Done, "the execution ended");
        session.BeforeOperation = null;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Done, with the domain; the button of the step is gone with the step.
        Assert.Equal(
            $"“{editor.ChangeTitle}” was executed in {domain}: 2 objects changed. The report is in the list of background operations.",
            line.Message);
        Assert.False(ring.IsActive);
        Assert.Equal(Visibility.Collapsed, action.Visibility);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.False(window.ExecuteButton.IsVisible);
        Assert.True(window.ResultsPanel.IsVisible);
        Assert.Equal(2, editor.Result!.SucceededCount);
        context.AssertHealthy();
    });

    [Fact]
    public Task StopOfAnExecution_OnTheExecuteButton_SaysStopping_AndEndsAsAttentionWithRetryFailed() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await CreateContextAsync();
        var company = context.Company;
        var (window, editor) = await OpenAsync(context, new BatchEditorRequest([company.JanKowalski, company.LukaszWisniewski])
        {
            Operations = [new SetAttributeOperation(Attr.Title, "Koordynator V2b")],
        });
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var session = context.Directory.Session;
        var domain = session.Info.DomainDnsName;
        await editor.PreviewCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

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
                () => held.IsSet && editor.CurrentActivity is { IsRunning: true } && window.ExecuteButton.State == ActionButtonState.Running,
                "the execution runs");
            var activity = editor.CurrentActivity!;

            // Cancel where you started: "Stopping…" on the button and in the line at once - the step that is on its
            // way to the domain controller still has to end - and the activity of this execution is cancelled.
            ControlHost.Press(window.ExecuteButton);
            await UiWait.UntilAsync(() => window.ExecuteButton.State == ActionButtonState.Cancelling && editor.Status.IsCancelling, "the execution is stopping");
            Assert.True(activity.IsCancelling);
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.Equal("Stopping…", line.Message);
            Assert.Equal(Visibility.Collapsed, action.Visibility);
            Assert.True(window.ExecuteButton.IsEnabled);
            Assert.True(window.ExecuteButton.IsVisible);
        }
        finally
        {
            release.Set();
        }

        await UiWait.UntilAsync(() => editor.Step == BatchEditorStep.Results && line.Kind == PageStatusKind.Attention, "the execution was stopped");
        session.BeforeOperation = null;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Stopped: what was changed, what was not, in which domain - and the way on as the action of the line.
        Assert.True(editor.Result!.WasCancelled);
        Assert.Equal(
            $"Executing “{editor.ChangeTitle}” in {domain} was stopped: 1 object changed, failed: 0, the rest was not executed. "
            + "Press “Retry failed” to plan the objects that were not changed again.",
            line.Message);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Retry failed", Assert.IsType<string>(action.Content));
        Assert.Same(editor.RetryFailedCommand, action.Command);
        Assert.False(window.ExecuteButton.IsVisible);

        // The action plans what was not executed: its preview, and the line is about that preview.
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => editor.Step == BatchEditorStep.Preview && !editor.IsBusy && line.Kind == PageStatusKind.Idle, "the preview of the retry");
        Assert.Equal(1, editor.ObjectCount);
        Assert.Equal(editor.PreviewSummary, line.Message);
        Assert.Equal(ActionButtonState.Idle, window.ExecuteButton.State);
        Assert.True(window.ExecuteButton.IsVisible);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheCsvImport_SaysInTheLine_WhichFileIsLoaded_AndThatAFileCouldNotBeRead() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await CreateContextAsync();
        var (window, editor) = await OpenAsync(context, new BatchEditorRequest([]));
        var line = window.PageStatus;
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var csv = editor.CsvImport!;

        // No file yet: the import is in front and has nothing to say (the hint is next to the Browse button).
        Assert.True(window.CsvEditPanel.IsVisible);
        Assert.Same(csv.Status, editor.Status.Front);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(string.Empty, line.Message);
        AssertNoOldFeedback(window);

        var path = Path.Combine(Path.GetTempPath(), $"admng-uitest-status-{Guid.NewGuid():N}.csv");
        var name = Path.GetFileName(path);
        try
        {
            await File.WriteAllTextAsync(
                path,
                "sAMAccountName;title\r\njan.kowalski;Kierownik zespołu\r\nlukasz.wisniewski;Programista\r\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            await csv.LoadFileAsync(path);
        }
        finally
        {
            File.Delete(path);
        }

        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // The file is the content: its name and size are the summary of the line; the name stays next to Browse.
        var summary = $"{name} · 2 rows, delimiter: ;";
        Assert.Null(csv.Error);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(summary, line.Message);
        Assert.Equal(name, window.FileNameText.Text);
        AssertNoOldFeedback(window);

        // The same file again, gone meanwhile: what was tried, why and what to do - in the line, and nowhere else; the
        // file that was loaded stays.
        await csv.LoadFileAsync(path);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        var reason = Assert.IsType<string>(csv.Error);
        Assert.Equal(PageStatusKind.Error, line.Kind);
        Assert.StartsWith($"Could not read “{name}”: {reason}", line.Message, StringComparison.Ordinal);
        Assert.Contains(" Choose another file.", line.Message, StringComparison.Ordinal);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Equal(2, csv.RowCount);
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(window),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text.Contains(reason, StringComparison.Ordinal));
        AssertNoOldFeedback(window);

        // Closed: the summary of the loaded file again.
        ControlHost.Press(close);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(summary, line.Message);
        context.AssertHealthy();
    });

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>
    /// A connected test host: confirmations are answered with "yes" without a window, and the batch runs one object
    /// after another, so "1 of 2" is a state a test can hold.
    /// </summary>
    private static async Task<UiTestContext> CreateContextAsync()
    {
        var dialogs = new ScriptedDialogService { ConfirmAnswer = true };
        var context = await UiTestContext.CreateAsync(
            configure: services => services.Replace(ServiceDescriptor.Singleton<IDialogService>(dialogs)));
        await context.ConnectAsync();
        await context.Services.GetRequiredService<ISettingsStore>().UpdateAsync(s => s.BatchMaxParallelism = 1);
        return context;
    }

    private static async Task<(BatchEditorWindow Window, BatchEditorViewModel Editor)> OpenAsync(UiTestContext context, BatchEditorRequest request)
    {
        var editor = context.Services.GetRequiredService<IDialogViewModelFactory>().CreateBatchEditor(request);
        var window = (BatchEditorWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(editor);
        UiTestSupport.ShowOffScreen(window);
        await UiWait.InitializedAsync(window.Initialization);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        return (window, editor);
    }

    /// <summary>The old places of feedback are gone from the window: no progress bar and no banner of the styles of before.</summary>
    private static void AssertNoOldFeedback(BatchEditorWindow window)
    {
        var errorBar = Application.Current.FindResource("Ad.ErrorBar");
        var warningBar = Application.Current.FindResource("Ad.WarningBar");
        Assert.Empty(UiWait.Descendants<ProgressBar>(window));
        Assert.DoesNotContain(
            UiWait.Descendants<Border>(window),
            border => ReferenceEquals(border.Style, errorBar) || ReferenceEquals(border.Style, warningBar));
        Assert.Same(window.PageStatus, Assert.Single(UiWait.Descendants<StatusLine>(window)));
    }

    private static double TopOf(FrameworkElement element, UIElement relativeTo) =>
        element.TranslatePoint(new Point(0, 0), relativeTo).Y;
}
