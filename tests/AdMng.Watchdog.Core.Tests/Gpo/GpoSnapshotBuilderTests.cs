using System.Text;
using System.Text.Json;
using AdMng.Watchdog.Core.Gpo;
using AdMng.Watchdog.Testing.Gpo;

namespace AdMng.Watchdog.Core.Tests.Gpo;

/// <summary>A whole GPO read through the SYSVOL abstraction into a snapshot (stage B design 8.3–8.5), failures fail closed per file.</summary>
public sealed class GpoSnapshotBuilderTests
{
    private static readonly DateTimeOffset ReadAt = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Gpo = GpoFixtures.Gpo;

    /// <summary>Secrets of the fixtures (made up) – never in a snapshot, not even hashed.</summary>
    private static readonly string[] Secrets =
    [
        "Zm9vYmFyLW5vdC1hLXJlYWwtY3Bhc3N3b3JkLTIwMjY=", "UHJ6eWvFgmFkb3dlLWhhc8WCby1uaWUtcHJhd2R6aXdl", "Hasło!Autologon1",
        "Tajne!Hasło2026", "public-monitoring",
    ];

    [Fact]
    public async Task SampleGpoSnapshot()
    {
        var result = await ReadAsync(GpoFixtures.SampleGpo());
        var snapshot = result.Snapshot;

        Assert.Equal(GpoContentState.Complete, snapshot.State);
        Assert.Empty(result.UnreadableFiles);
        Assert.False(result.TooLarge);
        Assert.Equal(131075, snapshot.GptVersion);
        Assert.Equal(131075, snapshot.GpcVersion);
        Assert.Equal(GpoFixtures.Dc, snapshot.Dc);
        Assert.Equal("REG_DWORD:1", Value(snapshot, @"Computer/Registry/HKLM\Software\Policies\Microsoft\Windows\WindowsUpdate\AU!NoAutoUpdate"));
        Assert.Equal("REG_SZ:900", Value(snapshot, @"User/Registry/HKCU\Software\Policies\Microsoft\Windows\Control Panel\Desktop!ScreenSaveTimeOut"));
        Assert.Equal("14", Value(snapshot, "Computer/Security/System Access/MinimumPasswordLength"));
        Assert.Equal("Success and Failure", Value(snapshot, "Computer/Audit/{0cce923f-69ae-11d9-bed3-505054503030}"));
        Assert.Equal("mapuj-dyski.cmd", Value(snapshot, "Computer/Scripts/Startup/0"));
        Assert.Equal("Zasada dla kontrolerów domeny – właściciel: zespół Tier 0.", Value(snapshot, "Gpo/Comment"));
        var script = Setting(snapshot, "Computer/Scripts/Files/Startup/mapuj-dyski.cmd");
        Assert.Equal(GpoSettingKind.Hash, script.Kind);
        Assert.Equal(GpoValues.Hash(Encoding.ASCII.GetBytes("@echo off\r\nnet use Z: \\\\fs01\\dane\r\n")) + " size:35", script.Value);
        Assert.Contains(snapshot.Inventory, e => e.Path == "Adm/system.adm");
        Assert.Equal(snapshot.Settings.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase).Select(s => s.Key), snapshot.Settings.Select(s => s.Key));
        Assert.Equal(snapshot.Settings.Count, snapshot.Settings.Select(s => s.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task SecretsAreNeverInTheSnapshot()
    {
        var snapshot = (await ReadAsync(GpoFixtures.SampleGpo())).Snapshot;
        var json = JsonSerializer.Serialize(snapshot);

        foreach (var secret in Secrets)
        {
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
            Assert.DoesNotContain(GpoValues.Hash(secret)[GpoValues.HashPrefix.Length..], json, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)), json, StringComparison.Ordinal);
        }

        Assert.True(GpoSettingsDiffer.HasGppPassword(snapshot.Settings));
    }

