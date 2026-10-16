using AdMng.Core.ActiveDirectory;
using AdMng.Core.Audit;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Security;
using AdMng.Core.Templates;
using AdMng.Testing;

namespace AdMng.Core.Tests.Batch;

public class RollbackPlannerTests
{
    private static readonly BatchOptions Fast = new() { InitialRetryDelay = TimeSpan.FromMilliseconds(1) };

    private readonly ManualTimeProvider _time = new();
    private readonly FakeDirectoryBuilder _builder;
    private readonly SampleCompany _company;
    private readonly RollbackPlanner _rollback = new();

    public RollbackPlannerTests()
    {
        _builder = new FakeDirectoryBuilder(timeProvider: _time);
        _company = _builder.AddSampleCompany();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakeDirectorySession Session => _builder.Session;

    private async Task<BatchResult> RunAsync(IReadOnlyList<string> targets, params BatchOperation[] operations)
    {
        var plan = await new ChangePlanner(new TemplateEngine(), new PasswordGenerator(), _time)
            .PlanAsync(Session, new BatchRequest(targets, operations) { Title = "Porządki" }, null, null, Ct);
        return await ExecuteAsync(plan);
    }

    private Task<BatchResult> ExecuteAsync(ChangePlan plan) =>
        new BatchExecutor(new InMemoryAuditLog(), _time).ExecuteAsync(Session, plan, Fast, null, Ct);

    [Fact]
    public async Task Modify_IsRestoredWithSwappedValues()
    {
        var result = await RunAsync([_company.JanKowalski], new SetAttributeOperation(Attr.Title, "Kierownik"), new ClearAttributeOperation(Attr.Department));

        var rollback = _rollback.CreateRollbackPlan(result);
        var changes = Assert.Single(Assert.Single(rollback.Objects).Changes).Attributes;
        var outcome = await ExecuteAsync(rollback);

        Assert.Equal("Rollback: Porządki", rollback.Title);
        Assert.Equal(result.Server, rollback.Server);
        Assert.Equal(result.FinishedAt, rollback.CreatedAt);
        Assert.Equal([Attr.Title, Attr.Department], changes.Select(c => c.Attribute));
        Assert.Equal(["Kierownik"], changes[0].OldValues);
        Assert.Equal(["Administrator systemów"], changes[0].NewValues);
        Assert.Equal(AttributeChangeKind.Replace, changes[1].Kind);
        Assert.Equal(["IT"], changes[1].NewValues);
        Assert.Equal(1, outcome.SucceededCount);
        Assert.Equal(["Administrator systemów"], Session.GetValues(_company.JanKowalski, Attr.Title));
        Assert.Equal(["IT"], Session.GetValues(_company.JanKowalski, Attr.Department));
    }

    [Fact]
    public async Task NewValue_IsClearedOnRollback()
    {
        var result = await RunAsync([_company.JanKowalski], new SetAttributeOperation(Attr.Info, "Notatka"));

        var change = Assert.Single(Assert.Single(_rollback.CreateRollbackPlan(result).Objects[0].Changes).Attributes);
        await ExecuteAsync(_rollback.CreateRollbackPlan(result));

        Assert.Equal(AttributeChangeKind.Clear, change.Kind);
        Assert.Equal(["Notatka"], change.OldValues);
        Assert.Empty(Session.GetValues(_company.JanKowalski, Attr.Info));
    }

    [Fact]
    public async Task ValueAdditionsAndRemovals_AreInverted()
    {
        Session.SetValues(_company.JanKowalski, Attr.OtherTelephone, "111", "222");
        var result = await RunAsync([_company.JanKowalski],
            new AddValuesOperation(Attr.ProxyAddresses, ["smtp:jan@firma.pl"]),
            new RemoveValuesOperation(Attr.OtherTelephone, ["222"]));

        var rollback = _rollback.CreateRollbackPlan(result);
        var changes = rollback.Objects[0].Changes[0].Attributes;
        await ExecuteAsync(rollback);

        Assert.Equal(AttributeChangeKind.RemoveValues, changes.Single(c => c.Attribute == Attr.ProxyAddresses).Kind);
        Assert.Equal(AttributeChangeKind.AddValues, changes.Single(c => c.Attribute == Attr.OtherTelephone).Kind);
        Assert.Empty(Session.GetValues(_company.JanKowalski, Attr.ProxyAddresses));
        Assert.Equal(["111", "222"], Session.GetValues(_company.JanKowalski, Attr.OtherTelephone));
    }

    [Fact]
    public async Task Membership_IsSwapped()
    {
        var result = await RunAsync([_company.JanKowalski], new AddToGroupsOperation([_company.GroupSales]), new RemoveFromGroupsOperation([_company.GroupIt]));

        var rollback = _rollback.CreateRollbackPlan(result);
        await ExecuteAsync(rollback);

        Assert.Equal([ChangeAction.AddMember, ChangeAction.RemoveMember], rollback.Objects[0].Changes.Select(c => c.Action));
        Assert.Equal([_company.GroupIt, _company.GroupSales], rollback.Objects[0].Changes.Select(c => c.TargetDn));
        Assert.Equal([_company.GroupIt], Session.GetValues(_company.JanKowalski, Attr.MemberOf));
    }

    [Fact]
    public async Task RenameAndMove_AreUndoneInReverseOrderFromTheFinalDn()
    {
        var result = await RunAsync([_company.JanKowalski],
            new SetAttributeOperation(Attr.Title, "Handlowiec"),
            new RenameOperation("{givenName} {sn}-Nowak"),
            new MoveOperation(_company.SalesOu));

        var rollback = _rollback.CreateRollbackPlan(result);
        var plan = Assert.Single(rollback.Objects);
        var outcome = await ExecuteAsync(rollback);

        Assert.Equal("CN=Jan Kowalski-Nowak," + _company.SalesOu, plan.DistinguishedName);
        Assert.Equal([ChangeAction.Move, ChangeAction.Rename, ChangeAction.Modify], plan.Changes.Select(c => c.Action));
        Assert.Equal(_company.ItOu, plan.Changes[0].TargetDn);
        Assert.Equal("CN=Jan Kowalski", plan.Changes[1].TargetDn);
        Assert.Equal(_company.JanKowalski, Assert.Single(outcome.Objects).FinalDn);
        Assert.True(Session.Exists(_company.JanKowalski));
        Assert.Equal(["Administrator systemów"], Session.GetValues(_company.JanKowalski, Attr.Title));
    }

    [Fact]
    public async Task EscapedNames_AreRestored()
    {
        var result = await RunAsync([_company.JanKowalski], new RenameOperation("{sn}, {givenName}"));

        var rollback = _rollback.CreateRollbackPlan(result);
        await ExecuteAsync(rollback);

        Assert.Equal(@"CN=Kowalski\, Jan," + _company.ItOu, rollback.Objects[0].DistinguishedName);
        Assert.True(Session.Exists(_company.JanKowalski));
    }

    [Fact]
    public async Task PasswordResetAndUnlock_AreNotReversible()
    {
        var result = await RunAsync([_company.ZanetaSzymanska], new ResetPasswordOperation(PasswordMode.GenerateUnique));

        var rollback = _rollback.CreateRollbackPlan(result);
        var plan = Assert.Single(rollback.Objects);
        var outcome = await ExecuteAsync(rollback);

        // Neither the password nor the unlock can be undone, and "must change at next logon" stays with the
        // administrator-set password.
        Assert.Empty(plan.Changes);
        Assert.True(plan.IsNoOp);
        Assert.Equal(
            [null, Attr.LockoutTime, Attr.PwdLastSet],
            plan.Issues.Where(i => i is { Code: IssueCodes.NotReversible, Severity: IssueSeverity.Warning }).Select(i => i.Attribute).Order());
        Assert.False(plan.HasErrors);
        Assert.Equal(ObjectResultStatus.Skipped, Assert.Single(outcome.Objects).Status);
        Assert.Equal(["0"], Session.GetValues(_company.ZanetaSzymanska, Attr.PwdLastSet));
    }

    [Fact]
    public async Task PasswordReset_RollbackRestoresOtherValuesButKeepsMustChange()
    {
        var result = await RunAsync([_company.JanKowalski],
            new ResetPasswordOperation(PasswordMode.GenerateUnique),
            new SetAttributeOperation(Attr.Title, "Kierownik"));

        var rollback = _rollback.CreateRollbackPlan(result);
        await ExecuteAsync(rollback);

        Assert.Equal([Attr.Title], Assert.Single(Assert.Single(rollback.Objects).Changes).Attributes.Select(a => a.Attribute));
        Assert.Equal(["Administrator systemów"], Session.GetValues(_company.JanKowalski, Attr.Title));
        Assert.Equal(["0"], Session.GetValues(_company.JanKowalski, Attr.PwdLastSet));
    }

    [Fact]
    public async Task MustChangePassword_IsRestoredExactly()
    {
        Session.SetValues(_company.JanKowalski, Attr.PwdLastSet, "0");
        var result = await RunAsync([_company.JanKowalski], new RequirePasswordChangeOperation(false));

        var change = Assert.Single(Assert.Single(_rollback.CreateRollbackPlan(result).Objects[0].Changes).Attributes);

        Assert.Equal(["0"], change.NewValues);
    }

    [Fact]
    public async Task Delete_IsNotReversible()
    {
        var result = await RunAsync([_company.JanKowalski], new DeleteOperation());

        var plan = Assert.Single(_rollback.CreateRollbackPlan(result).Objects);

        Assert.Empty(plan.Changes);
        Assert.True(plan.IsNoOp);
        Assert.Equal(IssueCodes.NotReversible, Assert.Single(plan.Issues).Code);
    }

    [Fact]
    public async Task OnlySuccessfulStepsAreRolledBack()
    {
        var plan = await new ChangePlanner(new TemplateEngine(), new PasswordGenerator(), _time).PlanAsync(
            Session,
            new BatchRequest([_company.JanKowalski, _company.LukaszWisniewski], [new SetAttributeOperation(Attr.Title, "Nowy"), new MoveOperation(_company.SalesOu)]),
            null,
            null,
            Ct);
        Session.FailNext(FakeOperationKind.Move, _company.JanKowalski, DirectoryErrorKind.AccessDenied);
        Session.FailNext(FakeOperationKind.Modify, _company.LukaszWisniewski, DirectoryErrorKind.AccessDenied);
        var result = await ExecuteAsync(plan);

        var rollback = _rollback.CreateRollbackPlan(result);

        var jan = Assert.Single(rollback.Objects);
        Assert.Equal(_company.JanKowalski, jan.DistinguishedName);
        Assert.Equal(ChangeAction.Modify, Assert.Single(jan.Changes).Action);
    }

    [Fact]
    public async Task ChangesMadeAfterTheBatch_AreReportedAsConflicts()
    {
        var result = await RunAsync([_company.JanKowalski], new SetAttributeOperation(Attr.Title, "Kierownik"));
        Session.SetValues(_company.JanKowalski, Attr.Title, "Ktoś zmienił");

        var outcome = await ExecuteAsync(_rollback.CreateRollbackPlan(result));

        var change = Assert.Single(Assert.Single(outcome.Objects).Changes);
        Assert.False(change.Succeeded);
        Assert.StartsWith(BatchExecutor.ConflictMessagePrefix, change.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(["Ktoś zmienił"], Session.GetValues(_company.JanKowalski, Attr.Title));
    }

    [Fact]
    public void Create_IsRolledBackByDelete()
    {
        var created = new ObjectPlan
        {
            DistinguishedName = "CN=Anna Nowak," + _company.SalesOu,
            DisplayName = "Anna Nowak",
            Changes = [new PlannedChange { Action = ChangeAction.Create }, new PlannedChange { Action = ChangeAction.AddMember, TargetDn = _company.GroupSales }],
        };
        var now = _time.GetUtcNow();
        var result = new BatchResult(
            new ChangePlan { Objects = [created] },
            [new ObjectResult(created, ObjectResultStatus.Succeeded, created.Changes.Select(c => new ChangeResult(c, true)).ToList(), created.DistinguishedName, now, now)],
            "dc01.contoso.test",
            "CONTOSO\\Administrator",
            now,
            now,
            WasCancelled: false);

        var rollback = _rollback.CreateRollbackPlan(result);

        Assert.Equal("Rollback", rollback.Title);
        Assert.Equal(ChangeAction.Delete, Assert.Single(Assert.Single(rollback.Objects).Changes).Action);
    }
}
