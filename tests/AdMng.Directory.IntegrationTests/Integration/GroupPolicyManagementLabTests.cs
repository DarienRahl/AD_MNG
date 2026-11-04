using System.Globalization;
using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Audit;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Backup;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.Ldap;
using AdMng.Directory.IntegrationTests.Infrastructure;

namespace AdMng.Directory.IntegrationTests.Integration;

/// <summary>
/// Group Policy management (K2) against the Samba lab through the change pipeline with SYSVOL writes over smbclient
/// (<see cref="SmbClientSysvolFileWriter"/>). The lab is shared: every GPO is named <c>K2T-&lt;run&gt; …</c> and linked only
/// to this run's <c>OU=K2T-&lt;run&gt;</c> below the run OU; the GPOs and WMI filters are deleted at the end (through the
/// pipeline, <c>samba-tool gpo del</c> as the fallback). Backups go to a temporary folder of the test (the operator's
/// chosen folder in the application), removed afterwards. In the <see cref="SambaToolCollection"/>: it writes
/// security descriptors of GPOs and calls samba-tool.
/// </summary>
[Collection(SambaToolCollection.Name)]
public sealed class GroupPolicyManagementLabTests(LabFixture lab)
{
    private static readonly BatchOptions Options = new() { InitialRetryDelay = TimeSpan.FromMilliseconds(50) };

    private readonly GroupPolicyPlanner _planner = new(new GroupPolicyReader(), TimeProvider.System);
    private readonly SmbClientSysvolFileWriter _writer = new(SambaTools.Address, LabSettings.AdminUser, LabSettings.AdminPassword);
    private readonly InMemoryAuditLog _audit = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GroupPolicyReader Reader => _planner.Reader;

