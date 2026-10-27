using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Services;
using AdMng.Presentation.Tests.Lists;
using AdMng.Presentation.Tests.TestDoubles;
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
