using System.Globalization;
using System.Text.Json;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Presentation.Tests.TestDoubles;

namespace AdMng.Presentation.Tests.Settings;

/// <summary>
/// The window background setting of track V (stage V1): the theme's background (Mica on Windows 11) by default, a solid
/// one as a choice for remote desktop sessions and slower computers. The application applies it once at start.
/// </summary>
public sealed class WindowBackgroundTests : IDisposable
{
    private readonly TempAppPaths _paths = new();

    public void Dispose() => _paths.Dispose();

    [Fact]
    public void NewSettings_UseTheBackgroundOfTheTheme()
    {
        Assert.Equal(WindowBackgroundPreference.Theme, new AppSettings().WindowBackground);
        // The default is the zero value, so a value missing in a file can never turn the theme's background off.
        Assert.Equal(WindowBackgroundPreference.Theme, default(WindowBackgroundPreference));
    }

    [Fact]
    public async Task SettingsFileWrittenBeforeTheOption_LoadsAsTheme()
    {
        // A settings file of an earlier version: a theme, a language and an icon style, but no window background.
        var writer = new JsonSettingsStore(_paths);
        Directory.CreateDirectory(Path.GetDirectoryName(writer.FilePath)!);
        await File.WriteAllTextAsync(
            writer.FilePath,
            """{"version":1,"language":"pl","theme":"Dark","iconStyle":"Monochrome","pageSize":750}""",
            TestContext.Current.CancellationToken);

        var store = new JsonSettingsStore(_paths);
        await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("pl", store.Current.Language);
        Assert.Equal(ThemePreference.Dark, store.Current.Theme);
        Assert.Equal(IconStylePreference.Monochrome, store.Current.IconStyle);
        Assert.Equal(750, store.Current.PageSize);
        Assert.Equal(WindowBackgroundPreference.Theme, store.Current.WindowBackground);
    }

    [Theory]
    [InlineData("\"Acrylic\"", WindowBackgroundPreference.Theme)]     // a background of a later version
    [InlineData("\"\"", WindowBackgroundPreference.Theme)]
    [InlineData("7", WindowBackgroundPreference.Theme)]
    [InlineData("-1", WindowBackgroundPreference.Theme)]
    [InlineData("1.5", WindowBackgroundPreference.Theme)]
    [InlineData("\"7\"", WindowBackgroundPreference.Theme)]
    [InlineData("null", WindowBackgroundPreference.Theme)]
    [InlineData("false", WindowBackgroundPreference.Theme)]
    [InlineData("[\"Solid\"]", WindowBackgroundPreference.Theme)]
    [InlineData("{\"name\":\"Solid\"}", WindowBackgroundPreference.Theme)]
    [InlineData("\"solid\"", WindowBackgroundPreference.Solid)]
    [InlineData("\"Solid\"", WindowBackgroundPreference.Solid)]
    [InlineData("1", WindowBackgroundPreference.Solid)]
    public async Task UnknownWindowBackground_LoadsAsTheme_AndKeepsTheRestOfTheFile(string json, WindowBackgroundPreference expected)
    {
        // A failed read would make the store treat the whole file (profiles, views) as corrupt and reset it.
        var writer = new JsonSettingsStore(_paths);
        var directory = Path.GetDirectoryName(writer.FilePath)!;
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            writer.FilePath,
            $$"""{"version":1,"language":"pl","theme":"Dark","windowBackground":{{json}},"iconStyle":"Monochrome","pageSize":750}""",
            TestContext.Current.CancellationToken);

