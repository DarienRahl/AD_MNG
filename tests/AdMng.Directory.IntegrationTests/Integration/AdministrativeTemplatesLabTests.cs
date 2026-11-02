using AdMng.Core.Audit;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Directory.IntegrationTests.Infrastructure;
using AdMng.Testing;

namespace AdMng.Directory.IntegrationTests.Integration;

/// <summary>
/// Administrative templates (K3) against the Samba lab through the change pipeline with SYSVOL writes over smbclient
/// (<see cref="SmbClientSysvolFileWriter"/>): a GPO's Registry.pol files saved (read back by samba-tool's own PReg parser)
/// and undone, and a central store update (created or extended, backed up, replaced, undone). The lab is shared: the GPO
/// is named <c>K3T-&lt;run&gt; …</c> and deleted at the end; the central store files are this run's own
/// (<c>k3t-&lt;run&gt;.admx</c> with its own namespace) and removed by the undo, the store's folders too when the lab had no
/// central store before. Templates are the synthetic Polish samples (<see cref="SampleAdministrativeTemplates"/>), never
/// vendor files.
/// </summary>
[Collection(SambaToolCollection.Name)]
public sealed class AdministrativeTemplatesLabTests(LabFixture lab)
{
    private const string RegistryAndMachineTool = "[{35378EAC-683F-11D2-A89A-00C04FBBCFA2}{0F6B957D-509E-11D1-A7CC-0000F87571E3}]";

    private const string RegistryAndUserTool = "[{35378EAC-683F-11D2-A89A-00C04FBBCFA2}{0F6B957E-509E-11D1-A7CC-0000F87571E3}]";

    private static readonly BatchOptions Options = new() { InitialRetryDelay = TimeSpan.FromMilliseconds(50) };

    private static readonly PolicyDefinitionSet Set = SampleAdministrativeTemplates.LoadPolish();

