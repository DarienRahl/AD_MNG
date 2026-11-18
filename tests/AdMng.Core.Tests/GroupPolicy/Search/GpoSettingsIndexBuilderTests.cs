using System.Globalization;
using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Search;
using AdMng.Testing;

namespace AdMng.Core.Tests.GroupPolicy.Search;

/// <summary>The settings index (21.3) on the GPO management sample: full and incremental builds, statuses, budgets, two passes.</summary>
public sealed class GpoSettingsIndexBuilderTests : GroupPolicyTestBed
{
    private const string LengthBaseId = "K|Computer/Security/System Access/MinimumPasswordLength";

    [Fact]
    public async Task Full_ReadsEveryGpo_WithLinksUnitsAndDirectoryFacts()
    {
        Session.Seed($"CN=Class Store,CN=Machine,{GpoDn(Sample.ServerSecurity)}", new Dictionary<string, object?> { [Attr.ObjectClass] = new[] { "top", "classStore" } });
        await using var sysvol = await OpenSysvolAsync();
        var progress = new List<GpoIndexProgress>();

        var index = await GpoSettingsIndexBuilder.BuildAsync(Session, sysvol, new GpoSettingsIndexRequest { Generation = 3 }, new SyncProgress<GpoIndexProgress>(progress.Add), Ct);

        Assert.Equal((Session.Info.Server, Builder.DomainDn, Builder.DomainDnsName, Builder.DomainSid, 3L, true),
            (index.Server, index.DomainDn, index.DomainDnsName, index.DomainSid, index.Generation, index.SysvolRead));
        Assert.Equal(7, index.Entries.Count);
        Assert.All(index.Entries, e => Assert.Equal(GpoIndexStatus.Read, e.Status));
        Assert.Equal(new[] { Sample.WorkstationPasswords, Sample.DefaultDomainPolicy }.Order(), index.Occurrences(LengthBaseId).Select(o => o.GpoId).Order());
        Assert.Contains(LengthBaseId, index.BaseIds);
        Assert.Contains(LengthBaseId.ToUpperInvariant(), Assert.IsAssignableFrom<IReadOnlySet<string>>(index.BaseIds)); // a case-insensitive set
        Assert.Equal("I|User|Drives|Drive|K", Assert.Single(index.Find(Sample.DriveMappings)!.Units).BaseId);
        Assert.Equal(GpoSettingUnitKind.Comment, index.Find(Sample.WorkstationPasswords)!.Units.Single(u => u.Scope == GpoScope.Gpo).Kind);
        Assert.Equal(1, index.Find(Sample.ServerSecurity)!.DirectorySettings);
        Assert.True(index.Find(Sample.ServerSecurity)!.IsLinked);
        Assert.Equal(Sample.SiteDn, Assert.Single(index.Find(Sample.SitePrinters)!.Links).ContainerDn, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(GpoVersion.FromInt32(1 << 16), index.Find(Sample.DriveMappings)!.Gpo.SysvolVersion);
        Assert.Contains(index.Containers, c => c.Kind == GroupPolicySomKind.Site);
        Assert.Null(index.Find(Guid.NewGuid()));
        var groupIt = Builder.SidOf(Company.GroupIt);
        Assert.Equal(Sample.WorkstationRemoteDesktop, Assert.Single(index.WithPrincipal(groupIt).Select(r => r.GpoId).Distinct()));
        Assert.Equal(index.WithPrincipal(groupIt).Count, index.WithPrincipal("*" + groupIt.ToLowerInvariant()).Count);
        Assert.Equal(
            [GpoIndexStage.Directory, GpoIndexStage.Versions, GpoIndexStage.Folders, GpoIndexStage.Principals, GpoIndexStage.Units, GpoIndexStage.Completed],
            progress.Select(p => p.Stage).Distinct());
        Assert.Equal("7 GPOs, generation 3", index.ToString());
    }

    [Fact]
    public async Task Entries_KeepNoRawBytes()
    {
        await using var sysvol = await OpenSysvolAsync();

        var index = await GpoSettingsIndexBuilder.BuildAsync(Session, sysvol, new GpoSettingsIndexRequest(), null, Ct);

        var properties = typeof(GpoIndexEntry).GetProperties().Where(p => p.Name != nameof(GpoIndexEntry.Gpo)).ToList();
        Assert.DoesNotContain(properties, p => p.PropertyType == typeof(byte[]) || p.PropertyType == typeof(GpoFolderContent));
        Assert.All(index.Entries, e => Assert.DoesNotContain("Księgowość", e.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Incremental_ReReadsOnlyTheChangedGpo_DropsDeletedOnes()
    {
        await using var sysvol = await OpenSysvolAsync();
        var first = await GpoSettingsIndexBuilder.BuildAsync(Session, sysvol, new GpoSettingsIndexRequest(), null, Ct);
        Bump(Sample.WorkstationPasswords, "MinimumPasswordLength = 14");
        await Session.DeleteAsync(GpoDn(Sample.SitePrinters), subtree: true, Ct);
        Sysvol.ClearCalls();

        var second = await GpoSettingsIndexBuilder.BuildAsync(Session, sysvol, new GpoSettingsIndexRequest { Previous = first, Generation = 2 }, null, Ct);

        var lists = Sysvol.Calls.Where(c => c.Operation == "List").ToList();
        Assert.Equal(Sample.WorkstationPasswords, Assert.Single(lists).GpoId);
        Assert.Equal(6, second.Entries.Count);
        Assert.Null(second.Find(Sample.SitePrinters));
        Assert.Equal("REG:14", ValueOf(second, Sample.WorkstationPasswords)); // re-read
        Assert.Same(first.Find(Sample.DefaultDomainPolicy)!.Settings, second.Find(Sample.DefaultDomainPolicy)!.Settings); // reused
        Assert.Equal(2, second.Generation);
    }

    [Fact]
    public async Task Incremental_AnotherServerOrDefinitionSet_ReadsEverything()
    {
        await using var sysvol = await OpenSysvolAsync();
        var first = await GpoSettingsIndexBuilder.BuildAsync(Session, sysvol, new GpoSettingsIndexRequest(), null, Ct);
        Sysvol.ClearCalls();

        await GpoSettingsIndexBuilder.BuildAsync(Session, sysvol, new GpoSettingsIndexRequest { Previous = first, Definitions = SampleAdministrativeTemplates.LoadPolish() }, null, Ct);

        Assert.Equal(7, Sysvol.Calls.Count(c => c.Operation == "List"));
    }

    [Fact]
    public async Task WithDefinitions_RebuildsPolicyUnits_WithoutIo()
    {
        var id = Builder.AddManagedGroupPolicy(Sysvol, "Eksplorator – Podgląd", 0, 1);
        var document = new RegistryPolicyDocument();
        document.Set(@"Software\Policies\Contoso\Eksplorator", "NoPreview", RegistryFormat.RegDword, BitConverter.GetBytes(1u));
        Sysvol.Put(id, "Machine/Registry.pol", document.ToBytes());
        await using var sysvol = await OpenSysvolAsync();
        var index = await GpoSettingsIndexBuilder.BuildAsync(Session, sysvol, new GpoSettingsIndexRequest(), null, Ct);
        Sysvol.ClearCalls();
        Session.ClearOperations();
        var definitions = SampleAdministrativeTemplates.LoadPolish();

        var withDefinitions = index.WithDefinitions(definitions);

        Assert.Empty(Sysvol.Calls);
        Assert.Empty(Session.Operations);
        Assert.Same(definitions, withDefinitions.Definitions);
        Assert.Equal(GpoSettingUnitKind.RegistryValue, Assert.Single(index.Find(id)!.Units).Kind);
        var policy = Assert.Single(withDefinitions.Find(id)!.Units);
        Assert.Equal(("P|Machine|" + SampleAdministrativeTemplates.HidePreview, GpoSettingUnitKind.AdministrativeTemplate), (policy.BaseId, policy.Kind));
        Assert.Single(withDefinitions.Find(id)!.ComputerPolicies.Policies);
        Assert.Single(withDefinitions.Occurrences(policy.BaseId));
        Assert.Equal(index.Entries.Count, withDefinitions.Entries.Count);
    }

    [Fact]
    public async Task NoSysvol_DirectoryDataOnly_EveryEntryNotRead()
    {
        var index = await GpoSettingsIndexBuilder.BuildAsync(Session, null, new GpoSettingsIndexRequest(), null, Ct);

        Assert.False(index.SysvolRead);
        Assert.All(index.Entries, e => Assert.Equal((GpoIndexStatus.NotRead, 0), (e.Status, e.Units.Count)));
        Assert.True(index.Find(Sample.ServerSecurity)!.IsLinked);
        Assert.Empty(Sysvol.Calls);
    }

    [Fact]
    public async Task StatusesAndBudgets_IncompleteNotFoundAccessDeniedNotRead()
    {
        var missing = Builder.AddManagedGroupPolicy(Sysvol, "Bez folderu", 0, 1);
        await using var sysvol = await OpenSysvolAsync();
        await sysvol.DeleteGpoFolderAsync(missing, Ct);
        var budget = Builder.AddManagedGroupPolicy(Sysvol, "Duży komentarz", 0, 1);
        Sysvol.PutText(budget, "GPO.cmt", new string('ż', 3000));
        Sysvol.PutText(budget, "Machine/Applications/pakiet.aas", "x");
        var denied = Builder.AddManagedGroupPolicy(Sysvol, "Zabroniony", 0, 1);
        Sysvol.Put(denied, SecurityTemplate.RelativePath, FakeSysvolFileReader.Utf16(FakeDirectoryBuilder.Template("[System Access]", "LockoutBadCount = 3")));
        Sysvol.BeforeCall = (operation, gpo, _) =>
        {
            if (operation == "Read" && gpo == denied)
            {
                throw new SysvolWriteException(SysvolWriteError.AccessDenied, "test");
            }
        };

        var index = await GpoSettingsIndexBuilder.BuildAsync(Session, sysvol,
            new GpoSettingsIndexRequest { Folders = GpoFolderReadOptions.Settings with { MaxFileBytes = 2000 } }, null, Ct);

        Assert.Equal(GpoIndexStatus.NotFound, index.Find(missing)!.Status);
        var incomplete = index.Find(budget)!;
        Assert.Equal((GpoIndexStatus.Incomplete, true), (incomplete.Status, incomplete.HasContent));
        Assert.Equal(["GPO.cmt"], incomplete.NotReadFiles);
        Assert.Equal(["Machine/Applications/pakiet.aas"], incomplete.OtherFiles);
        Assert.Equal(GpoIndexStatus.AccessDenied, index.Find(denied)!.Status);
    }

    [Fact]
    public async Task RunBudget_LeavesTheRestNotRead()
    {
        await using var sysvol = await OpenSysvolAsync();

        var index = await GpoSettingsIndexBuilder.BuildAsync(Session, sysvol,
            new GpoSettingsIndexRequest { Folders = GpoFolderReadOptions.Settings with { Parallelism = 1, MaxTotalBytes = 1 } }, null, Ct);

        Assert.Single(index.Entries, e => e.HasContent); // the first GPO with content uses up the budget
        Assert.Contains(index.Entries, e => e.Status == GpoIndexStatus.NotRead);
    }

    [Fact]
    public async Task TwoPasses_NamesResolvedBeforeTheUnits_OneLdapBatch()
    {
        var byName = Builder.AddManagedGroupPolicy(Sysvol, "Pulpit – po nazwie", 0, 1);
        Sysvol.Put(byName, SecurityTemplate.RelativePath, FakeSysvolFileReader.Utf16(FakeDirectoryBuilder.Template(
            "[Privilege Rights]", @"SeRemoteInteractiveLogonRight = CONTOSO\GG_IT,CONTOSO\jan.kowalski")));
        var bySid = Builder.AddManagedGroupPolicy(Sysvol, "Pulpit – po SID", 0, 1);
        Sysvol.Put(bySid, SecurityTemplate.RelativePath, FakeSysvolFileReader.Utf16(FakeDirectoryBuilder.Template(
            "[Privilege Rights]", $"SeRemoteInteractiveLogonRight = *{Builder.SidOf(Company.GroupIt)},*{Builder.SidOf(Company.JanKowalski)}")));
        await using var sysvol = await OpenSysvolAsync();
        Session.ClearOperations();

        var index = await GpoSettingsIndexBuilder.BuildAsync(Session, sysvol, new GpoSettingsIndexRequest(), null, Ct);

        var a = Assert.Single(index.Find(byName)!.Units);
        var b = Assert.Single(index.Find(bySid)!.Units);
        Assert.True(GpoSettingUnits.SameValue(a, b));
        Assert.Equal(Builder.SidOf(Company.GroupIt), index.SidsByName[@"contoso\gg_it"]);
        Assert.Single(Session.Operations, o => o.Kind == FakeOperationKind.Search && o.Search!.Filter.Contains("sAMAccountName", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, index.WithPrincipal(Builder.SidOf(Company.JanKowalski)).Count);

        // Incremental: the names are kept, no new lookup for unchanged GPOs.
        Session.ClearOperations();
        var again = await GpoSettingsIndexBuilder.BuildAsync(Session, sysvol, new GpoSettingsIndexRequest { Previous = index }, null, Ct);
        Assert.DoesNotContain(Session.Operations, o => o.Kind == FakeOperationKind.Search && o.Search!.Filter.Contains("sAMAccountName", StringComparison.OrdinalIgnoreCase));
        Assert.True(GpoSettingUnits.SameValue(Assert.Single(again.Find(byName)!.Units), b));
    }

    [Fact]
    public async Task FiveHundredGpos_AtMostFourSysvolCalls_AllRead()
    {
        var builder = new FakeDirectoryBuilder();
        var company = builder.AddSampleCompany();
        var sysvol = new FakeSysvol();
        builder.AddGroupPolicyManagementSample(company, sysvol);
        var template = FakeSysvolFileReader.Utf16(FakeDirectoryBuilder.Template("[System Access]", "MinimumPasswordLength = 10", "[Privilege Rights]", "SeDebugPrivilege = *S-1-5-32-544"));
        var drives = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?><Drives clsid=\"{8FDDCC1A-0C3C-43cd-A6B4-71A6DF20DA8C}\"><Drive clsid=\"{935D1B74-9CB8-4e3c-9914-7DD559B7A417}\" name=\"S:\" uid=\"{0D1F7F22-6D1B-4A35-9D9C-0E3D9F3F0009}\"><Properties action=\"U\" path=\"\\\\fs01\\Sprzedaż\" letter=\"S\"/></Drive></Drives>");
        for (var i = 0; i < 493; i++)
        {
            var id = AddPlainGpo(builder, sysvol, i);
            sysvol.Put(id, SecurityTemplate.RelativePath, template);
            sysvol.Put(id, "User/Preferences/Drives/Drives.xml", drives);
        }

        await using var inner = await sysvol.OpenForWriteAsync(builder.Session, Ct);
        var counting = new CountingSysvolConnection(inner) { Delay = TimeSpan.FromMilliseconds(1) };

        var index = await GpoSettingsIndexBuilder.BuildAsync(builder.Session, counting, new GpoSettingsIndexRequest(), null, Ct);

        Assert.Equal(500, index.Entries.Count);
        Assert.All(index.Entries, e => Assert.Equal(GpoIndexStatus.Read, e.Status));
        Assert.InRange(counting.MaxConcurrent, 1, 4);
        Assert.Equal(494, index.Occurrences("I|User|Drives|Drive|S").Count + index.Occurrences("I|User|Drives|Drive|K").Count);
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------------

    /// <summary>A GPO container with CN=Machine/User and a folder, without the DACL work of <c>AddManagedGroupPolicy</c>.</summary>
    private static Guid AddPlainGpo(FakeDirectoryBuilder builder, FakeSysvol sysvol, int i)
    {
        var id = builder.AddGroupPolicyObject(string.Create(CultureInfo.InvariantCulture, $"Oddział {i:000} – Sprzedaż"), attributes: new Dictionary<string, object?> { ["versionNumber"] = 1 });
        sysvol.AddGpoFolder(id, 1);
        return id;
    }

    private void Bump(Guid id, string line)
    {
        Sysvol.Put(id, SecurityTemplate.RelativePath, FakeSysvolFileReader.Utf16(FakeDirectoryBuilder.Template("[System Access]", line)));
        Sysvol.PutText(id, "GPT.INI", "[General]\r\nVersion=5\r\n");
        Session.SetValues(GpoDn(id), "versionNumber", "5");
    }

    private static string ValueOf(GpoSettingsIndex index, Guid id) =>
        "REG:" + index.Find(id)!.Settings.Single(s => s.Key.EndsWith("MinimumPasswordLength", StringComparison.Ordinal)).Value;

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