        var store = new JsonSettingsStore(_paths);
        await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected, store.Current.WindowBackground);
        Assert.Equal("pl", store.Current.Language);
        Assert.Equal(ThemePreference.Dark, store.Current.Theme);
        // The property after the odd value is still read: the value was consumed as exactly one JSON value.
        Assert.Equal(IconStylePreference.Monochrome, store.Current.IconStyle);
        Assert.Equal(750, store.Current.PageSize);
        Assert.Empty(Directory.GetFiles(directory, "settings.corrupt-*"));

        // The next save writes a name this version knows.
        await store.UpdateAsync(s => s.PageSize = 800, TestContext.Current.CancellationToken);
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(store.FilePath, TestContext.Current.CancellationToken));
        Assert.Equal(expected.ToString(), saved.RootElement.GetProperty("windowBackground").GetString());
        Assert.Equal(expected, store.Current.WindowBackground);
    }

    [Fact]
    public async Task WindowBackground_IsPersistedByName_AndReloads()
    {
        var store = new JsonSettingsStore(_paths);
        await store.LoadAsync(TestContext.Current.CancellationToken);
        await store.UpdateAsync(s => s.WindowBackground = WindowBackgroundPreference.Solid, TestContext.Current.CancellationToken);

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(store.FilePath, TestContext.Current.CancellationToken));
        Assert.Equal("Solid", json.RootElement.GetProperty("windowBackground").GetString());

        var reloaded = new JsonSettingsStore(_paths);
        await reloaded.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(WindowBackgroundPreference.Solid, reloaded.Current.WindowBackground);
    }

    [Fact]
    public void TolerantConverter_WritesTheDefaultForAValueOutsideTheEnum()
    {
        // A value outside the enum can only come from code; it is written as the default, never as a bare number.
        var options = new JsonSerializerOptions();
        options.Converters.Add(new TolerantEnumJsonConverter<WindowBackgroundPreference>());

        Assert.Equal("\"Theme\"", JsonSerializer.Serialize((WindowBackgroundPreference)42, options));
        Assert.Equal("\"Solid\"", JsonSerializer.Serialize(WindowBackgroundPreference.Solid, options));
        Assert.Equal(WindowBackgroundPreference.Solid, JsonSerializer.Deserialize<WindowBackgroundPreference>("\"SOLID\"", options));
        Assert.Equal(WindowBackgroundPreference.Theme, JsonSerializer.Deserialize<WindowBackgroundPreference>("99999999999", options));
    }

    [Fact]
    public async Task SettingsViewModel_OffersBothBackgrounds_AndRoundTripsTheChoice()
    {
        var harness = await B2Harness.CreateAsync();
        using (var settings = harness.Factory.CreateSettings())
        {
            Assert.Equal([WindowBackgroundPreference.Theme, WindowBackgroundPreference.Solid], settings.WindowBackgrounds.Select(b => b.Value));
            Assert.Equal(["Follow the theme", "Solid"], settings.WindowBackgrounds.Select(b => b.Text));
            Assert.Equal(WindowBackgroundPreference.Theme, settings.WindowBackground.Value);

            settings.WindowBackground = settings.WindowBackgrounds.Single(b => b.Value == WindowBackgroundPreference.Solid);
            // Nothing is stored before Save.
            Assert.Equal(WindowBackgroundPreference.Theme, harness.Settings.Current.WindowBackground);
            await settings.SaveCommand.ExecuteAsync(null);
        }

        Assert.Equal(WindowBackgroundPreference.Solid, harness.Settings.Current.WindowBackground);
        // The other appearance choices are saved as they were.
        Assert.Equal(IconStylePreference.Colour, harness.Settings.Current.IconStyle);
        Assert.Equal(ThemePreference.System, harness.Settings.Current.Theme);

        // The next settings window shows the stored choice; cancelling a change keeps it.
        using (var again = harness.Factory.CreateSettings())
        {
            Assert.Equal(WindowBackgroundPreference.Solid, again.WindowBackground.Value);
            again.WindowBackground = again.WindowBackgrounds.Single(b => b.Value == WindowBackgroundPreference.Theme);
            again.CancelCommand.Execute(null);
        }

        Assert.Equal(WindowBackgroundPreference.Solid, harness.Settings.Current.WindowBackground);
    }

    [Fact]
    public async Task SettingsViewModel_NamesTheBackgroundsInPolish()
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo("pl-PL"));
        using var settings = harness.Factory.CreateSettings();

        Assert.Equal(["Zgodnie z motywem", "Jednolite"], settings.WindowBackgrounds.Select(b => b.Text));
    }

    [Fact]
    public void EveryBackground_HasItsDisplayNameKey_AndTheTextsExistInBothLanguages()
    {
        Assert.Equal("WindowBackground.Theme", DisplayNames.WindowBackgroundKey(WindowBackgroundPreference.Theme));
        Assert.Equal("WindowBackground.Solid", DisplayNames.WindowBackgroundKey(WindowBackgroundPreference.Solid));

        var english = new ResxLocalizer(CultureInfo.GetCultureInfo("en-US"));
        var polish = new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL"));
        Assert.Equal("Background", english[L.Settings.WindowBackground]);
        Assert.Equal("Tło", polish[L.Settings.WindowBackground]);

        // The hint names the choice by the text of the choice, in the quotes of its language, and says "restart".
        Assert.Contains($"“{english[DisplayNames.WindowBackgroundKey(WindowBackgroundPreference.Solid)]}”", english[L.Settings.WindowBackgroundHint], StringComparison.Ordinal);
        Assert.Contains($"„{polish[DisplayNames.WindowBackgroundKey(WindowBackgroundPreference.Solid)]}”", polish[L.Settings.WindowBackgroundHint], StringComparison.Ordinal);
        Assert.Contains("restarted", english[L.Settings.WindowBackgroundHint], StringComparison.Ordinal);
        Assert.Contains("ponownym uruchomieniu", polish[L.Settings.WindowBackgroundHint], StringComparison.Ordinal);
    }
}
