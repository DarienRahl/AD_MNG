using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Services;
using AdMng.Presentation.Tests.Lists;
using AdMng.Presentation.Tests.TestDoubles;
using AdMng.Presentation.Tests.Watchdog;
using static AdMng.Presentation.Tests.TestDoubles.TestDirectory;

namespace AdMng.Presentation.Tests.Changes;

public class ChangePipelineTests
{
    [Fact]
    public void DirectoryChanges_AreDerivedFromTheBatchResult()
    {
        var now = TestHarness.Now;
        ObjectResult Result(string dn, ObjectResultStatus status, string finalDn, params PlannedChange[] changes) => new(
            new ObjectPlan { DistinguishedName = dn, DisplayName = dn, Kind = ObjectKind.User, Changes = changes },
            status,
            [.. changes.Select(c => new ChangeResult(c, status != ObjectResultStatus.Failed))],
            finalDn,
            now,
            now);
        var modify = new PlannedChange { Action = ChangeAction.Modify };
        var plan = new ChangePlan { Objects = [] };
        var result = new BatchResult(
            plan,
            [
                Result(Lukasz, ObjectResultStatus.Succeeded, Lukasz, modify, new PlannedChange { Action = ChangeAction.AddMember, TargetDn = ItGroup }),
                Result(Ewa, ObjectResultStatus.Succeeded, "CN=Ewa Kowalska,OU=Księgowość,OU=Firma,DC=contoso,DC=test", new PlannedChange { Action = ChangeAction.Move, TargetDn = Ksiegowosc }),
                Result(Jan, ObjectResultStatus.Succeeded, Jan, new PlannedChange { Action = ChangeAction.Delete }),
                Result(Stary, ObjectResultStatus.Failed, Stary, modify),
            ],
            "dc01",
            "op",
            now,
            now,
            WasCancelled: false);

        var changes = DirectoryChanges.FromResult(result);

        Assert.Equal(
            [
                new DirectoryChange(DirectoryChangeKind.Modified, Lukasz, null, ObjectKind.User),
                new DirectoryChange(DirectoryChangeKind.Modified, ItGroup, null, ObjectKind.Group),
                new DirectoryChange(DirectoryChangeKind.Moved, Ewa, "CN=Ewa Kowalska,OU=Księgowość,OU=Firma,DC=contoso,DC=test", ObjectKind.User),
                new DirectoryChange(DirectoryChangeKind.Deleted, Jan, null, ObjectKind.User),
            ],
            changes);
        Assert.Equal(Ksiegowosc, changes[2].NewParentDn, ignoreCase: true);
        Assert.Equal(It, changes[2].OldParentDn, ignoreCase: true);
    }

    [Fact]
    public async Task Coordinator_WithPartialErrors_AsksBeforeExecutingTheValidObjects()
    {
        var harness = await TestHarness.ConnectedAsync();
        harness.Planner.PlanFactory = r => new ChangePlan
        {
            Objects =
            [
                RecordingChangePlanner.WithIssue(Lukasz, IssueSeverity.Error, IssueCodes.AdminSdHolder),
                RecordingChangePlanner.DefaultPlan(r).Objects[1],
            ],
        };
        var request = new ChangeRequest([Lukasz, Ewa], [new SetAccountEnabledOperation(true)], "Enable");

        harness.Dialogs.ConfirmAnswers.Enqueue(false);
        Assert.Equal(ChangeOutcome.Cancelled, await harness.Coordinator.ApplyAsync(request, TestContext.Current.CancellationToken));
        Assert.Empty(harness.Executor.Plans);
        Assert.Contains("Privileged account protected by AdminSDHolder", harness.Dialogs.Shown.Single(), StringComparison.Ordinal);

        harness.Dialogs.ConfirmAnswers.Enqueue(true);
        Assert.Equal(ChangeOutcome.Executed, await harness.Coordinator.ApplyAsync(request, TestContext.Current.CancellationToken));
        Assert.Single(harness.Executor.Plans);
    }

