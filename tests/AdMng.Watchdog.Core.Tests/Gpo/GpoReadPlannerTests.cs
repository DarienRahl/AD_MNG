using AdMng.Core.GroupPolicy.Content;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Gpo;
using AdMng.Watchdog.Testing.Gpo;

namespace AdMng.Watchdog.Core.Tests.Gpo;

/// <summary>The read plan of stage B design 8.3/8.4: roles, priorities, inventory and every limit.</summary>
public sealed class GpoReadPlannerTests
{
    private static readonly Guid Gpo = GpoFixtures.Gpo;

    [Fact]
    public async Task SampleGpo()
    {
        var plan = GpoReadPlanner.Plan(await ListAsync(GpoFixtures.SampleGpo()));

        Assert.Empty(plan.Problems);
        Assert.Empty(plan.NotRead);
        Assert.Equal("GPT.INI", plan.Read[0].Path);
        Assert.Equal(GpoFileRole.SecurityTemplate, plan.Read[1].Role);
        Assert.Equal(
        [
            GpoFileRole.GptIni, GpoFileRole.SecurityTemplate, GpoFileRole.AuditCsv, GpoFileRole.RegistryPol, GpoFileRole.RegistryPol,
            GpoFileRole.PsScriptsIni, GpoFileRole.ScriptsIni, GpoFileRole.Preferences, GpoFileRole.Preferences, GpoFileRole.Preferences,
            GpoFileRole.Preferences, GpoFileRole.ScriptFile, GpoFileRole.GpoComment, GpoFileRole.Comment,
        ], plan.Read.Select(f => f.Role));
        Assert.Contains(plan.Inventory, e => e.Path == "Adm/system.adm" && !e.IsDirectory);
        Assert.Contains(plan.Inventory, e => e.Path == "Machine/Microsoft/Windows NT/SecEdit" && e.IsDirectory);
        Assert.DoesNotContain(plan.Read, f => f.Path.EndsWith("edb.log", StringComparison.Ordinal));
        Assert.Equal("Groups", plan.Read.Single(f => f.Path.EndsWith("Groups.xml", StringComparison.Ordinal)).PreferenceType);
        Assert.Equal(GpoScope.User, plan.Read.Single(f => f.Path == "User/Registry.pol").Scope);
    }

    [Fact]
    public void ClassifierIsCaseInsensitive()
    {
        Assert.Equal(GpoFileRole.SecurityTemplate, GpoFileClassifier.Classify("MACHINE/microsoft/WINDOWS NT/secedit/gpttmpl.INF").Role);
        Assert.Equal(GpoFileRole.RegistryPol, GpoFileClassifier.Classify("user/registry.POL").Role);
        Assert.Equal(GpoFileRole.Inventory, GpoFileClassifier.Classify("User/Microsoft/Windows NT/SecEdit/GptTmpl.inf").Role);
        Assert.Equal(GpoFileRole.Inventory, GpoFileClassifier.Classify("Machine/Preferences/Groups/Groups.bak").Role);
        Assert.Equal(GpoFileRole.ScriptFile, GpoFileClassifier.Classify("User/Scripts/Logon/a.ps1").Role);
    }

    [Fact]
    public async Task FileAboveTheFileLimitIsNotRead()
    {
        var reader = GpoFixtures.SampleGpo().SetFile(Gpo, "Machine/Preferences/Files/Files.xml", new byte[2 * 1024 * 1024]);
        var plan = GpoReadPlanner.Plan(await ListAsync(reader), GpoReadLimits.From(new GpoContentSettings { MaxFileMb = 1 }));

        Assert.Equal(["Machine/Preferences/Files/Files.xml"], plan.NotRead.Select(f => f.Path));
        Assert.Equal([GpoReadProblems.MaxFileSize], plan.Problems);
        Assert.Contains(plan.Inventory, e => e.Path == "Machine/Preferences/Files/Files.xml" && e.Size == 2 * 1024 * 1024);
    }

