using System.Globalization;
using System.Text.Json;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Motion;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Presentation.Tests.TestDoubles;

namespace AdMng.Presentation.Tests.Settings;

/// <summary>
/// The motion setting of track V (stage V1): follow Windows by default, always on or always off as a choice, and a
/// forced switch-off (UI tests, the smoke test) that wins over all of them.
/// </summary>
public sealed class MotionSettingTests : IDisposable
{
    private readonly TempAppPaths _paths = new();

    public void Dispose() => _paths.Dispose();

    [Theory]
    // preference, Windows "Animation effects", hardware accelerated, forced off -> motion
    [InlineData(MotionPreference.FollowWindows, true, true, false, true)]
    [InlineData(MotionPreference.FollowWindows, false, true, false, false)]
    [InlineData(MotionPreference.FollowWindows, true, false, false, false)]
    [InlineData(MotionPreference.FollowWindows, false, false, false, false)]
    [InlineData(MotionPreference.On, true, true, false, true)]
    [InlineData(MotionPreference.On, false, true, false, true)]
    [InlineData(MotionPreference.On, true, false, false, true)]
    [InlineData(MotionPreference.On, false, false, false, true)]
    [InlineData(MotionPreference.Off, true, true, false, false)]
    [InlineData(MotionPreference.Off, false, true, false, false)]
    [InlineData(MotionPreference.Off, true, false, false, false)]
    [InlineData(MotionPreference.Off, false, false, false, false)]
    [InlineData(MotionPreference.FollowWindows, true, true, true, false)]
    [InlineData(MotionPreference.On, true, true, true, false)]
    [InlineData(MotionPreference.On, false, false, true, false)]
    [InlineData(MotionPreference.Off, true, true, true, false)]
    public void EffectiveMotion_FollowsTheRule(MotionPreference preference, bool windowsAnimations, bool hardwareAccelerated, bool forcedOff, bool expected) =>
        Assert.Equal(expected, MotionRules.Effective(preference, windowsAnimations, hardwareAccelerated, forcedOff));

    [Fact]
    public void EffectiveMotion_IsNeverOnWhenForcedOff_AndAnUnknownPreferenceFollowsWindows()
    {
        foreach (var preference in Enum.GetValues<MotionPreference>())
        {
            foreach (var windows in (bool[])[false, true])
            {
                foreach (var hardware in (bool[])[false, true])
                {
                    Assert.False(MotionRules.Effective(preference, windows, hardware, forcedOff: true));
                }
            }
        }

        // A value outside the enum can only come from code: it is treated as the default, not as "on".
        Assert.True(MotionRules.Effective((MotionPreference)42, windowsAnimations: true, hardwareAccelerated: true, forcedOff: false));
        Assert.False(MotionRules.Effective((MotionPreference)42, windowsAnimations: false, hardwareAccelerated: true, forcedOff: false));
        Assert.False(MotionRules.Effective((MotionPreference)42, windowsAnimations: true, hardwareAccelerated: false, forcedOff: false));
    }

    [Fact]
    public void NewSettings_FollowWindows()
    {
        Assert.Equal(MotionPreference.FollowWindows, new AppSettings().Motion);
        // The default is the zero value, so a value missing in a file can never switch motion on against Windows.
        Assert.Equal(MotionPreference.FollowWindows, default(MotionPreference));
        Assert.Equal([MotionPreference.FollowWindows, MotionPreference.On, MotionPreference.Off], Enum.GetValues<MotionPreference>());
    }

    [Fact]
    public async Task SettingsFileWrittenBeforeTheOption_LoadsAsFollowWindows()
    {
        // A settings file of an earlier version: the appearance choices of V1a and V1b, but no motion.
        var writer = new JsonSettingsStore(_paths);
        Directory.CreateDirectory(Path.GetDirectoryName(writer.FilePath)!);
        await File.WriteAllTextAsync(
            writer.FilePath,
            """{"version":1,"language":"pl","theme":"Dark","iconStyle":"Monochrome","windowBackground":"Solid","pageSize":750}""",
            TestContext.Current.CancellationToken);

        var store = new JsonSettingsStore(_paths);
        await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("pl", store.Current.Language);
        Assert.Equal(ThemePreference.Dark, store.Current.Theme);
        Assert.Equal(IconStylePreference.Monochrome, store.Current.IconStyle);
        Assert.Equal(WindowBackgroundPreference.Solid, store.Current.WindowBackground);
        Assert.Equal(750, store.Current.PageSize);
        Assert.Equal(MotionPreference.FollowWindows, store.Current.Motion);
    }

