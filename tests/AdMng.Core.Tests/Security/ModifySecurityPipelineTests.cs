using AdMng.Core.ActiveDirectory;
using AdMng.Core.Audit;
using AdMng.Core.Batch;
using AdMng.Core.Batch.Planning;
using AdMng.Core.Directory;
using AdMng.Core.Security;
using AdMng.Testing;

namespace AdMng.Core.Tests.Security;

/// <summary><see cref="ChangeAction.ModifySecurity"/> through planning, execution, audit, rollback and the PowerShell export.</summary>
public class ModifySecurityPipelineTests
{
    private static readonly BatchOptions Fast = new() { InitialRetryDelay = TimeSpan.FromMilliseconds(1) };

    private readonly ManualTimeProvider _time = new();
    private readonly FakeDirectoryBuilder _builder;
    private readonly SampleCompany _company;
    private readonly InMemoryAuditLog _audit = new();
    private readonly string _helpdesk;
    private readonly string _helpdeskSid;

    public ModifySecurityPipelineTests()
    {
        _builder = new FakeDirectoryBuilder(timeProvider: _time);
        _company = _builder.AddSampleCompany();
        _helpdeskSid = _builder.NextSid();
        _helpdesk = _builder.AddGroup("Pomoc techniczna – Łódź", _company.GroupsOu, attributes: new Dictionary<string, object?> { [Attr.ObjectSid] = _helpdeskSid });
        Session.SetValues(_company.Computer1, Attr.NtSecurityDescriptor, FakeSecurityDescriptor.Build(
            DomainAdminsSid,
            DomainAdminsSid,
            FakeAce.FullControl(DomainAdminsSid),
            new FakeAce(FakeAce.AccessAllowed, SecurityDescriptorAce.InheritedFlag, 0x20094, "S-1-5-11")));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string DomainAdminsSid => $"{_builder.DomainSid}-512";

    private FakeDirectorySession Session => _builder.Session;

    private BatchExecutor Executor => new(_audit, _time);

    private AclChange Join => new([
        AceSpec.Allow(_helpdeskSid, 0x100, ExtendedRightIds.ResetPassword, "CONTOSO\\Pomoc techniczna – Łódź"),
        AceSpec.Allow(_helpdeskSid, 0x30, ExtendedRightIds.AccountRestrictions),
    ], []);

    private async Task<ChangePlan> PlanAsync(AclChange change)
    {
        var step = await AclPlanning.PlanAsync(Session, _company.Computer1, change, Ct);
        return new ChangePlan
        {
            Title = "Uprawnienia",
            Server = Session.Info.Server,
            CreatedAt = _time.GetUtcNow(),
            Objects = [new ObjectPlan
            {
                DistinguishedName = _company.Computer1,
                DisplayName = "WAW-PC001",
                Changes = step is null ? [] : [step],
            }],
        };
    }

    private IReadOnlyList<SecurityDescriptorAce> Aces()
    {
        var descriptor = (byte[])Session.Snapshot(_company.Computer1)!.Attributes[Attr.NtSecurityDescriptor][0];
        Assert.True(SecurityDescriptorSummary.TryParse(descriptor, out var summary));
        return summary!.Aces;
    }

    private string? Owner()
    {
        var descriptor = (byte[])Session.Snapshot(_company.Computer1)!.Attributes[Attr.NtSecurityDescriptor][0];
        Assert.True(SecurityDescriptorSummary.TryParse(descriptor, out var summary));
        return summary!.Owner;
    }

    [Fact]
    public async Task Planner_KeepsOnlyTheEffectiveDelta()
    {
        var plan = await PlanAsync(Join with { Removed = [AceSpec.Allow(_helpdeskSid, 0x08, ExtendedRightIds.ValidatedWriteServicePrincipalName)] });

        var step = Assert.Single(Assert.Single(plan.Objects).Changes);
        Assert.Equal(ChangeAction.ModifySecurity, step.Action);
        Assert.Equal(2, step.Acl!.Added.Count);
        Assert.Empty(step.Acl.Removed);
        Assert.Equal("CONTOSO\\Pomoc techniczna – Łódź", step.Acl.Added[0].TrusteeName);
        Assert.Single(step.Acl.PlannedFrom);
    }

    [Fact]
    public async Task Planner_ReturnsNothingWhenTheDaclIsAlreadyAsWanted()
    {
        var executed = await Executor.ExecuteAsync(Session, await PlanAsync(Join), Fast, null, Ct);
        Assert.Equal(1, executed.SucceededCount);

        Assert.Null(await AclPlanning.PlanAsync(Session, _company.Computer1, Join, Ct));
    }

    [Fact]
    public async Task Planner_ReportsAnUnreadableDescriptor()
    {
        var step = await AclPlanning.PlanAsync(Session, _company.Computer2, Join, Ct);

        Assert.NotNull(step);
        Assert.Contains(step!.Issues, i => i.Code == IssueCodes.SecurityNotReadable && i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public async Task Executor_WritesTheDaclWithTheSdFlagsControl_AndKeepsTheOwner()
    {
        var plan = await PlanAsync(Join);
        Session.ClearOperations();

        var result = await Executor.ExecuteAsync(Session, plan, Fast, null, Ct);

        Assert.Equal(ObjectResultStatus.Succeeded, Assert.Single(result.Objects).Status);
        var write = Assert.Single(Session.Writes);
        Assert.Equal(SecurityDescriptorRights.Dacl, write.ModifyOptions!.SecurityDescriptorFlags);
        var modification = Assert.Single(write.Modifications);
        Assert.Equal(Attr.NtSecurityDescriptor, modification.Attribute);
        Assert.Equal(ModificationOperation.Replace, modification.Operation);
        Assert.Equal(DomainAdminsSid, Owner());
        Assert.Contains(Aces(), a => a.Sid == _helpdeskSid && a.ObjectType == ExtendedRightIds.ResetPassword && a.Mask == 0x100);
        Assert.Contains(Aces(), a => a.IsInherited && a.Sid == "S-1-5-11");
    }

    [Fact]
    public async Task Executor_KeepsAnUnrelatedAceAddedAfterThePlan()
    {
        var plan = await PlanAsync(Join);
        var other = new FakeAce(FakeAce.AccessAllowedObject, 0, 0x08, $"{_builder.DomainSid}-1300", ExtendedRightIds.ValidatedWriteDnsHostName);
        Session.SetValues(_company.Computer1, Attr.NtSecurityDescriptor, FakeSecurityDescriptor.Build(
            DomainAdminsSid, null, FakeAce.FullControl(DomainAdminsSid), other));

        var result = await Executor.ExecuteAsync(Session, plan, Fast, null, Ct);

        Assert.Equal(1, result.SucceededCount);
        Assert.Contains(Aces(), a => a.Sid == $"{_builder.DomainSid}-1300");
        Assert.Equal(2, Aces().Count(a => a.Sid == _helpdeskSid));
    }

    [Fact]
    public async Task Executor_WritesNothingWhenTheDeltaIsAlreadyThere()
    {
        var plan = await PlanAsync(Join);
        await Executor.ExecuteAsync(Session, plan, Fast, null, Ct);
        Session.ClearOperations();

        var again = await Executor.ExecuteAsync(Session, plan, Fast, null, Ct);

        Assert.Equal(1, again.SucceededCount);
        Assert.Empty(Session.Writes);
    }

    [Fact]
    public async Task Executor_RetriesATransientError_AndReappliesTheDelta()
    {
        var plan = await PlanAsync(Join);
        Session.FailNext(FakeOperationKind.Modify, _company.Computer1, DirectoryErrorKind.Busy);

        var result = await Executor.ExecuteAsync(Session, plan, Fast, null, Ct);

        var step = Assert.Single(Assert.Single(result.Objects).Changes);
        Assert.True(step.Succeeded);
        Assert.Equal(2, step.Attempts);
        Assert.Equal(2, Aces().Count(a => a.Sid == _helpdeskSid));
    }

    [Fact]
    public async Task Executor_AuditsTheAcesWithoutNames()
    {
        await Executor.ExecuteAsync(Session, await PlanAsync(Join), Fast, null, Ct);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal("ModifySecurity", entry.Action);
        Assert.Equal(Attr.NtSecurityDescriptor, entry.Attribute);
        Assert.Null(entry.OldValue);
        Assert.Contains($"allow User-Force-Change-Password {_helpdeskSid}", entry.NewValue, StringComparison.Ordinal);
        Assert.DoesNotContain("Pomoc", entry.NewValue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rollback_RemovesTheAddedAces()
    {
        var result = await Executor.ExecuteAsync(Session, await PlanAsync(Join), Fast, null, Ct);

        var rollback = new RollbackPlanner().CreateRollbackPlan(result);
        var step = Assert.Single(Assert.Single(rollback.Objects).Changes);
        Assert.Equal(ChangeAction.ModifySecurity, step.Action);
        Assert.Equal(2, step.Acl!.Removed.Count);
        Assert.Empty(step.Acl.Added);

        var undone = await Executor.ExecuteAsync(Session, rollback, Fast, null, Ct);
        Assert.Equal(1, undone.SucceededCount);
        Assert.DoesNotContain(Aces(), a => a.Sid == _helpdeskSid);
        Assert.Equal(DomainAdminsSid, Owner());
    }

    [Fact]
    public async Task PowerShell_UsesGetAclAndSetAclOnTheDriveOfTheDc()
    {
        var plan = await PlanAsync(Join with { Removed = [] });

        var script = new PowerShellExporter().Export(plan, null);

        Assert.Contains("New-PSDrive -Name ADMNG -PSProvider ActiveDirectory -Root '//RootDSE/' -Server $server", script, StringComparison.Ordinal);
        Assert.Contains("$acl = Get-Acl -LiteralPath \"ADMNG:$dn\"", script, StringComparison.Ordinal);
        Assert.Contains(
            $"$acl.AddAccessRule([System.DirectoryServices.ActiveDirectoryAccessRule]::new([System.Security.Principal.SecurityIdentifier]'{_helpdeskSid}', "
            + "[System.DirectoryServices.ActiveDirectoryRights]'ExtendedRight', [System.Security.AccessControl.AccessControlType]'Allow', "
            + "[guid]'00299570-246d-11d0-a768-00aa006e0529', [System.DirectoryServices.ActiveDirectorySecurityInheritance]'None', "
            + "[guid]'00000000-0000-0000-0000-000000000000'))",
            script,
            StringComparison.Ordinal);
        Assert.Contains("[System.DirectoryServices.ActiveDirectoryRights]'ReadProperty, WriteProperty'", script, StringComparison.Ordinal);
        Assert.Contains("# + CONTOSO\\Pomoc techniczna – Łódź: allow User-Force-Change-Password", script, StringComparison.Ordinal);
        Assert.Contains("Set-Acl -LiteralPath \"ADMNG:$dn\" -AclObject $acl", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PowerShell_RemovesWithRemoveAccessRuleSpecific_WithoutAPinnedDc()
    {
        var plan = new ChangePlan
        {
            Objects = [new ObjectPlan
            {
                DistinguishedName = _company.Computer1,
                DisplayName = "WAW-PC001",
                Changes = [new PlannedChange { Action = ChangeAction.ModifySecurity, Acl = new AclChange([], [new AceSpec
                {
                    Qualifier = AceQualifier.Deny,
                    Flags = SecurityDescriptorAce.ContainerInheritFlag | SecurityDescriptorAce.InheritOnlyFlag,
                    Mask = 0x12345,
                    TrusteeSid = _helpdeskSid,
                }]) }],
            }],
        };

        var script = new PowerShellExporter().Export(plan, null);

        Assert.DoesNotContain("New-PSDrive", script, StringComparison.Ordinal);
        Assert.Contains("$acl = Get-Acl -LiteralPath \"AD:$dn\"", script, StringComparison.Ordinal);
        Assert.Contains("$acl.RemoveAccessRuleSpecific(", script, StringComparison.Ordinal);
        Assert.Contains("[System.DirectoryServices.ActiveDirectoryRights]74565", script, StringComparison.Ordinal);
        Assert.Contains("[System.Security.AccessControl.AccessControlType]'Deny'", script, StringComparison.Ordinal);
        Assert.Contains("[System.DirectoryServices.ActiveDirectorySecurityInheritance]'Descendents'", script, StringComparison.Ordinal);
    }
}