    [Fact]
    public async Task CreateLinkCopyRenameStatusBackupRestoreAndDelete_ThroughThePipeline()
    {
        SambaTools.Require();
        var admin = lab.Admin;
        var run = Guid.NewGuid().ToString("N")[..6];
        var ou = await lab.CreateOuAsync($"K2T-{run}");
        var stations = await lab.CreateOuAsync("Stacje – Łódź", ou);
        using var tools = SambaTools.Create();
        var source = Guid.NewGuid();
        var copy = Guid.NewGuid();
        var created = new List<Guid>();
        await using var sysvol = await _writer.OpenForWriteAsync(admin, Ct);
        try
        {
            // Create with a link: container, CN=Machine/CN=User, folder with GPT.INI, folder DACL from the container's.
            var create = await _planner.PlanCreateAsync(admin, $"K2T-{run} Stacje – Zasady haseł", ou, Ct, source);
            created.Add(source);
            await ExecuteOkAsync(create);

            var gpo = await Reader.ReadPolicyAsync(admin, source, Ct);
            Assert.NotNull(gpo);
            Assert.Equal($"K2T-{run} Stacje – Zasady haseł", gpo!.DisplayName);
            Assert.Equal(GpoVersion.Zero, gpo.DirectoryVersion);
            Assert.Equal(GpoStatus.AllSettingsEnabled, gpo.Status);
            Assert.EndsWith(@"\Policies\" + SysvolPaths.FolderName(source), gpo.FileSysPath, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(await admin.GetAsync(DistinguishedName.Parse(gpo.Dn).Child("CN", "Machine").ToString(), [Attr.ObjectClass], Ct));
            Assert.NotNull(await admin.GetAsync(DistinguishedName.Parse(gpo.Dn).Child("CN", "User").ToString(), [Attr.ObjectClass], Ct));
            Assert.Equal(GpoVersion.Zero, await GroupPolicyPlanner.GptVersionAsync(sysvol, source, Ct));
            var entries = await sysvol.ListGpoFolderAsync(source, SysvolListOptions.Default, Ct);
            Assert.Contains(entries, e => e.IsFolder && e.RelativePath == "Machine");
            Assert.Contains(entries, e => e.IsFolder && e.RelativePath == "User");
            Assert.True(GpoFolderAcl.IsConsistent(await sysvol.GetGpoFolderSecurityAsync(source, Ct), gpo.SecurityDescriptor),
                "The folder's DACL follows the container's (dsacl2fsacl).");
            Assert.True((await Reader.ReadContainerAsync(admin, ou, Ct))!.Links.Contains(source));
            Assert.All(_writer.OpenedOn, server => Assert.Equal(admin.Info.Server, server, ignoreCase: true));

            // samba-tool reads the new GPO and its link like its own.
            var show = await tools.GpoQueryAsync("show", SysvolPaths.FolderName(source), Ct);
            Assert.Contains($"K2T-{run} Stacje – Zasady haseł", show, StringComparison.Ordinal);
            Assert.Contains(SysvolPaths.FolderName(source), await tools.GpoQueryAsync("getlink", ou, Ct), StringComparison.OrdinalIgnoreCase);

            // Samba's own reader sees the link (U4c topology, independent parser).
            var topology = await GroupPolicyTopology.ReadAsync(admin, 500, Ct);
            Assert.Equal($"K2T-{run} Stacje – Zasady haseł", topology.Policies[source].DisplayName);
            Assert.Equal([source], Assert.Single(topology.Containers, c => DistinguishedName.AreEqual(c.Dn, ou)).Links.Select(l => l.GpoId!.Value));

            // Settings written by the test (as a GPO editor would), then a copy takes them with a new GUID and version 1.
            await tools.PutAsync(source, SecurityTemplatePath, SecurityTemplate(12), Ct);
            var copyPlan = await _planner.PlanCopyAsync(admin, sysvol, source, $"K2T-{run} Stacje – Kopia", copyPermissions: false, Ct, copy);
            created.Add(copy);
            await ExecuteOkAsync(copyPlan);
            Assert.Equal(SecurityTemplate(12), await ReadAsync(sysvol, copy, SecurityTemplatePath));
            Assert.Equal(new GpoVersion(0, 1), await GroupPolicyPlanner.GptVersionAsync(sysvol, copy, Ct));
            var copied = await Reader.ReadPolicyAsync(admin, copy, Ct);
            Assert.Equal(new GpoVersion(0, 1), copied!.DirectoryVersion);
            Assert.True(GpoFolderAcl.IsConsistent(await sysvol.GetGpoFolderSecurityAsync(copy, Ct), copied.SecurityDescriptor));
            await AssertNoTempFilesAsync(sysvol, copy);

            // Links: the copy below the source (lowest precedence), then moved up, enforced, disabled; block inheritance below.
            await ExecuteOkAsync(await _planner.PlanLinkAsync(admin, ou, copy, Ct));
            Assert.Equal([source, copy], (await Reader.ReadContainerAsync(admin, ou, Ct))!.Links.Links.OrderBy(l => l.Order).Select(l => l.GpoId));
            await ExecuteOkAsync(await _planner.PlanMoveLinkAsync(admin, ou, copy, 1, Ct));
            await ExecuteOkAsync(await _planner.PlanLinkOptionsAsync(admin, ou, copy, enforced: true, enabled: null, Ct));
            await ExecuteOkAsync(await _planner.PlanLinkOptionsAsync(admin, ou, source, enforced: null, enabled: false, Ct));
            await ExecuteOkAsync(await _planner.PlanBlockInheritanceAsync(admin, stations, block: true, Ct));
            topology = await GroupPolicyTopology.ReadAsync(admin, 500, Ct);
            var links = Assert.Single(topology.Containers, c => DistinguishedName.AreEqual(c.Dn, ou)).Links;
            Assert.Equal([copy, source], links.OrderBy(l => l.Order).Select(l => l.GpoId!.Value));
            Assert.True(links.Single(l => l.GpoId == copy).Enforced);
            Assert.True(links.Single(l => l.GpoId == source).Disabled);
            Assert.True(Assert.Single(topology.Containers, c => DistinguishedName.AreEqual(c.Dn, stations)).BlocksInheritance);
            Assert.Contains("GPO_BLOCK_INHERITANCE", await tools.GpoQueryAsync("getinheritance", stations, Ct), StringComparison.Ordinal);
            var getlink = await tools.GpoQueryAsync("getlink", ou, Ct);
            Assert.True(getlink.IndexOf(SysvolPaths.FolderName(copy), StringComparison.OrdinalIgnoreCase) >= 0
                && getlink.Contains("ENFORCE", StringComparison.Ordinal) && getlink.Contains("DISABLE", StringComparison.Ordinal), getlink);

            // Back up the source to the operator's folder; then rename, disable computer settings and lose the file.
            var backupFolder = Path.Combine(tools.Directory, "Kopie zapasowe GPO – Łódź");
            System.IO.Directory.CreateDirectory(backupFolder);
            var backup = await _planner.ReadBackupAsync(admin, sysvol, source, "Przed zmianą – test K2", Ct);
            await GpmcBackupFormat.WriteAsync(backupFolder, backup, "0.0.0-lab", Ct);
            Assert.Contains(GpmcBackupFormat.List(backupFolder), e => e.BackupId == backup.BackupId && e.GpoId == source);

            await ExecuteOkAsync(await _planner.PlanRenameAsync(admin, source, $"K2T-{run} Stacje – Zmieniona", Ct));
            await ExecuteOkAsync(await _planner.PlanStatusAsync(admin, source, GpoStatus.ComputerSettingsDisabled, Ct));
            var changed = await Reader.ReadPolicyAsync(admin, source, Ct);
            Assert.Equal($"K2T-{run} Stacje – Zmieniona", changed!.DisplayName);
            Assert.Equal(GpoStatus.ComputerSettingsDisabled, changed.Status);
            await sysvol.DeleteFileAsync(source, SecurityTemplatePath, Ct);

            // Restore: name, status and the file come back; both version halves move up (never down).
            var read = await GpmcBackupFormat.ReadAsync(backupFolder, backup.BackupId, Ct);
            var restore = await _planner.PlanRestoreAsync(admin, sysvol, read, backupFolder, restoreLinks: false, Ct);
            var restored = await ExecuteOkAsync(restore);
            var after = await Reader.ReadPolicyAsync(admin, source, Ct);
            Assert.Equal($"K2T-{run} Stacje – Zasady haseł", after!.DisplayName);
            Assert.Equal(GpoStatus.AllSettingsEnabled, after.Status);
            Assert.Equal(SecurityTemplate(12), await ReadAsync(sysvol, source, SecurityTemplatePath));
            Assert.True(after.DirectoryVersion!.Value.User > 0 && after.DirectoryVersion.Value.Computer > 0);
            Assert.Equal(after.DirectoryVersion, await GroupPolicyPlanner.GptVersionAsync(sysvol, source, Ct));
            await AssertNoTempFilesAsync(sysvol, source);

            // Rollback of the restore: the file is gone again, the version moves up once more.
            var undo = new RollbackPlanner().CreateRollbackPlan(restored);
            await ExecuteOkAsync(undo);
            Assert.Equal(SysvolFileStatus.NotFound, (await sysvol.ReadGpoFileAsync(source, SecurityTemplatePath, 1 << 20, Ct)).Status);
            var undone = await Reader.ReadPolicyAsync(admin, source, Ct);
            Assert.True(undone!.DirectoryVersion!.Value.Computer > after.DirectoryVersion.Value.Computer);
            Assert.Equal($"K2T-{run} Stacje – Zmieniona", undone.DisplayName);

            // Import into the copy: only content and extension names; a file of the copy the backup lacks is deleted.
            await tools.PutAsync(copy, ScriptsPath, Encoding.Unicode.GetBytes("﻿[Startup]\r\n0CmdLine=start.cmd\r\n0Parameters=\r\n"), Ct);
            var import = await _planner.PlanImportAsync(admin, sysvol, copy, read, backupFolder, Ct);
            await ExecuteOkAsync(import);
            Assert.Equal(SecurityTemplate(12), await ReadAsync(sysvol, copy, SecurityTemplatePath));
            Assert.Equal(SysvolFileStatus.NotFound, (await sysvol.ReadGpoFileAsync(copy, ScriptsPath, 1 << 20, Ct)).Status);
            Assert.Equal($"K2T-{run} Stacje – Kopia", (await Reader.ReadPolicyAsync(admin, copy, Ct))!.DisplayName);

            // Delete both: links first, then the containers and the folders.
            foreach (var id in created.ToList())
            {
                await ExecuteOkAsync(await _planner.PlanDeleteAsync(admin, id, Ct));
                created.Remove(id);
                Assert.Null(await Reader.ReadPolicyAsync(admin, id, Ct));
                Assert.False(await sysvol.GpoFolderExistsAsync(id, Ct));
            }

            Assert.False((await Reader.ReadContainerAsync(admin, ou, Ct))!.Links.Links.Any());
        }
        finally
        {
            foreach (var id in created)
            {
                await tools.DeleteGpoAsync(id);
            }
        }
    }

    [Fact]
    public async Task VersionNumberChangedBehindThePlan_IsAConflict_AndNothingIsSwitched()
    {
        SambaTools.Require();
        var admin = lab.Admin;
        var run = Guid.NewGuid().ToString("N")[..6];
        var ou = await lab.CreateOuAsync($"K2T-{run}");
        using var tools = SambaTools.Create();
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var created = new List<Guid>();
        await using var sysvol = await _writer.OpenForWriteAsync(admin, Ct);
        try
        {
            created.Add(source);
            await ExecuteOkAsync(await _planner.PlanCreateAsync(admin, $"K2T-{run} Źródło", ou, Ct, source));
            created.Add(target);
            await ExecuteOkAsync(await _planner.PlanCreateAsync(admin, $"K2T-{run} Cel", ou, Ct, target));
            await tools.PutAsync(source, SecurityTemplatePath, SecurityTemplate(14), Ct);
            await tools.PutAsync(target, SecurityTemplatePath, SecurityTemplate(8), Ct);
            var backup = await _planner.ReadBackupAsync(admin, sysvol, source, string.Empty, Ct);
            var folder = Path.Combine(tools.Directory, "backup");
            System.IO.Directory.CreateDirectory(folder);
            await GpmcBackupFormat.WriteAsync(folder, backup, "0.0.0-lab", Ct);
            var plan = await _planner.PlanImportAsync(admin, sysvol, target, await GpmcBackupFormat.ReadAsync(folder, backup.BackupId, Ct), folder, Ct);
            Assert.Equal(0, plan.ErrorCount);

            // Another editor saves the GPO between the preview and the execution.
            var dn = GpLinkValue.GpoDn(target, admin.Info.DefaultNamingContext);
            await admin.ModifyAsync(dn, [AttributeModification.Replace(GroupPolicyAttributes.VersionNumber, new GpoVersion(0, 7).ToInt32().ToString(CultureInfo.InvariantCulture))], null, Ct);

            var result = await Execute(plan);
            var failed = Assert.Single(result.Objects.SelectMany(o => o.Changes), c => !c.Succeeded && c.Change.Action == ChangeAction.WriteGpoFile);
            Assert.StartsWith(BatchExecutor.ConflictMessagePrefix, failed.ErrorMessage, StringComparison.Ordinal);
            Assert.Equal(SecurityTemplate(8), await ReadAsync(sysvol, target, SecurityTemplatePath));
            Assert.Equal(new GpoVersion(0, 7), (await Reader.ReadPolicyAsync(admin, target, Ct))!.DirectoryVersion);
            await AssertNoTempFilesAsync(sysvol, target);
        }
        finally
        {
            foreach (var id in created)
            {
                try
                {
                    await ExecuteOkAsync(await _planner.PlanDeleteAsync(admin, id, Ct));
                }
                catch (Exception ex) when (ex is DirectoryException or Xunit.Sdk.XunitException)
                {
                    await tools.DeleteGpoAsync(id);
                }
            }
        }
    }

    [Fact]
    public async Task InterruptedWrite_IsCompensated_AndTheGpoStaysAsBefore()
    {
        SambaTools.Require();
        var admin = lab.Admin;
        var run = Guid.NewGuid().ToString("N")[..6];
        var ou = await lab.CreateOuAsync($"K2T-{run}");
        using var tools = SambaTools.Create();
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var created = new List<Guid>();
        var oldScripts = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("[Startup]\r\n0CmdLine=stare.cmd\r\n0Parameters=\r\n")).ToArray();
        var newScripts = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("[Startup]\r\n0CmdLine=nowe-łódź.cmd\r\n0Parameters=\r\n")).ToArray();
        await using var sysvol = await _writer.OpenForWriteAsync(admin, Ct);
        try
        {
            created.Add(source);
            await ExecuteOkAsync(await _planner.PlanCreateAsync(admin, $"K2T-{run} Źródło", ou, Ct, source));
            created.Add(target);
            await ExecuteOkAsync(await _planner.PlanCreateAsync(admin, $"K2T-{run} Cel", ou, Ct, target));
            await tools.PutAsync(source, SecurityTemplatePath, SecurityTemplate(14), Ct);
            await tools.PutAsync(source, ScriptsPath, newScripts, Ct);
            await tools.PutAsync(target, SecurityTemplatePath, SecurityTemplate(8), Ct);
            await tools.PutAsync(target, ScriptsPath, oldScripts, Ct);
            var before = (await Reader.ReadPolicyAsync(admin, target, Ct))!.DirectoryVersion;
            var backup = await _planner.ReadBackupAsync(admin, sysvol, source, string.Empty, Ct);
            var plan = await _planner.PlanImportAsync(admin, sysvol, target, backup, "C:\\Kopie", Ct);
            Assert.Equal(2, plan.Objects.SelectMany(o => o.Changes).Count(c => c.Action == ChangeAction.WriteGpoFile));

            // The second file switch fails: the first one is put back from memory, the version is not raised.
            _writer.FailReplace(2);
            var result = await Execute(plan);
            _writer.FailReplace(null);

            Assert.Contains(result.Objects.SelectMany(o => o.Changes), c => !c.Succeeded && c.Change.Action == ChangeAction.WriteGpoFile);
            Assert.Equal(SecurityTemplate(8), await ReadAsync(sysvol, target, SecurityTemplatePath));
            Assert.Equal(oldScripts, await ReadAsync(sysvol, target, ScriptsPath));
            Assert.Equal(before, (await Reader.ReadPolicyAsync(admin, target, Ct))!.DirectoryVersion);
            await AssertNoTempFilesAsync(sysvol, target);
        }
        finally
        {
            _writer.FailReplace(null);
            foreach (var id in created)
            {
                try
                {
                    await ExecuteOkAsync(await _planner.PlanDeleteAsync(admin, id, Ct));
                }
                catch (Exception ex) when (ex is DirectoryException or Xunit.Sdk.XunitException)
                {
                    await tools.DeleteGpoAsync(id);
                }
            }
        }
    }

    [Fact]
    public async Task WmiFilterAndSecurityFiltering_ThroughThePipeline()
    {
        SambaTools.Require();
        var admin = lab.Admin;
        var run = Guid.NewGuid().ToString("N")[..6];
        var ou = await lab.CreateOuAsync($"K2T-{run}");
        using var tools = SambaTools.Create();
        var gpoId = Guid.NewGuid();
        var copyId = Guid.NewGuid();
        var filterId = Guid.NewGuid();
        var created = false;
        var copied = false;
        var filterDn = default(string);
        await using var sysvol = await _writer.OpenForWriteAsync(admin, Ct);
        try
        {
            created = true;
            await ExecuteOkAsync(await _planner.PlanCreateAsync(admin, $"K2T-{run} Serwery – Pulpit zdalny", ou, Ct, gpoId));
            var group = DistinguishedName.Parse(ou).Child("CN", $"GG_K2T_{run}_Serwery_Łódź").ToString();
            await admin.AddAsync(group, new Dictionary<string, IReadOnlyList<object>>
            {
                [Attr.ObjectClass] = ["top", ObjectClasses.Group],
                [Attr.SamAccountName] = [$"GG_K2T_{run}"],
            }, Ct);
            var groupSid = (await admin.GetAsync(group, [Attr.ObjectSid], Ct))!.ObjectSid!;

            // WMI filter: created in CN=SOM,CN=WMIPolicy, linked to the GPO, deleted (the link is cleared first).
            var createFilter = await _planner.PlanCreateWmiFilterAsync(admin, $"K2T-{run} Tylko serwery", "Systemy serwerowe (ProductType 3)",
                [new WmiFilterQuery(@"root\CIMv2", "SELECT * FROM Win32_OperatingSystem WHERE ProductType = 3")], Ct, filterId);
            await ExecuteOkAsync(createFilter);
            var filter = Assert.Single(await Reader.ReadWmiFiltersAsync(admin, Ct), f => f.Name == $"K2T-{run} Tylko serwery");
            filterDn = filter.Dn;
            Assert.Equal("SELECT * FROM Win32_OperatingSystem WHERE ProductType = 3", Assert.Single(filter.Queries).Query);
            await ExecuteOkAsync(await _planner.PlanWmiFilterLinkAsync(admin, gpoId, filter, Ct));
            Assert.Equal(filter.Id, (await Reader.ReadPolicyAsync(admin, gpoId, Ct))!.WmiFilterId, ignoreCase: true);

            // Security filtering: the group gets Apply; the folder DACL follows; the group's removal keeps AU's Read.
            await ExecuteOkAsync(await _planner.PlanAddSecurityFilterAsync(admin, sysvol, gpoId, groupSid, "GG_K2T", Ct));
            var gpo = await Reader.ReadPolicyAsync(admin, gpoId, Ct);
            Assert.Contains(groupSid, gpo!.SecurityFilter!.Allowed);
            Assert.Contains(GpoPermissionLevels.Read(gpo.SecurityDescriptor), p => p.Sid == groupSid && p.Level == GpoPermissionLevel.Apply);
            Assert.True(GpoFolderAcl.IsConsistent(await sysvol.GetGpoFolderSecurityAsync(gpoId, Ct), gpo.SecurityDescriptor));

            await ExecuteOkAsync(await _planner.PlanRemoveSecurityFilterAsync(admin, sysvol, gpoId, "S-1-5-11", "Authenticated Users", Ct));
            gpo = await Reader.ReadPolicyAsync(admin, gpoId, Ct);
            Assert.DoesNotContain("S-1-5-11", gpo!.SecurityFilter!.Allowed);
            Assert.Contains(GpoPermissionLevels.Read(gpo.SecurityDescriptor), p => p.Sid == "S-1-5-11" && p.Level == GpoPermissionLevel.Read);
            Assert.True(GpoFolderAcl.IsConsistent(await sysvol.GetGpoFolderSecurityAsync(gpoId, Ct), gpo.SecurityDescriptor));

            // A copy with its permissions keeps the filtering: Samba's default DACL for the new container (Authenticated
            // Users with Apply Group Policy) is replaced by exactly the source's explicit ACEs.
            copied = true;
            await ExecuteOkAsync(await _planner.PlanCopyAsync(admin, sysvol, gpoId, $"K2T-{run} Serwery – Kopia z uprawnieniami", copyPermissions: true, Ct, copyId));
            var copy = await Reader.ReadPolicyAsync(admin, copyId, Ct);
            Assert.DoesNotContain("S-1-5-11", copy!.SecurityFilter!.Allowed);
            Assert.Contains(groupSid, copy.SecurityFilter.Allowed);
            Assert.Contains(GpoPermissionLevels.Read(copy.SecurityDescriptor), p => p.Sid == "S-1-5-11" && p.Level == GpoPermissionLevel.Read);
            Assert.True(GpoFolderAcl.IsConsistent(await sysvol.GetGpoFolderSecurityAsync(copyId, Ct), copy.SecurityDescriptor));

            await ExecuteOkAsync(await _planner.PlanDeleteWmiFilterAsync(admin, filter.Dn, Ct));
            filterDn = null;
            Assert.DoesNotContain(await Reader.ReadWmiFiltersAsync(admin, Ct), f => f.Id == filter.Id);
            Assert.Null((await Reader.ReadPolicyAsync(admin, gpoId, Ct))!.WmiFilterLink);

            await ExecuteOkAsync(await _planner.PlanDeleteAsync(admin, copyId, Ct));
            copied = false;
            await ExecuteOkAsync(await _planner.PlanDeleteAsync(admin, gpoId, Ct));
            created = false;
        }
        finally
        {
            if (filterDn is not null)
            {
                await admin.DeleteAsync(filterDn, subtree: false, CancellationToken.None);
            }

            if (copied)
            {
                await tools.DeleteGpoAsync(copyId);
            }

            if (created)
            {
                await tools.DeleteGpoAsync(gpoId);
            }
        }
    }

    [Fact]
    public async Task FolderDaclMapping_EqualsWhatSambaToolGivesItsOwnGpo()
    {
        SambaTools.Require();
        var admin = lab.Admin;
        var run = Guid.NewGuid().ToString("N")[..6];
        using var tools = SambaTools.Create();
        var id = await tools.CreateGpoAsync($"K2T-{run} Mapowanie uprawnień – Łódź", Ct);
        try
        {
            await using var sysvol = await _writer.OpenForWriteAsync(admin, Ct);
            var gpo = await Reader.ReadPolicyAsync(admin, id, Ct);
            var folder = await sysvol.GetGpoFolderSecurityAsync(id, Ct);
            Assert.NotNull(folder);
            Assert.True(GpoFolderAcl.IsConsistent(folder, gpo!.SecurityDescriptor),
                "dsacl2fsacl of samba-tool gpo create and GpoFolderAcl give the same folder DACL.");
            Assert.Equal(GpoVersion.Zero, await GroupPolicyPlanner.GptVersionAsync(sysvol, id, Ct));
        }
        finally
        {
            await tools.DeleteGpoAsync(id);
        }
    }

    private const string SecurityTemplatePath = "Machine/Microsoft/Windows NT/SecEdit/GptTmpl.inf";

    private const string ScriptsPath = "Machine/Scripts/scripts.ini";

    private static byte[] SecurityTemplate(int minimumLength) => Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(
        "[Unicode]\r\nUnicode=yes\r\n[System Access]\r\nMinimumPasswordLength = " + minimumLength.ToString(CultureInfo.InvariantCulture)
        + "\r\n[Version]\r\nsignature=\"$CHICAGO$\"\r\nRevision=1\r\n")).ToArray();

    private Task<BatchResult> Execute(ChangePlan plan) =>
        new BatchExecutor(_audit, TimeProvider.System, null, _writer).ExecuteAsync(lab.Admin, plan, Options, null, Ct);

    private async Task<BatchResult> ExecuteOkAsync(ChangePlan plan)
    {
        Assert.Equal(0, plan.ErrorCount);
        var result = await Execute(plan);
        var failures = result.Objects.SelectMany(o => o.Changes).Where(c => !c.Succeeded).Select(c => $"{c.Change.Action}: {c.ErrorMessage}").ToList();
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        return result;
    }

    private static async Task<byte[]?> ReadAsync(ISysvolWriteConnection sysvol, Guid gpoId, string path) =>
        (await sysvol.ReadGpoFileAsync(gpoId, path, 1 << 20, Ct)).Content;

    private static async Task AssertNoTempFilesAsync(ISysvolWriteConnection sysvol, Guid gpoId) =>
        Assert.DoesNotContain(await sysvol.ListGpoFolderAsync(gpoId, SysvolListOptions.Default, Ct), e => GpoTransaction.IsTempName(e.RelativePath));
}
