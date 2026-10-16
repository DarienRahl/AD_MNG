using System.Globalization;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Tests.TestDoubles;
using AdMng.Presentation.Watchdog;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Testing.Client;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Presentation.Tests.Watchdog;

/// <summary>The service address (https only, stored in the settings), client configuration and error texts.</summary>
public sealed class WatchdogConnectionServiceTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    [Theory]
    [InlineData("https://admng-wd.contoso.com:8443", "https://admng-wd.contoso.com:8443/")]
    [InlineData("  https://admng-wd.contoso.com  ", "https://admng-wd.contoso.com/")]
    [InlineData("https://ADMNG-WD.contoso.com/watchdog", "https://admng-wd.contoso.com/watchdog/")]
    [InlineData("https://[fd00::25]:8443/", "https://[fd00::25]:8443/")]
    public void TryParseAddress_AcceptsHttpsUrls_Normalized(string text, string expected)
    {
        Assert.True(WatchdogConnectionService.TryParseAddress(text, out var address));
        Assert.Equal(expected, address.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("admng-wd.contoso.com")]
    [InlineData("http://admng-wd.contoso.com")]
    [InlineData("ftp://admng-wd.contoso.com")]
    [InlineData("https://jan:hasło@admng-wd.contoso.com")]
    [InlineData("https://admng-wd.contoso.com/?token=abc")]
    [InlineData("https://admng-wd.contoso.com/#stan")]
    [InlineData("/api/v1")]
    public void TryParseAddress_RefusesEverythingElse(string? text) =>
        Assert.False(WatchdogConnectionService.TryParseAddress(text, out _));

    [Fact]
    public void HostOf_OmitsTheDefaultPort()
    {
        Assert.Equal("admng-wd.contoso.com", WatchdogConnectionService.HostOf(new Uri("https://admng-wd.contoso.com/")));
        Assert.Equal("admng-wd.contoso.com:8443", WatchdogConnectionService.HostOf(new Uri("https://admng-wd.contoso.com:8443/")));
    }

    [Theory]
    [InlineData(WatchdogClientErrorKind.NotConfigured, WatchdogConnectionState.NotConfigured)]
    [InlineData(WatchdogClientErrorKind.Unreachable, WatchdogConnectionState.Unreachable)]
    [InlineData(WatchdogClientErrorKind.Timeout, WatchdogConnectionState.Unreachable)]
    [InlineData(WatchdogClientErrorKind.CertificateInvalid, WatchdogConnectionState.CertificateInvalid)]
    [InlineData(WatchdogClientErrorKind.Unauthorized, WatchdogConnectionState.Unauthorized)]
    [InlineData(WatchdogClientErrorKind.Forbidden, WatchdogConnectionState.Forbidden)]
    [InlineData(WatchdogClientErrorKind.ApiVersionMismatch, WatchdogConnectionState.VersionMismatch)]
    [InlineData(WatchdogClientErrorKind.NotFound, WatchdogConnectionState.Error)]
    [InlineData(WatchdogClientErrorKind.Validation, WatchdogConnectionState.Error)]
    [InlineData(WatchdogClientErrorKind.VersionConflict, WatchdogConnectionState.Error)]
    [InlineData(WatchdogClientErrorKind.ServerError, WatchdogConnectionState.Error)]
    public void StateFor_MapsEveryErrorKind(WatchdogClientErrorKind kind, WatchdogConnectionState expected)
    {
        Assert.Equal(expected, WatchdogConnectionService.StateFor(kind));
        Assert.Equal(expected, WatchdogConnectionService.StateFor(new WatchdogClientException(kind, "x")));
        Assert.Equal(expected is not WatchdogConnectionState.Error, WatchdogConnectionService.IsConnectionFailure(new WatchdogClientException(kind, "x")));
    }

    [Fact]
    public void StateFor_OtherExceptions_IsAnError()
    {
        Assert.Equal(WatchdogConnectionState.Error, WatchdogConnectionService.StateFor(new InvalidOperationException()));
        Assert.False(WatchdogConnectionService.IsConnectionFailure(new InvalidOperationException()));
    }

    [Fact]
    public async Task SetAddress_StoresTheNormalizedHttpsAddress_AndRaisesAddressChanged()
    {
        var (connection, settings, client) = await CreateAsync();
        var changed = 0;
        connection.AddressChanged += (_, _) => changed++;

        Assert.Equal(new ResxLocalizer(Polish)[L.Watchdog.AddressHttpsOnly], await connection.SetAddressAsync("http://admng-wd.contoso.com"));
        Assert.Equal(new ResxLocalizer(Polish)[L.Watchdog.AddressRequired], await connection.SetAddressAsync("  "));
        Assert.Equal(0, changed);
        Assert.False(connection.HasServiceUrl);

        Assert.Null(await connection.SetAddressAsync(" https://admng-wd.contoso.com:8443/ "));

        Assert.Equal(1, changed);
        Assert.Equal("https://admng-wd.contoso.com:8443", settings.Current.Watchdog.ServiceUrl);
        Assert.True(connection.HasServiceUrl);
        Assert.Equal("admng-wd.contoso.com:8443", connection.HostText);
        Assert.False(client.IsConfigured);

        await connection.ClearAddressAsync();
        Assert.Equal(2, changed);
        Assert.Null(settings.Current.Watchdog.ServiceUrl);
        Assert.Equal(string.Empty, connection.HostText);
    }

    [Fact]
    public async Task EnsureConfigured_PassesTheAddressAndThePinnedCertificate_Once()
    {
        var (connection, settings, client) = await CreateAsync();
        Assert.False(connection.EnsureConfigured());
        Assert.False(client.IsConfigured);

        await settings.UpdateAsync(s =>
        {
            s.Watchdog.ServiceUrl = "https://admng-wd.contoso.com:8443";
            s.Watchdog.PinnedCertificateSha256 = "3F2A9C";
        }, TestContext.Current.CancellationToken);

        Assert.True(connection.EnsureConfigured());
        Assert.Equal(new Uri("https://admng-wd.contoso.com:8443/"), client.Options!.BaseAddress);
        Assert.Equal("3F2A9C", client.Options.PinnedCertificateSha256);
        Assert.Null(client.Options.Credential);
        Assert.False(client.Options.AllowInsecureLoopback);

        Assert.True(connection.EnsureConfigured());
        Assert.Equal(2, client.Calls.Count(nameof(IWatchdogClient.Configure)));

        await connection.SetAddressAsync("https://wd02.contoso.com");
        Assert.True(connection.EnsureConfigured());
        Assert.Equal(new Uri("https://wd02.contoso.com/"), client.BaseAddress);
    }

    [Fact]
    public async Task Describe_ExplainsEveryFailure_WithTheHost()
    {
        var (connection, settings, _) = await CreateAsync();
        await settings.UpdateAsync(s => s.Watchdog.ServiceUrl = "https://admng-wd.contoso.com:8443", TestContext.Current.CancellationToken);
        var l = new ResxLocalizer(Polish);

        Assert.Equal(l.Format(L.Watchdog.ConnectionUnreachable, "admng-wd.contoso.com:8443"), connection.Describe(Error(WatchdogClientErrorKind.Unreachable)));
        Assert.Equal(l.Format(L.Watchdog.ConnectionTimeout, "admng-wd.contoso.com:8443"), connection.Describe(Error(WatchdogClientErrorKind.Timeout)));
        Assert.Equal(l.Format(L.Watchdog.ConnectionCertificateInvalid, "admng-wd.contoso.com:8443"), connection.Describe(Error(WatchdogClientErrorKind.CertificateInvalid)));
        Assert.Equal(l[L.Watchdog.ConnectionUnauthorized], connection.Describe(Error(WatchdogClientErrorKind.Unauthorized)));
        Assert.Equal(l[L.Watchdog.ConnectionForbidden], connection.Describe(Error(WatchdogClientErrorKind.Forbidden)));
        Assert.Equal(l[L.Watchdog.ConnectionVersionMismatch], connection.Describe(Error(WatchdogClientErrorKind.ApiVersionMismatch)));
        Assert.Equal(l[L.Watchdog.ConnectionNotConfigured], connection.Describe(Error(WatchdogClientErrorKind.NotConfigured)));
        Assert.Equal(l[L.Watchdog.ErrorNotFound], connection.Describe(Error(WatchdogClientErrorKind.NotFound)));
        Assert.Equal(l[L.Watchdog.ErrorValidation], connection.Describe(Error(WatchdogClientErrorKind.Validation)));
        Assert.Equal(l[L.Watchdog.ErrorVersionConflict], connection.Describe(Error(WatchdogClientErrorKind.VersionConflict)));
        Assert.Equal(l[L.Errors.Cancelled], connection.Describe(Error(WatchdogClientErrorKind.Cancelled)));
        Assert.Equal(l[L.Errors.Cancelled], connection.Describe(new OperationCanceledException()));
        Assert.Equal(l.Format(L.Watchdog.ErrorServerStatus, 503), connection.Describe(new WatchdogClientException(WatchdogClientErrorKind.ServerError, "x") { StatusCode = 503 }));
        Assert.Equal(l[L.Watchdog.ErrorServer], connection.Describe(Error(WatchdogClientErrorKind.ServerError)));
        Assert.Equal(l[L.Watchdog.ErrorUnexpected], connection.Describe(new InvalidOperationException("Szczegóły wewnętrzne")));
        Assert.Contains("Kerberos", l[L.Watchdog.ConnectionUnauthorized], StringComparison.Ordinal);
    }

    [Fact]
    public async Task HistoryLauncher_FollowsTheAddress()
    {
        var (connection, _, _) = await CreateAsync();
        var launcher = new WatchdogObjectHistoryLauncher(connection);
        var requests = new List<WatchdogHistoryRequest>();
        var availability = 0;
        launcher.Requested += (_, r) => requests.Add(r);
        launcher.AvailabilityChanged += (_, _) => availability++;
        var guid = Guid.Parse("0b6f7c1e-2d3a-4b5c-9d8e-7f6a5b4c3d2e");

        Assert.False(launcher.IsAvailable);
        Assert.False(launcher.CanShow(guid));
        Assert.False(launcher.Show(guid, "Żaneta Szymańska"));

        await connection.SetAddressAsync("https://admng-wd.contoso.com");

        Assert.Equal(1, availability);
        Assert.True(launcher.CanShow(guid));
        Assert.False(launcher.CanShow(null));
        Assert.True(launcher.Show(guid, "Żaneta Szymańska"));
        Assert.Equal([new WatchdogHistoryRequest(guid, "Żaneta Szymańska")], requests);
    }

    private static WatchdogClientException Error(WatchdogClientErrorKind kind) => new(kind, "x");

    private static async Task<(WatchdogConnectionService Connection, InMemorySettingsStore Settings, FakeWatchdogClient Client)> CreateAsync()
    {
        var settings = new InMemorySettingsStore();
        await settings.LoadAsync(TestContext.Current.CancellationToken);
        var client = new FakeWatchdogClient(new FakeTimeProvider(SampleEvents.Now));

        // The application's client starts without an address.
        client.Configure(null);
        return (new WatchdogConnectionService(client, settings, new ResxLocalizer(Polish)), settings, client);
    }
}
