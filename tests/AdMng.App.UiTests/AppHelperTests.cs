using System.Windows.Input;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.Localization;
using AdMng.App.Services;
using AdMng.Core.Model;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;

namespace AdMng.App.UiTests;

/// <summary>Pure helpers of the WPF shell (no window needed).</summary>
public sealed class AppHelperTests
{
    [Theory]
    [InlineData("F5", Key.F5, ModifierKeys.None)]
    [InlineData("Delete", Key.Delete, ModifierKeys.None)]
    [InlineData("Alt+Enter", Key.Enter, ModifierKeys.Alt)]
    [InlineData("Ctrl+Shift+B", Key.B, ModifierKeys.Control | ModifierKeys.Shift)]
    [InlineData("Ctrl+OemComma", Key.OemComma, ModifierKeys.Control)]
    [InlineData("Alt+Left", Key.Left, ModifierKeys.Alt)]
    public void ShortcutText_ParsesDescriptorGestures(string shortcut, Key key, ModifierKeys modifiers)
    {
        SkipUnlessWindows();
        var gesture = ShortcutText.TryParse(shortcut);

        Assert.NotNull(gesture);
        Assert.Equal(key, gesture!.Key);
        Assert.Equal(modifiers, gesture.Modifiers);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("Ctrl+OemComma", "Ctrl+,")]
    [InlineData("Delete", "Del")]
    [InlineData("Ctrl+Shift+B", "Ctrl+Shift+B")]
    public void ShortcutText_FormatsForMenus(string? shortcut, string expected)
    {
        SkipUnlessWindows();
        Assert.Equal(expected, ShortcutText.Format(shortcut));
    }

    [Fact]
    public void ShortcutText_RejectsInvalidGestures()
    {
        SkipUnlessWindows();
        Assert.Null(ShortcutText.TryParse("Ctrl+NoSuchKey"));
        Assert.Null(ShortcutText.TryParse("B"));
        Assert.Null(ShortcutText.TryParse(null));
    }

    [Theory]
    [InlineData("department", "[department]")]
    [InlineData("msDS-UserPasswordExpiryTimeComputed", "[msDS-UserPasswordExpiryTimeComputed]")]
    [InlineData("$location", "[$location]")]
    [InlineData("a]b,c(d", "[a^]b^,c^(d]")]
    [InlineData(" x ", "[^ x^ ]")]
    public void BindingPaths_EscapeIndexerArguments(string name, string expected)
    {
        SkipUnlessWindows();
        Assert.Equal(expected, BindingPaths.Indexer(name));
    }

    [Fact]
    public void StartupOptions_ParseTheCommandLine()
    {
        SkipUnlessWindows();
        Assert.Equal(new StartupOptions(), StartupOptions.Parse([]));
        Assert.Equal(new StartupOptions(SmokeTest: true, Language: "pl"), StartupOptions.Parse(["--smoke-test", "--lang", "PL"]));
        Assert.Equal(new StartupOptions(Language: null), StartupOptions.Parse(["--lang", "de"]));
        Assert.Equal(new StartupOptions(SettingsDirectory: @"C:\temp\x"), StartupOptions.Parse(["/settings-dir", @"C:\temp\x", "--unknown"]));
    }

    [Fact]
    public void TrConverter_UsesTheDisplayNameKeyFamilies()
    {
        SkipUnlessWindows();
        Assert.Equal("ObjectKind.User", TrConverter.KeyFor(ObjectKind.User));
        Assert.Equal("Theme.Dark", TrConverter.KeyFor(ThemePreference.Dark));
        Assert.Equal("Severity.Error", TrConverter.KeyFor("Error", "Severity"));
        Assert.Equal("Common.Ok", TrConverter.KeyFor("Common.Ok"));
    }