    [Fact]
    public async Task Coordinator_NoEffectiveChange_InformsWithoutExecuting()
    {
        var harness = await TestHarness.ConnectedAsync();
        harness.Planner.PlanFactory = r => new ChangePlan
        {
            Objects = [new ObjectPlan { DistinguishedName = r.TargetDns[0], DisplayName = "x" }],
        };

        var outcome = await harness.Coordinator.ApplyAsync(
            new ChangeRequest([Ewa], [new SetAccountEnabledOperation(false)], "Disable Account"), TestContext.Current.CancellationToken);

        Assert.Equal(ChangeOutcome.NothingToDo, outcome);
        Assert.Empty(harness.Executor.Plans);
        Assert.Equal(NotificationSeverity.Info, Assert.Single(harness.Notifications.Items).Severity);
    }

    [Fact]
    public async Task Coordinator_PlannerFailure_IsShownAsError()
    {
        var harness = await TestHarness.ConnectedAsync();
        harness.Planner.PlanFactory = _ => throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "down");

        var outcome = await harness.Coordinator.ApplyAsync(
            new ChangeRequest([Ewa], [new UnlockAccountOperation()], "Unlock"), TestContext.Current.CancellationToken);

        Assert.Equal(ChangeOutcome.Failed, outcome);
        Assert.Equal("The domain controller is not available.", Assert.Single(harness.Dialogs.Errors).Message);
    }

    [Fact]
    public async Task Coordinator_WhenDisconnected_DoesNothing()
    {
        var harness = TestHarness.Disconnected();

        var outcome = await harness.Coordinator.ApplyAsync(
            new ChangeRequest([Ewa], [new UnlockAccountOperation()], "Unlock"), TestContext.Current.CancellationToken);

        Assert.Equal(ChangeOutcome.NotConnected, outcome);
        Assert.Empty(harness.Planner.Requests);
    }

    [Fact]
    public async Task Activity_ReportsProgress_AndCanBeCancelled()
    {
        var harness = await TestHarness.ConnectedAsync();
        var gate = new TaskCompletionSource();
        harness.Executor.Gate = gate;
        var plan = RecordingChangePlanner.DefaultPlan(new BatchRequest([Lukasz, Ewa], [new UnlockAccountOperation()]));

        var execution = harness.Coordinator.ExecutePlanAsync(plan, "Unlock", TestContext.Current.CancellationToken);
        var activity = Assert.Single(harness.Activities.Activities);
        Assert.True(activity.IsRunning);
        Assert.True(harness.Activities.HasRunning);
        Assert.Equal("Running operations: 1", harness.Activities.Summary);
        Assert.Equal("0 / 2", activity.StatusText);

        activity.CancelCommand.Execute(null);
        Assert.True(activity.IsCancelling);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);

        await ObjectListViewModelTests.WaitUntilAsync(() => !harness.Activities.HasRunning);
        Assert.False(activity.IsRunning);
        Assert.Equal("The operation was cancelled.", activity.Error);
        harness.Activities.ClearFinishedCommand.Execute(null);
        Assert.Empty(harness.Activities.Activities);
    }

    [Fact]
    public async Task Coordinator_CancelledFromTheActivityCenter_EndsWithoutAnErrorDialog()
    {
        var harness = await TestHarness.ConnectedAsync();
        harness.Executor.Gate = new TaskCompletionSource();

        var apply = harness.Coordinator.ApplyAsync(new ChangeRequest([Ewa], [new UnlockAccountOperation()], "Unlock"), TestContext.Current.CancellationToken);
        await ObjectListViewModelTests.WaitUntilAsync(() => harness.Activities.Activities.Count == 1);
        harness.Activities.Activities[0].CancelCommand.Execute(null);

        Assert.Equal(ChangeOutcome.Cancelled, await apply);
        Assert.Empty(harness.Dialogs.Errors);
    }

    [Fact]
    public async Task Coordinator_AStopThatLeftNothingUndone_IsAnExecutedPlan_NotACancelledOne()
    {
        // Stop finishes the object in progress: a one-object plan whose Stop comes while it runs is carried through. The
        // caller's follow-up (the re-read after an unlock, "renamed") depends on ChangeOutcome.Executed.
        var harness = await TestHarness.ConnectedAsync();
        harness.Executor.OnExecute = _ => harness.Activities.Activities[0].CancelCommand.Execute(null);

        var outcome = await harness.Coordinator.ApplyAsync(new ChangeRequest([Ewa], [new UnlockAccountOperation()], "Unlock"), TestContext.Current.CancellationToken);

        Assert.Equal(ChangeOutcome.Executed, outcome);
        Assert.Empty(harness.Dialogs.Errors);
        var toast = Assert.Single(harness.Notifications.Items);
        Assert.Equal(NotificationSeverity.Success, toast.Severity);
    }

    [Fact]
    public async Task ApplyAsync_SessionReplacedDuringPlanning_WritesNothing()
    {
        // Planning is not an activity, so "Rozłącz" does not wait for it and the (modeless) connect window lets the operator
        // reconnect - here as another account. The plan of the old session must never run with the new identity.
        var harness = await TestHarness.ConnectedAsync();
        var planning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Planner.BeforePlan = async _ =>
        {
            planning.TrySetResult();
            await release.Task;
        };
        var reader = new InMemoryDirectorySession(InMemoryDirectorySession.DefaultInfo with { BoundIdentity = @"CONTOSO\odczyt.test" });

        var apply = harness.Coordinator.ApplyAsync(new ChangeRequest([Lukasz], [new SetAccountEnabledOperation(false)], "Wyłącz konto"), TestContext.Current.CancellationToken);
        await planning.Task.WaitAsync(TestContext.Current.CancellationToken);
        await harness.Sessions.DisconnectAsync();
        harness.Connector.NextSession = reader;
        await harness.Sessions.ConnectAsync(new ConnectionOptions { Domain = "contoso.test" }, null, TestContext.Current.CancellationToken);
        release.SetResult();

        Assert.Equal(ChangeOutcome.Failed, await apply);
        Assert.Empty(harness.Executor.Plans);
        Assert.Empty(harness.Activities.Activities);
        var error = Assert.Single(harness.Dialogs.Errors);
        Assert.Equal(harness.Localizer[Presentation.Localization.L.WindowSession.Ended], error.Message);
    }

    // ---- A run whose window closed (the owner's decision D-A) -------------------------------------------------------

    [Fact]
    public async Task ARunThatLeftItsWindow_IsReportedByTheConnection_ANotificationWithTheDomain()
    {
        var harness = await TestHarness.ConnectedAsync();
        var (running, execution) = await StartFollowedAsync(harness, [Lukasz, Ewa]);

        Assert.True(execution.LeaveWindow());
        harness.Executor.Gate!.SetResult();
        var result = await running;

        // What the line would have said - with the domain - as a notification of the connection.
        Assert.Equal(2, result.SucceededCount);
        var toast = Assert.Single(harness.Notifications.Items);
        Assert.Equal(NotificationSeverity.Success, toast.Severity);
        Assert.Equal("“Disable accounts” was executed in contoso.test: 2 objects changed. The report is in the list of background operations.", toast.Message);
        Assert.Empty(harness.Dialogs.Errors);
    }

    [Fact]
    public async Task ARunThatLeftItsWindow_AndWasStopped_IsAWarning_ThatSaysWhatWasLeft()
    {
        var harness = await TestHarness.ConnectedAsync();
        var (running, execution) = await StartFollowedAsync(harness, [Lukasz, Ewa]);
        Assert.True(execution.LeaveWindow());

        // Stopped in the list of background operations before the first object: nothing was changed, nothing failed.
        harness.Activities.Activities[0].CancelCommand.Execute(null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        var toast = Assert.Single(harness.Notifications.Items);
        Assert.Equal(NotificationSeverity.Warning, toast.Severity);
        Assert.Equal("Executing “Disable accounts” in contoso.test was stopped.", toast.Message);
        Assert.Empty(harness.Dialogs.Errors);
    }

    [Fact]
    public async Task ARunThatLeftItsWindow_WithFailures_IsADialog_WithTheObjectsTheirReasonsAndTheSessionLog()
    {
        var harness = await TestHarness.ConnectedAsync(bulkUsers: 12);
        harness.Executor.StatusOf = obj => obj.DistinguishedName.Contains("OU=Masowe", StringComparison.OrdinalIgnoreCase)
            ? ObjectResultStatus.Failed
            : ObjectResultStatus.Succeeded;
        var bulk = Enumerable.Range(1, 12).Select(i => $"CN=Masowy {i:D4},OU=Masowe,DC=contoso,DC=test").ToList();
        var (running, execution) = await StartFollowedAsync(harness, [Lukasz, .. bulk]);
        Assert.True(execution.LeaveWindow());
        harness.Executor.Gate!.SetResult();
        await running;

        // The lead with the numbers, ten objects with their reasons, how many more, and where every change is.
        Assert.Empty(harness.Notifications.Items);
        var (title, message) = Assert.Single(harness.Dialogs.Errors);
        Assert.Equal("Disable accounts", title);
        var lines = message.Split('\n');
        Assert.Equal("“Disable accounts” was executed only in part in contoso.test: 1 object changed, failed: 12.", lines[0]);
        Assert.Equal(string.Empty, lines[1]);
        Assert.Equal(10, lines.Count(l => l.StartsWith("• Masowy ", StringComparison.Ordinal)));
        Assert.All(lines.Where(l => l.StartsWith("• ", StringComparison.Ordinal)), l => Assert.EndsWith(": " + harness.Errors.TranslateFailure(Core.Directory.DirectoryErrorKind.AccessDenied, "denied"), l, StringComparison.Ordinal));
        Assert.Contains("…and 2 more.", lines);
        Assert.Equal("Every change and its result are in the session log.", lines[^1]);
        Assert.Equal(string.Empty, lines[^2]);
    }

    [Fact]
    public async Task ARunThatLeftItsWindow_AndFailedAsAWhole_IsADialog_WithTheReason()
    {
        var harness = await TestHarness.ConnectedAsync();
        harness.Executor.Failure = new Core.Directory.DirectoryException(Core.Directory.DirectoryErrorKind.ServerUnavailable, "down");
        var (running, execution) = await StartFollowedAsync(harness, [Lukasz]);
        Assert.True(execution.LeaveWindow());
        harness.Executor.Gate!.SetResult();
        await Assert.ThrowsAsync<Core.Directory.DirectoryException>(() => running);

        var (title, message) = Assert.Single(harness.Dialogs.Errors);
        Assert.Equal("Disable accounts", title);
        Assert.StartsWith("Could not execute “Disable accounts” in contoso.test: ", message, StringComparison.Ordinal);
        Assert.Contains(harness.Errors.Translate(harness.Executor.Failure).TrimEnd('.'), message, StringComparison.Ordinal);
        Assert.Empty(harness.Notifications.Items);
    }

    [Fact]
    public async Task ARunThatStayedInItsWindow_IsNotReported()
    {
        var harness = await TestHarness.ConnectedAsync();
        var (running, execution) = await StartFollowedAsync(harness, [Lukasz]);
        harness.Executor.Gate!.SetResult();
        await running;

        // The window says it - in its line.
        Assert.Empty(harness.Notifications.Items);
        Assert.Empty(harness.Dialogs.Errors);
        Assert.True(execution.CanLeave is false);
        execution.Dispose();
    }

    [Fact]
    public async Task ARunWhoseWindowClosesAfterIt_IsReportedByTheConnection_OnlyOnceTheWindowClosed()
    {
        var harness = await TestHarness.ConnectedAsync();
        var (running, execution) = await StartFollowedAsync(harness, [Lukasz]);

        // Stop and close: the window closes right after the run, its line with it.
        execution.ReportByConnection();
        harness.Executor.Gate!.SetResult();
        var result = await running;
        execution.Complete(result);
        execution.Dispose();

        // Not while the window is still there - a dialog of the connection would be owned by the window that closes.
        Assert.False(execution.LeftWindow);
        Assert.Empty(harness.Notifications.Items);

        // The window closed (its view model is disposed): now the connection says it, once.
        execution.WindowClosed();
        execution.WindowClosed();
        var toast = Assert.Single(harness.Notifications.Items);
        Assert.Equal("“Disable accounts” was executed in contoso.test: 1 object changed. The report is in the list of background operations.", toast.Message);
    }

    [Fact]
    public async Task ARunWhoseWindowStayedAfterAll_IsNotReported()
    {
        var harness = await TestHarness.ConnectedAsync();
        var (running, execution) = await StartFollowedAsync(harness, [Lukasz]);
        execution.ReportByConnection();
        harness.Executor.Gate!.SetResult();
        execution.Complete(await running);
        execution.Dispose();

        // The window's own question kept it (generated passwords nobody took): its line says the result - the connection does not.
        execution.WindowStays();
        Assert.False(execution.ReportsByConnection);
        execution.WindowClosed();
        Assert.Empty(harness.Notifications.Items);
        Assert.Empty(harness.Dialogs.Errors);
    }

    [Fact]
    public async Task ARunWhoseWindowSaidTheOutcomeInItsOwnNotification_IsNotReportedAgain()
    {
        var harness = await TestHarness.ConnectedAsync();
        var (running, execution) = await StartFollowedAsync(harness, [Lukasz]);
        harness.Executor.Gate!.SetResult();
        var result = await running;

        // The Recycle Bin says a restore without failures in a toast of the main window, then reads its list again - and is
        // closed during that read: its toast was the report.
        execution.WindowNotifies = true;
        execution.ReportByConnection();
        execution.Complete(result);
        execution.Dispose();
        execution.WindowClosed();
        Assert.Empty(harness.Notifications.Items);
    }

    [Fact]
    public async Task ARunWhoseWindowClosedLater_WithoutAQuestionAboutIt_IsNotReported()
    {
        // The result came back, the line said it, the window's code ended - and only then the operator closed the window: they
        // saw how it ended. Nothing was kept for the connection.
        var harness = await TestHarness.ConnectedAsync();
        var (running, execution) = await StartFollowedAsync(harness, [Lukasz]);
        harness.Executor.Gate!.SetResult();
        execution.Complete(await running);
        execution.Dispose();

        execution.ReportByConnection();
        execution.WindowClosed();
        Assert.Empty(harness.Notifications.Items);
        Assert.Null(harness.Activities.Activities[0].TakeReport());
    }

    [Fact]
    public async Task TheReportOfARunWhoseWindowCloses_IsAskedForAfterTheWindowClosed_OnTheRealUiThread()
    {
        // One UI thread, one queue - like WPF. Stop and close of a run that fails: the window closes after the end, and only then
        // the connection asks for its failure dialog. The real dialog service owns a dialog by the active window of the connection;
        // asked for while the closing window was still there, the dialog would be owned by it and close with it.
        var harness = await TestHarness.ConnectedAsync();
        using var ui = new TestUiThread();
        var activities = new ActivityCenterViewModel(ui, harness.Localizer, harness.Errors, harness.Time, harness.Sessions);
        var coordinator = new ChangeCoordinator(
            harness.Sessions, harness.Planner, harness.Executor, harness.Dialogs, harness.Notifications, harness.Settings, activities, harness.Changes,
            harness.Errors, harness.Localizer, ui);
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        harness.Executor.OnExecute = _ =>
        {
            started.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        };
        harness.Executor.StatusOf = _ => ObjectResultStatus.Failed;
        var plan = RecordingChangePlanner.DefaultPlan(new BatchRequest([Lukasz], [new SetAccountEnabledOperation(false)]) { Title = "Disable accounts" });
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        harness.Dialogs.OnError = (_, _) => events.Enqueue("error dialog");
        PageStatusHolder holder = null!;
        Task window = null!;
        await ui.RunAsync(() =>
        {
            holder = new PageStatusHolder(harness.Localizer, ui);
            window = WindowCodeAsync();
            return Task.CompletedTask;
        });
        Assert.True(started.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));

        harness.Dialogs.CloseAnswers.Enqueue(CloseAnswer.StopAndClose);
        var closing = ui.RunAsync(async () =>
        {
            // What WindowViewModelBase and its host do: ask, close the window, then dispose the view model - which tells the runs.
            var reportOnClose = new List<IWindowRun>();
            var closes = await RunningWork.ConfirmCloseAsync(harness.Localizer, harness.Dialogs, "Window", [holder], reportOnClose: reportOnClose);
            events.Enqueue("window closed");
            foreach (var run in reportOnClose)
            {
                run.WindowClosed();
            }

            return closes;
        });
        await ui.IdleAsync();
        Assert.Single(harness.Dialogs.CloseQuestions);
        release.Set();

        Assert.True(await closing.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
        await window.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await ui.IdleAsync(10);
        Assert.Empty(ui.Errors);
        Assert.Equal(["window closed", "error dialog"], events);

        async Task WindowCodeAsync()
        {
            using var execution = ExecutionStatus.Begin(holder, harness.Localizer, "Disable accounts", "contoso.test", 1);
            var result = await coordinator.ExecutePlanAsync(plan, "Disable accounts", harness.Sessions.Current!, execution.Follow);
            if (!execution.LeftWindow)
            {
                execution.Complete(result);
            }
        }
    }

    [Fact]
    public async Task AChangeThatFailsAsAWhole_AfterItsConnectionWindowStoppedIt_IsNotReported()
    {
        // The main window's Stop and close stops a change of a simple action, and the execution then fails as a whole: nothing is
        // shown over a window that goes - not even the failure.
        var harness = await TestHarness.ConnectedAsync();
        harness.Executor.Failure = new Core.Directory.DirectoryException(Core.Directory.DirectoryErrorKind.ServerUnavailable, "down");
        harness.Executor.OnExecute = _ => harness.Activities.Activities[0].StopBecauseTheConnectionCloses();

        var outcome = await harness.Coordinator.ApplyAsync(new ChangeRequest([Ewa], [new UnlockAccountOperation()], "Unlock"), TestContext.Current.CancellationToken);

        Assert.Equal(ChangeOutcome.Failed, outcome);
        Assert.Empty(harness.Dialogs.Errors);
        Assert.Empty(harness.Notifications.Items);
    }

    [Fact]
    public async Task AChangeThatFailsAsAWhole_IsStillReportedByItsCaller()
    {
        // The control of the test above: without the connection window's stop, ApplyAsync says the failure.
        var harness = await TestHarness.ConnectedAsync();
        harness.Executor.Failure = new Core.Directory.DirectoryException(Core.Directory.DirectoryErrorKind.ServerUnavailable, "down");

        var outcome = await harness.Coordinator.ApplyAsync(new ChangeRequest([Ewa], [new UnlockAccountOperation()], "Unlock"), TestContext.Current.CancellationToken);

        Assert.Equal(ChangeOutcome.Failed, outcome);
        Assert.Single(harness.Dialogs.Errors);
    }

    [Fact]
    public async Task TheReportNeverHoldsTheExecution()
    {
        // The real dialog service shows a dialog inline when it is asked on the UI thread (a nested message loop): a report made
        // in the coordinator's continuation would hold the execution - and the window that waits for it - until the operator
        // answered. The coordinator posts it to a later turn of the UI thread instead.
        var harness = await TestHarness.ConnectedAsync();
        var later = new LaterUiDispatcher();
        using var dialogs = new InlineErrorDialogs(harness.Dialogs);
        var coordinator = new ChangeCoordinator(
            harness.Sessions, harness.Planner, harness.Executor, dialogs, harness.Notifications, harness.Settings, harness.Activities, harness.Changes,
            harness.Errors, harness.Localizer, later);
        harness.Executor.StatusOf = _ => ObjectResultStatus.Failed;
        harness.Executor.Gate = new TaskCompletionSource();
        var status = new PageStatusHolder(harness.Localizer, harness.Dispatcher);
        var execution = ExecutionStatus.Begin(status, harness.Localizer, "Disable accounts", "contoso.test", 1);
        var plan = RecordingChangePlanner.DefaultPlan(new BatchRequest([Lukasz], [new SetAccountEnabledOperation(false)]) { Title = "Disable accounts" });
        var running = coordinator.ExecutePlanAsync(plan, "Disable accounts", harness.Sessions.Current!, execution.Follow, TestContext.Current.CancellationToken);
        Assert.True(execution.LeaveWindow());
        harness.Executor.Gate.SetResult();

        // The execution ends - nothing was shown yet; the report waits for its turn.
        await running.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(1, later.Pending);
        Assert.False(dialogs.IsShowing);

        // Its turn: the dialog is open (and blocks that turn, as the real one does) - the execution had ended before.
        var turn = Task.Run(later.RunPending, TestContext.Current.CancellationToken);
        await dialogs.Shown.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True(running.IsCompletedSuccessfully);
        dialogs.Release();
        await turn.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.StartsWith("Could not execute “Disable accounts” in contoso.test: nothing was changed, failed: 1.", Assert.Single(harness.Dialogs.Errors).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChangeStoppedBecauseItsConnectionWindowCloses_IsNotReported()
    {
        var harness = await TestHarness.ConnectedAsync();
        harness.Executor.StatusOf = _ => ObjectResultStatus.Failed;
        harness.Executor.OnExecute = _ => harness.Activities.Activities[0].StopBecauseTheConnectionCloses();

        var outcome = await harness.Coordinator.ApplyAsync(new ChangeRequest([Ewa], [new UnlockAccountOperation()], "Unlock"), TestContext.Current.CancellationToken);

        // Nothing is shown over a window that goes: no failure dialog, no toast. The outcome is still the true one.
        Assert.Equal(ChangeOutcome.Failed, outcome);
        Assert.Empty(harness.Dialogs.Errors);
        Assert.Empty(harness.Notifications.Items);
    }

    /// <summary>A plan of <paramref name="dns"/> whose execution a window follows; the executor waits for the test's gate.</summary>
    private static async Task<(Task<BatchResult> Running, ExecutionStatus Execution)> StartFollowedAsync(TestHarness harness, IReadOnlyList<string> dns)
    {
        await Task.CompletedTask;
        harness.Executor.Gate ??= new TaskCompletionSource();
        var status = new PageStatusHolder(harness.Localizer, harness.Dispatcher);
        var execution = ExecutionStatus.Begin(status, harness.Localizer, "Disable accounts", "contoso.test", dns.Count);
        var plan = RecordingChangePlanner.DefaultPlan(new BatchRequest(dns, [new SetAccountEnabledOperation(false)]) { Title = "Disable accounts" });
        var running = harness.Coordinator.ExecutePlanAsync(plan, "Disable accounts", harness.Sessions.Current!, execution.Follow, TestContext.Current.CancellationToken);
        Assert.True(execution.CanLeave);
        return (running, execution);
    }

    /// <summary>The UI thread of the real program: work handed to it runs in a later turn, never inside the caller.</summary>
    private sealed class LaterUiDispatcher : IUiDispatcher
    {
        private readonly Lock _gate = new();
        private readonly List<Action> _pending = [];

        public int Pending
        {
            get
            {
                lock (_gate)
                {
                    return _pending.Count;
                }
            }
        }

        public bool CheckAccess() => false;

        public Task InvokeAsync(Action action)
        {
            lock (_gate)
            {
                _pending.Add(action);
            }

            return Task.CompletedTask;
        }

        public void RunPending()
        {
            List<Action> due;
            lock (_gate)
            {
                due = [.. _pending];
                _pending.Clear();
            }

            foreach (var action in due)
            {
                action();
            }
        }
    }

    /// <summary>An error dialog shown the way the real one is on the UI thread: the call returns when the operator closed it.</summary>
    private sealed class InlineErrorDialogs(ScriptedDialogService inner) : IDialogService, IDisposable
    {
        private readonly ManualResetEventSlim _closed = new();
        private readonly TaskCompletionSource _shown = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Shown => _shown.Task;

        public bool IsShowing => _shown.Task.IsCompleted && !_closed.IsSet;

        public void Release() => _closed.Set();

        public void Dispose() => _closed.Dispose();

        public Task ShowErrorAsync(string title, string message)
        {
            _ = inner.ShowErrorAsync(title, message);
            _shown.TrySetResult();
            _closed.Wait(TimeSpan.FromSeconds(30));
            return Task.CompletedTask;
        }

        public Task ShowInfoAsync(string title, string message) => inner.ShowInfoAsync(title, message);

        public Task<bool> ConfirmAsync(string title, string message, bool destructive) => inner.ConfirmAsync(title, message, destructive);

        public Task<bool> ConfirmTypedAsync(string title, string message, string expectedText) => inner.ConfirmTypedAsync(title, message, expectedText);

        public Task<CloseAnswer> AskCloseAsync(string title, string message, bool canContinue) => inner.AskCloseAsync(title, message, canContinue);

        public Task<string?> PickContainerAsync(Core.Directory.IDirectorySession session, string? startDn) => inner.PickContainerAsync(session, startDn);

        public Task<IReadOnlyList<string>> PickObjectsAsync(Core.Directory.IDirectorySession session, IReadOnlyList<ObjectKind> kinds, bool multiSelect) =>
            inner.PickObjectsAsync(session, kinds, multiSelect);

        public Task ShowPropertiesAsync(string distinguishedName) => inner.ShowPropertiesAsync(distinguishedName);

        public Task ShowBatchEditorAsync(BatchEditorRequest request) => inner.ShowBatchEditorAsync(request);

        public Task ShowResetPasswordAsync(IReadOnlyList<string> distinguishedNames) => inner.ShowResetPasswordAsync(distinguishedNames);

        public Task<string?> ShowNewObjectAsync(ObjectKind kind, string parentDn) => inner.ShowNewObjectAsync(kind, parentDn);

        public Task ShowFindAsync(string? startDn) => inner.ShowFindAsync(startDn);

        public Task ShowSessionLogAsync() => inner.ShowSessionLogAsync();

        public Task ShowPowerShellAsync() => inner.ShowPowerShellAsync();

        public Task ShowPowerShellGridAsync(Core.PowerShell.PowerShellGridTable table) => inner.ShowPowerShellGridAsync(table);

        public Task ShowProblemReportAsync() => inner.ShowProblemReportAsync();

        public Task ShowRecycleBinAsync(Guid? select = null) => inner.ShowRecycleBinAsync(select);

        public Task ShowAccountReviewAsync() => inner.ShowAccountReviewAsync();

        public Task ShowSettingsAsync() => inner.ShowSettingsAsync();

        public Task ShowConnectAsync() => inner.ShowConnectAsync();

        public Task<bool> ShowChangeDomainControllerAsync() => inner.ShowChangeDomainControllerAsync();

        public Task<bool> ShowSavedViewEditorAsync(Presentation.Views.SavedViewEditorViewModel editor) => inner.ShowSavedViewEditorAsync(editor);

        public Task<bool> ShowFilterEditorAsync(Presentation.Filters.FilterEditorViewModel editor) => inner.ShowFilterEditorAsync(editor);

        public Task ShowSavedFiltersAsync() => inner.ShowSavedFiltersAsync();

        public Task<bool> ShowColumnChooserAsync(Presentation.Lists.ColumnChooserViewModel chooser) => inner.ShowColumnChooserAsync(chooser);

        public Task<bool> ShowLogonHoursAsync(Presentation.Dialogs.LogonHoursViewModel editor) => inner.ShowLogonHoursAsync(editor);

        public Task<bool> ShowWatchdogCertificateTrustAsync(Presentation.Watchdog.WatchdogCertificateTrustViewModel dialog) => inner.ShowWatchdogCertificateTrustAsync(dialog);
    }

    [Fact]
    public async Task Activity_CompletesWithResult()
    {
        var harness = await TestHarness.ConnectedAsync();
        var plan = RecordingChangePlanner.DefaultPlan(new BatchRequest([Lukasz, Ewa], [new UnlockAccountOperation()]));

        var result = await harness.Coordinator.ExecutePlanAsync(plan, "Unlock", TestContext.Current.CancellationToken);
        var activity = Assert.Single(harness.Activities.Activities);

        await ObjectListViewModelTests.WaitUntilAsync(() => !activity.IsRunning);
        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(1, activity.Fraction);
        Assert.Equal("Completed (2 objects)", activity.StatusText);
    }
}
