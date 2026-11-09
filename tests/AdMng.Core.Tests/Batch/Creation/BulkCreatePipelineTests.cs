using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Batch.Creation;
using AdMng.Core.Directory;
using AdMng.Testing;

namespace AdMng.Core.Tests.Batch.Creation;

/// <summary>
/// The plans of the bulk creation wizard through the real <see cref="BatchExecutor"/> on the fake with the SAM rules
/// (docs/dev/phase3-bulk-create.md 2.11): accounts created disabled, their password, enabled with must-change; a password
/// policy failure, a login taken meanwhile and an unencrypted channel stop only their object; execute → retry → undo deletes
/// every object of both executions; the audit has the creates and never a password.
/// </summary>
public sealed class BulkCreatePipelineTests
{
    private readonly BulkCreateTestBed _bed = new();

    private async Task<(BulkCreateComputation Computation, ChangePlan Plan)> PlanAsync(int rows, BulkAccountOptions? options = null)
    {
        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.GeneratedRows(rows), options: options));
        Assert.False(computation.HasRunErrors);
        return (computation, _bed.Planner.BuildPlan(computation, null, "Utworzenie kont (50) w jednostce „Nowi pracownicy”"));
    }

    [Fact]
    public async Task FiftyUsers_AreCreatedEnabled_WithMustChangeAtLogon()
    {
        var (computation, plan) = await PlanAsync(50, new BulkAccountOptions { MatchAttributes = [], GroupDns = [_bed.Sample.GroupNew] });

        var result = await _bed.ExecuteAsync(plan);

        Assert.Equal(50, result.SucceededCount);
        foreach (var row in computation.Rows)
        {
            var dn = row.DistinguishedName!;
            Assert.Equal(["512"], _bed.Session.GetValues(dn, Attr.UserAccountControl));
            Assert.Equal(["0"], _bed.Session.GetValues(dn, Attr.PwdLastSet));
            Assert.Equal([row.Value(Attr.SamAccountName)!], _bed.Session.GetValues(dn, Attr.SamAccountName));
            Assert.Equal([row.Value(Attr.GivenName)!], _bed.Session.GetValues(dn, Attr.GivenName));
        }

        Assert.Equal(50, _bed.Session.PasswordResets.Count);
        Assert.Equal(50, _bed.Session.GetValues(_bed.Sample.GroupNew, Attr.Member).Count);
        Assert.Equal("kd202", computation.Rows[0].Value(Attr.SamAccountName));
    }

    [Fact]
    public async Task APasswordPolicyFailure_LeavesThatAccountDisabled_AndWithoutGroups()
    {
        var (_, plan) = await PlanAsync(5, new BulkAccountOptions { MatchAttributes = [], GroupDns = [_bed.Sample.GroupNew] });
        var third = plan.Objects[2].DistinguishedName;
        _bed.Session.FailNext(FakeOperationKind.ResetPassword, third, DirectoryErrorKind.PasswordPolicy);

        var result = await _bed.ExecuteAsync(plan);

        Assert.Equal(ObjectResultStatus.PartiallySucceeded, result.Objects[2].Status);
        Assert.Equal(4, result.SucceededCount);
        Assert.Equal(["514"], _bed.Session.GetValues(third, Attr.UserAccountControl));
        Assert.DoesNotContain(third, _bed.Session.GetValues(_bed.Sample.GroupNew, Attr.Member).OfType<string>(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ALoginTakenMeanwhile_FailsThatRowOnly()
    {
        var (computation, plan) = await PlanAsync(5);
        _bed.AddUser("Ktoś", "Szybszy", new Dictionary<string, object?> { [Attr.SamAccountName] = computation.Rows[1].Value(Attr.SamAccountName) });

        var result = await _bed.ExecuteAsync(plan);

        Assert.Equal(ObjectResultStatus.Failed, result.Objects[1].Status);
        Assert.Equal(DirectoryErrorKind.AlreadyExists, result.Objects[1].Changes.Single().ErrorKind);
        Assert.Equal(4, result.SucceededCount);
    }

    [Fact]
    public async Task TheCanary_OnAnUnencryptedChannel_StopsAtTheFirstReset()
    {
        var (_, plan) = await PlanAsync(5);
        var canary = plan with { Objects = [plan.Objects[0]] };
        _bed.Session.IsChannelEncrypted = false;

        var result = await _bed.ExecuteAsync(canary);

        var only = Assert.Single(result.Objects);
        Assert.Equal(ObjectResultStatus.PartiallySucceeded, only.Status);
        Assert.Equal(DirectoryErrorKind.StrongAuthRequired, only.Changes[1].ErrorKind);
        Assert.Equal(["514"], _bed.Session.GetValues(only.FinalDn, Attr.UserAccountControl));
        Assert.All(plan.Objects.Skip(1), o => Assert.False(_bed.Session.Exists(o.DistinguishedName)));
    }

    [Fact]
    public async Task ExecuteRetryUndo_DeletesEveryObjectOfBothExecutions()
    {
        var (computation, plan) = await PlanAsync(6, new BulkAccountOptions { MatchAttributes = [], GroupDns = [_bed.Sample.GroupNew] });
        _bed.Session.FailNext(FakeOperationKind.ResetPassword, plan.Objects[1].DistinguishedName, DirectoryErrorKind.PasswordPolicy);
        _bed.Session.FailNext(FakeOperationKind.Add, plan.Objects[4].DistinguishedName, DirectoryErrorKind.AccessDenied);
        var first = await _bed.ExecuteAsync(plan);
        var accumulated = BulkCreateResults.Merge(null, first);
        var created = (await _bed.Planner.ReadCreatedAsync(_bed.Session, first, BulkCreateTestBed.Ct)).ToList();

        var retry = _bed.Planner.BuildRetryPlan(computation, accumulated, null, "Ponowienie");
        Assert.Equal([2, 5], retry.Objects.Select(o => o.SourceRow!.Value));
        var second = await _bed.ExecuteAsync(retry);
        accumulated = BulkCreateResults.Merge(accumulated, second);
        created.AddRange(await _bed.Planner.ReadCreatedAsync(_bed.Session, second, BulkCreateTestBed.Ct));

        Assert.All(accumulated.Objects, o => Assert.Equal(ObjectResultStatus.Succeeded, o.Status));
        Assert.Equal(6, created.Count);
        Assert.Equal(1, _bed.Session.PasswordResets.Count(r => string.Equals(r.DistinguishedName, plan.Objects[1].DistinguishedName, StringComparison.OrdinalIgnoreCase)));

        var undo = await _bed.Planner.PlanUndoAsync(_bed.Session, created, "Cofnięcie", BulkCreateTestBed.Ct);
        var undone = await _bed.ExecuteAsync(undo);

        Assert.Equal(6, undone.SucceededCount);
        Assert.All(plan.Objects, o => Assert.False(_bed.Session.Exists(o.DistinguishedName)));
        Assert.DoesNotContain(_bed.Session.GetValues(_bed.Sample.GroupNew, Attr.Member).OfType<string>(), m => m.Contains("Nowi pracownicy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheAudit_HasTheCreates_AndNeverAPassword()
    {
        var (_, plan) = await PlanAsync(3);
        var passwords = plan.Objects.SelectMany(o => o.Changes).Select(c => c.Password).OfType<string>().ToList();

        await _bed.ExecuteAsync(plan);

        var entries = _bed.Audit.Entries;
        Assert.Equal(3, entries.Where(e => e.Action == nameof(ChangeAction.Create)).Select(e => e.DistinguishedName).Distinct().Count());
        Assert.Equal(3, entries.Count(e => e.Action == nameof(ChangeAction.ResetPassword)));
        Assert.All(entries, e => Assert.All(passwords, p =>
        {
            Assert.DoesNotContain(p, e.OldValue ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(p, e.NewValue ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(p, e.ToString(), StringComparison.Ordinal);
        }));
    }

    [Fact]
    public async Task Computers_GetTheirMachinePassword_AndEndAsWorkstationAccounts()
    {
        var input = _bed.Input(BulkCreateTestBed.Table(["pc-nowy-01"], ["pc-nowy-02"]), [new AttributeRule(Attr.SamAccountName, "{cn}")],
            [new ColumnMapping(0, ColumnTargetKind.Attribute, Attr.Cn)], kind: Model.ObjectKind.Computer);
        var computation = await _bed.ComputeAsync(input);

        var result = await _bed.ExecuteAsync(_bed.Planner.BuildPlan(computation, null, "Utworzenie komputerów"));

        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(["PC-NOWY-01$"], _bed.Session.GetValues(computation.Rows[0].DistinguishedName!, Attr.SamAccountName));
        Assert.Equal(["4096"], _bed.Session.GetValues(computation.Rows[0].DistinguishedName!, Attr.UserAccountControl));
        Assert.Equal(2, _bed.Session.PasswordResets.Count);
    }
}
