using System.Reflection;
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
    public void RecentContainers_AreMostRecentFirst_Deduplicated_AndCapped()
    {
        var settings = new AppSettings();
        for (var i = 0; i < 12; i++)
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
