using System.Reflection;
using System.Text.Json;
using AdMng.Core.Directory;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Tests.TestDoubles;
using AdMng.Presentation.Views;

namespace AdMng.Presentation.Tests.Settings;

public sealed class SettingsTests : IDisposable
{
    private readonly TempAppPaths _paths = new();

    public void Dispose() => _paths.Dispose();

    [Fact]
    public async Task Update_PersistsAndReloads()
    {
        var store = new JsonSettingsStore(_paths);
        await store.LoadAsync(TestContext.Current.CancellationToken);
        var changed = 0;
        store.Changed += (_, _) => changed++;

        await store.UpdateAsync(
            s =>
            {
                s.Language = "pl";
                s.Theme = ThemePreference.Dark;
                s.ConnectionProfiles.Add(new ConnectionProfile { Name = "Produkcja", Domain = "contoso.test", Authentication = AuthenticationMode.Credentials, UserName = @"CONTOSO\admin.łukasz" });
                s.SavedViews.Add(new SavedView { Name = "Księgowość – wyłączeni", BaseDn = "OU=Księgowość,{domain}", QuickFilters = [QuickFilterKind.Disabled] });
                s.ColumnLayouts["container"] = [new ColumnLayoutEntry("name", 220), new ColumnLayoutEntry("department", 0)];
                s.AddRecentContainer("OU=IT,DC=contoso,DC=test");
            },
            TestContext.Current.CancellationToken);

        var reloaded = new JsonSettingsStore(_paths);
        await reloaded.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, changed);
        Assert.Equal("pl", reloaded.Current.Language);
        Assert.Equal(ThemePreference.Dark, reloaded.Current.Theme);
        Assert.Equal(@"CONTOSO\admin.łukasz", reloaded.Current.ConnectionProfiles.Single().UserName);
        Assert.Equal(AuthenticationMode.Credentials, reloaded.Current.ConnectionProfiles.Single().Authentication);
        Assert.Equal("Księgowość – wyłączeni", reloaded.Current.SavedViews.Single().Name);
        Assert.Equal([QuickFilterKind.Disabled], reloaded.Current.SavedViews.Single().QuickFilters);
        Assert.Equal(220, reloaded.Current.ColumnLayouts["container"][0].Width);
        Assert.Equal(["OU=IT,DC=contoso,DC=test"], reloaded.Current.RecentContainers);
        Assert.False(File.Exists(store.FilePath + ".tmp"));
    }

    [Fact]
    public async Task ConcurrentUpdatesFromTwoWindows_KeepBothChanges_AndTheNewestFile()
    {
        // Two connection windows share one settings file; one write is slow (an antivirus scan, a network profile).
        var store = new JsonSettingsStore(_paths);
        await store.LoadAsync(TestContext.Current.CancellationToken);
        var firstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        store.WriteFile = async (path, json, token) =>
        {
            if (Interlocked.Increment(ref writes) == 1)
            {
                await firstWrite.Task.WaitAsync(token);
            }

            await File.WriteAllTextAsync(path, json, token);
        };

        var fromA = Task.Run(() => store.UpdateAsync(s => s.AddRecentContainer("OU=IT,DC=contoso,DC=test"), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        await WaitForAsync(() => Volatile.Read(ref writes) == 1);
        var fromB = Task.Run(() => store.UpdateAsync(s => s.AddRecentContainer("OU=Sprzedaż,DC=fabrikam,DC=test"), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        var fromAAgain = store.UpdateAsync(s => s.PageSize = 750, TestContext.Current.CancellationToken);
        firstWrite.SetResult();
        await Task.WhenAll(fromA, fromB, fromAAgain);

        var reloaded = new JsonSettingsStore(_paths);
        await reloaded.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["OU=Sprzedaż,DC=fabrikam,DC=test", "OU=IT,DC=contoso,DC=test"], reloaded.Current.RecentContainers);
        Assert.Equal(750, reloaded.Current.PageSize);
        Assert.Equal(JsonSettingsStore.Serialize(store.Current), await File.ReadAllTextAsync(store.FilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnOlderSnapshot_NeverReplacesANewerFile()
    {
        var store = new JsonSettingsStore(_paths);
        await store.LoadAsync(TestContext.Current.CancellationToken);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var written = new List<int>();
        store.WriteFile = async (path, json, token) =>
        {
            var pageSize = System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("pageSize").GetInt32();
            if (pageSize == 600)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }

            lock (written)
            {
                written.Add(pageSize);
            }

            await File.WriteAllTextAsync(path, json, token);
        };

        // The first update holds the write gate; two more queue behind it.
        var first = Task.Run(() => store.UpdateAsync(s => s.PageSize = 600, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = store.UpdateAsync(s => s.PageSize = 700, TestContext.Current.CancellationToken);
        var third = store.UpdateAsync(s => s.PageSize = 800, TestContext.Current.CancellationToken);
        release.SetResult();
        await Task.WhenAll(first, second, third);

        var reloaded = new JsonSettingsStore(_paths);
        await reloaded.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(800, reloaded.Current.PageSize);
        Assert.Equal(800, written[^1]);
        Assert.DoesNotContain(written.SkipWhile(p => p != 800).Skip(1), p => p < 800);
    }

    [Fact]
    public async Task AReader_NeverSeesAListBeingChanged()
    {
        var store = new JsonSettingsStore(_paths);
        await store.LoadAsync(TestContext.Current.CancellationToken);
        store.WriteFile = (_, _, _) => Task.CompletedTask;
        var before = store.Current;
        using var stop = new CancellationTokenSource();

        // Window A's address bar enumerates the recent containers while window B adds 1 000 of them from the pool.
        var reader = Task.Run(() =>
        {
            var reads = 0;
            while (!stop.IsCancellationRequested)
            {
                var snapshot = store.Current;
                var count = snapshot.RecentContainers.Select(dn => dn.Length).Count();
                Assert.InRange(count, 0, AppSettings.MaxRecentContainers);
                reads++;
            }

            return reads;
        }, TestContext.Current.CancellationToken);
        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < 1000; i++)
            {
                await store.UpdateAsync(s => s.AddRecentContainer($"OU=Dział {i},DC=fabrikam,DC=test"), TestContext.Current.CancellationToken);
            }
        }, TestContext.Current.CancellationToken);

        await writer;
        await stop.CancelAsync();
        Assert.True(await reader > 0);

        // An object handed out is never changed afterwards.
        Assert.Empty(before.RecentContainers);
        Assert.Equal("OU=Dział 999,DC=fabrikam,DC=test", store.Current.RecentContainers[0]);
        Assert.Equal(AppSettings.MaxRecentContainers, store.Current.RecentContainers.Count);
    }

    [Fact]
    public async Task TheProfileColour_SurvivesTheFile()
    {
        var store = new JsonSettingsStore(_paths);
        await store.LoadAsync(TestContext.Current.CancellationToken);
        await store.UpdateAsync(
            s => s.ConnectionProfiles.Add(new ConnectionProfile { Name = "Fabrikam – sprzedaż", Domain = "fabrikam.test", Color = Presentation.Connections.ConnectionColor.Indigo }),
            TestContext.Current.CancellationToken);

        var reloaded = new JsonSettingsStore(_paths);
        await reloaded.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Presentation.Connections.ConnectionColor.Indigo, reloaded.Current.ConnectionProfiles.Single().Color);
        Assert.Null(new ConnectionProfile().Color);
    }

    [Fact]
    public void SerializedSettings_NeverContainPasswords()
    {
        var settings = new AppSettings
        {
            ConnectionProfiles = [new ConnectionProfile { Name = "lab", UserName = "Administrator@contoso.test", Authentication = AuthenticationMode.SimpleBind, Security = TransportSecurity.Tls }],
            SavedViews = [new SavedView { Name = "v", Filter = "(objectClass=user)" }],
            PinnedContainers = [new PinnedContainer("OU=IT,DC=contoso,DC=test", "IT")],
        };

        var json = JsonSettingsStore.Serialize(settings);

        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pwd", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        var names = PersistedPropertyNames(typeof(AppSettings)).ToList();
        Assert.Contains("UserName", names);
        Assert.DoesNotContain(names, n =>
            n.Contains("password", StringComparison.OrdinalIgnoreCase)
            || n.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || n.Contains("credential", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CorruptFile_IsBackedUp_AndDefaultsAreUsed()
    {
        Directory.CreateDirectory(_paths.SettingsDirectory);
        var path = Path.Combine(_paths.SettingsDirectory, JsonSettingsStore.FileName);
        await File.WriteAllTextAsync(path, "{ \"language\": \"pl\", \"savedViews\": [ { broken", TestContext.Current.CancellationToken);
        var store = new JsonSettingsStore(_paths);

        await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, store.Current.Language);
        Assert.Equal(500, store.Current.PageSize);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(_paths.SettingsDirectory, "settings.corrupt-*.json"));
    }

    [Fact]
    public async Task MissingFile_GivesDefaults()
    {
        var store = new JsonSettingsStore(_paths);

        await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(25, store.Current.ConfirmationThreshold);
        Assert.Equal(4, store.Current.BatchMaxParallelism);
        Assert.Equal(90, store.Current.InactiveDays);
        Assert.True(store.Current.ShowDetailsPane);
        Assert.False(store.Current.ShowAdvancedFeatures);
    }

    [Fact]
    public async Task Update_WhenTheFileCannotBeWritten_KeepsTheChange_AndDoesNotThrow()
    {
        // The settings "directory" is a file: every write fails with an IOException (like a locked or read-only profile).
        Directory.CreateDirectory(_paths.SettingsDirectory);
        var blocked = new BlockedAppPaths(Path.Combine(_paths.SettingsDirectory, "not-a-directory"));
        await File.WriteAllTextAsync(blocked.SettingsDirectory, "x", TestContext.Current.CancellationToken);
        var store = new JsonSettingsStore(blocked);
        await store.LoadAsync(TestContext.Current.CancellationToken);
        var changed = 0;
        store.Changed += (_, _) => changed++;

        await store.UpdateAsync(s => s.ShowAdvancedFeatures = true, TestContext.Current.CancellationToken);

        Assert.True(store.Current.ShowAdvancedFeatures);
        Assert.Equal(1, changed);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    [Fact]
    public void RecentContainers_AreMostRecentFirst_Deduplicated_AndCapped()
    {
        var settings = new AppSettings();
        for (var i = 0; i < AppSettings.MaxRecentContainers + 2; i++)
        {
            settings.AddRecentContainer($"OU=O{i},DC=contoso,DC=test");
        }

        settings.AddRecentContainer("ou=o5,dc=contoso,dc=test");

        Assert.Equal(AppSettings.MaxRecentContainers, settings.RecentContainers.Count);
        Assert.Equal("ou=o5,dc=contoso,dc=test", settings.RecentContainers[0]);
        Assert.Single(settings.RecentContainers, d => d.Equals("OU=O5,DC=contoso,DC=test", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("OU=O0,DC=contoso,DC=test", settings.RecentContainers);
    }

    [Fact]
    public void Normalize_ClampsHandEditedValues()
    {
        var settings = new AppSettings { PageSize = 1, BatchMaxParallelism = 99, ConfirmationThreshold = 0, InactiveDays = -5, Language = "de" };

        settings.Normalize();

        Assert.Equal(50, settings.PageSize);
        Assert.Equal(16, settings.BatchMaxParallelism);
        Assert.Equal(1, settings.ConfirmationThreshold);
        Assert.Equal(1, settings.InactiveDays);
        Assert.Equal(string.Empty, settings.Language);
    }

    [Fact]
    public void Computers_NormalizeRepairsHandEditedValues_AndRoundTrip()
    {
        var settings = new AppSettings();
        settings.Computers.Parameters = new Core.Computers.ComputerReportParameters { InactiveDays = 0, EndingSoonDays = 100_000 };
        settings.Computers.DisabledReports = [Core.Computers.ComputerReport.Releases, Core.Computers.ComputerReport.Releases, (Core.Computers.ComputerReport)99];
        settings.Computers.PlacementRules =
        [
            new ComputerPlacementSetting { Type = Core.Computers.ComputerType.Server, Containers = [" OU=Serwery,{domain} ", "ou=serwery,{domain}", ""] },
            new ComputerPlacementSetting { Type = Core.Computers.ComputerType.Server, Containers = ["OU=Inne,{domain}"] },
            new ComputerPlacementSetting { Type = Core.Computers.ComputerType.Workstation, Containers = [] },
        ];
        settings.Computers.ReleaseOverrides =
        [
            new ComputerReleaseOverrideSetting { Build = 26100, Edition = Core.Computers.EditionClass.EnterpriseEducation, EndOfSupport = new DateOnly(2027, 10, 12) },
            new ComputerReleaseOverrideSetting { Build = 26100, Edition = Core.Computers.EditionClass.EnterpriseEducation, EndOfSupport = new DateOnly(2030, 1, 1) },
            new ComputerReleaseOverrideSetting { Build = 0, Edition = Core.Computers.EditionClass.EnterpriseEducation },
        ];
        settings.Computers.RemoteTimeoutSeconds = 100_000;
        settings.Computers.RemotePort = 70_000;
        settings.Computers.RestartDelayMinutes = -3;
        settings.Computers.RestartMessage = new string('ż', 600);

        settings.Normalize();
        var computers = JsonSerializer.Deserialize<AppSettings>(JsonSettingsStore.Serialize(settings), JsonSettingsStore.SerializerOptions)!.Computers;

        Assert.Equal(1, computers.Parameters.InactiveDays);
        Assert.Equal(Core.Computers.ComputerReportParameters.MaxDays, computers.Parameters.EndingSoonDays);
        Assert.Equal([Core.Computers.ComputerReport.Releases], computers.DisabledReports);
        var rule = Assert.Single(computers.PlacementRules);
        Assert.Equal(["OU=Serwery,{domain}"], rule.Containers);
        var release = Assert.Single(computers.ReleaseOverrides);
        Assert.Equal(new DateOnly(2027, 10, 12), release.EndOfSupport);
        Assert.Equal(Core.Computers.Remote.RemoteOptions.MaxTimeoutSeconds, computers.RemoteTimeoutSeconds);
        Assert.Null(computers.RemotePort);
        Assert.Equal(0, computers.RestartDelayMinutes);
        Assert.Equal(ComputerSettings.MaxRestartMessageLength, computers.RestartMessage!.Length);
    }

    private sealed class BlockedAppPaths(string directory) : Services.IAppPaths
    {
        public string SettingsDirectory { get; } = directory;
    }

    private static IEnumerable<string> PersistedPropertyNames(Type type, HashSet<Type>? seen = null)
    {
        seen ??= [];
        if (!seen.Add(type) || type.IsPrimitive || type == typeof(string) || type.IsEnum)
        {
            yield break;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            yield return property.Name;
            var propertyType = property.PropertyType;
            var element = propertyType.IsGenericType ? propertyType.GetGenericArguments().Last() : propertyType;
            if (element.Assembly == typeof(AppSettings).Assembly)
            {
                foreach (var nested in PersistedPropertyNames(element, seen))
                {
                    yield return nested;
                }
            }

            if (element.IsGenericType)
            {
                foreach (var nested in element.GetGenericArguments().Where(a => a.Assembly == typeof(AppSettings).Assembly).SelectMany(a => PersistedPropertyNames(a, seen)))
                {
                    yield return nested;
                }
            }
        }
    }
}