    private readonly GroupPolicyPlanner _planner = new(new GroupPolicyReader(), TimeProvider.System);
    private readonly SmbClientSysvolFileWriter _writer = new(SambaTools.Address, LabSettings.AdminUser, LabSettings.AdminPassword);
    private readonly InMemoryAuditLog _audit = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RegistryPolicy_SavedThroughThePipeline_IsReadBySambaTool_AndUndone()
    {
        SambaTools.Require();
        var admin = lab.Admin;
        var run = Guid.NewGuid().ToString("N")[..6];
        var ou = await lab.CreateOuAsync($"K3T-{run}");
        using var tools = SambaTools.Create();
        var gpoId = Guid.NewGuid();
        var created = false;
        await using var sysvol = await _writer.OpenForWriteAsync(admin, Ct);
        try
        {
            await ExecuteOkAsync(await _planner.PlanCreateAsync(admin, $"K3T-{run} Aktualizacje – Łódź", ou, Ct, gpoId));
            created = true;

            // Computer: automatic updates with their options; user: a drive mapping to a Polish UNC path.
            var machine = new RegistryPolicyDocument();
            PolicyEngine.Write(Set.Find(SampleAdministrativeTemplates.Updates)!.Policy, PolicySetting.Enabled(new Dictionary<string, PolicyElementValue>
            {
                ["Tryb"] = new EnumElementValue(2),
                ["Dzien"] = new NumberElementValue(3),
                ["InneProdukty"] = new BooleanElementValue(true),
            }), machine);
            var user = new RegistryPolicyDocument();
            PolicyEngine.Write(Set.Find(SampleAdministrativeTemplates.DriveMappings)!.Policy, PolicySetting.Enabled(new Dictionary<string, PolicyElementValue>
            {
                ["Mapy"] = new ListElementValue([new("P", @"\\pliki\Wydział Prawa – Łódź")]),
            }), user);

            var save = await _planner.PlanAdministrativeTemplatesAsync(admin, sysvol, gpoId,
                new RegistryPolicySave { Content = machine.ToBytes() }, new RegistryPolicySave { Content = user.ToBytes() }, Ct);
            var saved = await ExecuteOkAsync(save);

            Assert.Equal(machine.ToBytes(), await ReadAsync(sysvol, gpoId, GroupPolicyPlanner.MachineRegistryPolicyPath));
            Assert.Equal(user.ToBytes(), await ReadAsync(sysvol, gpoId, GroupPolicyPlanner.UserRegistryPolicyPath));
            var gpo = (await _planner.Reader.ReadPolicyAsync(admin, gpoId, Ct))!;
            Assert.Equal(new GpoVersion(1, 1), gpo.DirectoryVersion);
            Assert.Equal(new GpoVersion(1, 1), await GroupPolicyPlanner.GptVersionAsync(sysvol, gpoId, Ct));
            Assert.Equal(RegistryAndMachineTool, gpo.MachineExtensionNames);
            Assert.Equal(RegistryAndUserTool, gpo.UserExtensionNames);

            // Our reading of the written files gives the settings back.
            var policy = Set.Find(SampleAdministrativeTemplates.Updates)!.Policy;
            var read = PolicyEngine.Read(policy, RegistryPolicyDocument.Parse(await ReadAsync(sysvol, gpoId, GroupPolicyPlanner.MachineRegistryPolicyPath)).View);
            Assert.Equal(PolicyState.Enabled, read.State);
            Assert.Equal(new EnumElementValue(2), read.Values["Tryb"]);

            // Samba's own PReg parser (samba-tool gpo show, "Policies") reads both files: keys, value names and data.
            var show = await tools.GpoQueryAsync("show", SysvolPaths.FolderName(gpoId), Ct);
            Assert.Contains(RegistryAndMachineTool, show, StringComparison.OrdinalIgnoreCase);
            foreach (var expected in new[] { "AUOptions", "ScheduledInstallDay", "AllowMUUpdateService", "NoAutoUpdate", "\"MACHINE\"", "\"USER\"", "Wydział Prawa – Łódź" })
            {
                Assert.True(show.Contains(expected, StringComparison.Ordinal) || show.Contains(JsonEscaped(expected), StringComparison.OrdinalIgnoreCase),
                    $"samba-tool gpo show does not list {expected}:{Environment.NewLine}{show}");
            }

            // Undo: the files go (they did not exist), the extension names come back, the versions still grow.
            await ExecuteOkAsync(new RollbackPlanner().CreateRollbackPlan(saved));
            Assert.Equal(SysvolFileStatus.NotFound, (await sysvol.ReadGpoFileAsync(gpoId, GroupPolicyPlanner.MachineRegistryPolicyPath, 1 << 20, Ct)).Status);
            Assert.Equal(SysvolFileStatus.NotFound, (await sysvol.ReadGpoFileAsync(gpoId, GroupPolicyPlanner.UserRegistryPolicyPath, 1 << 20, Ct)).Status);
            gpo = (await _planner.Reader.ReadPolicyAsync(admin, gpoId, Ct))!;
            Assert.True(string.IsNullOrEmpty(gpo.MachineExtensionNames), gpo.MachineExtensionNames);
            Assert.True(string.IsNullOrEmpty(gpo.UserExtensionNames), gpo.UserExtensionNames);
            var version = gpo.DirectoryVersion!.Value;
            Assert.True(version.User > 1 && version.Computer > 1, version.ToString());
            Assert.Equal(gpo.DirectoryVersion, await GroupPolicyPlanner.GptVersionAsync(sysvol, gpoId, Ct));
            Assert.DoesNotContain(await sysvol.ListGpoFolderAsync(gpoId, SysvolListOptions.Default, Ct), e => GpoTransaction.IsTempName(e.RelativePath));
        }
        finally
        {
            if (created)
            {
                try
                {
                    await ExecuteOkAsync(await _planner.PlanDeleteAsync(admin, gpoId, Ct));
                }
                catch (Exception ex) when (ex is DirectoryException or Xunit.Sdk.XunitException)
                {
                    await tools.DeleteGpoAsync(gpoId);
                }
            }
        }
    }

    [Fact]
    public async Task CentralStore_IsUpdatedThroughThePipeline_BackedUp_AndUndone()
    {
        SambaTools.Require();
        var admin = lab.Admin;
        var run = Guid.NewGuid().ToString("N")[..6];
        using var tools = SambaTools.Create();
        var name = "k3t-" + run;
        var admx = Admx(run, "1.2");
        var newer = Admx(run, "1.3");
        var options = new CentralStoreUpdateOptions { Cultures = ["en-US", "pl"] };
        await using var sysvol = await _writer.OpenForWriteAsync(admin, Ct);
        var existed = await sysvol.ListPolicyDefinitionsAsync(GroupPolicyPlanner.MaxCentralStoreFiles, Ct) is not null;
        BatchResult? first = null;
        try
        {
            // New files (the store is created when the lab has none): one transaction, no temporary files left.
            var comparison = await GroupPolicyPlanner.CompareCentralStoreAsync(sysvol, Package(name, admx), options, Ct);
            Assert.Equal(existed, comparison.StoreExists);
            Assert.Equal(
                [("en-US/" + name + ".adml", CentralStoreFileStatus.New), (name + ".admx", CentralStoreFileStatus.New), ("pl-PL/" + name + ".adml", CentralStoreFileStatus.New)],
                comparison.Files.Select(f => (f.RelativePath, f.Status)).OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase));
            first = await ExecuteOkAsync(_planner.PlanCentralStoreUpdate(admin, comparison, $"K3T-{run} magazyn centralny"));
            Assert.Equal(admx, await ReadStoreAsync(sysvol, name + ".admx"));
            Assert.Equal(SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmlPl), await ReadStoreAsync(sysvol, "pl-PL/" + name + ".adml"));
            await AssertNoTempFilesAsync(sysvol);