    [Fact]
    public async Task TotalLimitKeepsTheSecurityFilesFirst()
    {
        var reader = new InMemorySysvolReader()
            .SetFile(Gpo, GpoFixtures.GptTmplPath, new byte[600 * 1024])
            .SetFile(Gpo, "Machine/Preferences/Groups/Groups.xml", new byte[600 * 1024]);
        var plan = GpoReadPlanner.Plan(await ListAsync(reader), new GpoReadLimits { MaxTotalBytes = 1024 * 1024 });

        Assert.Equal([GpoFixtures.GptTmplPath], plan.Read.Select(f => f.Path));
        Assert.Equal(["Machine/Preferences/Groups/Groups.xml"], plan.NotRead.Select(f => f.Path));
        Assert.Equal([GpoReadProblems.MaxTotalSize], plan.Problems);
    }

    [Fact]
    public async Task LargeScriptsAreListedWithTheirSizeOnly()
    {
        var reader = new InMemorySysvolReader().SetFile(Gpo, "Machine/Scripts/Startup/duzy.exe", new byte[GpoReadLimits.ScriptHashBytes + 1]);
        var plan = GpoReadPlanner.Plan(await ListAsync(reader));

        Assert.Empty(plan.Read);
        Assert.Equal("Machine/Scripts/Startup/duzy.exe", Assert.Single(plan.LargeScripts).Path);
        Assert.Empty(plan.Problems);
    }

    [Fact]
    public void EntryLimitDepthLimitAndHostilePaths()
    {
        var entries = new List<SysvolEntry>
        {
            new() { RelativePath = "../../etc/shadow", Size = 10 },
            new() { RelativePath = "/etc/passwd", Size = 10 },
            new() { RelativePath = "Machine/x", Size = -1 },
            new() { RelativePath = string.Join('/', Enumerable.Range(0, 14).Select(i => "D" + GpoFixtures.Invariant(i))) + "/deep.txt", Size = 1 },
            new() { RelativePath = "GPT.INI", Size = 30 },
            new() { RelativePath = "gpt.ini", Size = 31 },
        };
        entries.AddRange(Enumerable.Range(0, 30).Select(i => new SysvolEntry { RelativePath = "Zz/f" + GpoFixtures.Invariant(i), Size = 1 }));

        var plan = GpoReadPlanner.Plan(new SysvolListing { Entries = entries }, new GpoReadLimits { MaxEntries = 20 });

        Assert.Equal(20, plan.Inventory.Count);
        Assert.Contains(GpoReadProblems.InvalidPath, plan.Problems);
        Assert.Contains(GpoReadProblems.MaxDepth, plan.Problems);
        Assert.Contains(GpoReadProblems.MaxEntries, plan.Problems);
        Assert.Single(plan.Inventory, e => e.Path.Equals("GPT.INI", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Inventory, e => e.Path.Contains("..", StringComparison.Ordinal) || e.Path.Contains("etc", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InventoryComparison()
    {
        var reader = GpoFixtures.SampleGpo();
        var stored = GpoReadPlanner.Inventory(await ListAsync(reader));

        Assert.False(GpoInventoryComparer.Differs(stored, GpoReadPlanner.Inventory(await ListAsync(reader))));

        // same size, newer time: a direct SYSVOL edit without a version change (8.1)
        reader.SetFile(Gpo, GpoFixtures.GptTmplPath, GpoFixtures.Sysvol("GptTmpl.inf"), reader.DefaultModifiedAt.AddMinutes(5));
        Assert.True(GpoInventoryComparer.Differs(stored, GpoReadPlanner.Inventory(await ListAsync(reader))));

        // sub-second differences do not count (smbclient prints seconds)
        var shifted = stored.Select(e => e with { ModifiedAt = e.ModifiedAt.AddMilliseconds(300) }).ToList();
        Assert.False(GpoInventoryComparer.Differs(stored, shifted));
        Assert.True(GpoInventoryComparer.Differs(stored, stored.Skip(1).ToList()));
    }

    internal static Task<SysvolListing> ListAsync(InMemorySysvolReader reader) =>
        reader.ListAsync(GpoFixtures.Folder, TestContext.Current.CancellationToken);
}
