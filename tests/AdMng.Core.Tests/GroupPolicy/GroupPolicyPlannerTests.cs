using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.Security;
using AdMng.Testing;

namespace AdMng.Core.Tests.GroupPolicy;

/// <summary>Every GPO operation of K2: the plan's steps, its PowerShell and its execution on the fake (design note 5.3).</summary>
public class GroupPolicyPlannerTests : GroupPolicyTestBed
{
    private const string PolishName = "Stacje robocze – „Zasady” Łukasza O'Briena";

    [Fact]
    public async Task Create_PlansContainerFolderChildrenAndLink_AndExecutes()
    {
        var id = Guid.NewGuid();
        var plan = await Planner.PlanCreateAsync(Session, PolishName, Company.ComputersOu, Ct, id);

        Assert.True(plan.Sequential);
        Assert.Equal(Session.Info.Server, plan.Server);
        Assert.Equal([GpoDn(id), "CN=Machine," + GpoDn(id), "CN=User," + GpoDn(id), Company.ComputersOu], plan.Objects.Select(o => o.DistinguishedName));
        Assert.Equal([ChangeAction.Create, ChangeAction.CreateGpoFolder, ChangeAction.SyncGpoFolderSecurity], plan.Objects[0].Changes.Select(c => c.Action));
        var script = Script(plan);
        Assert.Contains("#Requires -Modules GroupPolicy", script);
        Assert.Contains("New-GPO -Name 'Stacje robocze – „Zasady” Łukasza O''Briena' -Domain 'contoso.test' -Server $server", script);
        Assert.Contains($"New-GPLink -Name 'Stacje robocze – „Zasady” Łukasza O''Briena' -Target '{Company.ComputersOu}' -LinkEnabled 'Yes'", script);

        await ExecuteOkAsync(plan);

        Assert.Equal(PolishName, Value(GpoDn(id), Attr.DisplayName));
        Assert.Equal("0", Value(GpoDn(id), "versionNumber"));
        Assert.Equal("2", Value(GpoDn(id), "gPCFunctionalityVersion"));
        Assert.Equal(@"\\contoso.test\SysVol\contoso.test\Policies\" + SysvolPaths.FolderName(id), Value(GpoDn(id), "gPCFileSysPath"));
        Assert.True(Session.Exists("CN=Machine," + GpoDn(id)));
        Assert.True(Sysvol.FolderExists(id));
        Assert.Equal("[General]\r\nVersion=0\r\n", Sysvol.GetText(id, "GPT.INI"));
        Assert.True(GpoFolderAcl.IsConsistent(Sysvol.FolderSecurity(id), Session.Snapshot(GpoDn(id))!.GetBytes(Attr.NtSecurityDescriptor)));
        Assert.Equal(3, GpLinkValue.Parse(Value(Company.ComputersOu, Attr.GpLink), Builder.DomainDn).Find(id)!.Order);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Default Domain Policy")]
    [InlineData("stacje robocze – zasady haseł")]
    public async Task Create_RefusesEmptyAndDuplicateNames(string name)
    {
        var plan = await Planner.PlanCreateAsync(Session, name, null, Ct);

        Assert.True(plan.Objects[0].HasErrors);
        Assert.Contains(plan.Objects[0].Changes[0].Issues, i => i.Code is IssueCodes.TooShort or IssueCodes.Duplicate);
    }

    [Fact]
    public async Task Create_WithADuplicateName_RunsNothing_NeitherChildrenNorTheLink()
    {
        var id = Guid.NewGuid();
        var linksBefore = Value(Company.ComputersOu, Attr.GpLink);
        var plan = await Planner.PlanCreateAsync(Session, "Stacje robocze – Zasady haseł", Company.ComputersOu, Ct, id);

        Assert.True(plan.IsBlocked);
        Assert.Equal(0, plan.ExecutableCount);
        var result = await ExecuteAsync(plan);

        Assert.All(result.Objects, o => Assert.Equal(ObjectResultStatus.Skipped, o.Status));
        Assert.False(Session.Exists("CN=Machine," + GpoDn(id)));
        Assert.Equal(linksBefore, Value(Company.ComputersOu, Attr.GpLink));
    }

    [Fact]
    public async Task Copy_TakesFilesStatusWmiFilterAndVersionOne_WithANewGuid()
    {
        var id = Guid.NewGuid();
        await using var sysvol = await OpenSysvolAsync();
        var plan = await Planner.PlanCopyAsync(Session, sysvol, Sample.WorkstationPasswords, "Kopia – Zasady haseł", copyPermissions: false, Ct, id);

        Assert.Contains("Copy-GPO -SourceGuid", Script(plan));
        Assert.DoesNotContain(plan.Objects[0].Changes, c => c.Action == ChangeAction.ModifySecurity);
        await ExecuteOkAsync(plan);

        Assert.Equal(Sysvol.Get(Sample.WorkstationPasswords, SecurityTemplate.RelativePath), Sysvol.Get(id, SecurityTemplate.RelativePath));
        Assert.Equal(Sysvol.Get(Sample.WorkstationPasswords, "GPO.cmt"), Sysvol.Get(id, "GPO.cmt"));
        Assert.Equal("[General]\r\nVersion=1\r\n", Sysvol.GetText(id, "GPT.INI"));
        Assert.Equal("1", Value(GpoDn(id), "versionNumber"));
        Assert.Equal(Value(GpoDn(Sample.WorkstationPasswords), "gPCWQLFilter"), Value(GpoDn(id), "gPCWQLFilter"));
        Assert.Equal(Value(GpoDn(Sample.WorkstationPasswords), "gPCMachineExtensionNames"), Value(GpoDn(id), "gPCMachineExtensionNames"));
    }

    [Fact]
    public async Task Copy_OfAGpoWithSettingsKeptInTheDirectory_WarnsThatTheyAreNotCopied()
    {
        await using var sysvol = await OpenSysvolAsync();
        var plain = await Planner.PlanCopyAsync(Session, sysvol, Sample.WorkstationPasswords, "Kopia bez obiektów", copyPermissions: false, Ct);
        Assert.DoesNotContain(plain.Objects[0].Changes.SelectMany(c => c.Issues), i => i.Code == IssueCodes.GpoDirectorySettings);
        Session.Seed("CN=Class Store,CN=Machine," + GpoDn(Sample.WorkstationPasswords), new Dictionary<string, object?> { [Attr.ObjectClass] = new[] { "top", "container" } });

        var plan = await Planner.PlanCopyAsync(Session, sysvol, Sample.WorkstationPasswords, "Kopia – instalacja oprogramowania", copyPermissions: false, Ct);

        var issue = Assert.Single(plan.Objects[0].Changes.SelectMany(c => c.Issues), i => i.Code == IssueCodes.GpoDirectorySettings);
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
    }

    [Fact]
    public async Task Copy_WithPermissions_AddsTheSourcesExplicitAces()
    {
        var id = Guid.NewGuid();
        await using var sysvol = await OpenSysvolAsync();
        var plan = await Planner.PlanCopyAsync(Session, sysvol, Sample.WorkstationPasswords, "Kopia z uprawnieniami", copyPermissions: true, Ct, id);

        Assert.Contains("-CopyAcl", Script(plan));
        await ExecuteOkAsync(plan);

        var permissions = GpoPermissionLevels.Read(Session.Snapshot(GpoDn(id))!.GetBytes(Attr.NtSecurityDescriptor));
        Assert.Contains(permissions, p => p.Sid == Builder.SidOf(Company.LukaszWisniewski) && p.Level == GpoPermissionLevel.Edit);
    }

    [Fact]
    public async Task Copy_WithPermissions_OfAFilteredGpo_KeepsItsSecurityFiltering()
    {
        // "Stacje robocze – Pulpit zdalny" applies to GG_IT only: Authenticated Users may read it but not apply it.
        var id = Guid.NewGuid();
        await using var sysvol = await OpenSysvolAsync();
        var plan = await Planner.PlanCopyAsync(Session, sysvol, Sample.WorkstationRemoteDesktop, "Kopia – Pulpit zdalny (GG_IT)", copyPermissions: true, Ct, id);

        var security = Assert.Single(plan.Objects[0].Changes, c => c.Action == ChangeAction.ModifySecurity);
        Assert.True(security.Acl!.ReplacesExplicit);
        Assert.Contains(security.Issues, i => i.Code == IssueCodes.GpoPermissionsReplaced);
        await ExecuteOkAsync(plan);

        var copied = Session.Snapshot(GpoDn(id))!.GetBytes(Attr.NtSecurityDescriptor)!;
        var source = Session.Snapshot(GpoDn(Sample.WorkstationRemoteDesktop))!.GetBytes(Attr.NtSecurityDescriptor)!;
        var permissions = GpoPermissionLevels.Read(copied);
        Assert.Equal(GpoPermissionLevel.Read, Assert.Single(permissions, p => p.Sid == GpoPermissionLevels.AuthenticatedUsersSid).Level);
        Assert.Equal(GpoPermissionLevel.Apply, Assert.Single(permissions, p => p.Sid == Builder.SidOf(Company.GroupIt)).Level);
        Assert.True(DaclEditor.TryReadExplicit(copied, out var copiedAces));
        Assert.True(DaclEditor.TryReadExplicit(source, out var sourceAces));
        Assert.Equal(sourceAces.Count, copiedAces.Count);
        Assert.All(sourceAces, a => Assert.Contains(copiedAces, c => c.SameAce(a)));
        Assert.True(GpoFolderAcl.IsConsistent(Sysvol.FolderSecurity(id), copied));
        var audit = Assert.Single(Audit.Entries, e => e.Action == nameof(ChangeAction.ModifySecurity) && e.DistinguishedName == GpoDn(id));
        Assert.Contains(GpoPermissionLevels.AuthenticatedUsersSid, audit.OldValue ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rename_IsACompareAndSwapOfDisplayName_AndLeavesGptIniAlone()
    {
        var gptBefore = Sysvol.Get(Sample.ServerSecurity, "GPT.INI");
        var plan = await Planner.PlanRenameAsync(Session, Sample.ServerSecurity, "Serwery – Zabezpieczenia (2026)", Ct);

        var change = Assert.Single(Assert.Single(plan.Objects).Changes);
        Assert.Equal(["Serwery – Zabezpieczenia"], change.Attributes[0].OldValues);
        Assert.Contains($"Rename-GPO -Guid '{Sample.ServerSecurity:D}' -TargetName 'Serwery – Zabezpieczenia (2026)'", Script(plan));
        await ExecuteOkAsync(plan);

        Assert.Equal("Serwery – Zabezpieczenia (2026)", Value(GpoDn(Sample.ServerSecurity), Attr.DisplayName));
        Assert.Equal(gptBefore, Sysvol.Get(Sample.ServerSecurity, "GPT.INI"));
    }

    [Fact]
    public async Task Delete_RemovesLinksThenContainerAndFolder()
    {
        var plan = await Planner.PlanDeleteAsync(Session, Sample.WorkstationRemoteDesktop, Ct);

        Assert.Equal([Company.ComputersOu, GpoDn(Sample.WorkstationRemoteDesktop)], plan.Objects.Select(o => o.DistinguishedName));
        Assert.Equal([ChangeAction.Delete, ChangeAction.DeleteGpoFolder], plan.Objects[^1].Changes.Select(c => c.Action));
        Assert.Contains($"Remove-GPO -Guid '{Sample.WorkstationRemoteDesktop:D}'", Script(plan));
        await ExecuteOkAsync(plan);

        Assert.False(Session.Exists(GpoDn(Sample.WorkstationRemoteDesktop)));
        Assert.False(Sysvol.FolderExists(Sample.WorkstationRemoteDesktop));
        var links = GpLinkValue.Parse(Value(Company.ComputersOu, Attr.GpLink), Builder.DomainDn);
        Assert.False(links.Contains(Sample.WorkstationRemoteDesktop));
        Assert.True(links.Contains(Sample.WorkstationPasswords));
    }

    [Fact]
    public async Task Delete_OfAGpoWhoseFolderIsAlreadyGone_Succeeds()
    {
        var plan = await Planner.PlanDeleteAsync(Session, Sample.WorkstationRemoteDesktop, Ct);
        await using (var sysvol = await OpenSysvolAsync())
        {
            await sysvol.DeleteGpoFolderAsync(Sample.WorkstationRemoteDesktop, Ct);
        }

        var result = await ExecuteOkAsync(plan);

        Assert.All(result.Objects, o => Assert.Equal(ObjectResultStatus.Succeeded, o.Status));
        Assert.False(Session.Exists(GpoDn(Sample.WorkstationRemoteDesktop)));
    }

    [Fact]
    public async Task DeleteOrphanedFolder_OfALiveGpo_IsRefused_AndGuardedAtExecution()
    {
        var refused = await Planner.PlanDeleteOrphanedFolderAsync(Session, Sample.WorkstationRemoteDesktop, "Stacje robocze – Pulpit zdalny", Ct);
        Assert.Contains(refused.Objects[0].Changes[0].Issues, i => i.Code == IssueCodes.GpoExists && i.Severity == IssueSeverity.Error);

        // Planned while the container was missing (replication lag), the GPO is back with the same GUID before it runs.
        var dn = GpoDn(Sample.WorkstationRemoteDesktop);
        var plan = await Planner.PlanDeleteOrphanedFolderAsync(Session, Guid.NewGuid(), "Pozostałość", Ct);
        var stale = plan with { Objects = [plan.Objects[0] with { DistinguishedName = dn, Changes = [plan.Objects[0].Changes[0] with
        {
            Sysvol = plan.Objects[0].Changes[0].Sysvol! with { GpoId = Sample.WorkstationRemoteDesktop },
        }] }] };
        Assert.Equal(0, stale.ErrorCount);

        var result = await ExecuteAsync(stale);

        Assert.False(result.Objects[0].Changes[0].Succeeded);
        Assert.True(Sysvol.FolderExists(Sample.WorkstationRemoteDesktop));
    }

    [Fact]
    public async Task DeleteOrphanedFolder_OfADeletedGpo_DeletesTheFolder()
    {
        var id = Sample.WorkstationRemoteDesktop;
        await Session.DeleteAsync(GpoDn(id), subtree: true, Ct);

        await ExecuteOkAsync(await Planner.PlanDeleteOrphanedFolderAsync(Session, id, "Stacje robocze – Pulpit zdalny", Ct));

        Assert.False(Sysvol.FolderExists(id));
    }

    [Fact]
    public async Task Delete_OfASiteLinkedGpo_RemovesTheSiteLinkWithRemoveGpLink()
    {
        var plan = await Planner.PlanDeleteAsync(Session, Sample.SitePrinters, Ct);

        Assert.Contains($"Remove-GPLink -Guid '{Sample.SitePrinters:D}' -Target '{Sample.SiteDn}'", Script(plan));
        await ExecuteOkAsync(plan);
        Assert.Null(Value(Sample.SiteDn, Attr.GpLink));
    }

    [Fact]
    public async Task Delete_OfADefaultGpo_IsRefused()
    {
        var plan = await Planner.PlanDeleteAsync(Session, Sample.DefaultDomainPolicy, Ct);

        Assert.All(plan.Objects, o => Assert.True(o.HasErrors));
        Assert.Contains(plan.Objects[^1].Changes[0].Issues, i => i.Code == IssueCodes.DefaultGpo);
        Assert.Equal(0, plan.ExecutableCount);
    }

    [Fact]
    public async Task Link_Unlink_Reorder_Enforce_Enable_OnTheWholeGpLinkValue()
    {
        var ou = Company.ItOu;
        await ExecuteOkAsync(await Planner.PlanLinkAsync(Session, ou, Sample.ServerSecurity, Ct));
        await ExecuteOkAsync(await Planner.PlanLinkAsync(Session, ou, Sample.WorkstationPasswords, Ct, enforced: true));
        var links = GpLinkValue.Parse(Value(ou, Attr.GpLink), Builder.DomainDn);
        Assert.Equal([(Sample.ServerSecurity, 1), (Sample.WorkstationPasswords, 2)], links.Links.Select(l => (l.GpoId, l.Order)).Reverse());
        Assert.Equal(1, links.Find(Sample.ServerSecurity)!.Order);

        var reorder = await Planner.PlanMoveLinkAsync(Session, ou, Sample.WorkstationPasswords, 1, Ct);
        Assert.Contains("Set-GPLink", Script(reorder));
        Assert.Contains("-Order 1", Script(reorder));
        await ExecuteOkAsync(reorder);
        Assert.Equal(1, GpLinkValue.Parse(Value(ou, Attr.GpLink), Builder.DomainDn).Find(Sample.WorkstationPasswords)!.Order);

        await ExecuteOkAsync(await Planner.PlanLinkOptionsAsync(Session, ou, Sample.WorkstationPasswords, enforced: false, enabled: false, Ct));
        var link = GpLinkValue.Parse(Value(ou, Attr.GpLink), Builder.DomainDn).Find(Sample.WorkstationPasswords)!;
        Assert.False(link.Enforced);
        Assert.False(link.Enabled);

        await ExecuteOkAsync(await Planner.PlanUnlinkAsync(Session, ou, Sample.WorkstationPasswords, Ct));
        await ExecuteOkAsync(await Planner.PlanUnlinkAsync(Session, ou, Sample.ServerSecurity, Ct));
        Assert.Null(Value(ou, Attr.GpLink));
    }

    [Fact]
    public async Task Link_ChangedBehindThePlan_IsAConflict()
    {
        var plan = await Planner.PlanLinkAsync(Session, Company.ComputersOu, Sample.ServerSecurity, Ct);
        Session.SetValues(Company.ComputersOu, Attr.GpLink, Builder.GpLink(Sample.DriveMappings));

        var result = await ExecuteAsync(plan);

        Assert.StartsWith(BatchExecutor.ConflictMessagePrefix, Assert.Single(result.Objects[0].Changes).ErrorMessage);
    }

    [Fact]
    public async Task Unlink_OfAGpoThatIsNotLinked_IsAnError()
    {
        var plan = await Planner.PlanUnlinkAsync(Session, Company.ItOu, Sample.ServerSecurity, Ct);

        Assert.True(plan.Objects[0].HasErrors);
    }

    [Fact]
    public async Task BlockInheritance_SetsAndClearsGpOptions()
    {
        var block = await Planner.PlanBlockInheritanceAsync(Session, Company.ComputersOu, true, Ct);
        Assert.Contains("Set-GPInheritance", Script(block));
        Assert.Contains("-IsBlocked 'Yes'", Script(block));
        await ExecuteOkAsync(block);
        Assert.Equal("1", Value(Company.ComputersOu, "gPOptions"));

        await ExecuteOkAsync(await Planner.PlanBlockInheritanceAsync(Session, Company.ComputersOu, false, Ct));
        Assert.Null(Value(Company.ComputersOu, "gPOptions"));
        Assert.True((await Planner.PlanBlockInheritanceAsync(Session, Sample.SiteDn, true, Ct)).Objects[0].HasErrors);
    }

    [Fact]
    public async Task Status_WritesFlags_AndTheGpoStatusAssignment()
    {
        var plan = await Planner.PlanStatusAsync(Session, Sample.WorkstationPasswords, GpoStatus.UserSettingsDisabled, Ct);

        Assert.Contains($"(Get-GPO -Guid '{Sample.WorkstationPasswords:D}' -Domain 'contoso.test' -Server $server).GpoStatus = 'UserSettingsDisabled'", Script(plan));
        await ExecuteOkAsync(plan);
        Assert.Equal("1", Value(GpoDn(Sample.WorkstationPasswords), "flags"));
    }

    [Fact]
    public async Task WmiFilterLink_SetsAndClears()
    {
        var filters = await Reader.ReadWmiFiltersAsync(Session, Ct);
        var filter = Assert.Single(filters);

        await ExecuteOkAsync(await Planner.PlanWmiFilterLinkAsync(Session, Sample.ServerSecurity, filter, Ct));
        Assert.Equal(Builder.WmiFilterLinkValue(Sample.WmiFilterId), Value(GpoDn(Sample.ServerSecurity), "gPCWQLFilter"));

        await ExecuteOkAsync(await Planner.PlanWmiFilterLinkAsync(Session, Sample.ServerSecurity, null, Ct));
        Assert.Null(Value(GpoDn(Sample.ServerSecurity), "gPCWQLFilter"));
    }

    [Fact]
    public async Task WmiFilter_CreateEditDelete_ClearsTheLinksOfItsGpos()
    {
        Time.SetUtcNow(new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero));
        var create = await Planner.PlanCreateWmiFilterAsync(Session, "Windows 11 – stacje", "Tylko Windows 11 (kompilacja ≥ 22000)",
            [new WmiFilterQuery(@"root\CIMv2", "SELECT * FROM Win32_OperatingSystem WHERE BuildNumber >= 22000 AND ProductType = 1")], Ct);
        await ExecuteOkAsync(create);
        var created = (await Reader.ReadWmiFiltersAsync(Session, Ct)).Single(f => f.Name == "Windows 11 – stacje");
        Assert.Equal("20261027100000.000000-000", Value(created.Dn, "msWMI-CreationDate"));
        Assert.Equal(Session.Info.BoundIdentity, created.Author);
        Assert.Single(created.Queries);

        var edit = await Planner.PlanEditWmiFilterAsync(Session, created.Dn, "Windows 11 – stacje robocze", null,
            [new WmiFilterQuery(@"root\CIMv2", "SELECT * FROM Win32_OperatingSystem WHERE BuildNumber >= 26100")], Ct);
        await ExecuteOkAsync(edit);
        var edited = (await Reader.ReadWmiFiltersAsync(Session, Ct)).Single(f => f.Dn == created.Dn);
        Assert.Equal("Windows 11 – stacje robocze", edited.Name);
        Assert.Null(edited.Description);

        var delete = await Planner.PlanDeleteWmiFilterAsync(Session, Sample.WmiFilterDn, Ct);
        Assert.Equal([GpoDn(Sample.WorkstationPasswords), Sample.WmiFilterDn], delete.Objects.Select(o => o.DistinguishedName));
        await ExecuteOkAsync(delete);
        Assert.False(Session.Exists(Sample.WmiFilterDn));
        Assert.Null(Value(GpoDn(Sample.WorkstationPasswords), "gPCWQLFilter"));
    }

    [Fact]
    public async Task WmiFilter_RefusesDuplicateNamesAndEmptyQueries()
    {
        var plan = await Planner.PlanCreateWmiFilterAsync(Session, "tylko stacje robocze", null, [], Ct);

        var issues = plan.Objects[0].Changes[0].Issues.Select(i => i.Code).ToList();
        Assert.Contains(IssueCodes.Duplicate, issues);
        Assert.Contains(IssueCodes.MandatoryAttribute, issues);
    }

    [Fact]
    public async Task AlignVersions_RaisesBothHalvesAboveTheHigherOfAdAndGptIni()
    {
        await using var sysvol = await OpenSysvolAsync();
        var plan = await Planner.PlanAlignVersionsAsync(Session, sysvol, Sample.DriveMappings, Ct);

        var version = Assert.Single(plan.Objects[0].Changes).GpoVersion!;
        Assert.Equal(new GpoVersion(3, 1), version.New);
        await ExecuteOkAsync(plan);
        Assert.Equal(new GpoVersion(3, 1).ToInt32().ToString(System.Globalization.CultureInfo.InvariantCulture), Value(GpoDn(Sample.DriveMappings), "versionNumber"));
        Assert.Equal($"[General]\r\nVersion={new GpoVersion(3, 1).ToInt32()}\r\n", Sysvol.GetText(Sample.DriveMappings, "GPT.INI"));
    }

    [Fact]
    public async Task SecurityFilter_AddAndRemove_KeepsTheFolderDaclConsistent()
    {
        await using var sysvol = await OpenSysvolAsync();
        var helpdesk = Company.GroupSales;
        var sid = Builder.SidOf(helpdesk);

        var add = await Planner.PlanAddSecurityFilterAsync(Session, sysvol, Sample.WorkstationRemoteDesktop, sid, "CONTOSO\\GG_Sprzedaz", Ct);
        Assert.Equal([ChangeAction.ModifySecurity, ChangeAction.SyncGpoFolderSecurity], add.Objects[0].Changes.Select(c => c.Action));
        Assert.Contains("Set-GPPermission", Script(add));
        Assert.Contains("-PermissionLevel 'GpoApply'", Script(add));
        await ExecuteOkAsync(add);
        var descriptor = Session.Snapshot(GpoDn(Sample.WorkstationRemoteDesktop))!.GetBytes(Attr.NtSecurityDescriptor);
        Assert.Contains(GpoPermissionLevels.Read(descriptor), p => p.Sid == sid && p.Level == GpoPermissionLevel.Apply);
        Assert.True(GpoFolderAcl.IsConsistent(Sysvol.FolderSecurity(Sample.WorkstationRemoteDesktop), descriptor));

        await ExecuteOkAsync(await Planner.PlanRemoveSecurityFilterAsync(Session, sysvol, Sample.WorkstationRemoteDesktop, sid, null, Ct));
        descriptor = Session.Snapshot(GpoDn(Sample.WorkstationRemoteDesktop))!.GetBytes(Attr.NtSecurityDescriptor);
        Assert.DoesNotContain(GpoPermissionLevels.Read(descriptor), p => p.Sid == sid);
    }

    [Fact]
    public async Task SecurityFilter_RemovingAuthenticatedUsers_KeepsRead()
    {
        await using var sysvol = await OpenSysvolAsync();

        var plan = await Planner.PlanRemoveSecurityFilterAsync(Session, sysvol, Sample.WorkstationPasswords, "S-1-5-11", "NT AUTHORITY\\Authenticated Users", Ct);
        var acl = plan.Objects[0].Changes[0].Acl!;

        Assert.All(acl.Removed, a => Assert.Equal(GroupPolicySecurityFilter.ApplyGroupPolicyRight, a.ObjectType));
        Assert.DoesNotContain(plan.Objects[0].Changes[0].Issues, i => i.Code == IssueCodes.Ms16072Read);
        await ExecuteOkAsync(plan);
        var permissions = GpoPermissionLevels.Read(Session.Snapshot(GpoDn(Sample.WorkstationPasswords))!.GetBytes(Attr.NtSecurityDescriptor));
        Assert.Contains(permissions, p => p.Sid == "S-1-5-11" && p.Level == GpoPermissionLevel.Read);
    }

    [Fact]
    public async Task Delegation_RemovingTheLastComputerReader_WarnsAboutMs16072()
    {
        await using var sysvol = await OpenSysvolAsync();

        var plan = await Planner.PlanRemoveDelegationAsync(Session, sysvol, Sample.WorkstationRemoteDesktop, "S-1-5-11", null, Ct);

        Assert.Contains(plan.Objects[0].Changes[0].Issues, i => i.Code == IssueCodes.Ms16072Read);
    }

    [Fact]
    public async Task Delegation_SetLevel_ReplacesTheTrusteesAces()
    {
        await using var sysvol = await OpenSysvolAsync();
        var sid = Builder.SidOf(Company.LukaszWisniewski);

        var plan = await Planner.PlanSetDelegationAsync(Session, sysvol, Sample.WorkstationPasswords, sid, "CONTOSO\\lukasz.wisniewski",
            GpoPermissionLevel.EditDeleteModifySecurity, Ct);
        Assert.Contains("-PermissionLevel 'GpoEditDeleteModifySecurity' -Replace", Script(plan));
        await ExecuteOkAsync(plan);

        var permissions = GpoPermissionLevels.Read(Session.Snapshot(GpoDn(Sample.WorkstationPasswords))!.GetBytes(Attr.NtSecurityDescriptor));
        Assert.Contains(permissions, p => p.Sid == sid && p.Level == GpoPermissionLevel.EditDeleteModifySecurity);

        await ExecuteOkAsync(await Planner.PlanRemoveDelegationAsync(Session, sysvol, Sample.WorkstationPasswords, sid, null, Ct));
        permissions = GpoPermissionLevels.Read(Session.Snapshot(GpoDn(Sample.WorkstationPasswords))!.GetBytes(Attr.NtSecurityDescriptor));
        Assert.DoesNotContain(permissions, p => p.Sid == sid);
    }

    [Fact]
    public async Task Delegation_SetLevel_KeepsTheTrusteesSecurityFiltering()
    {
        // GG_IT is in the security filtering of "Stacje robocze – Pulpit zdalny" (Read + Apply).
        await using var sysvol = await OpenSysvolAsync();
        var sid = Builder.SidOf(Company.GroupIt);

        await ExecuteOkAsync(await Planner.PlanSetDelegationAsync(Session, sysvol, Sample.WorkstationRemoteDesktop, sid, "CONTOSO\\GG_IT", GpoPermissionLevel.Edit, Ct));

        var trustee = Assert.Single(GpoPermissionLevels.Read(Session.Snapshot(GpoDn(Sample.WorkstationRemoteDesktop))!.GetBytes(Attr.NtSecurityDescriptor)), p => p.Sid == sid);
        Assert.Equal(GpoPermissionLevel.Edit, trustee.Level);
        Assert.True(trustee.Applies);
    }

    [Fact]
    public async Task Rollback_OfALinkChange_RestoresTheValue()
    {
        var before = Value(Company.ComputersOu, Attr.GpLink);
        var result = await ExecuteOkAsync(await Planner.PlanMoveLinkAsync(Session, Company.ComputersOu, Sample.WorkstationRemoteDesktop, 1, Ct));

        await ExecuteOkAsync(new RollbackPlanner().CreateRollbackPlan(result));

        Assert.Equal(before, Value(Company.ComputersOu, Attr.GpLink));
    }

    [Fact]
    public async Task Rollback_OfAGpoDelete_DoesNotPutBackLinksToTheDeletedGpo()
    {
        var result = await ExecuteOkAsync(await Planner.PlanDeleteAsync(Session, Sample.WorkstationRemoteDesktop, Ct));
        var linksAfterDelete = Value(Company.ComputersOu, Attr.GpLink);

        var rollback = new RollbackPlanner().CreateRollbackPlan(result);

        Assert.All(rollback.Objects, o => Assert.DoesNotContain(o.Changes, c => c.Attributes.Any(a => a.Attribute == Attr.GpLink)));
        Assert.Contains(rollback.Objects.SelectMany(o => o.Issues), i => i.Code == IssueCodes.NotReversible && i.Attribute == Attr.GpLink);
        Assert.Equal(0, rollback.ExecutableCount);
        await ExecuteAsync(rollback);
        Assert.Equal(linksAfterDelete, Value(Company.ComputersOu, Attr.GpLink));
    }

    [Fact]
    public async Task Rollback_OfAGpoDeleteWhoseContainerDeleteFailed_PutsTheLinksBack()
    {
        var before = Value(Company.ComputersOu, Attr.GpLink);
        var plan = await Planner.PlanDeleteAsync(Session, Sample.WorkstationRemoteDesktop, Ct);
        Session.BeforeOperation = op =>
        {
            if (op.Kind == FakeOperationKind.Delete && op.DistinguishedName == GpoDn(Sample.WorkstationRemoteDesktop))
            {
                throw new AdMng.Core.Directory.DirectoryException(AdMng.Core.Directory.DirectoryErrorKind.AccessDenied, "Brak uprawnień.");
            }
        };
        var result = await ExecuteAsync(plan);
        Session.BeforeOperation = null;
        Assert.True(Session.Exists(GpoDn(Sample.WorkstationRemoteDesktop)));

        await ExecuteOkAsync(new RollbackPlanner().CreateRollbackPlan(result));

        Assert.Equal(before, Value(Company.ComputersOu, Attr.GpLink));
    }

    [Fact]
    public async Task Rollback_OfAWmiFilterDelete_DoesNotPutBackTheFilterLink()
    {
        var result = await ExecuteOkAsync(await Planner.PlanDeleteWmiFilterAsync(Session, Sample.WmiFilterDn, Ct));

        var rollback = new RollbackPlanner().CreateRollbackPlan(result);

        Assert.All(rollback.Objects, o => Assert.DoesNotContain(o.Changes, c => c.Attributes.Any(a => a.Attribute == "gPCWQLFilter")));
        Assert.Contains(rollback.Objects.SelectMany(o => o.Issues), i => i.Code == IssueCodes.NotReversible && i.Attribute == "gPCWQLFilter");
        Assert.Null(Value(GpoDn(Sample.WorkstationPasswords), "gPCWQLFilter"));
    }

    [Fact]
    public async Task Rollback_OfACreate_RemovesTheNewGpoAgain()
    {
        var id = Guid.NewGuid();
        var result = await ExecuteOkAsync(await Planner.PlanCreateAsync(Session, "Do cofnięcia – Zażółć gęślą jaźń", Company.ComputersOu, Ct, id));

        await ExecuteOkAsync(new RollbackPlanner().CreateRollbackPlan(result));

        Assert.False(Session.Exists(GpoDn(id)));
        Assert.False(Sysvol.FolderExists(id));
        Assert.False(GpLinkValue.Parse(Value(Company.ComputersOu, Attr.GpLink), Builder.DomainDn).Contains(id));
    }

    [Fact]
    public async Task EveryGpoPlan_GoesToTheSessionsDc()
    {
        var result = await ExecuteOkAsync(await Planner.PlanCreateAsync(Session, "Na kontrolerze sesji", null, Ct));

        Assert.All(Sysvol.Calls, c => Assert.Equal(Session.Info.Server, c.Server));
        Assert.Equal(Session.Info.Server, result.Server);
    }

    [Fact]
    public async Task Reader_ReadsVersionsLinksInheritanceAndPdc()
    {
        var policies = await Reader.ReadPoliciesAsync(Session, Ct);
        await using var sysvol = await OpenSysvolAsync();
        var withVersions = await Reader.WithSysvolVersionsAsync(sysvol, policies, Ct);
        var drives = withVersions.Single(p => p.Id == Sample.DriveMappings);
        Assert.True(drives.VersionMismatch);
        Assert.Equal(new GpoVersion(1, 0), drives.SysvolVersion);
        Assert.Equal(GpoStatus.UserSettingsDisabled, withVersions.Single(p => p.Id == Sample.ServerSecurity).Status);
        Assert.Equal(Sample.WmiFilterId, withVersions.Single(p => p.Id == Sample.WorkstationPasswords).WmiFilterId);

        var containers = await Reader.ReadLinkingContainersAsync(Session, Ct);
        var byGpo = GroupPolicyReader.LinksByGpo(containers);
        Assert.Contains(byGpo[Sample.SitePrinters], l => l.Kind == GroupPolicySomKind.Site && l.ContainerName == "Warszawa");
        Assert.True(containers.Single(c => c.Dn == Company.EmployeesOu).BlocksInheritance);

        var computers = containers.Single(c => c.Dn == Company.ComputersOu);
        var inherited = GroupPolicyReader.Inheritance(Builder.DomainDn, containers, computers);
        Assert.Equal([Sample.DefaultDomainPolicy, Sample.WorkstationPasswords, Sample.ServerSecurity], inherited.Select(a => a.GpoId));

        var links = await Reader.ReadLinksOfAsync(Session, Sample.WorkstationRemoteDesktop, Ct);
        var link = Assert.Single(links);
        Assert.False(link.Enabled);
        Assert.Equal(2, link.Order);
    }

    [Fact]
    public async Task Reader_SettingsOfAGpo_UseTheSharedParsers()
    {
        await using var sysvol = await OpenSysvolAsync();
        var content = await GroupPolicyReader.ReadFolderAsync(sysvol, Sample.WorkstationPasswords, Ct);

        var settings = GroupPolicyReader.Settings(content);

        Assert.Contains(settings, s => s.Key.Contains("MinimumPasswordLength", StringComparison.Ordinal) && s.Value == "12");
        Assert.Contains(settings, s => s.Key.StartsWith("Comment", StringComparison.OrdinalIgnoreCase) || s.Value.Contains("Łukaszem", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadFolder_RefusesAGpoBeyondTheLimits()
    {
        Sysvol.Put(Sample.ServerSecurity, "Machine/huge.bin", new byte[GpoTransaction.MaxFileBytes + 1]);
        await using var sysvol = await OpenSysvolAsync();

        await Assert.ThrowsAsync<GpoTooLargeException>(() => GroupPolicyReader.ReadFolderAsync(sysvol, Sample.ServerSecurity, Ct));
    }

    [Fact]
    public async Task Leftovers_AreDeletedByTheNextContentWrite()
    {
        Sysvol.PutText(Sample.ServerSecurity, "Machine/Registry.pol.admng-0123456789abcdef0123456789abcdef.tmp", "pozostałość");
        await using var sysvol = await OpenSysvolAsync();
        var backup = await Planner.ReadBackupAsync(Session, sysvol, Sample.ServerSecurity, "przed", Ct);

        var plan = await Planner.PlanImportAsync(Session, sysvol, Sample.ServerSecurity, backup with { Files = new Dictionary<string, byte[]>(backup.Files) { ["Machine/new.txt"] = Encoding.UTF8.GetBytes("nowy") } }, "C:\\Kopie", Ct);

        Assert.Contains(plan.Objects[0].Changes, c => c.Action == ChangeAction.DeleteGpoFile && c.Issues.Any(i => i.Code == IssueCodes.SysvolLeftovers));
        await ExecuteOkAsync(plan);
        Assert.DoesNotContain(Sysvol.Files(Sample.ServerSecurity), GpoTransaction.IsTempName);
    }
}