    [Fact]
    public void Glyphs_CoverEveryObjectIcon_WithBadgesForDisabledAndLocked()
    {
        SkipUnlessWindows();
        foreach (var icon in Enum.GetValues<ObjectIcon>())
        {
            Assert.False(string.IsNullOrEmpty(Glyphs.For(icon)));
        }

        Assert.Equal(IconOverlay.Disabled, Glyphs.OverlayFor(ObjectIcon.UserDisabled));
        Assert.Equal(IconOverlay.Disabled, Glyphs.OverlayFor(ObjectIcon.ComputerDisabled));
        Assert.Equal(IconOverlay.Locked, Glyphs.OverlayFor(ObjectIcon.UserLocked));
        Assert.Equal(IconOverlay.None, Glyphs.OverlayFor(ObjectIcon.User));
        Assert.Equal(IconTone.Container, Glyphs.ToneFor(ObjectIcon.OrganizationalUnit));
    }

    [Fact]
    public void FileDialogFilter_IsBuiltFromTheFilters()
    {
        SkipUnlessWindows();
        var filters = new[] { new FileFilter("Pliki CSV (*.csv)", "*.csv"), new FileFilter("A|B", "*.json") };

        Assert.Equal("Pliki CSV (*.csv)|*.csv|A B|*.json", FileDialogService.BuildFilter(filters));
        Assert.Equal(".csv", FileDialogService.DefaultExtension("*.csv"));
        Assert.Equal(string.Empty, FileDialogService.DefaultExtension("*.csv;*.txt"));
    }

    [Fact]
    public void NotificationDurations_KeepErrorsLongest()
    {
        SkipUnlessWindows();
        Assert.True(NotificationService.DurationOf(NotificationSeverity.Error) > NotificationService.DurationOf(NotificationSeverity.Warning));
        Assert.True(NotificationService.DurationOf(NotificationSeverity.Warning) > NotificationService.DurationOf(NotificationSeverity.Success));
    }

    [Fact]
    public void BindingErrorListener_IgnoresOnlyFluentTemplateNoise()
    {
        SkipUnlessWindows();
        Assert.True(BindingErrorListener.IsThemeNoise(
            "Cannot find source for binding with reference 'RelativeSource FindAncestor, AncestorType='System.Windows.Controls.ItemsControl', "
            + "AncestorLevel='1''. BindingExpression:Path=HorizontalContentAlignment; DataItem=null;"));

        // The DatePicker template's text box during a theme switch (seen in the smoke test of the published app).
        Assert.True(BindingErrorListener.IsThemeNoise(
            "Cannot find source for binding with reference 'RelativeSource FindAncestor, AncestorType='System.Windows.Controls.DatePicker', "
            + "AncestorLevel='1''. BindingExpression:Path=(0); DataItem=null; target element is 'DatePickerTextBox' (Name=''); "
            + "target property is 'Name' (type 'String')"));

        // The same ancestor lookup from application XAML (another element or property) is still an error.
        Assert.False(BindingErrorListener.IsThemeNoise(
            "Cannot find source for binding with reference 'RelativeSource FindAncestor, AncestorType='System.Windows.Controls.DatePicker', "
            + "AncestorLevel='1''. BindingExpression:Path=SelectedDate; DataItem=null; target element is 'TextBlock' (Name=''); "
            + "target property is 'Text' (type 'String')"));
        Assert.False(BindingErrorListener.IsThemeNoise("BindingExpression path error: 'Nmae' property not found on 'object'"));
    }

    [Fact]
    public void ClipboardSecret_IsKeptOutOfHistoryCloudClipboardAndMonitors()
    {
        SkipUnlessWindows();
        var data = ClipboardService.CreateSecretData("Zażółć!Hasło2026");

        Assert.Equal("Zażółć!Hasło2026", data.GetText(System.Windows.TextDataFormat.UnicodeText));
        foreach (var format in new[]
        {
            ClipboardService.ExcludeFromMonitoringFormat, ClipboardService.CanIncludeInHistoryFormat, ClipboardService.CanUploadToCloudFormat,
        })
        {
            Assert.True(data.GetDataPresent(format, autoConvert: false), format);
            var stream = Assert.IsAssignableFrom<System.IO.MemoryStream>(data.GetData(format, autoConvert: false));
            Assert.Equal(new byte[4], stream.ToArray());
        }

        Assert.Equal(TimeSpan.FromSeconds(60), ClipboardService.SecretLifetime);
    }

    private static void SkipUnlessWindows() => Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF tests need Windows.");
}
