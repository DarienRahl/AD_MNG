using AdMng.Core.ActiveDirectory;
using AdMng.Core.Audit;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Security;
using AdMng.Core.Templates;
using AdMng.Testing;

namespace AdMng.Core.Tests.Batch;

/// <summary>
/// Stop finishes the object in progress (V2b-5b): a cancellation starts no new object, but an object whose first change
/// has succeeded gets all its changes - an account is never left with its new password and without its other settings.
/// An object whose first change has not begun is cancelled, a failed change still ends its object, objects that run in
/// parallel each finish, and the audit and the undo plan have every change that was made.
/// </summary>
public class BatchExecutorStopTests
{
    private static readonly BatchOptions Fast = new() { InitialRetryDelay = TimeSpan.FromMilliseconds(1), MaxDegreeOfParallelism = 1 };

    private readonly ManualTimeProvider _time = new();
    private readonly FakeDirectoryBuilder _builder;
    private readonly SampleCompany _company;
    private readonly InMemoryAuditLog _audit = new();

    public BatchExecutorStopTests()
    {
        _builder = new FakeDirectoryBuilder(timeProvider: _time);
        _company = _builder.AddSampleCompany();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakeDirectorySession Session => _builder.Session;

    private BatchExecutor Executor => new(_audit, _time);

    /// <summary>Each target gets three changes in plan order: Modify (title), AddMember (sales group), Move (sales unit).</summary>
    private async Task<ChangePlan> ThreeChangesEachAsync(params string[] targets)
    {
        var plan = await new ChangePlanner(new TemplateEngine(), new PasswordGenerator(), _time).PlanAsync(
            Session,
            new BatchRequest(targets, [new SetAttributeOperation(Attr.Title, "Po zatrzymaniu"), new AddToGroupsOperation([_company.GroupSales]), new MoveOperation(_company.SalesOu)])
            {
                Title = "Zmiany",
            },
            null,
            null,
            Ct);
        Assert.All(plan.Objects, o => Assert.Equal([ChangeAction.Modify, ChangeAction.AddMember, ChangeAction.Move], o.Changes.Select(c => c.Action)));
        Session.ClearOperations();
        return plan;
    }

    [Fact]
    public async Task StopAfterTheFirstChangeOfTheSecondObject_FinishesThatObject_AndCancelsTheThird()
    {
        var plan = await ThreeChangesEachAsync(_company.JanKowalski, _company.LukaszWisniewski, _company.MalgorzataWojcik);
        using var stop = new CancellationTokenSource();

        // Right after the audit of the first change of the second object - before its second change is looked at.
        _audit.EntryRecorded += (_, entry) =>
        {
            if (entry.Action == "Modify" && entry.DistinguishedName == _company.LukaszWisniewski)
            {
                stop.Cancel();
            }
        };

        var result = await Executor.ExecuteAsync(Session, plan, Fast, null, stop.Token);

        Assert.True(result.WasCancelled);
        Assert.Equal([ObjectResultStatus.Succeeded, ObjectResultStatus.Succeeded, ObjectResultStatus.Cancelled], result.Objects.Select(o => o.Status));
        var lukasz = result.Objects[1];
        Assert.Equal([ChangeAction.Modify, ChangeAction.AddMember, ChangeAction.Move], lukasz.Changes.Select(c => c.Change.Action));
        Assert.All(lukasz.Changes, c => Assert.True(c.Succeeded));
        Assert.Equal("CN=Łukasz Wiśniewski," + _company.SalesOu, lukasz.FinalDn);
        Assert.Equal(1, result.FinishedAfterStopCount);

        // On the directory: the second object is complete, the third was not touched.
        Assert.Equal(["Po zatrzymaniu"], Session.GetValues(lukasz.FinalDn, Attr.Title));
        Assert.Contains(lukasz.FinalDn, Session.GetValues(_company.GroupSales, Attr.Member));
        Assert.Empty(result.Objects[2].Changes);
        Assert.NotEqual(["Po zatrzymaniu"], Session.GetValues(_company.MalgorzataWojcik, Attr.Title));
        Assert.True(Session.Exists(_company.MalgorzataWojcik));
        Assert.DoesNotContain(Session.Writes, w => w.DistinguishedName == _company.MalgorzataWojcik);
    }

    [Fact]
    public async Task EveryChangeThatWasMade_IsAudited_AfterAStop()
    {
        var plan = await ThreeChangesEachAsync(_company.JanKowalski, _company.LukaszWisniewski, _company.MalgorzataWojcik);
        using var stop = new CancellationTokenSource();
        _audit.EntryRecorded += (_, entry) =>
        {
            if (entry.Action == "Modify" && entry.DistinguishedName == _company.LukaszWisniewski)
            {
                stop.Cancel();
            }
        };

        await Executor.ExecuteAsync(Session, plan, Fast, null, stop.Token);

        var lukasz = _audit.Entries.Where(e => e.Succeeded && (e.DistinguishedName == _company.LukaszWisniewski
            || e.NewValue == "CN=Łukasz Wiśniewski," + _company.SalesOu || e.OldValue == _company.LukaszWisniewski)).ToList();
        Assert.Equal(["Modify", "AddMember", "Move"], lukasz.Select(e => e.Action));
        Assert.Equal(["AddMember", "AddMember", "Modify", "Modify", "Move", "Move"], _audit.Entries.Select(e => e.Action).Order());
        Assert.DoesNotContain(_audit.Entries, e => !e.Succeeded);
        Assert.DoesNotContain(_audit.Entries, e => e.DistinguishedName == _company.MalgorzataWojcik);
    }

    [Fact]
    public async Task TheUndoPlanOfAStoppedExecution_HasEveryChangeThatWasMade()
    {
        var plan = await ThreeChangesEachAsync(_company.JanKowalski, _company.LukaszWisniewski, _company.MalgorzataWojcik);
        using var stop = new CancellationTokenSource();
        _audit.EntryRecorded += (_, entry) =>
        {
            if (entry.Action == "Modify" && entry.DistinguishedName == _company.LukaszWisniewski)
            {
                stop.Cancel();
            }
        };
        var result = await Executor.ExecuteAsync(Session, plan, Fast, null, stop.Token);

        var rollback = new RollbackPlanner().CreateRollbackPlan(result);

        // The two objects that were changed, each with all three changes undone; the cancelled one is not in it.
        Assert.Equal(["Jan Kowalski", "Łukasz Wiśniewski"], rollback.Objects.Select(o => o.DisplayName).Order());
        ChangeAction[] undoActions = [ChangeAction.Modify, ChangeAction.RemoveMember, ChangeAction.Move];
        Assert.All(rollback.Objects, o => Assert.Equal(undoActions.Order(), o.Changes.Select(c => c.Action).Order()));

        // And it works: the second object is as it was before.
        var undone = await Executor.ExecuteAsync(Session, rollback, Fast, null, Ct);

        Assert.Equal(2, undone.SucceededCount);
        Assert.Equal(["Programista"], Session.GetValues(_company.LukaszWisniewski, Attr.Title));
        Assert.DoesNotContain(_company.LukaszWisniewski, Session.GetValues(_company.GroupSales, Attr.Member));
        Assert.False(Session.Exists("CN=Łukasz Wiśniewski," + _company.SalesOu));
    }

    [Fact]
    public async Task ObjectsThatRunInParallel_EachFinish_WhenTheStopComesWhileThreeRun()
    {
        var plan = await ThreeChangesEachAsync(
            _company.JanKowalski, _company.LukaszWisniewski, _company.MalgorzataWojcik, _company.GrazynaDabrowska, _company.ZbigniewKazmierczak);
        using var stop = new CancellationTokenSource();
        var arrived = 0;
        var allThree = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The first change of each of the three objects that run waits until all three have arrived; the Stop comes then.
        Session.BeforeOperationAsync = async operation =>
        {
            if (operation.Kind != FakeOperationKind.Modify)
            {
                return;
            }

            var number = Interlocked.Increment(ref arrived);
            if (number <= 3)
            {
                if (number == 3)
                {
                    stop.Cancel();
                    allThree.SetResult();
                }

                await allThree.Task;
            }
        };

        var result = await Executor.ExecuteAsync(Session, plan, Fast with { MaxDegreeOfParallelism = 3 }, null, stop.Token);

        Assert.True(result.WasCancelled);
        Assert.Equal(
            [ObjectResultStatus.Succeeded, ObjectResultStatus.Succeeded, ObjectResultStatus.Succeeded, ObjectResultStatus.Cancelled, ObjectResultStatus.Cancelled],
            result.Objects.Select(o => o.Status));
        Assert.All(result.Objects.Take(3), o => Assert.Equal(3, o.Changes.Count(c => c.Succeeded)));
        Assert.Equal(3, result.FinishedAfterStopCount);
        Assert.All(result.Objects.Skip(3), o => Assert.Empty(o.Changes));
        Assert.DoesNotContain(Session.Writes, w => w.DistinguishedName == _company.GrazynaDabrowska || w.DistinguishedName == _company.ZbigniewKazmierczak);
        Assert.True(Session.Exists(_company.GrazynaDabrowska));
        Assert.True(Session.Exists(_company.ZbigniewKazmierczak));
    }

    [Fact]
    public async Task AFailedFirstChange_EndsTheObject_AndTheStopDoesNotContinueIt()
    {
        var plan = await ThreeChangesEachAsync(_company.JanKowalski, _company.LukaszWisniewski, _company.MalgorzataWojcik);
        using var stop = new CancellationTokenSource();
        Session.FailNext(FakeOperationKind.Modify, _company.LukaszWisniewski, DirectoryErrorKind.AccessDenied);
        Session.BeforeOperation = operation =>
        {
            if (operation.Kind == FakeOperationKind.Modify && operation.DistinguishedName == _company.LukaszWisniewski)
            {
                stop.Cancel();
            }
        };

        var result = await Executor.ExecuteAsync(Session, plan, Fast, null, stop.Token);

        Assert.Equal([ObjectResultStatus.Succeeded, ObjectResultStatus.Failed, ObjectResultStatus.Cancelled], result.Objects.Select(o => o.Status));
        var failed = Assert.Single(result.Objects[1].Changes);
        Assert.Equal(DirectoryErrorKind.AccessDenied, failed.ErrorKind);
        Assert.Equal(0, result.FinishedAfterStopCount);
        Assert.True(Session.Exists(_company.LukaszWisniewski));
        Assert.DoesNotContain(_company.LukaszWisniewski, Session.GetValues(_company.GroupSales, Attr.Member));
    }

    [Fact]
    public async Task AFailureAfterTheStop_StillEndsTheObject_AsPartiallySucceeded()
    {
        var plan = await ThreeChangesEachAsync(_company.JanKowalski, _company.LukaszWisniewski);
        using var stop = new CancellationTokenSource();
        Session.FailNext(FakeOperationKind.Modify, _company.GroupSales, DirectoryErrorKind.AccessDenied);
        _audit.EntryRecorded += (_, entry) =>
        {
            if (entry.Action == "Modify" && entry.DistinguishedName == _company.JanKowalski)
            {
                stop.Cancel();
            }
        };

        var result = await Executor.ExecuteAsync(Session, plan, Fast, null, stop.Token);

        // The stop does not change what a failure means: the object is carried on until a change fails, and then it ends.
        var jan = result.Objects[0];
        Assert.Equal(ObjectResultStatus.PartiallySucceeded, jan.Status);
        Assert.Equal([true, false], jan.Changes.Select(c => c.Succeeded));
        Assert.Equal(ObjectResultStatus.Cancelled, result.Objects[1].Status);
        Assert.Equal(0, result.FinishedAfterStopCount);
    }

    [Fact]
    public async Task AStopBetweenTwoObjects_FinishesNothing_AndCancelsTheNext()
    {
        var plan = await ThreeChangesEachAsync(_company.JanKowalski, _company.LukaszWisniewski);
        using var stop = new CancellationTokenSource();
        var progress = new CancelAfterFirstObject(stop);

        var result = await Executor.ExecuteAsync(Session, plan, Fast, progress, stop.Token);

        Assert.Equal([ObjectResultStatus.Succeeded, ObjectResultStatus.Cancelled], result.Objects.Select(o => o.Status));
        Assert.True(result.WasCancelled);
        Assert.Equal(0, result.FinishedAfterStopCount);
    }

    [Fact]
    public async Task ARunThatWasNotStopped_FinishesNothingAfterAStop()
    {
        var plan = await ThreeChangesEachAsync(_company.JanKowalski, _company.LukaszWisniewski);

        var result = await Executor.ExecuteAsync(Session, plan, Fast, null, Ct);

        Assert.False(result.WasCancelled);
        Assert.Equal(0, result.FinishedAfterStopCount);
        Assert.All(result.Objects, o => Assert.Equal(ObjectResultStatus.Succeeded, o.Status));
    }

    [Fact]
    public async Task AStopWhileARequestIsBackingOff_DoesNotInterruptTheRetries_SoTheObjectIsNotLeftHalfDone()
    {
        var plan = await ThreeChangesEachAsync(_company.JanKowalski, _company.LukaszWisniewski);
        using var stop = new CancellationTokenSource();

        // The second change (AddMember) of the first object fails twice with a busy server; the Stop comes meanwhile.
        Session.FailNext(FakeOperationKind.Modify, _company.GroupSales, DirectoryErrorKind.Busy, times: 2);
        var seen = 0;
        Session.BeforeOperation = operation =>
        {
            if (operation.Kind == FakeOperationKind.Modify && operation.DistinguishedName == _company.GroupSales && Interlocked.Increment(ref seen) == 1)
            {
                stop.Cancel();
            }
        };

        var result = await Executor.ExecuteAsync(Session, plan, Fast, null, stop.Token);

        var jan = result.Objects[0];
        Assert.Equal(ObjectResultStatus.Succeeded, jan.Status);
        Assert.Equal(3, jan.Changes.Count);
        Assert.Equal(3, jan.Changes[1].Attempts);
        Assert.Equal(1, result.FinishedAfterStopCount);
        Assert.Equal(ObjectResultStatus.Cancelled, result.Objects[1].Status);
    }

    private sealed class CancelAfterFirstObject(CancellationTokenSource stop) : IProgress<BatchProgress>
    {
        public void Report(BatchProgress value)
        {
            if (value.Completed == 1)
            {
                stop.Cancel();
            }
        }
    }
}
