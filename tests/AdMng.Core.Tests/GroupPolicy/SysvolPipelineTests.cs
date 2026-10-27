using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Audit;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Testing;

namespace AdMng.Core.Tests.GroupPolicy;

/// <summary>SYSVOL steps through the executor, the rollback planner and the PowerShell exporter (design note 3.3).</summary>
public class SysvolPipelineTests
{
    private static readonly BatchOptions Fast = new() { InitialRetryDelay = TimeSpan.FromMilliseconds(1), MaxDegreeOfParallelism = 8 };
    private static readonly byte[] Pol = Encoding.UTF8.GetBytes("PReg – zasady haseł stacji roboczych");

    private readonly ManualTimeProvider _time = new();
    private readonly FakeDirectoryBuilder _builder;
    private readonly SampleCompany _company;
    private readonly FakeSysvol _sysvol = new();
    private readonly InMemoryAuditLog _audit = new();

    public SysvolPipelineTests()
    {
        _builder = new FakeDirectoryBuilder(timeProvider: _time);
        _company = _builder.AddSampleCompany();
        _builder.EnsurePoliciesContainer();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakeDirectorySession Session => _builder.Session;

    private BatchExecutor Executor(ISysvolFileWriter? writer) => new(_audit, _time, null, writer);

    private Task<BatchResult> ExecuteAsync(ChangePlan plan, ISysvolFileWriter? writer = null) =>
        Executor(writer ?? _sysvol).ExecuteAsync(Session, plan, Fast, null, Ct);

    /// <summary>A new GPO with one file, its child containers and a link (what the K2 planner produces for "create").</summary>
    private ChangePlan NewGpoPlan(Guid id, string name)
    {
        var gpcDn = GpLinkValue.GpoDn(id, _builder.DomainDn);
        var folder = SysvolPaths.FolderName(id);
        AttributeChange Set(string attribute, string value) => new(attribute, AttributeChangeKind.Replace, [], [value]);
        return new ChangePlan
        {
            Title = "Nowe GPO",
            Server = Session.Info.Server,
            Sequential = true,
            Objects =
            [
                new ObjectPlan
                {
                    DistinguishedName = gpcDn,
                    DisplayName = name,
                    ObjectClasses = ["groupPolicyContainer"],
                    PowerShell = PowerShellEquivalent.Of(PowerShellCommand.Of("New-GPO",
                        PowerShellParameter.Literal("Name", name), PowerShellParameter.Server)),
                    Changes =
                    [
                        new PlannedChange
                        {
                            Action = ChangeAction.Create,
                            Attributes =
                            [
                                new(Attr.ObjectClass, AttributeChangeKind.Replace, [], ["groupPolicyContainer"]),
                                Set(Attr.DisplayName, name),
                                Set(GroupPolicyAttributes.FileSysPath, $@"\\contoso.test\SysVol\contoso.test\Policies\{folder}"),
                                Set(GroupPolicyAttributes.VersionNumber, "0"),
                                Set(GroupPolicyAttributes.Flags, "0"),
                                Set(GroupPolicyAttributes.FunctionalityVersion, "2"),
                            ],
                        },
                        new PlannedChange { Action = ChangeAction.CreateGpoFolder, Sysvol = new SysvolChange { GpoId = id } },
                        new PlannedChange
                        {
                            Action = ChangeAction.WriteGpoFile,
                            Sysvol = new SysvolChange { GpoId = id, RelativePath = "Machine/Registry.pol", NewContent = Pol },
                        },
                        new PlannedChange
                        {
                            Action = ChangeAction.SetGpoVersion,
                            GpoVersion = new GpoVersionChange(GpoVersion.Zero, GpoVersion.Zero, new GpoVersion(0, 1)),
                        },
                    ],
                },
                new ObjectPlan
                {
                    DistinguishedName = "CN=Machine," + gpcDn,
                    DisplayName = "Machine",
                    PowerShell = PowerShellEquivalent.Covered,
                    Changes = [new PlannedChange { Action = ChangeAction.Create, Attributes = [new(Attr.ObjectClass, AttributeChangeKind.Replace, [], ["container"])] }],
                },
                new ObjectPlan
                {
                    DistinguishedName = "CN=User," + gpcDn,
                    DisplayName = "User",
                    PowerShell = PowerShellEquivalent.Covered,
                    Changes = [new PlannedChange { Action = ChangeAction.Create, Attributes = [new(Attr.ObjectClass, AttributeChangeKind.Replace, [], ["container"])] }],
                },
                new ObjectPlan
                {
                    DistinguishedName = _company.ComputersOu,
                    DisplayName = "Komputery",
                    PowerShell = PowerShellEquivalent.Of(PowerShellCommand.Of("New-GPLink",
                        PowerShellParameter.Literal("Guid", id.ToString("D")), PowerShellParameter.Literal("Target", _company.ComputersOu),
                        PowerShellParameter.Literal("LinkEnabled", "Yes"), PowerShellParameter.Server)),
                    Changes =
                    [
                        new PlannedChange
                        {
                            Action = ChangeAction.Modify,
                            Attributes = [new(Attr.GpLink, AttributeChangeKind.Replace, [], [GpLinkValue.Format(id, _builder.DomainDn, 0)])],
                        },
                    ],
                },
            ],
        };
    }

    [Fact]
    public async Task NewGpo_CreatesContainerFolderContentAndLink_OnTheSessionsDc()
    {
        var id = Guid.NewGuid();

        var result = await ExecuteAsync(NewGpoPlan(id, "Stacje robocze – Zasady haseł"));

        Assert.Equal(4, result.SucceededCount);
        Assert.True(_sysvol.FolderExists(id));
        Assert.Equal(Pol, _sysvol.Get(id, "Machine/Registry.pol"));
        Assert.Equal("[General]\r\nVersion=1\r\n", _sysvol.GetText(id, "GPT.INI"));
        Assert.Equal("1", Assert.Single(Session.GetValues(GpLinkValue.GpoDn(id, _builder.DomainDn), "versionNumber")));
        Assert.All(_sysvol.Calls, c => Assert.Equal(Session.Info.Server, c.Server));
        Assert.Single(_sysvol.OpenedOn);
        Assert.Equal(0, _sysvol.OpenConnections);
    }

    [Fact]
    public async Task Audit_HasHashesAndSizes_NeverContent()
    {
        var id = Guid.NewGuid();

        await ExecuteAsync(NewGpoPlan(id, "Stacje robocze – Zasady haseł"));

        var write = Assert.Single(_audit.Entries, e => e.Attribute == "sysvol:Machine/Registry.pol");
        Assert.Null(write.OldValue);
        Assert.StartsWith("sha256:", write.NewValue);
        Assert.EndsWith($"({Pol.Length} B)", write.NewValue);
        var version = Assert.Single(_audit.Entries, e => e.Attribute == "versionNumber" && e.Action == nameof(ChangeAction.SetGpoVersion));
        Assert.Equal(("0", "1"), (version.OldValue, version.NewValue));
        Assert.DoesNotContain(_audit.Entries, e => (e.NewValue ?? string.Empty).Contains("zasady haseł", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SequentialPlan_StopsAtTheFirstFailure()
    {
        var id = Guid.NewGuid();
        _sysvol.Fail("CreateGpoFolder", SysvolWriteError.AccessDenied);

        var result = await ExecuteAsync(NewGpoPlan(id, "Zasady – odmowa"));

        Assert.Equal(ObjectResultStatus.PartiallySucceeded, result.Objects[0].Status);
        Assert.Equal(DirectoryErrorKind.AccessDenied, result.Objects[0].Changes[^1].ErrorKind);
        Assert.All(result.Objects.Skip(1), o => Assert.Equal(ObjectResultStatus.Skipped, o.Status));
        Assert.Empty(Session.GetValues(_company.ComputersOu, Attr.GpLink));
    }

    [Fact]
    public async Task WithoutAWriter_SysvolStepsFail_WithTheUnavailableMessage()
    {
        var plan = NewGpoPlan(Guid.NewGuid(), "Bez SYSVOL");

        var result = await new BatchExecutor(_audit, _time).ExecuteAsync(Session, plan, Fast, null, Ct);

        var failed = result.Objects[0].Changes.Single(c => !c.Succeeded);
        Assert.Equal(ChangeAction.CreateGpoFolder, failed.Change.Action);
        Assert.Equal(BatchExecutor.SysvolUnavailableMessage, failed.ErrorMessage);
    }

    [Fact]
    public async Task UnavailableShare_ReportsTheReasonKind()
    {
        _sysvol.Unavailable = SysvolUnavailableReason.LogonFailure;

        var result = await ExecuteAsync(NewGpoPlan(Guid.NewGuid(), "Zła logowanie"));

        Assert.Equal(DirectoryErrorKind.InvalidCredentials, result.Objects[0].Changes.Single(c => !c.Succeeded).ErrorKind);
    }

    [Fact]
    public async Task Rollback_OfANewGpo_RunsInReverseOrder_AndDeletesTheUntouchedFolder()
    {
        var id = Guid.NewGuid();
        var result = await ExecuteAsync(NewGpoPlan(id, "Do cofnięcia"));

        var rollback = new RollbackPlanner().CreateRollbackPlan(result);

        Assert.True(rollback.Sequential);
        Assert.Equal(
            [_company.ComputersOu, "CN=User," + GpLinkValue.GpoDn(id, _builder.DomainDn), "CN=Machine," + GpLinkValue.GpoDn(id, _builder.DomainDn), GpLinkValue.GpoDn(id, _builder.DomainDn)],
            rollback.Objects.Select(o => o.DistinguishedName));
        Assert.Equal([ChangeAction.DeleteGpoFolder, ChangeAction.Delete], rollback.Objects[^1].Changes.Select(c => c.Action));

        var undone = await ExecuteAsync(rollback);

        Assert.Equal(4, undone.SucceededCount);
        Assert.False(_sysvol.FolderExists(id));
        Assert.False(Session.Exists(GpLinkValue.GpoDn(id, _builder.DomainDn)));
        Assert.Empty(Session.GetValues(_company.ComputersOu, Attr.GpLink));
    }

    [Fact]
    public async Task Rollback_OfANewGpo_KeepsAFolderSomebodyChanged()
    {
        var id = Guid.NewGuid();
        var result = await ExecuteAsync(NewGpoPlan(id, "Zmienione później"));
        _sysvol.PutText(id, "User/Documents & Settings/fdeploy.ini", "[FolderStatus]");

        var undone = await ExecuteAsync(new RollbackPlanner().CreateRollbackPlan(result));

        Assert.True(_sysvol.FolderExists(id));
        Assert.True(Session.Exists(GpLinkValue.GpoDn(id, _builder.DomainDn)));
        Assert.Equal(BatchExecutor.FolderChangedMessage, undone.Objects[^1].Changes.Single(c => !c.Succeeded).ErrorMessage);
    }

    [Fact]
    public async Task Rollback_OfAContentChange_WritesTheOldContentAndRaisesTheVersionAgain()
    {
        var id = _builder.AddGroupPolicyObject("Serwery – Rejestr", attributes: new Dictionary<string, object?> { ["versionNumber"] = 3 });
        var gpcDn = GpLinkValue.GpoDn(id, _builder.DomainDn);
        _sysvol.AddGpoFolder(id, version: 3);
        _sysvol.Put(id, "Machine/Registry.pol", Pol);
        var newPol = Encoding.UTF8.GetBytes("PReg – nowe");
        var plan = new ChangePlan
        {
            Server = Session.Info.Server,
            Sequential = true,
            Objects =
            [
                new ObjectPlan
                {
                    DistinguishedName = gpcDn,
                    DisplayName = "Serwery – Rejestr",
                    Changes =
                    [
                        new PlannedChange
                        {
                            Action = ChangeAction.WriteGpoFile,
                            Sysvol = new SysvolChange { GpoId = id, RelativePath = "Machine/Registry.pol", OldContent = Pol, NewContent = newPol },
                        },
                        new PlannedChange
                        {
                            Action = ChangeAction.SetGpoVersion,
                            GpoVersion = new GpoVersionChange(new GpoVersion(0, 3), new GpoVersion(0, 3), new GpoVersion(0, 4)),
                        },
                    ],
                },
            ],
        };

        var result = await ExecuteAsync(plan);
        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(newPol, _sysvol.Get(id, "Machine/Registry.pol"));

        var rollback = new RollbackPlanner().CreateRollbackPlan(result);
        var version = rollback.Objects[0].Changes.Single(c => c.Action == ChangeAction.SetGpoVersion).GpoVersion!;
        Assert.Equal(new GpoVersion(0, 5), version.New);

        var undone = await ExecuteAsync(rollback);

        Assert.Equal(1, undone.SucceededCount);
        Assert.Equal(Pol, _sysvol.Get(id, "Machine/Registry.pol"));
        Assert.Equal("[General]\r\nVersion=5\r\n", _sysvol.GetText(id, "GPT.INI"));
        Assert.Equal("5", Assert.Single(Session.GetValues(gpcDn, "versionNumber")));
    }

    [Fact]
    public async Task VersionNumberChangedBehindThePlan_FailsWithAConflict_AndKeepsTheFiles()
    {
        var id = _builder.AddGroupPolicyObject("Konflikt wersji", attributes: new Dictionary<string, object?> { ["versionNumber"] = 3 });
        var gpcDn = GpLinkValue.GpoDn(id, _builder.DomainDn);
        _sysvol.AddGpoFolder(id, version: 3);
        Session.SetValues(gpcDn, "versionNumber", "7");
        var plan = new ChangePlan
        {
            Server = Session.Info.Server,
            Sequential = true,
            Objects =
            [
                new ObjectPlan
                {
                    DistinguishedName = gpcDn,
                    DisplayName = "Konflikt wersji",
                    Changes =
                    [
                        new PlannedChange
                        {
                            Action = ChangeAction.WriteGpoFile,
                            Sysvol = new SysvolChange { GpoId = id, RelativePath = "Machine/Registry.pol", NewContent = Pol },
                        },
                        new PlannedChange
                        {
                            Action = ChangeAction.SetGpoVersion,
                            GpoVersion = new GpoVersionChange(new GpoVersion(0, 3), new GpoVersion(0, 3), new GpoVersion(0, 4)),
                        },
                    ],
                },
            ],
        };

        var result = await ExecuteAsync(plan);

        var failed = Assert.Single(result.Objects[0].Changes);
        Assert.StartsWith(BatchExecutor.ConflictMessagePrefix, failed.ErrorMessage);
        Assert.Null(_sysvol.Get(id, "Machine/Registry.pol"));
        Assert.DoesNotContain(_sysvol.Files(id), GpoTransaction.IsTempName);
    }

    [Fact]
    public void PowerShell_UsesTheSemanticCmdlets_AndRequiresTheGroupPolicyModule()
    {
        var plan = NewGpoPlan(new Guid("5D2F1A6B-0C3E-4E8B-9A7D-1B2C3D4E5F60"), "Stacje robocze – „Zasady” O'Brien");

        var script = new PowerShellExporter().Export(plan, "dc01.contoso.test");

        Assert.Contains("#Requires -Modules GroupPolicy", script);
        Assert.Contains("New-GPO -Name 'Stacje robocze – „Zasady” O''Brien' -Server $server", script);
        Assert.Contains($"New-GPLink -Guid '5d2f1a6b-0c3e-4e8b-9a7d-1b2c3d4e5f60' -Target '{_company.ComputersOu}' -LinkEnabled 'Yes' -Server $server", script);
        Assert.Contains("# Covered by the command of an earlier object.", script);
        Assert.DoesNotContain("New-ADObject", script);
        Assert.DoesNotContain("PReg", script);
    }

    [Fact]
    public async Task PowerShell_OfARollback_WritesSysvolStepsAsCommentsWithoutContent()
    {
        var id = _builder.AddGroupPolicyObject("Komentarze", attributes: new Dictionary<string, object?> { ["versionNumber"] = 0 });
        _sysvol.AddGpoFolder(id);
        var plan = new ChangePlan
        {
            Server = Session.Info.Server,
            Sequential = true,
            Objects =
            [
                new ObjectPlan
                {
                    DistinguishedName = GpLinkValue.GpoDn(id, _builder.DomainDn),
                    DisplayName = "Komentarze",
                    Changes =
                    [
                        new PlannedChange { Action = ChangeAction.WriteGpoFile, Sysvol = new SysvolChange { GpoId = id, RelativePath = "Machine/Registry.pol", NewContent = Pol } },
                        new PlannedChange { Action = ChangeAction.SetGpoVersion, GpoVersion = new GpoVersionChange(GpoVersion.Zero, GpoVersion.Zero, new GpoVersion(0, 1)) },
                    ],
                },
            ],
        };
        var result = await ExecuteAsync(plan);

        var script = new PowerShellExporter().Export(new RollbackPlanner().CreateRollbackPlan(result), null);

        Assert.Contains("# SYSVOL: delete Machine/Registry.pol", script);
        Assert.Contains("# SYSVOL: GPO version", script);
        Assert.DoesNotContain("PReg", script);
        Assert.DoesNotContain("#Requires -Modules GroupPolicy", script);
    }

    [Fact]
    public void PlannedChange_ToString_PrintsPathsAndSizes_NeverContent()
    {
        var change = new PlannedChange
        {
            Action = ChangeAction.WriteGpoFile,
            Sysvol = new SysvolChange { GpoId = Guid.Empty, RelativePath = "Machine/Registry.pol", OldContent = [1, 2], NewContent = Pol },
        };

        var text = change.ToString();

        Assert.Contains("Machine/Registry.pol 2 B -> ", text);
        Assert.DoesNotContain("PReg", text);
    }
}
