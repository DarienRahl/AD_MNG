using AdMng.Core.ActiveDirectory;
using AdMng.Core.Audit;
using AdMng.Core.Batch;
using AdMng.Core.Computers;
using AdMng.Core.Security;
using AdMng.Core.Templates;
using AdMng.Testing;

namespace AdMng.Core.Tests.Computers;

/// <summary>Computer account lifecycle (K1 design note 4.6): reset, join rights, validations of DC accounts.</summary>
public class ComputerLifecycleTests
{
    private static readonly BatchOptions Fast = new() { InitialRetryDelay = TimeSpan.FromMilliseconds(1) };

    private readonly ManualTimeProvider _time = new();
    private readonly FakeDirectoryBuilder _builder;
    private readonly SampleCompany _company;

    public ComputerLifecycleTests()
    {
        _builder = new FakeDirectoryBuilder(timeProvider: _time);
        _company = _builder.AddSampleCompany();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<ChangePlan> PlanAsync(IReadOnlyList<string> targets, params BatchOperation[] operations) =>
        new ChangePlanner(new TemplateEngine(), new PasswordGenerator(), _time)
            .PlanAsync(_builder.Session, new BatchRequest(targets, operations) { Title = "Resetuj konto" }, null, null, Ct);

    private static IEnumerable<ValidationIssue> AllIssues(ObjectPlan plan) => plan.Issues.Concat(plan.Changes.SelectMany(c => c.Issues));

    [Fact]
    public async Task Reset_GeneratesA120CharacterPasswordNobodySees()
    {
        var plan = await PlanAsync([_company.Computer1], new ResetComputerAccountOperation());

        var step = Assert.Single(Assert.Single(plan.Objects).Changes);
        Assert.Equal(ChangeAction.ResetPassword, step.Action);
        Assert.Equal(120, step.Password!.Length);
        Assert.DoesNotContain(step.Password, step.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(step.Password, plan.ToString(), StringComparison.Ordinal);
        Assert.Contains(step.Issues, i => i.Code == IssueCodes.ComputerTrustLost && i.Severity == IssueSeverity.Warning);
        Assert.Equal("ResetComputerAccountOperation { }", new ResetComputerAccountOperation().ToString());
    }

    [Fact]
    public async Task Reset_ExecutesAndIsNeverInTheReport_OrThePowerShell()
    {
        var plan = await PlanAsync([_company.Computer1], new ResetComputerAccountOperation());
        var password = plan.Objects[0].Changes[0].Password!;
        var audit = new InMemoryAuditLog();

        var result = await new BatchExecutor(audit, _time).ExecuteAsync(_builder.Session, plan, Fast, null, Ct);

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(password, Assert.Single(_builder.Session.PasswordResets).Password);
        var report = new StringWriter();
        new BatchReportExporter(new Core.Csv.CsvCodec()).Write(report, result, includePasswords: true);
        Assert.DoesNotContain(password, report.ToString(), StringComparison.Ordinal);
        Assert.False(BatchReportExporter.ContainsSecrets(result));
        var script = new PowerShellExporter().Export(plan, null);
        Assert.DoesNotContain(password, script, StringComparison.Ordinal);
        Assert.Contains("Read-Host -AsSecureString", script, StringComparison.Ordinal);
        Assert.All(audit.Entries, e => Assert.DoesNotContain(password, $"{e.OldValue}{e.NewValue}", StringComparison.Ordinal));
        var rollback = new RollbackPlanner().CreateRollbackPlan(result);
        Assert.Contains(rollback.Objects[0].Issues, i => i.Code == IssueCodes.NotReversible);
    }

    [Fact]
    public async Task Reset_IsRefusedForDomainControllers_AndNotApplicableToUsers()
    {
        var plan = await PlanAsync([_builder.DomainControllerDn, _company.JanKowalski], new ResetComputerAccountOperation());

        Assert.Contains(AllIssues(plan.Objects[0]), i => i.Code == IssueCodes.DomainControllerAccount && i.Severity == IssueSeverity.Error);
        Assert.True(plan.Objects[0].HasErrors);
        Assert.Contains(AllIssues(plan.Objects[1]), i => i.Code == IssueCodes.NotApplicable);
        Assert.Equal(0, plan.ExecutableCount);
    }

    [Fact]
    public async Task DisablingOrDeletingADomainController_IsAnError_ThatNamesTheDemotion()
    {
        var disable = await PlanAsync([_builder.DomainControllerDn], new SetAccountEnabledOperation(false));
        var delete = await PlanAsync([_builder.DomainControllerDn], new DeleteOperation());

        Assert.Contains(AllIssues(disable.Objects[0]), i => i.Code == IssueCodes.DomainControllerAccount);
        Assert.Contains(AllIssues(delete.Objects[0]), i => i.Code == IssueCodes.DomainControllerAccount);
        Assert.Contains(AllIssues(disable.Objects[0]), i => i.Code == IssueCodes.ProtectedObject);
    }

    [Fact]
    public async Task MovingADomainController_OutOfItsOu_IsAWarning()
    {
        var plan = await PlanAsync([_builder.DomainControllerDn], new MoveOperation(_company.ComputersOu));
        var member = await PlanAsync([_company.Computer1], new MoveOperation(_builder.ComputersDn));

        var warning = Assert.Single(AllIssues(plan.Objects[0]), i => i.Code == IssueCodes.DomainControllerOutsideOu);
        Assert.Equal(IssueSeverity.Warning, warning.Severity);
        Assert.DoesNotContain(AllIssues(member.Objects[0]), i => i.Code == IssueCodes.DomainControllerOutsideOu);
    }

    [Fact]
    public async Task DeletingAComputerWithBitLockerRecovery_NeedsTheSubtree_AndWarns()
    {
        _builder.Session.Seed($"CN=2026-10-01T08:00:00+01:00{{0A2B3C4D-1111-2222-3333-444455556666}},{_company.Computer1}", new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "msFVE-RecoveryInformation" },
        });

        var plain = await PlanAsync([_company.Computer1], new DeleteOperation());
        var subtree = await PlanAsync([_company.Computer1], new DeleteOperation(IncludeSubtree: true));
        var empty = await PlanAsync([_company.Computer2], new DeleteOperation(IncludeSubtree: true));

        Assert.Contains(AllIssues(plain.Objects[0]), i => i.Code == IssueCodes.HasChildren);
        Assert.Contains(AllIssues(subtree.Objects[0]), i => i.Code == IssueCodes.DeletesBitLockerRecovery && i.Severity == IssueSeverity.Warning);
        Assert.False(subtree.Objects[0].HasErrors);
        Assert.DoesNotContain(AllIssues(empty.Objects[0]), i => i.Code == IssueCodes.DeletesBitLockerRecovery);
    }

    [Fact]
    public void JoinRights_AreTheFourAcesAducWrites()
    {
        const string Sid = "S-1-5-21-3623811015-3361044348-30300820-1201";

        var aces = ComputerJoinRights.For(Sid, "CONTOSO\\Pomoc techniczna");

        Assert.Equal(
            [
                (0x100, ExtendedRightIds.ResetPassword),
                (0x30, ExtendedRightIds.AccountRestrictions),
                (0x08, ExtendedRightIds.ValidatedWriteDnsHostName),
                (0x08, ExtendedRightIds.ValidatedWriteServicePrincipalName),
            ],
            aces.Select(a => (a.Mask, a.ObjectType!.Value)));
        Assert.All(aces, a =>
        {
            Assert.Equal(AceQualifier.Allow, a.Qualifier);
            Assert.Equal(0, a.Flags);
            Assert.Equal(Sid, a.TrusteeSid);
            Assert.Equal((byte)0x05, a.AceType);
        });
        Assert.Empty(ComputerJoinRights.Grant(Sid).Removed);
    }
}
