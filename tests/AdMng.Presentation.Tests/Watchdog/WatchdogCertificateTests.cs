using System.Globalization;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Presentation.Tests.TestDoubles;
using AdMng.Presentation.Watchdog;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Testing.Client;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Presentation.Tests.Watchdog;

/// <summary>
/// The certificate confirmation on the first connection (stage B design 18 item 1): the presented fingerprint is shown in
/// groups of four, pinned only after "Ufam temu certyfikatowi", a changed certificate shows both fingerprints and is never
/// re-pinned silently, and Settings → Watchdog removes the pin.
/// </summary>
public sealed class WatchdogCertificateTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    private const string FormattedSample = "3F2A 9C1E 0B7D 4A55 9E8C 1F20 6A3B 5D7E\n9F0C 2B4D 6E8A 1C3F 5B7D 9E0A 2C4F 6B8D";

    [Fact]
    public async Task FirstConnection_AsksOnce_AndPinsOnlyWhenTheOperatorTrustsTheCertificate()
    {
        using var h = await WatchdogHarness.CreateAsync(culture: Polish);
        h.Client.ServerCertificate = SampleStatus.Certificate;
        await h.Ui.RunAsync(async () =>
        {
            var tab = h.CreateDocument();
            tab.IsVisible = true;
            await tab.InitializeAsync();

            // Cancelled: nothing is pinned, the banner offers the check again.
            var dialog = Assert.Single(h.Ad.Dialogs.CertificateDialogs);
            Assert.False(dialog.IsChanged);
            Assert.Equal(FormattedSample, dialog.Fingerprint);
            Assert.Equal("admng-wd.contoso.com:8443", dialog.HostText);
            Assert.Equal("Porównaj z wynikiem sudo admng-watchdog cert-fingerprint na serwerze", dialog.CompareText);
            Assert.Equal("CN=admng-wd.contoso.test", dialog.Subject);
            Assert.Equal("admng-wd.contoso.test, admng-wd", dialog.DnsNames);
            Assert.False(dialog.IsOutsideValidity);
            Assert.Equal(WatchdogConnectionState.CertificateInvalid, tab.ConnectionState);
            Assert.Equal(h.Ad.Localizer.Format(L.WatchdogLinux.ConnectionCertificateUntrusted, "admng-wd.contoso.com:8443"), tab.ConnectionMessage);
            Assert.Same(SampleStatus.Certificate, tab.PendingCertificate);
            Assert.True(tab.CanReviewCertificate);
            Assert.True(tab.ReviewCertificateCommand.CanExecute(null));
            Assert.Null(h.Ad.Settings.Current.Watchdog.PinnedCertificateSha256);
            Assert.Null(h.Client.Options!.PinnedCertificateSha256);

            // "Sprawdź certyfikat…" → "Ufam temu certyfikatowi": pinned, connected at once.
            h.Ad.Dialogs.CertificateTrustHandler = _ => true;
            await tab.ReviewCertificateCommand.ExecuteAsync(null);
            await tab.Events.Loading;

            Assert.Equal(2, h.Ad.Dialogs.CertificateDialogs.Count);
            Assert.Equal(SampleStatus.Certificate.Sha256, h.Ad.Settings.Current.Watchdog.PinnedCertificateSha256);
            Assert.Equal(SampleStatus.Certificate.Sha256, h.Client.Options!.PinnedCertificateSha256);
            Assert.True(tab.IsConnected);
            Assert.Null(tab.PendingCertificate);
            Assert.False(tab.CanReviewCertificate);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task FirstConnection_Trusted_ConnectsWithoutAskingAgain()
    {
        using var h = await WatchdogHarness.CreateAsync();
        h.Client.ServerCertificate = SampleStatus.Certificate;
        h.Ad.Dialogs.CertificateTrustHandler = _ => true;
        await h.Ui.RunAsync(async () =>
        {
            var tab = h.CreateDocument();
            await tab.InitializeAsync();

            Assert.Single(h.Ad.Dialogs.CertificateDialogs);
            Assert.True(tab.IsConnected);
            Assert.Equal(SampleStatus.Certificate.Sha256, h.Ad.Settings.Current.Watchdog.PinnedCertificateSha256);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task ChangedCertificate_ShowsBothFingerprints_AndIsNeverRepinnedSilently()
    {
        using var h = await WatchdogHarness.CreateAsync(culture: Polish);
        await h.Ad.Settings.UpdateAsync(s => s.Watchdog.PinnedCertificateSha256 = SampleStatus.PreviousCertificateSha256, TestContext.Current.CancellationToken);
        h.Client.ServerCertificate = SampleStatus.Certificate;
        await h.Ui.RunAsync(async () =>
        {
            var tab = h.CreateDocument();
            await tab.InitializeAsync();

            var dialog = Assert.Single(h.Ad.Dialogs.CertificateDialogs);
            Assert.True(dialog.IsChanged);
            Assert.Equal(SampleStatus.PreviousCertificateSha256, dialog.PreviousSha256);
            Assert.StartsWith("9B41 D07C 2E6F 8A13", dialog.PreviousFingerprint, StringComparison.Ordinal);
            Assert.Equal(FormattedSample, dialog.Fingerprint);
            Assert.Equal(h.Ad.Localizer[L.WatchdogLinux.CertificateChanged], dialog.Message);
            Assert.Equal(h.Ad.Localizer.Format(L.WatchdogLinux.ConnectionCertificateChanged, "admng-wd.contoso.com:8443"), tab.ConnectionMessage);
            Assert.Equal(SampleStatus.PreviousCertificateSha256, tab.PendingPreviousPin);

            // Retry is a new attempt and asks once more; without "Ufam temu certyfikatowi" the old pin stays.
            await tab.RefreshNowCommand.ExecuteAsync(null);
            Assert.Equal(2, h.Ad.Dialogs.CertificateDialogs.Count);
            Assert.Equal(SampleStatus.PreviousCertificateSha256, h.Ad.Settings.Current.Watchdog.PinnedCertificateSha256);
            Assert.False(tab.IsConnected);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task ACertificateChangeWhileConnected_ChangesTheBanner_WithoutAPopup()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync();
            Assert.True(tab.IsConnected);

            h.Client.ServerCertificate = SampleStatus.Certificate;
            await tab.Status.LoadAsync();

            Assert.Equal(WatchdogConnectionState.CertificateInvalid, tab.ConnectionState);
            Assert.True(tab.CanReviewCertificate);
            Assert.Empty(h.Ad.Dialogs.CertificateDialogs);
        });
        h.AssertNoUiErrors();
    }

    [Theory]
    [InlineData("3f2a9c1e0b7d4a559e8c1f206a3b5d7e9f0c2b4d6e8a1c3f5b7d9e0a2c4f6b8d", FormattedSample)]
    [InlineData("3F:2A:9C:1E:0B:7D:4A:55:9E:8C:1F:20:6A:3B:5D:7E:9F:0C:2B:4D:6E:8A:1C:3F:5B:7D:9E:0A:2C:4F:6B:8D", FormattedSample)]
    [InlineData("3F2A9C", "3F2A 9C")]
    [InlineData(null, "")]
    public void Fingerprints_AreShownInGroupsOfFour(string? value, string expected) =>
        Assert.Equal(expected, WatchdogCertificateTrustViewModel.FormatFingerprint(value));

    [Fact]
    public void ExpiredCertificate_IsFlagged()
    {
        var localizer = new ResxLocalizer(Polish);
        var dialog = new WatchdogCertificateTrustViewModel(
            localizer, SampleStatus.Certificate, null, "admng-wd.contoso.com", SampleStatus.Certificate.NotAfter.AddDays(1), TimeZoneInfo.Utc);

        Assert.True(dialog.IsOutsideValidity);
        Assert.Equal("Certyfikat jest poza okresem ważności.", dialog.ValidityWarning);
        Assert.Equal("Potwierdź certyfikat usługi Watchdog", dialog.Title);
        Assert.Equal(
            localizer.Format(L.WatchdogLinux.CertificateValidity, localizer.FormatTime(SampleStatus.Certificate.NotBefore, TimeZoneInfo.Utc), localizer.FormatTime(SampleStatus.Certificate.NotAfter, TimeZoneInfo.Utc)),
            dialog.Validity);
        Assert.StartsWith("Ważny od ", dialog.Validity, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectionService_PinsReconfiguresAndForgetsThePinOfAnotherService()
    {
        var settings = new InMemorySettingsStore();
        await settings.LoadAsync(TestContext.Current.CancellationToken);
        var client = new FakeWatchdogClient(new FakeTimeProvider(SampleEvents.Now));
        client.Configure(null);
        var connection = new WatchdogConnectionService(client, settings, new ResxLocalizer(Polish));
        await connection.SetAddressAsync("https://admng-wd.contoso.com:8443");
        Assert.True(connection.EnsureConfigured());
        Assert.Null(client.Options!.PinnedCertificateSha256);

        await Assert.ThrowsAsync<ArgumentException>(() => connection.PinCertificateAsync("ABCD"));
        await connection.PinCertificateAsync(SampleStatus.Certificate.Sha256.ToLowerInvariant());
        Assert.Equal(SampleStatus.Certificate.Sha256, connection.PinnedCertificateSha256);
        Assert.True(connection.EnsureConfigured());
        Assert.Equal(SampleStatus.Certificate.Sha256, client.Options!.PinnedCertificateSha256);
        var configured = client.Calls.Count(nameof(IWatchdogClient.Configure));
        Assert.True(connection.EnsureConfigured());
        Assert.Equal(configured, client.Calls.Count(nameof(IWatchdogClient.Configure)));

        // Another path of the same service keeps the pin; another host or port starts over.
        await connection.SetAddressAsync("https://ADMNG-WD.contoso.com:8443/watchdog");
        Assert.Equal(SampleStatus.Certificate.Sha256, settings.Current.Watchdog.PinnedCertificateSha256);
        await connection.SetAddressAsync("https://admng-wd.contoso.com:9443");
        Assert.Null(settings.Current.Watchdog.PinnedCertificateSha256);

        await connection.PinCertificateAsync(SampleStatus.Certificate.Sha256);
        await connection.RemovePinAsync();
        Assert.Null(connection.PinnedCertificateSha256);
    }

    [Theory]
    [InlineData(WatchdogClientErrorKind.CertificateUntrusted)]
    [InlineData(WatchdogClientErrorKind.CertificateChanged)]
    public void ConnectionService_DescribesTheNewCertificateErrors(WatchdogClientErrorKind kind)
    {
        Assert.Equal(WatchdogConnectionState.CertificateInvalid, WatchdogConnectionService.StateFor(kind));
        var en = new ResxLocalizer(CultureInfo.GetCultureInfo("en-US"));
        var key = kind == WatchdogClientErrorKind.CertificateUntrusted ? L.WatchdogLinux.ConnectionCertificateUntrusted : L.WatchdogLinux.ConnectionCertificateChanged;
        Assert.Contains("{0}", en[key], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settings_ShowThePin_AndRemoveItOnSaveOnly()
    {
        using var h = await WatchdogHarness.CreateAsync(culture: Polish);
        await h.Ad.Settings.UpdateAsync(s => s.Watchdog.PinnedCertificateSha256 = SampleStatus.Certificate.Sha256, TestContext.Current.CancellationToken);
        await h.Ui.RunAsync(async () =>
        {
            using (var cancelled = new SettingsViewModel(h.Services.Ui, new FakeThemeService()))
            {
                Assert.True(cancelled.HasPinnedCertificate);
                Assert.Equal(FormattedSample, cancelled.PinnedCertificateText);
                cancelled.RemovePinCommand.Execute(null);
                Assert.False(cancelled.RemovePinCommand.CanExecute(null));
                Assert.True(cancelled.IsPinRemoved);
                Assert.Equal("Żaden certyfikat nie jest przypięty.", cancelled.PinnedCertificateText);
                cancelled.CancelCommand.Execute(null);
            }

            Assert.Equal(SampleStatus.Certificate.Sha256, h.Ad.Settings.Current.Watchdog.PinnedCertificateSha256);

            using var saved = new SettingsViewModel(h.Services.Ui, new FakeThemeService());
            saved.RemovePinCommand.Execute(null);
            await saved.SaveCommand.ExecuteAsync(null);
            Assert.Null(h.Ad.Settings.Current.Watchdog.PinnedCertificateSha256);
        });
        h.AssertNoUiErrors();
    }
}