    [Theory]
    [InlineData("\"Reduced\"", MotionPreference.FollowWindows)]     // a choice of a later version
    [InlineData("\"\"", MotionPreference.FollowWindows)]
    [InlineData("7", MotionPreference.FollowWindows)]
    [InlineData("-1", MotionPreference.FollowWindows)]
    [InlineData("1.5", MotionPreference.FollowWindows)]
    [InlineData("\"7\"", MotionPreference.FollowWindows)]
    [InlineData("null", MotionPreference.FollowWindows)]
    [InlineData("true", MotionPreference.FollowWindows)]
    [InlineData("[\"On\"]", MotionPreference.FollowWindows)]
    [InlineData("{\"name\":\"Off\"}", MotionPreference.FollowWindows)]
    [InlineData("\"followwindows\"", MotionPreference.FollowWindows)]
    [InlineData("\"on\"", MotionPreference.On)]
    [InlineData("\"On\"", MotionPreference.On)]
    [InlineData("1", MotionPreference.On)]
    [InlineData("\"OFF\"", MotionPreference.Off)]
    [InlineData("2", MotionPreference.Off)]
    public async Task UnknownMotion_LoadsAsFollowWindows_AndKeepsTheRestOfTheFile(string json, MotionPreference expected)
    {
        // A failed read would make the store treat the whole file (profiles, views) as corrupt and reset it.
        var writer = new JsonSettingsStore(_paths);
        var directory = Path.GetDirectoryName(writer.FilePath)!;
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            writer.FilePath,
            $$"""{"version":1,"language":"pl","theme":"Dark","motion":{{json}},"iconStyle":"Monochrome","pageSize":750}""",
            TestContext.Current.CancellationToken);

