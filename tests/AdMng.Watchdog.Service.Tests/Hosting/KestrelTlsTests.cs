using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Service.Auth;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using AdMng.Watchdog.Testing.Certificates;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Watchdog.Service.Tests.Hosting;

/// <summary>The real Kestrel listener with the selected certificate (spec 17.1, 19.3): TLS 1.2+ only, HTTP/1.1.</summary>
public sealed class KestrelTlsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Https_ServesHealthz_WithTheSelectedCertificate_AndRefusesTls11()
    {
        using var directory = new TestDataDirectory();
        var settings = new Dictionary<string, string?>
        {
            ["Watchdog:DataDirectory"] = directory.Path,
            ["Watchdog:Authentication"] = "Test",
            ["Watchdog:Hosting:Urls"] = "https://127.0.0.1:0",
            ["Watchdog:Hosting:Certificate:Thumbprint"] = TestCertificates.Localhost.Thumbprint,
            ["Watchdog:Directory:Disabled"] = "true",
        };
        await using var app = WatchdogHost.Build(["--environment", "Development"], builder =>
        {
            builder.Configuration.AddInMemoryCollection(settings);
            builder.Services.AddSingleton<ICertificateStore>(new SingleCertificateStore(TestCertificates.Localhost));
            builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(AuthenticationSetup.TestScheme, null);
        });
        await app.StartAsync(Ct);
        try
        {
            var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());

            Assert.Equal("ok", await GetHealthAsync(address));
#pragma warning disable SYSLIB0039 // TLS 1.1 is requested on purpose: the server must refuse it
            await Assert.ThrowsAnyAsync<Exception>(() => HandshakeAsync(address, SslProtocols.Tls11));
#pragma warning restore SYSLIB0039
            Assert.Equal(SslProtocols.Tls13, await HandshakeAsync(address, SslProtocols.Tls13));
        }
        finally
        {
            await app.StopAsync(Ct);
        }
    }

    private static async Task<string> GetHealthAsync(Uri address)
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            SslOptions = { RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate?.GetCertHashString() == TestCertificates.Localhost.Thumbprint },
        };
        using var client = new HttpClient(handler) { BaseAddress = address };
        return await client.GetStringAsync(WatchdogRoutes.Health, Ct);
    }

    private static async Task<SslProtocols> HandshakeAsync(Uri address, SslProtocols protocol)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(address.Host, address.Port, Ct);
        await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost", EnabledSslProtocols = protocol }, Ct);
        return ssl.SslProtocol;
    }

    private sealed class SingleCertificateStore(X509Certificate2 certificate) : ICertificateStore
    {
        public IReadOnlyList<X509Certificate2> GetCertificates(StoreName storeName, StoreLocation location) => [certificate];
    }
}
