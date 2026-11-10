using System.Text.Json;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Presentation.Tests.TestDoubles;

namespace AdMng.Presentation.Tests.Settings;

/// <summary>
/// The icon style setting of track V (stage V1): colour icons by default, monochrome as a choice, and monochrome
/// always when Windows High Contrast is on.
/// </summary>
public sealed class IconStyleTests : IDisposable
{
    private readonly TempAppPaths _paths = new();

    public void Dispose() => _paths.Dispose();

    [Theory]
    [InlineData(IconStylePreference.Colour, false, IconStylePreference.Colour)]
    [InlineData(IconStylePreference.Colour, true, IconStylePreference.Monochrome)]
    [InlineData(IconStylePreference.Monochrome, false, IconStylePreference.Monochrome)]
    [InlineData(IconStylePreference.Monochrome, true, IconStylePreference.Monochrome)]
    public void EffectiveStyle_IsMonochromeWhenChosenOrInHighContrast(IconStylePreference preference, bool highContrast, IconStylePreference expected) =>
        Assert.Equal(expected, IconStyleRules.Effective(preference, highContrast));

    [Fact]
    public void NewSettings_UseColourIcons()
    {
        Assert.Equal(IconStylePreference.Colour, new AppSettings().IconStyle);
        // The default is the zero value, so a value missing in a file can never turn into another style.
        Assert.Equal(IconStylePreference.Colour, default(IconStylePreference));
    }

    [Fact]
    public async Task SettingsFileWrittenBeforeTheOption_LoadsAsColour()
    {
        // A settings file of an earlier version: it has a theme and a language but no icon style.
        var writer = new JsonSettingsStore(_paths);
        Directory.CreateDirectory(Path.GetDirectoryName(writer.FilePath)!);
        await File.WriteAllTextAsync(
            writer.FilePath,
            """{"version":1,"language":"pl","theme":"Dark","pageSize":750}""",
            TestContext.Current.CancellationToken);

        var store = new JsonSettingsStore(_paths);
        await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("pl", store.Current.Language);
        Assert.Equal(ThemePreference.Dark, store.Current.Theme);
        Assert.Equal(750, store.Current.PageSize);
        Assert.Equal(IconStylePreference.Colour, store.Current.IconStyle);
    }

    [Fact]
    public async Task IconStyle_IsPersistedByName_AndReloads()
    {
        var store = new JsonSettingsStore(_paths);
        await store.LoadAsync(TestContext.Current.CancellationToken);
        await store.UpdateAsync(s => s.IconStyle = IconStylePreference.Monochrome, TestContext.Current.CancellationToken);

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(store.FilePath, TestContext.Current.CancellationToken));
        Assert.Equal("Monochrome", json.RootElement.GetProperty("iconStyle").GetString());

        var reloaded = new JsonSettingsStore(_paths);
        await reloaded.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(IconStylePreference.Monochrome, reloaded.Current.IconStyle);
    }

    [Fact]
    public async Task SettingsViewModel_OffersBothStyles_AndRoundTripsTheChoice()
    {
        var harness = await B2Harness.CreateAsync();
        using (var settings = harness.Factory.CreateSettings())
        {
            Assert.Equal([IconStylePreference.Colour, IconStylePreference.Monochrome], settings.IconStyles.Select(i => i.Value));
            Assert.Equal(["Colour", "Monochrome"], settings.IconStyles.Select(i => i.Text));
            Assert.Equal(IconStylePreference.Colour, settings.IconStyle.Value);

            settings.IconStyle = settings.IconStyles.Single(i => i.Value == IconStylePreference.Monochrome);
            // Nothing is stored before Save.
            Assert.Equal(IconStylePreference.Colour, harness.Settings.Current.IconStyle);
            await settings.SaveCommand.ExecuteAsync(null);
        }

        Assert.Equal(IconStylePreference.Monochrome, harness.Settings.Current.IconStyle);

        // The next settings window shows the stored choice; cancelling a change keeps it.
        using (var again = harness.Factory.CreateSettings())
        {
            Assert.Equal(IconStylePreference.Monochrome, again.IconStyle.Value);
            again.IconStyle = again.IconStyles.Single(i => i.Value == IconStylePreference.Colour);
            again.CancelCommand.Execute(null);
        }

        Assert.Equal(IconStylePreference.Monochrome, harness.Settings.Current.IconStyle);
    }

    [Fact]
    public void EveryStyle_HasItsDisplayNameKey()
    {
        Assert.Equal("IconStyle.Colour", DisplayNames.IconStyleKey(IconStylePreference.Colour));
        Assert.Equal("IconStyle.Monochrome", DisplayNames.IconStyleKey(IconStylePreference.Monochrome));
    }
}
