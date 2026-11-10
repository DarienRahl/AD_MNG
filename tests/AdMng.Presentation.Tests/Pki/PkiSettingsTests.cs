using System.Globalization;
using AdMng.Presentation.Pki;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Tests.Dialogs;

namespace AdMng.Presentation.Tests.Pki;

/// <summary>The <i>Certyfikaty</i> section of the settings window (decision 2, docs/dev/p-certificate-services.md 2.5).</summary>
public sealed class PkiSettingsTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    [Fact]
    public async Task TheSection_ShowsTheDefaults_30And180Days()
    {
        var h = await B2Harness.CreateAsync(Polish);
        var settings = new SettingsViewModel(h.Services, h.Theme);

        Assert.Equal(30, settings.Pki.CertificateExpiryWarningDays);
        Assert.Equal(180, settings.Pki.CaCertificateExpiryWarningDays);
        Assert.Null(settings.Pki.Error);
        Assert.Contains("20%", settings.Pki.Hint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 180)]
    [InlineData(30, 3651)]
    [InlineData(-5, 10)]
    public async Task AThresholdOutsideOneTo3650Days_IsAnError_AndSaveWaits(int certificate, int ca)
    {
        var h = await B2Harness.CreateAsync(Polish);
        var settings = new SettingsViewModel(h.Services, h.Theme);

        settings.Pki.CertificateExpiryWarningDays = certificate;
        settings.Pki.CaCertificateExpiryWarningDays = ca;

        Assert.NotNull(settings.Pki.Error);
        Assert.Contains("1", settings.Pki.Error, StringComparison.Ordinal);
        Assert.Contains("3650", settings.Pki.Error, StringComparison.Ordinal);
        Assert.Equal(settings.Pki.Error, settings.Error);
        Assert.False(settings.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Save_StoresTheTwoThresholds_AndNothingElseOfPki()
    {
        var h = await B2Harness.CreateAsync(Polish);
        var settings = new SettingsViewModel(h.Services, h.Theme);
        var errorChanges = 0;
        settings.PropertyChanged += (_, e) => errorChanges += e.PropertyName == nameof(SettingsViewModel.Error) ? 1 : 0;

        settings.Pki.CertificateExpiryWarningDays = 45;
        settings.Pki.CaCertificateExpiryWarningDays = 365;
        Assert.True(errorChanges >= 2);
        await settings.SaveCommand.ExecuteAsync(null);

        var stored = h.Settings.Current.Pki;
        Assert.Equal(45, stored.CertificateExpiryWarningDays);
        Assert.Equal(365, stored.CaCertificateExpiryWarningDays);
        Assert.True(stored.ShowInTree);
        Assert.Equal(new Core.Pki.PkiHealthThresholds(45, 365), stored.Thresholds());
    }

    [Fact]
    public async Task Apply_ClampsAHandEditedValue_AndToStringCarriesNoValue()
    {
        var h = await B2Harness.CreateAsync(Polish);
        var section = new PkiSettingsViewModel(h.Services) { CertificateExpiryWarningDays = 9999, CaCertificateExpiryWarningDays = 180 };
        var target = new AppSettings();

        section.Apply(target);

        Assert.Equal(PkiSettings.MaxWarningDays, target.Pki.CertificateExpiryWarningDays);
        Assert.Equal("PkiSettingsViewModel(9999 d, CA 180 d)", section.ToString());
    }
}