    [Fact]
    public async Task OnlyThePinnedDcFolderIsAddressed()
    {
        var reader = GpoFixtures.SampleGpo();

        await ReadAsync(reader);

        Assert.All(reader.Calls, c => Assert.Equal(GpoFixtures.Folder, c.Folder));
        Assert.Equal(["list", "read"], reader.Calls.Select(c => c.Operation));
    }

    [Fact]
    public async Task UnparsableFileKeepsItsPreviousSettings()
    {
        var reader = GpoFixtures.SampleGpo();
        var first = (await ReadAsync(reader)).Snapshot;
        reader.SetFile(Gpo, GpoFixtures.GptTmplPath, [0xFE, 0xFF, 0x00, 0x5B]); // UTF-16BE: refused

        var second = await ReadAsync(reader, first);

        Assert.Equal([GpoFixtures.GptTmplPath], second.UnreadableFiles);
        Assert.True(second.HasParseErrors);
        Assert.Equal(GpoContentState.Partial, second.Snapshot.State);
        var marker = Setting(second.Snapshot, GpoSnapshotBuilder.FileMarkerPrefix + GpoFixtures.GptTmplPath);
        Assert.Equal(GpoSettingKind.Unreadable, marker.Kind);
        Assert.Equal("14", Value(second.Snapshot, "Computer/Security/System Access/MinimumPasswordLength")); // carried over
        var diff = GpoSettingsDiffer.Diff(first.Settings, second.Snapshot.Settings);
        var row = Assert.Single(diff.Changes);
        Assert.Equal((GpoSnapshotBuilder.FileMarkerPrefix + GpoFixtures.GptTmplPath, GpoSettingsDiffer.UnreadableValue), (row.Attribute, Assert.Single(row.NewValues)));
        Assert.True(diff.SensitiveChanged); // an unreadable security template counts as sensitive (fail closed)

        // fixed again: only the marker disappears
        reader.SetFile(Gpo, GpoFixtures.GptTmplPath, GpoFixtures.Sysvol("GptTmpl.inf"));
        var third = await ReadAsync(reader, second.Snapshot);
        var back = Assert.Single(GpoSettingsDiffer.Diff(second.Snapshot.Settings, third.Snapshot.Settings).Changes);
        Assert.Equal([GpoSettingsDiffer.UnreadableValue], back.OldValues);
        Assert.Empty(back.NewValues);
    }

    [Fact]
    public async Task ReadErrorsMarkTheFile()
    {
        var reader = GpoFixtures.SampleGpo().FailFile(Gpo, "Machine/Registry.pol", SysvolErrorKind.AccessDenied, "NT_STATUS_ACCESS_DENIED");

        var result = await ReadAsync(reader);

        Assert.Equal(["Machine/Registry.pol"], result.UnreadableFiles);
        Assert.Equal(GpoSettingCategory.SecurityRegistry, Setting(result.Snapshot, "File/Machine/Registry.pol").Category);
    }

    [Fact]
    public async Task SettingsLimitMakesTheFileTooLarge()
    {
        var reader = GpoFixtures.SampleGpo();
        var plan = GpoReadPlanner.Plan(await GpoReadPlannerTests.ListAsync(reader));
        var files = await reader.ReadAsync(GpoFixtures.Folder, plan.Read.Select(f => f.Path).ToList(), long.MaxValue, TestContext.Current.CancellationToken);

        var result = GpoSnapshotBuilder.Build(new GpoSnapshotInput
        {
            GpoGuid = Gpo,
            Dc = GpoFixtures.Dc,
            ReadAt = ReadAt,
            Plan = plan,
            Files = files,
            Limits = new GpoReadLimits { MaxSettings = 50 },
        });

        Assert.Contains(GpoReadProblems.MaxSettings, result.Problems);
        Assert.True(result.TooLarge);
        Assert.Contains(GpoFixtures.GptTmplPath, result.NotReadFiles);
        Assert.True(result.Snapshot.Settings.Count(s => s.Kind != GpoSettingKind.Unreadable) <= 50);
    }

