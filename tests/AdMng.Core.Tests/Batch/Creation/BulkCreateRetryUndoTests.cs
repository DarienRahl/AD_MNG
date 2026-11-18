using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Batch.Creation;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Testing;

namespace AdMng.Core.Tests.Batch.Creation;

/// <summary>
/// Retry, merged results, the created objects and the undo (docs/dev/phase3-bulk-create.md 2.5): the retry leaves out what
/// succeeded in any execution and gives a password that was never set a new one; the undo re-reads every object by its GUID
/// and never touches another object.
/// </summary>
public sealed class BulkCreateRetryUndoTests
{
    private readonly BulkCreateTestBed _bed = new();

    private static readonly (string, string)[] ThreePeople = [("Łucja", "Żak"), ("Zofia", "Bąk"), ("Ewa", "Nowak")];

    private async Task<(BulkCreateComputation Computation, ChangePlan Plan)> PlanAsync(BulkAccountOptions? options = null, string? shared = null)
    {
        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(ThreePeople), options: options));
        return (computation, _bed.Planner.BuildPlan(computation, shared, "Utworzenie kont"));
    }

    private static ObjectResult ResultOf(BatchResult result, int row) => result.Objects.Single(o => o.Plan.SourceRow == row);

    // ------------------------------------------------------------------ retry

    [Fact]
    public async Task Retry_LeavesOutTheSucceededSteps_AndGeneratesANewPassword()
    {
        var (computation, plan) = await PlanAsync(new BulkAccountOptions { MatchAttributes = [], GroupDns = [_bed.Sample.GroupNew] });
        var first = plan.Objects[0];
        _bed.Session.FailNext(FakeOperationKind.ResetPassword, first.DistinguishedName, DirectoryErrorKind.PasswordPolicy);
        var accumulated = BulkCreateResults.Merge(null, await _bed.ExecuteAsync(plan));

        var retry = _bed.Planner.BuildRetryPlan(computation, accumulated, null, "Ponowienie");

        var again = Assert.Single(retry.Objects);
        Assert.Equal((1, first.DistinguishedName, "Ponowienie"), (again.SourceRow!.Value, again.DistinguishedName, retry.Title));
        Assert.Equal([ChangeAction.ResetPassword, ChangeAction.Modify, ChangeAction.AddMember], again.Changes.Select(c => c.Action));
        Assert.NotEqual(first.Changes[1].Password, again.Changes[0].Password);
        Assert.Equal(16, again.Changes[0].Password!.Length);
        Assert.Null(again.PowerShell);
        Assert.Equal(1, retry.ExecutableCount);
    }

    [Fact]
    public async Task Retry_SharedPassword_TheNewOneOnlyAfterAPolicyFailure()
    {
        var (computation, plan) = await PlanAsync(new BulkAccountOptions { MatchAttributes = [], PasswordMode = InitialPasswordMode.SameForAll }, "Zima#2026!abc");
        _bed.Session.FailNext(FakeOperationKind.ResetPassword, plan.Objects[0].DistinguishedName, DirectoryErrorKind.PasswordPolicy);
        _bed.Session.FailNext(FakeOperationKind.ResetPassword, plan.Objects[1].DistinguishedName, DirectoryErrorKind.AccessDenied);
        var accumulated = BulkCreateResults.Merge(null, await _bed.ExecuteAsync(plan));

        var retry = _bed.Planner.BuildRetryPlan(computation, accumulated, "Wiosna#2027!xyz", "t");

        Assert.Equal(["Wiosna#2027!xyz", "Zima#2026!abc"], retry.Objects.Select(o => o.Changes.Single(c => c.Action == ChangeAction.ResetPassword).Password));
    }

    [Fact]
    public async Task Retry_NeverRepeatsASucceededReset()
    {
        var (computation, plan) = await PlanAsync();
        _bed.Session.FailNext(FakeOperationKind.Modify, plan.Objects[2].DistinguishedName, DirectoryErrorKind.AccessDenied);
        var accumulated = BulkCreateResults.Merge(null, await _bed.ExecuteAsync(plan));
        Assert.Equal(ObjectResultStatus.PartiallySucceeded, ResultOf(accumulated, 3).Status);

        var retry = _bed.Planner.BuildRetryPlan(computation, accumulated, null, "t");

        var again = Assert.Single(retry.Objects);
        Assert.Equal([ChangeAction.Modify], again.Changes.Select(c => c.Action));
        Assert.Equal([Attr.UserAccountControl, Attr.PwdLastSet], again.Changes[0].Attributes.Select(a => a.Attribute));
        Assert.False(retry.ContainsPasswords);
    }

    [Fact]
    public async Task Retry_ACreateThatFoundTheNameTaken_IsShownButNotExecuted()
    {
        var (computation, plan) = await PlanAsync();
        _bed.AddUser("Ktoś", "Szybszy", new Dictionary<string, object?> { [Attr.SamAccountName] = plan.Objects[1].Changes[0].Attributes.Single(a => a.Attribute == Attr.SamAccountName).NewValues[0] });
        var accumulated = BulkCreateResults.Merge(null, await _bed.ExecuteAsync(plan));
        Assert.Equal(ObjectResultStatus.Failed, ResultOf(accumulated, 2).Status);

        var retry = _bed.Planner.BuildRetryPlan(computation, accumulated, null, "t");

        var again = Assert.Single(retry.Objects);
        Assert.Contains(again.Issues, i => i.Code == BulkCreateIssueCodes.ValueTakenSinceCheck && i.Severity == IssueSeverity.Error);
        Assert.Equal(0, retry.ExecutableCount);
    }

    // ------------------------------------------------------------------ merged results

    private static ObjectPlan Object(int row) => new() { DistinguishedName = $"CN=Wiersz {row},OU=Nowi pracownicy,DC=contoso,DC=test", DisplayName = $"Wiersz {row}", SourceRow = row };

    private static ChangeResult Step(ChangeAction action, bool succeeded) => new(new PlannedChange { Action = action }, succeeded);

    private static BatchResult Execution(string title, DateTimeOffset at, params (int Row, ObjectResultStatus Status, ChangeResult[] Steps)[] objects)
    {
        var results = objects.Select(o => new ObjectResult(Object(o.Row), o.Status, o.Steps, Object(o.Row).DistinguishedName, at, at.AddSeconds(1))).ToList();
        return new BatchResult(new ChangePlan { Objects = [.. results.Select(r => r.Plan)], Title = title, Server = "dc01.contoso.test" }, results,
            "dc01.contoso.test", @"CONTOSO\Administrator", at, at.AddSeconds(2), WasCancelled: false);
    }

    [Fact]
    public void Merge_TheLatestResultPerRow_WithTheStepsOfAllExecutions()
    {
        var start = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        var first = Execution("Utworzenie kont", start,
            (1, ObjectResultStatus.PartiallySucceeded, [Step(ChangeAction.Create, true), Step(ChangeAction.ResetPassword, false)]),
            (2, ObjectResultStatus.Failed, [Step(ChangeAction.Create, false)]),
            (3, ObjectResultStatus.Succeeded, [Step(ChangeAction.Create, true)]),
            (4, ObjectResultStatus.Succeeded, [Step(ChangeAction.Create, true)]));
        var second = Execution("Ponowienie", start.AddMinutes(5),
            (1, ObjectResultStatus.Succeeded, [Step(ChangeAction.ResetPassword, true), Step(ChangeAction.Modify, true)]),
            (2, ObjectResultStatus.Failed, [Step(ChangeAction.Create, false)]),
            (5, ObjectResultStatus.Skipped, []));

        var merged = BulkCreateResults.Merge(BulkCreateResults.Merge(null, first), second);

        Assert.Equal([1, 2, 3, 4, 5], merged.Objects.Select(o => o.Plan.SourceRow!.Value));
        Assert.Equal(
            [ObjectResultStatus.Succeeded, ObjectResultStatus.Failed, ObjectResultStatus.Succeeded, ObjectResultStatus.Succeeded, ObjectResultStatus.Skipped],
            merged.Objects.Select(o => o.Status));
        Assert.Equal(4, merged.Objects[0].Changes.Count);
        Assert.Equal(2, merged.Objects[1].Changes.Count);
        Assert.Equal(("Utworzenie kont", start, start.AddMinutes(5).AddSeconds(2)), (merged.Plan.Title, merged.StartedAt, merged.FinishedAt));
        Assert.Equal(start, merged.Objects[0].StartedAt);
        Assert.Equal(5, merged.Plan.Objects.Count);
        Assert.Same(first, BulkCreateResults.Merge(null, first));
    }

    [Fact]
    public void Merge_AFailureAfterAnEarlierSuccess_IsPartial()
    {
        var start = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        var first = Execution("t", start, (1, ObjectResultStatus.PartiallySucceeded, [Step(ChangeAction.Create, true), Step(ChangeAction.ResetPassword, false)]));
        var second = Execution("t", start.AddMinutes(1), (1, ObjectResultStatus.Failed, [Step(ChangeAction.ResetPassword, false)]));

        Assert.Equal(ObjectResultStatus.PartiallySucceeded, BulkCreateResults.Merge(first, second).Objects.Single().Status);
    }

    // ------------------------------------------------------------------ created objects and undo

    [Fact]
    public async Task ReadCreated_GuidsAndCreationTimes_InMemory()
    {
        var (_, plan) = await PlanAsync();
        _bed.Session.FailNext(FakeOperationKind.Add, plan.Objects[1].DistinguishedName, DirectoryErrorKind.AccessDenied);
        var result = await _bed.ExecuteAsync(plan);

        var created = await _bed.Planner.ReadCreatedAsync(_bed.Session, result, BulkCreateTestBed.Ct);

        Assert.Equal([1, 3], created.Select(c => c.Row));
        Assert.All(created, c =>
        {
            Assert.Equal(_bed.Session.Snapshot(c.DistinguishedName)!.ObjectGuid, c.ObjectGuid);
            Assert.Equal(_bed.Time.GetUtcNow(), c.WhenCreated);
            Assert.True(c.MustChangeSet);
            Assert.Equal(ObjectKind.User, c.Kind);
        });
    }

    [Fact]
    public async Task Undo_DeletesWhatWasCreated_CheckedByGuid()
    {
        var (_, plan) = await PlanAsync();
        var result = await _bed.ExecuteAsync(plan);
        var created = await _bed.Planner.ReadCreatedAsync(_bed.Session, result, BulkCreateTestBed.Ct);

        var undo = await _bed.Planner.PlanUndoAsync(_bed.Session, created, "Cofnięcie", BulkCreateTestBed.Ct);

        Assert.Equal(("Cofnięcie", _bed.Session.Info.Server, 3), (undo.Title, undo.Server, undo.ExecutableCount));
        Assert.All(undo.Objects, o =>
        {
            Assert.Equal([ChangeAction.Delete], o.Changes.Select(c => c.Action));
            Assert.NotNull(o.ObjectGuid);
            Assert.Empty(o.Issues);
        });
        var executed = await _bed.ExecuteAsync(undo);
        Assert.Equal(3, executed.SucceededCount);
        Assert.All(plan.Objects, o => Assert.False(_bed.Session.Exists(o.DistinguishedName)));
    }

    [Fact]
    public async Task Undo_AnotherObjectAtTheName_IsLeftAlone()
    {
        var (_, plan) = await PlanAsync();
        var created = await _bed.Planner.ReadCreatedAsync(_bed.Session, await _bed.ExecuteAsync(plan), BulkCreateTestBed.Ct);
        var replaced = plan.Objects[0].DistinguishedName;
        await _bed.Session.DeleteAsync(replaced, subtree: false, BulkCreateTestBed.Ct);
        _bed.Builder.AddContact("Łucja", "Żak", _bed.Sample.NewEmployeesOu);

        var undo = await _bed.Planner.PlanUndoAsync(_bed.Session, created, "t", BulkCreateTestBed.Ct);

        var left = undo.Objects.Single(o => o.SourceRow == 1);
        Assert.Contains(left.Issues, i => i.Code == BulkCreateIssueCodes.UndoObjectReplaced && i.Severity == IssueSeverity.Error);
        Assert.Equal(2, undo.ExecutableCount);
        await _bed.ExecuteAsync(undo);
        Assert.True(_bed.Session.Exists(replaced));
    }

    [Fact]
    public async Task Undo_ARenamedObject_IsDeletedWhereItIsNow()
    {
        var (_, plan) = await PlanAsync();
        var created = await _bed.Planner.ReadCreatedAsync(_bed.Session, await _bed.ExecuteAsync(plan), BulkCreateTestBed.Ct);
        var moved = await _bed.Session.MoveAsync(plan.Objects[1].DistinguishedName, _bed.Company.SalesOu, "CN=Zofia Bąk-Nowa", BulkCreateTestBed.Ct);

        var undo = await _bed.Planner.PlanUndoAsync(_bed.Session, created, "t", BulkCreateTestBed.Ct);

        Assert.Equal(moved, undo.Objects.Single(o => o.SourceRow == 2).DistinguishedName);
        Assert.Equal(3, undo.ExecutableCount);
    }

    [Fact]
    public async Task Undo_UsedAccountsAndChildren_Warn()
    {
        var (_, plan) = await PlanAsync();
        var created = await _bed.Planner.ReadCreatedAsync(_bed.Session, await _bed.ExecuteAsync(plan), BulkCreateTestBed.Ct);
        _bed.Session.SetValues(plan.Objects[0].DistinguishedName, Attr.LogonCount, "3");
        _bed.Session.SetValues(plan.Objects[1].DistinguishedName, Attr.PwdLastSet, "134000000000000000");
        _bed.Builder.AddContainer("ExchangeActiveSyncDevices", plan.Objects[2].DistinguishedName);

        var undo = await _bed.Planner.PlanUndoAsync(_bed.Session, created, "t", BulkCreateTestBed.Ct);

        Assert.Contains(undo.Objects[0].Issues, i => i.Code == BulkCreateIssueCodes.UndoAccountInUse);
        Assert.Contains(undo.Objects[1].Issues, i => i.Code == BulkCreateIssueCodes.UndoAccountInUse);
        Assert.Contains(undo.Objects[2].Issues, i => i.Code == BulkCreateIssueCodes.UndoHasChildren);
        Assert.All(undo.Objects, o => Assert.DoesNotContain(o.Issues, i => i.Severity == IssueSeverity.Error));
    }

    [Fact]
    public async Task Undo_AnObjectWithoutGuid_IsNotDeleted()
    {
        var created = new[] { new CreatedObject(1, $"CN=Łucja Żak,{_bed.Sample.NewEmployeesOu}", ObjectKind.User, null, null, true) };

        var undo = await _bed.Planner.PlanUndoAsync(_bed.Session, created, "t", BulkCreateTestBed.Ct);

        Assert.Equal(0, undo.ExecutableCount);
        Assert.Contains(undo.Objects[0].Issues, i => i.Code == BulkCreateIssueCodes.UndoObjectReplaced);
    }
}
