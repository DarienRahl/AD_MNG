using System.IO.Compression;
using System.Text;
using AdMng.Core.Batch;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Testing;

namespace AdMng.Core.Tests.GroupPolicy.AdministrativeTemplates;

public class CentralStoreUpdateTests : GroupPolicyTestBed
{
    /// <summary>A vendor archive laid out like Chrome's: windows/admx/*.admx with culture folders, plus things to ignore.</summary>
    private static Dictionary<string, byte[]> ChromeLike(string contosoAdmx = SampleAdministrativeTemplates.ContosoAdmx) => new(StringComparer.Ordinal)
    {
        ["windows/admx/contoso.admx"] = SampleAdministrativeTemplates.Utf8(contosoAdmx),
        ["windows/admx/en-US/contoso.adml"] = SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmlEn),
        ["windows/admx/pl/contoso.adml"] = SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmlPl),
        ["windows/adm/en-US/contoso.adm"] = Encoding.ASCII.GetBytes("CLASS MACHINE"),
        ["windows/examples/contoso.reg"] = Encoding.ASCII.GetBytes("REGEDIT4"),
        ["mac/contoso.plist"] = Encoding.ASCII.GetBytes("<plist/>"),
        ["windows/admx/inny/contoso.adml"] = Encoding.ASCII.GetBytes("not a culture folder"),
    };

    private void AddStore(bool polish = true)
    {
        foreach (var (path, content) in SampleAdministrativeTemplates.Files(polish))
        {
            Sysvol.PutPolicyDefinition(path, content);
        }
    }

    [Fact]
    public async Task Package_NormalizesTheVendorLayout_AndIgnoresTheRest()
    {
        var package = PolicyTemplatePackage.FromFiles("policy_templates.zip", ChromeLike());

        Assert.Equal(["contoso.admx"], package.AdmxFiles);
        Assert.Equal(["en-US", "pl"], package.Cultures);
        Assert.Empty(package.Issues);
        var listed = await package.ListAsync(Ct);
        Assert.Equal(["contoso.admx", "en-US/contoso.adml", "pl/contoso.adml"], listed!.Select(f => f.RelativePath).Order(StringComparer.Ordinal));
        Assert.NotNull(await package.ReadAsync("pl/contoso.adml", 1_000_000, Ct));
    }

    [Fact]
    public async Task Package_FromAZip_ReadsEntriesOnDemand_AndKeepsTheFirstOfADuplicate()
    {
        var files = ChromeLike();
        files["windows/backup/contoso.admx"] = Encoding.ASCII.GetBytes("<old/>");
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                using var entry = zip.CreateEntry(path).Open();
                entry.Write(content);
            }
        }

        var package = PolicyTemplatePackage.FromZip("policy_templates.zip", stream.ToArray());

        Assert.Equal(["contoso.admx"], package.AdmxFiles);
        Assert.Single(package.Issues);
        Assert.Equal(files["windows/admx/contoso.admx"], await package.ReadAsync("contoso.admx", 1_000_000, Ct));
        Assert.Throws<InvalidDataException>(() => PolicyTemplatePackage.FromZip("x.zip", [1, 2, 3]));
    }

    [Fact]
    public async Task Package_FromAFolder_ReadsTheOperatorsTemplates()
    {
        var folder = System.IO.Directory.CreateTempSubdirectory("admng-k3-");
        try
        {
            foreach (var (path, content) in ChromeLike())
            {
                var file = Path.Combine([folder.FullName, .. path.Split('/')]);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllBytesAsync(file, content, Ct);
            }

            var lazy = PolicyTemplatePackage.FromFolder("Szablony", folder.FullName);
            var eager = PolicyTemplatePackage.FromFolder("Szablony", folder.FullName, eager: true);

            Assert.Equal(["contoso.admx"], lazy.AdmxFiles);
            Assert.Equal(await lazy.ReadAsync("en-US/contoso.adml", 1_000_000, Ct), await eager.ReadAsync("en-US/contoso.adml", 1_000_000, Ct));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Compare_WithoutAStore_MakesEverythingNew_InTheStoresCultureNames()
    {
        await using var sysvol = await OpenSysvolAsync();
        var package = PolicyTemplatePackage.FromFiles("policy_templates.zip", ChromeLike());

        var comparison = await GroupPolicyPlanner.CompareCentralStoreAsync(sysvol, package, new CentralStoreUpdateOptions { Cultures = ["en-US", "pl"] }, Ct);

        Assert.False(comparison.StoreExists);
        Assert.Equal([("contoso.admx", CentralStoreFileStatus.New), ("en-US/contoso.adml", CentralStoreFileStatus.New), ("pl-PL/contoso.adml", CentralStoreFileStatus.New)],
            comparison.Files.Select(f => (f.RelativePath, f.Status)));
        Assert.Equal(7, comparison.Files[0].PoliciesAdded);
        Assert.Empty(Sysvol.Writes);
    }

    [Fact]
    public async Task Compare_FindsChangedUnchangedAndCollidingFiles()
    {
        AddStore();
        var newer = SampleAdministrativeTemplates.ContosoAdmx.Replace(
            """<policy name="WylaczPodglad" """, """<policy name="NowaZasada" class="Machine" displayName="x" key="Software\Policies\Contoso\Nowa" valueName="V"><parentCategory ref="Aktualizacje" /></policy><policy name="WylaczPodglad" """,
            StringComparison.Ordinal).Replace("""<policy name="TrybPracy" """, """<policy name="TrybPracyStary" """, StringComparison.Ordinal);
        var files = ChromeLike(newer);
        files["windows/admx/contoso-kopia.admx"] = SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.BaseAdmx);
        await using var sysvol = await OpenSysvolAsync();

        var comparison = await GroupPolicyPlanner.CompareCentralStoreAsync(sysvol, PolicyTemplatePackage.FromFiles("x", files),
            new CentralStoreUpdateOptions { Cultures = ["en-US"] }, Ct);

        var contoso = comparison.Files.Single(f => f.RelativePath == "contoso.admx");
        Assert.Equal(CentralStoreFileStatus.Changed, contoso.Status);
        Assert.Equal((2, 1), (contoso.PoliciesAdded, contoso.PoliciesRemoved));
        Assert.Equal(CentralStoreFileStatus.Unchanged, comparison.Files.Single(f => f.RelativePath == "en-US/contoso.adml").Status);
        var copy = comparison.Files.Single(f => f.RelativePath == "contoso-kopia.admx");
        Assert.Equal(CentralStoreFileStatus.NamespaceCollision, copy.Status);
        Assert.Equal("windows.admx", copy.CollidesWith);
        Assert.Equal(1, comparison.WrittenCount);
        Assert.Equal(6, comparison.StoreFiles.Count);
    }

    [Fact]
    public async Task Compare_ReplacingACollidingFile_RemovesItWithItsLanguages()
    {
        AddStore();
        var files = new Dictionary<string, byte[]> { ["nowe/windows-nowy.admx"] = SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.BaseAdmx) };
        await using var sysvol = await OpenSysvolAsync();

        var comparison = await GroupPolicyPlanner.CompareCentralStoreAsync(sysvol, PolicyTemplatePackage.FromFiles("x", files),
            new CentralStoreUpdateOptions { Cultures = [], ReplaceCollidingFiles = true }, Ct);

        Assert.Equal([("en-US/windows.adml", CentralStoreFileStatus.Removed), ("pl-PL/windows.adml", CentralStoreFileStatus.Removed),
            ("windows-nowy.admx", CentralStoreFileStatus.New), ("windows.admx", CentralStoreFileStatus.Removed)], comparison.Files.Select(f => (f.RelativePath, f.Status)));
    }

    [Fact]
    public async Task Compare_SkipsUnreadableFiles_AndListsLeftovers()
    {
        AddStore();
        Sysvol.PutPolicyDefinition(GpoTransaction.TempName("contoso.admx", Guid.NewGuid()), [1, 2]);
        var files = ChromeLike();
        files["windows/admx/zepsuty.admx"] = Encoding.ASCII.GetBytes("<nie");
        await using var sysvol = await OpenSysvolAsync();

        var comparison = await GroupPolicyPlanner.CompareCentralStoreAsync(sysvol, PolicyTemplatePackage.FromFiles("x", files), new CentralStoreUpdateOptions { Cultures = ["en-US"] }, Ct);

        Assert.Equal(CentralStoreFileStatus.Unreadable, comparison.Files.Single(f => f.RelativePath == "zepsuty.admx").Status);
        Assert.Single(comparison.Files, f => f.Status == CentralStoreFileStatus.Leftover);
        Assert.True(comparison.HasChanges);
    }

    [Fact]
    public async Task Update_WritesTheStore_AsOneTransaction_AndRollsBack()
    {
        AddStore(polish: false);
        var newer = SampleAdministrativeTemplates.ContosoAdmx.Replace("revision=\"1.2\"", "revision=\"1.3\"", StringComparison.Ordinal);
        await using var sysvol = await OpenSysvolAsync();
        var comparison = await GroupPolicyPlanner.CompareCentralStoreAsync(sysvol, PolicyTemplatePackage.FromFiles("x", ChromeLike(newer)),
            new CentralStoreUpdateOptions { Cultures = ["en-US", "pl"] }, Ct);
        var plan = Planner.PlanCentralStoreUpdate(Session, comparison, "Aktualizacja szablonów");
        Sysvol.ClearCalls();

        Assert.Equal([ChangeAction.WritePolicyDefinitionsFile, ChangeAction.WritePolicyDefinitionsFile], plan.Objects.Single().Changes.Select(c => c.Action));
        Assert.Contains("PolicyDefinitions", plan.Objects.Single().DisplayName, StringComparison.Ordinal);
        var result = await ExecuteOkAsync(plan);

        Assert.Equal(SampleAdministrativeTemplates.Utf8(newer), Sysvol.GetPolicyDefinition("contoso.admx"));
        Assert.NotNull(Sysvol.GetPolicyDefinition("pl-PL/contoso.adml"));
        Assert.DoesNotContain(Sysvol.PolicyDefinitionFiles(), f => f.EndsWith(".tmp", StringComparison.Ordinal));
        Assert.Contains(Audit.Entries, e => e.Attribute == "sysvol:PolicyDefinitions/contoso.admx");

        var rollback = new RollbackPlanner().CreateRollbackPlan(result);
        await ExecuteOkAsync(rollback);
        Assert.Equal(SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmx), Sysvol.GetPolicyDefinition("contoso.admx"));
        Assert.Null(Sysvol.GetPolicyDefinition("pl-PL/contoso.adml"));
    }

    [Fact]
    public async Task Update_OfAStoreChangedAfterThePreview_IsAConflict_AndWritesNothing()
    {
        AddStore();
        await using var sysvol = await OpenSysvolAsync();
        var comparison = await GroupPolicyPlanner.CompareCentralStoreAsync(sysvol, PolicyTemplatePackage.FromFiles("x",
            ChromeLike(SampleAdministrativeTemplates.ContosoAdmx.Replace("revision=\"1.2\"", "revision=\"1.3\"", StringComparison.Ordinal))),
            new CentralStoreUpdateOptions { Cultures = ["en-US"] }, Ct);
        var plan = Planner.PlanCentralStoreUpdate(Session, comparison, "x");
        Sysvol.PutPolicyDefinition("contoso.admx", [1, 2, 3]); // another administrator's copy

        var result = await ExecuteAsync(plan);

        Assert.Contains(result.Objects.Single().Changes, c => !c.Succeeded && (c.ErrorMessage ?? string.Empty).StartsWith(BatchExecutor.ConflictMessagePrefix, StringComparison.Ordinal));
        Assert.Equal([1, 2, 3], Sysvol.GetPolicyDefinition("contoso.admx"));
        Assert.DoesNotContain(Sysvol.PolicyDefinitionFiles(), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_ThatFailsHalfway_PutsTheOldFilesBack()
    {
        AddStore();
        var files = ChromeLike(SampleAdministrativeTemplates.ContosoAdmx.Replace("revision=\"1.2\"", "revision=\"1.3\"", StringComparison.Ordinal));
        files["windows/admx/pl/contoso.adml"] = SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmlPl.Replace("Pulpit</string>", "Pulpit i ekran</string>", StringComparison.Ordinal));
        await using var sysvol = await OpenSysvolAsync();
        var comparison = await GroupPolicyPlanner.CompareCentralStoreAsync(sysvol, PolicyTemplatePackage.FromFiles("x", files),
            new CentralStoreUpdateOptions { Cultures = ["en-US", "pl"] }, Ct);
        var plan = Planner.PlanCentralStoreUpdate(Session, comparison, "x");
        Assert.Equal(["contoso.admx", "pl-PL/contoso.adml"], plan.Objects.Single().Changes.Select(c => c.Sysvol!.RelativePath));
        Sysvol.Fail("ReplaceDefinitions", SysvolWriteError.AccessDenied, "pl-PL");

        var result = await ExecuteAsync(plan);

        Assert.Contains(result.Objects.Single().Changes, c => !c.Succeeded && (c.ErrorMessage ?? string.Empty).StartsWith(GpoTransaction.RevertedMessage, StringComparison.Ordinal));
        Assert.Equal(SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmx), Sysvol.GetPolicyDefinition("contoso.admx"));
    }

    [Fact]
    public async Task Backup_WritesTheStoreBelowTheChosenFolder()
    {
        var folder = System.IO.Directory.CreateTempSubdirectory("admng-k3-backup-");
        try
        {
            var written = await CentralStoreBackup.WriteAsync(folder.FullName, "dc1.contoso.test",
                new Dictionary<string, byte[]> { ["contoso.admx"] = [1], ["pl-PL/contoso.adml"] = [2], ["../poza.admx"] = [3] },
                new DateTimeOffset(2026, 11, 2, 15, 30, 0, TimeSpan.Zero), Ct);

            Assert.Equal("PolicyDefinitions-dc1.contoso.test-20261102-153000", Path.GetFileName(written));
            Assert.Equal([2], await File.ReadAllBytesAsync(Path.Combine(written, "pl-PL", "contoso.adml"), Ct));
            Assert.False(File.Exists(Path.Combine(folder.FullName, "poza.admx")));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("pl", "pl-PL")]
    [InlineData("en-US", "en-US")]
    [InlineData("de", "de-DE")]
    [InlineData("zh-TW", "zh-TW")]
    public void TargetCulture_MapsNeutralCulturesToSpecificOnes(string package, string expected) =>
        Assert.Equal(expected, GroupPolicyPlanner.TargetCulture(package, ["en-US", "pl-PL"]));
}