    [Fact]
    public async Task FileReportedTooLargeByTheReader()
    {
        var reader = GpoFixtures.SampleGpo();
        var plan = GpoReadPlanner.Plan(await GpoReadPlannerTests.ListAsync(reader));
        reader.SetFile(Gpo, "Machine/Preferences/Groups/Groups.xml", new byte[GpoReadLimits.Default.MaxFileBytes + 1]); // grew after the listing

        var result = await ReadAsync(reader, previous: null, plan);

        Assert.Equal(["Machine/Preferences/Groups/Groups.xml"], result.NotReadFiles);
        Assert.Equal(GpoSettingCategory.PreferenceGroups, Setting(result.Snapshot, "File/Machine/Preferences/Groups/Groups.xml").Category);
    }

    [Fact]
    public void RegistryPolWithMoreThan10000ValuesIsTooLarge()
    {
        var entries = Enumerable.Range(0, 12_000)
            .Select(i => (@"Software\Policies\Masowe", "V" + GpoFixtures.Invariant(i), 4u, GpoFixtures.Dword((uint)i)))
            .ToArray();
        var plan = new GpoReadPlan { Read = [GpoFileClassifier.Classify("Machine/Registry.pol")] };

        var result = GpoSnapshotBuilder.Build(new GpoSnapshotInput
        {
            GpoGuid = Gpo,
            Dc = GpoFixtures.Dc,
            ReadAt = ReadAt,
            Plan = plan,
            Files = [new SysvolFile { RelativePath = "Machine/Registry.pol", Content = GpoFixtures.Pol(entries) }],
        });

        Assert.Equal([GpoReadProblems.MaxSettings], result.Problems);
        Assert.Equal(GpoSettingKind.Unreadable, Assert.Single(result.Snapshot.Settings).Kind);
    }

    [Fact]
    public void LaterDuplicateKeyWins()
    {
        var pol = GpoFixtures.Pol(
            (@"Software\Policies\T", "V", 4, GpoFixtures.Dword(1)),
            (@"SOFTWARE\Policies\T", "v", 4, GpoFixtures.Dword(2)));
        var plan = new GpoReadPlan { Read = [GpoFileClassifier.Classify("Machine/Registry.pol")] };

        var result = GpoSnapshotBuilder.Build(new GpoSnapshotInput
        {
            GpoGuid = Gpo,
            Dc = GpoFixtures.Dc,
            ReadAt = ReadAt,
            Plan = plan,
            Files = [new SysvolFile { RelativePath = "Machine/Registry.pol", Content = pol }],
        });

        Assert.Equal("REG_DWORD:2", Assert.Single(result.Snapshot.Settings).Value);
    }

    internal static async Task<GpoReadResult> ReadAsync(InMemorySysvolReader reader, GpoContentSnapshot? previous = null, GpoReadPlan? plan = null)
    {
        var ct = TestContext.Current.CancellationToken;
        plan ??= GpoReadPlanner.Plan(await reader.ListAsync(GpoFixtures.Folder, ct));
        var files = await reader.ReadAsync(GpoFixtures.Folder, plan.Read.Select(f => f.Path).ToList(), GpoReadLimits.Default.MaxFileBytes, ct);
        return GpoSnapshotBuilder.Build(new GpoSnapshotInput
        {
            GpoGuid = Gpo,
            Dc = GpoFixtures.Dc,
            GpcVersion = 131075,
            ReadAt = ReadAt,
            Plan = plan,
            Files = files,
            Previous = previous,
        });
    }

    internal static GpoSetting Setting(GpoContentSnapshot snapshot, string key) =>
        Assert.Single(snapshot.Settings, s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    private static string Value(GpoContentSnapshot snapshot, string key) => Setting(snapshot, key).Value;
}