            // The new templates are read from SYSVOL like the editor reads them (the store's source).
            var (definitions, _) = await PolicyDefinitionLoader.LoadAsync(new SysvolPolicyDefinitionSource(sysvol, "SYSVOL"), ["pl-PL", "en-US"], Ct);
            Assert.NotNull(definitions.Find($"Contoso.Policies.K3T{run}:KonfiguracjaAktualizacji"));

            // A newer revision: the store is backed up to the operator's folder first, then the ADMX is replaced.
            var changed = await GroupPolicyPlanner.CompareCentralStoreAsync(sysvol, Package(name, newer), options, Ct);
            Assert.Equal(CentralStoreFileStatus.Changed, changed.Files.Single(f => f.RelativePath == name + ".admx").Status);
            Assert.Equal(CentralStoreFileStatus.Unchanged, changed.Files.Single(f => f.RelativePath == "en-US/" + name + ".adml").Status);
            var backup = await CentralStoreBackup.WriteAsync(Path.Combine(tools.Directory, "Kopie magazynu – Łódź"), admin.Info.Server, changed.StoreFiles,
                TimeProvider.System.GetUtcNow(), Ct);
            Assert.Equal(admx, await File.ReadAllBytesAsync(Path.Combine(backup, name + ".admx"), Ct));
            var replaced = await ExecuteOkAsync(_planner.PlanCentralStoreUpdate(admin, changed, $"K3T-{run} nowsza wersja"));
            Assert.Equal(newer, await ReadStoreAsync(sysvol, name + ".admx"));

            // Undo of the replacement puts the old file back; undo of the first update deletes this run's files.
            await ExecuteOkAsync(new RollbackPlanner().CreateRollbackPlan(replaced));
            Assert.Equal(admx, await ReadStoreAsync(sysvol, name + ".admx"));
            await ExecuteOkAsync(new RollbackPlanner().CreateRollbackPlan(first));
            first = null;
            foreach (var path in new[] { name + ".admx", "en-US/" + name + ".adml", "pl-PL/" + name + ".adml" })
            {
                Assert.Equal(SysvolFileStatus.NotFound, (await sysvol.ReadPolicyDefinitionsFileAsync(path, 1 << 20, Ct)).Status);
            }

            await AssertNoTempFilesAsync(sysvol);
        }
        finally
        {
            if (first is not null)
            {
                await Execute(new RollbackPlanner().CreateRollbackPlan(first));
            }

            if (!existed)
            {
                // The lab had no central store: the folders this test created go too (only when empty).
                await tools.RemoveEmptyPoliciesFoldersAsync(
                    [SysvolPaths.PolicyDefinitionsFolder + "/en-US", SysvolPaths.PolicyDefinitionsFolder + "/pl-PL", SysvolPaths.PolicyDefinitionsFolder]);
            }
        }
    }

    /// <summary>The sample Contoso template with this run's namespace (no collision with another template of the store).</summary>
    private static byte[] Admx(string run, string revision) => SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmx
        .Replace(SampleAdministrativeTemplates.ContosoNamespace, "Contoso.Policies.K3T" + run, StringComparison.Ordinal)
        .Replace("<policyDefinitions revision=\"1.2\"", "<policyDefinitions revision=\"" + revision + "\"", StringComparison.Ordinal));

    /// <summary>A vendor-like package: the ADMX, en-US and a neutral "pl" folder (the store's pl-PL).</summary>
    private static PolicyTemplatePackage Package(string name, byte[] admx) => PolicyTemplatePackage.FromFiles("K3T", new Dictionary<string, byte[]>
    {
        [name + ".admx"] = admx,
        ["en-US/" + name + ".adml"] = SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmlEn),
        ["pl/" + name + ".adml"] = SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmlPl),
    });

    /// <summary>How samba-tool's JSON writes a text (Python's json: backslashes doubled, non-ASCII as \uxxxx – compared ignoring case).</summary>
    private static string JsonEscaped(string text) => System.Text.Json.JsonSerializer.Serialize(text)[1..^1];

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

    private static async Task<byte[]?> ReadStoreAsync(ISysvolWriteConnection sysvol, string path) =>
        (await sysvol.ReadPolicyDefinitionsFileAsync(path, 1 << 20, Ct)).Content;

    private static async Task AssertNoTempFilesAsync(ISysvolWriteConnection sysvol) =>
        Assert.DoesNotContain(await sysvol.ListPolicyDefinitionsAsync(GroupPolicyPlanner.MaxCentralStoreFiles, Ct) ?? [], e => GpoTransaction.IsTempName(e.RelativePath));
}
