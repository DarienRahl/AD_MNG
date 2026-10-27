using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Backup;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Testing;

namespace AdMng.Core.Tests.GroupPolicy;

/// <summary>GPMC-compatible backups (design note 5.5) on a test-only temporary folder: round trip, restore, import, hostile input.</summary>
public sealed class GpmcBackupTests : GroupPolicyTestBed, IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "admng-k2-backup-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (System.IO.Directory.Exists(_folder))
        {
            System.IO.Directory.Delete(_folder, recursive: true);
        }
    }

    private async Task<GpoBackup> BackUpAsync(Guid gpoId, string comment = "Kopia przed zmianą – Łódź")
    {
        await using var sysvol = await OpenSysvolAsync();
        var backup = await Planner.ReadBackupAsync(Session, sysvol, gpoId, comment, Ct);
        await GpmcBackupFormat.WriteAsync(_folder, backup, "0.5.0", Ct);
        return backup;
    }

    [Fact]
    public async Task Write_CreatesTheGpmcLayout_AndReadsBackTheSameBackup()
    {
        var backup = await BackUpAsync(Sample.WorkstationPasswords);

        var backupDir = Path.Combine(_folder, SysvolPaths.FolderName(backup.BackupId));
        Assert.True(File.Exists(Path.Combine(_folder, "manifest.xml")));
        Assert.True(File.Exists(Path.Combine(backupDir, "Backup.xml")));
        Assert.True(File.Exists(Path.Combine(backupDir, "bkupInfo.xml")));
        Assert.True(File.Exists(Path.Combine(backupDir, "admng.json")));
        Assert.True(File.Exists(Path.Combine(backupDir, "DomainSysvol", "GPO", "Machine", "Microsoft", "Windows NT", "SecEdit", "GptTmpl.inf")));
        Assert.False(File.Exists(Path.Combine(backupDir, "DomainSysvol", "GPO", "GPT.INI")));
        var xml = await File.ReadAllTextAsync(Path.Combine(backupDir, "Backup.xml"), Ct);
        Assert.Contains("GroupPolicyBackupScheme", xml);
        Assert.Contains("%GPO_MACH_FSPATH%\\Microsoft\\Windows NT\\SecEdit\\GptTmpl.inf", xml);
        Assert.Contains("{827D319E-6EAC-11D2-A4EA-00C04F79F83A}", xml);

        var entry = Assert.Single(GpmcBackupFormat.List(_folder));
        Assert.Equal(backup.BackupId, entry.BackupId);
        Assert.Equal("Kopia przed zmianą – Łódź", entry.Comment);
        Assert.Equal("Stacje robocze – Zasady haseł", entry.DisplayName);

        var read = await GpmcBackupFormat.ReadAsync(_folder, backup.BackupId, Ct);
        Assert.Equal(backup.GpoId, read.GpoId);
        Assert.Equal(backup.DisplayName, read.DisplayName);
        Assert.Equal(backup.Version, read.Version);
        Assert.Equal(backup.WmiFilterLink, read.WmiFilterLink);
        Assert.Equal(backup.SecurityDescriptor, read.SecurityDescriptor);
        Assert.Equal(backup.Files.Keys.Order(StringComparer.OrdinalIgnoreCase), read.Files.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(backup.Files, f => Assert.Equal(f.Value, read.Files[f.Key]));
        Assert.True(read.HasSidecar);
        Assert.Equal(backup.Links, read.Links);
        Assert.Equal(Sample.WmiFilterId, read.WmiFilter!.Id);
    }

    [Fact]
    public async Task Manifest_IsMerged_KeepingOtherBackups()
    {
        var first = await BackUpAsync(Sample.WorkstationPasswords);
        var second = await BackUpAsync(Sample.ServerSecurity);

        var entries = GpmcBackupFormat.List(_folder);

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.BackupId == first.BackupId);
        Assert.Contains(entries, e => e.BackupId == second.BackupId);
    }

    [Fact]
    public async Task Restore_OfAnExistingGpo_PutsFilesNameStatusAndPermissionsBack()
    {
        var backup = await BackUpAsync(Sample.WorkstationPasswords);
        var originalTemplate = Sysvol.Get(Sample.WorkstationPasswords, SecurityTemplate.RelativePath);
        Sysvol.Put(Sample.WorkstationPasswords, SecurityTemplate.RelativePath, FakeSysvolFileReader.Utf16("[Unicode]\r\nUnicode=yes\r\n[System Access]\r\nMinimumPasswordLength = 4\r\n"));
        Sysvol.PutText(Sample.WorkstationPasswords, "Machine/Scripts/Startup/obce.cmd", "echo obcy");
        Session.SetValues(GpoDn(Sample.WorkstationPasswords), Attr.DisplayName, "Zmieniona nazwa");
        Session.SetValues(GpoDn(Sample.WorkstationPasswords), "flags", "3");
        Session.SeedAttributes(GpoDn(Sample.WorkstationPasswords), new Dictionary<string, object?> { [Attr.NtSecurityDescriptor] = Builder.DefaultGroupPolicySecurityDescriptor() });
        var read = await GpmcBackupFormat.ReadAsync(_folder, backup.BackupId, Ct);
        await using var sysvol = await OpenSysvolAsync();

        var plan = await Planner.PlanRestoreAsync(Session, sysvol, read, _folder, restoreLinks: false, Ct);

        Assert.Contains("Restore-GPO -BackupId", Script(plan));
        var actions = plan.Objects[0].Changes.Select(c => c.Action).ToList();
        Assert.Equal(ChangeAction.WriteGpoFile, actions[0]);
        Assert.Contains(ChangeAction.DeleteGpoFile, actions);
        Assert.Contains(ChangeAction.SetGpoVersion, actions);
        await ExecuteOkAsync(plan);

        Assert.Equal(originalTemplate, Sysvol.Get(Sample.WorkstationPasswords, SecurityTemplate.RelativePath));
        Assert.Null(Sysvol.Get(Sample.WorkstationPasswords, "Machine/Scripts/Startup/obce.cmd"));
        Assert.Equal("Stacje robocze – Zasady haseł", Value(GpoDn(Sample.WorkstationPasswords), Attr.DisplayName));
        Assert.Equal("0", Value(GpoDn(Sample.WorkstationPasswords), "flags"));
        var version = GpoVersion.FromInt32(int.Parse(Value(GpoDn(Sample.WorkstationPasswords), "versionNumber")!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(new GpoVersion(1, 5), version);
        var permissions = GpoPermissionLevels.Read(Session.Snapshot(GpoDn(Sample.WorkstationPasswords))!.GetBytes(Attr.NtSecurityDescriptor));
        Assert.Contains(permissions, p => p.Sid == Builder.SidOf(Company.LukaszWisniewski) && p.Level == GpoPermissionLevel.Edit);
    }

    [Fact]
    public async Task Restore_OfADeletedFilteredGpo_KeepsItsSecurityFiltering()
    {
        // "Stacje robocze – Pulpit zdalny" applies to GG_IT only; the re-created container starts with the default DACL.
        var backup = await BackUpAsync(Sample.WorkstationRemoteDesktop);
        await ExecuteOkAsync(await Planner.PlanDeleteAsync(Session, Sample.WorkstationRemoteDesktop, Ct));
        var read = await GpmcBackupFormat.ReadAsync(_folder, backup.BackupId, Ct);
        await using var sysvol = await OpenSysvolAsync();

        await ExecuteOkAsync(await Planner.PlanRestoreAsync(Session, sysvol, read, _folder, restoreLinks: true, Ct));

        var permissions = GpoPermissionLevels.Read(Session.Snapshot(GpoDn(Sample.WorkstationRemoteDesktop))!.GetBytes(Attr.NtSecurityDescriptor));
        Assert.Equal(GpoPermissionLevel.Read, Assert.Single(permissions, p => p.Sid == GpoPermissionLevels.AuthenticatedUsersSid).Level);
        Assert.Equal(GpoPermissionLevel.Apply, Assert.Single(permissions, p => p.Sid == Builder.SidOf(Company.GroupIt)).Level);
    }

    [Fact]
    public async Task Restore_OfADeletedGpo_CreatesItWithTheBackupsGuid_AndLinksOnRequest()
    {
        var backup = await BackUpAsync(Sample.WorkstationPasswords);
        await ExecuteOkAsync(await Planner.PlanDeleteAsync(Session, Sample.WorkstationPasswords, Ct));
        Assert.False(Session.Exists(GpoDn(Sample.WorkstationPasswords)));
        var read = await GpmcBackupFormat.ReadAsync(_folder, backup.BackupId, Ct);
        await using var sysvol = await OpenSysvolAsync();

        var plan = await Planner.PlanRestoreAsync(Session, sysvol, read, _folder, restoreLinks: true, Ct);
        await ExecuteOkAsync(plan);

        Assert.True(Session.Exists(GpoDn(Sample.WorkstationPasswords)));
        Assert.Equal(read.Files[SecurityTemplate.RelativePath], Sysvol.Get(Sample.WorkstationPasswords, SecurityTemplate.RelativePath));
        Assert.Equal(new GpoVersion(0, 5).ToInt32().ToString(System.Globalization.CultureInfo.InvariantCulture), Value(GpoDn(Sample.WorkstationPasswords), "versionNumber"));
        Assert.True(GpLinkValue.Parse(Value(Company.ComputersOu, Attr.GpLink), Builder.DomainDn).Contains(Sample.WorkstationPasswords));
        var permissions = GpoPermissionLevels.Read(Session.Snapshot(GpoDn(Sample.WorkstationPasswords))!.GetBytes(Attr.NtSecurityDescriptor));
        Assert.Contains(permissions, p => p.Sid == Builder.SidOf(Company.LukaszWisniewski) && p.Level == GpoPermissionLevel.Edit);
    }

    [Fact]
    public async Task Import_TakesOnlyContentAndExtensionNames_AndReportsForeignReferences()
    {
        var backup = await BackUpAsync(Sample.WorkstationPasswords);
        var read = await GpmcBackupFormat.ReadAsync(_folder, backup.BackupId, Ct);
        var foreign = read with
        {
            Files = new Dictionary<string, byte[]>(read.Files)
            {
                [SecurityTemplate.RelativePath] = FakeSysvolFileReader.Utf16("[Unicode]\r\nUnicode=yes\r\n[Privilege Rights]\r\nSeDebugPrivilege = *S-1-5-21-111-222-333-1105\r\n"),
            },
        };
        await using var sysvol = await OpenSysvolAsync();

        var plan = await Planner.PlanImportAsync(Session, sysvol, Sample.ServerSecurity, foreign, _folder, Ct);

        Assert.Contains("Import-GPO -BackupId", Script(plan));
        Assert.Contains(plan.Objects[0].Changes.SelectMany(c => c.Issues), i => i.Code == IssueCodes.GpoForeignReferences && i.Message.Contains("S-1-5-21-111-222-333", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Objects[0].Changes, c => c.Action == ChangeAction.ModifySecurity);
        await ExecuteOkAsync(plan);
        Assert.Equal("Serwery – Zabezpieczenia", Value(GpoDn(Sample.ServerSecurity), Attr.DisplayName));
        Assert.Equal(foreign.Files["GPO.cmt"], Sysvol.Get(Sample.ServerSecurity, "GPO.cmt"));
    }

    [Fact]
    public async Task RestoreAndImport_WarnAboutGppPasswords_WithoutTheirValues()
    {
        const string Groups = "Machine/Preferences/Groups/Groups.xml";
        const string Secret = "j1Uyj3Vx8TY9LtLZil2uAuZkFQA/4latT76ZwgdHdhw";
        var backup = await BackUpAsync(Sample.WorkstationPasswords);
        var read = await GpmcBackupFormat.ReadAsync(_folder, backup.BackupId, Ct);
        var withPassword = read with
        {
            Files = new Dictionary<string, byte[]>(read.Files)
            {
                [Groups] = Encoding.UTF8.GetBytes(
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?><Groups clsid=\"{3125E937-EB16-4b4c-9934-544FC6D24D26}\">" +
                    "<User clsid=\"{DF5F1855-51E5-4d24-8B1A-D9BDE98BA1D1}\" name=\"Administrator (wbudowany)\" uid=\"{1}\">" +
                    $"<Properties action=\"U\" newName=\"admin.łódź\" cpassword=\"{Secret}\" userName=\"Administrator (wbudowany)\"/></User></Groups>"),
            },
        };
        await using var sysvol = await OpenSysvolAsync();

        var restore = await Planner.PlanRestoreAsync(Session, sysvol, withPassword, _folder, restoreLinks: false, Ct);
        var import = await Planner.PlanImportAsync(Session, sysvol, Sample.ServerSecurity, withPassword, _folder, Ct);

        foreach (var plan in new[] { restore, import })
        {
            var issue = Assert.Single(plan.Objects.SelectMany(o => o.Changes).SelectMany(c => c.Issues), i => i.Code == IssueCodes.GpoGppPassword);
            Assert.Equal(IssueSeverity.Warning, issue.Severity);
            Assert.Contains(Groups, issue.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, issue.Message, StringComparison.Ordinal);
        }

        // A removed password (cpassword="", as GPMC leaves it) is not reported.
        var cleared = new Dictionary<string, byte[]> { [Groups] = Encoding.UTF8.GetBytes("<Groups><User><Properties cpassword=\"\" /></User></Groups>") };
        Assert.Empty(GroupPolicyPlanner.GppPasswordFiles(cleared));
        Assert.Empty(GroupPolicyPlanner.GppPasswordFiles(read.Files));
    }

    [Fact]
    public async Task SettingsKeptInTheDirectory_AreCountedInTheBackup_AndWarnedAboutOnRestoreAndImport()
    {
        // A software installation class store below CN=Machine: not part of SYSVOL, so not part of the backup.
        Session.Seed("CN=Class Store,CN=Machine," + GpoDn(Sample.WorkstationPasswords), new Dictionary<string, object?> { [Attr.ObjectClass] = new[] { "top", "container" } });
        var backup = await BackUpAsync(Sample.WorkstationPasswords);
        Assert.Equal(1, backup.DirectoryObjectCount);
        var read = await GpmcBackupFormat.ReadAsync(_folder, backup.BackupId, Ct);
        Assert.Equal(1, read.DirectoryObjectCount);
        await using var sysvol = await OpenSysvolAsync();

        var restore = await Planner.PlanRestoreAsync(Session, sysvol, read, _folder, restoreLinks: false, Ct);
        var import = await Planner.PlanImportAsync(Session, sysvol, Sample.ServerSecurity, read, _folder, Ct);

        foreach (var plan in new[] { restore, import })
        {
            var issue = Assert.Single(plan.Objects.SelectMany(o => o.Changes).SelectMany(c => c.Issues), i => i.Code == IssueCodes.GpoDirectorySettings);
            Assert.Equal(IssueSeverity.Warning, issue.Severity);
        }
    }

    [Fact]
    public async Task Read_OfAGpmcBackup_CountsItsDirectoryObjects()
    {
        var backup = await BackUpAsync(Sample.ServerSecurity);
        var backupDir = Path.Combine(_folder, SysvolPaths.FolderName(backup.BackupId));
        File.Delete(Path.Combine(backupDir, "admng.json"));
        var xml = await File.ReadAllTextAsync(Path.Combine(backupDir, "Backup.xml"), Ct);
        var end = xml.LastIndexOf("</", xml.IndexOf("GroupPolicyObject>", xml.IndexOf("GroupPolicyCoreSettings", StringComparison.Ordinal), StringComparison.Ordinal), StringComparison.Ordinal);
        xml = xml.Insert(end,
            "<bkp:GroupPolicyExtension bkp:ID=\"{C6DC5466-785A-11D2-84D0-00C04FB169F7}\" bkp:DescName=\"Software Installation\">"
            + "<bkp:DSObject bkp:Path=\"CN=Packages,CN=Class Store,%GPO_MACH_DSPATH%\" bkp:ReEvaluateFunctionality=\"No\" /></bkp:GroupPolicyExtension>");
        await File.WriteAllTextAsync(Path.Combine(backupDir, "Backup.xml"), xml, Ct);

        var read = await GpmcBackupFormat.ReadAsync(_folder, backup.BackupId, Ct);

        Assert.Equal(1, read.DirectoryObjectCount);
    }

    [Fact]
    public async Task Read_RefusesAChangedFile()
    {
        var backup = await BackUpAsync(Sample.WorkstationPasswords);
        var path = Path.Combine(_folder, SysvolPaths.FolderName(backup.BackupId), "DomainSysvol", "GPO", "GPO.cmt");
        await File.WriteAllTextAsync(path, "podmieniony", Ct);

        var error = await Assert.ThrowsAsync<GpoBackupException>(() => GpmcBackupFormat.ReadAsync(_folder, backup.BackupId, Ct));
        Assert.Contains("SHA-256", error.Message);
    }

    [Fact]
    public async Task Read_RefusesAnExtraFile()
    {
        var backup = await BackUpAsync(Sample.WorkstationPasswords);
        await File.WriteAllTextAsync(Path.Combine(_folder, SysvolPaths.FolderName(backup.BackupId), "DomainSysvol", "GPO", "Machine", "dodany.txt"), "x", Ct);

        await Assert.ThrowsAsync<GpoBackupException>(() => GpmcBackupFormat.ReadAsync(_folder, backup.BackupId, Ct));
    }

    [Fact]
    public async Task Read_RefusesADtd()
    {
        var id = Guid.NewGuid();
        var dir = Path.Combine(_folder, SysvolPaths.FolderName(id));
        System.IO.Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "Backup.xml"),
            "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><GroupPolicyBackupScheme>&e;</GroupPolicyBackupScheme>", Ct);

        await Assert.ThrowsAsync<GpoBackupException>(() => GpmcBackupFormat.ReadAsync(_folder, id, Ct));
    }

    [Fact]
    public async Task Read_OfAMissingBackup_Fails()
    {
        System.IO.Directory.CreateDirectory(_folder);

        await Assert.ThrowsAsync<GpoBackupException>(() => GpmcBackupFormat.ReadAsync(_folder, Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task Read_RefusesAnOversizedFile()
    {
        var backup = await BackUpAsync(Sample.ServerSecurity);
        var sidecar = Path.Combine(_folder, SysvolPaths.FolderName(backup.BackupId), "admng.json");
        File.Delete(sidecar);
        await using (var stream = File.Create(Path.Combine(_folder, SysvolPaths.FolderName(backup.BackupId), "DomainSysvol", "GPO", "Machine", "huge.bin")))
        {
            stream.SetLength(GpoTransaction.MaxFileBytes + 1);
        }

        await Assert.ThrowsAsync<GpoTooLargeException>(() => GpmcBackupFormat.ReadAsync(_folder, backup.BackupId, Ct));
    }

    [Fact]
    public async Task Read_OfAGpmcBackupWithoutSidecar_Works()
    {
        var backup = await BackUpAsync(Sample.ServerSecurity);
        File.Delete(Path.Combine(_folder, SysvolPaths.FolderName(backup.BackupId), "admng.json"));

        var read = await GpmcBackupFormat.ReadAsync(_folder, backup.BackupId, Ct);

        Assert.False(read.HasSidecar);
        Assert.Empty(read.Links);
        Assert.Equal(backup.Files.Count, read.Files.Count);
    }

    [Fact]
    public async Task Write_RefusesTraversalInAPath()
    {
        var backup = new GpoBackup
        {
            BackupId = Guid.NewGuid(),
            GpoId = Guid.NewGuid(),
            DisplayName = "Zła",
            Domain = "contoso.test",
            Files = new Dictionary<string, byte[]> { ["../../poza.txt"] = Encoding.UTF8.GetBytes("x") },
        };

        await Assert.ThrowsAsync<GpoBackupException>(() => GpmcBackupFormat.WriteAsync(_folder, backup, "0.5.0", Ct));
        Assert.Empty(System.IO.Directory.EnumerateFileSystemEntries(_folder));
    }

    [Theory]
    [InlineData("Machine/Registry.pol", "{35378EAC-683F-11D2-A89A-00C04FBBCFA2}")]
    [InlineData("Machine/Microsoft/Windows NT/SecEdit/GptTmpl.inf", "{827D319E-6EAC-11D2-A4EA-00C04F79F83A}")]
    [InlineData("Machine/Preferences/Groups/Groups.xml", "{17D89FEC-5C44-4972-B12D-241CAEF74509}")]
    [InlineData("User/Scripts/Logon/logon.cmd", "{42B5FAAE-6536-11D2-AE5A-0000F87571E3}")]
    public void ExtensionOf_UsesTheFileClassifier(string path, string extension) =>
        Assert.Equal(new Guid(extension), GpmcBackupFormat.ExtensionOf(path).Id);
}