        var store = new JsonSettingsStore(_paths);
        await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected, store.Current.Motion);
        Assert.Equal("pl", store.Current.Language);
        Assert.Equal(ThemePreference.Dark, store.Current.Theme);
        // The property after the odd value is still read: the value was consumed as exactly one JSON value.
        Assert.Equal(IconStylePreference.Monochrome, store.Current.IconStyle);
        Assert.Equal(750, store.Current.PageSize);
        Assert.Empty(Directory.GetFiles(directory, "settings.corrupt-*"));

        // The next save writes a name this version knows.
        await store.UpdateAsync(s => s.PageSize = 800, TestContext.Current.CancellationToken);
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(store.FilePath, TestContext.Current.CancellationToken));
        Assert.Equal(expected.ToString(), saved.RootElement.GetProperty("motion").GetString());
        Assert.Equal(expected, store.Current.Motion);
    }

    [Theory]
    [InlineData(MotionPreference.On, "On")]
    [InlineData(MotionPreference.Off, "Off")]
    [InlineData(MotionPreference.FollowWindows, "FollowWindows")]
    public async Task Motion_IsPersistedByName_AndReloads(MotionPreference preference, string name)
    {
        var store = new JsonSettingsStore(_paths);
        await store.LoadAsync(TestContext.Current.CancellationToken);
        await store.UpdateAsync(s => s.Motion = preference, TestContext.Current.CancellationToken);

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(store.FilePath, TestContext.Current.CancellationToken));
        Assert.Equal(name, json.RootElement.GetProperty("motion").GetString());

        var reloaded = new JsonSettingsStore(_paths);
        await reloaded.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(preference, reloaded.Current.Motion);
    }

    [Fact]
    public async Task SettingsViewModel_OffersTheThreeChoices_AndRoundTripsTheChoice()
    {
        var harness = await B2Harness.CreateAsync();
        using (var settings = harness.Factory.CreateSettings())
        {
            Assert.Equal([MotionPreference.FollowWindows, MotionPreference.On, MotionPreference.Off], settings.MotionChoices.Select(m => m.Value));
            Assert.Equal(["Follow Windows", "On", "Off"], settings.MotionChoices.Select(m => m.Text));
            Assert.Equal(MotionPreference.FollowWindows, settings.Motion.Value);
            Assert.Same(settings.MotionChoices[0], settings.Motion);

            settings.Motion = settings.MotionChoices.Single(m => m.Value == MotionPreference.Off);
            // Nothing is stored before Save.
            Assert.Equal(MotionPreference.FollowWindows, harness.Settings.Current.Motion);
            await settings.SaveCommand.ExecuteAsync(null);
        }

        Assert.Equal(MotionPreference.Off, harness.Settings.Current.Motion);
        // The other appearance choices are saved as they were.
        Assert.Equal(IconStylePreference.Colour, harness.Settings.Current.IconStyle);
        Assert.Equal(WindowBackgroundPreference.Theme, harness.Settings.Current.WindowBackground);
        Assert.Equal(ThemePreference.System, harness.Settings.Current.Theme);

        // The next settings window shows the stored choice; cancelling a change keeps it.
        using (var again = harness.Factory.CreateSettings())
        {
            Assert.Equal(MotionPreference.Off, again.Motion.Value);
            again.Motion = again.MotionChoices.Single(m => m.Value == MotionPreference.On);
            again.CancelCommand.Execute(null);
        }

        Assert.Equal(MotionPreference.Off, harness.Settings.Current.Motion);
    }

    [Fact]
    public async Task SavingTheMotionChoice_RaisesTheSettingsChange_TheApplicationAppliesItFrom()
    {
        var harness = await B2Harness.CreateAsync();
        var changes = 0;
        harness.Settings.Changed += (_, _) => changes++;
        using var settings = harness.Factory.CreateSettings();

        settings.Motion = settings.MotionChoices.Single(m => m.Value == MotionPreference.On);
        await settings.SaveCommand.ExecuteAsync(null);

        Assert.True(changes > 0);
        Assert.Equal(MotionPreference.On, harness.Settings.Current.Motion);
    }

    [Fact]
    public async Task SettingsViewModel_NamesTheChoicesInPolish()
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo("pl-PL"));
        using var settings = harness.Factory.CreateSettings();

        Assert.Equal(["Zgodnie z systemem Windows", "Włączone", "Wyłączone"], settings.MotionChoices.Select(m => m.Text));
    }

    [Fact]
    public void EveryChoice_HasItsDisplayNameKey_AndTheTextsExistInBothLanguages()
    {
        Assert.Equal("Motion.FollowWindows", DisplayNames.MotionKey(MotionPreference.FollowWindows));
        Assert.Equal("Motion.On", DisplayNames.MotionKey(MotionPreference.On));
        Assert.Equal("Motion.Off", DisplayNames.MotionKey(MotionPreference.Off));

        var english = new ResxLocalizer(CultureInfo.GetCultureInfo("en-US"));
        var polish = new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL"));
        Assert.Equal("Motion", english[L.Settings.Motion]);
        Assert.Equal("Animacje", polish[L.Settings.Motion]);
        foreach (var preference in Enum.GetValues<MotionPreference>())
        {
            Assert.True(english.Contains(DisplayNames.MotionKey(preference)));
            Assert.True(polish.Contains(DisplayNames.MotionKey(preference)));
        }

        // The hint names the default by the text of the choice, in the quotes of its language, names the Windows
        // setting it follows and says that progress indicators stay.
        Assert.Contains($"“{english[DisplayNames.MotionKey(MotionPreference.FollowWindows)]}”", english[L.Settings.MotionHint], StringComparison.Ordinal);
        Assert.Contains($"„{polish[DisplayNames.MotionKey(MotionPreference.FollowWindows)]}”", polish[L.Settings.MotionHint], StringComparison.Ordinal);
        Assert.Contains("“Animation effects”", english[L.Settings.MotionHint], StringComparison.Ordinal);
        Assert.Contains("„Efekty animacji”", polish[L.Settings.MotionHint], StringComparison.Ordinal);
        Assert.Contains("Progress indicators", english[L.Settings.MotionHint], StringComparison.Ordinal);
        Assert.Contains("Wskaźniki postępu", polish[L.Settings.MotionHint], StringComparison.Ordinal);
    }
}
