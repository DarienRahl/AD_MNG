using System.Security.Cryptography.X509Certificates;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using AdMng.Watchdog.Testing.Certificates;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdMng.Watchdog.Service.Tests.Client;

/// <summary>The real service on Kestrel (https on an ephemeral loopback port with the test certificate) and a
/// <see cref="WatchdogHttpClient"/> pinned to that certificate.</summary>
public sealed class WatchdogClientServer : IAsyncDisposable
{
    /// <summary>The server certificate. The service selects it with its fake clock (<see cref="TestStore.Start"/>),
    /// the client checks it at the real time, so it is valid at both whatever the real date.</summary>
    private static readonly Lazy<X509Certificate2> LazyCertificate = new(() =>
    {
        var now = DateTimeOffset.UtcNow;
        var first = TestStore.Start < now ? TestStore.Start : now;
        var last = TestStore.Start > now ? TestStore.Start : now;
        return TestCertificates.Create("localhost", first.AddDays(-1), last.AddDays(30));
    });

    private WatchdogClientServer(WatchdogFactory factory, Uri address)
    {
        Factory = factory;
        Address = address;
    }

    public WatchdogFactory Factory { get; }

    public Uri Address { get; }

    private static X509Certificate2 Certificate => LazyCertificate.Value;

    public static WatchdogClientServer Start(string? account, params string[] sids) => Start(null, account, sids);

    /// <summary>The server with extra service registrations (after the service's, so they win).</summary>
    public static WatchdogClientServer Start(Action<IServiceCollection>? configure, string? account, params string[] sids)
    {
        var factory = new WatchdogFactory(
            new Dictionary<string, string?>
            {
                ["Watchdog:Hosting:Urls"] = "https://127.0.0.1:0",
                ["Watchdog:Hosting:Certificate:Thumbprint"] = Certificate.Thumbprint,
            },
            services =>
            {
                services.AddSingleton<ICertificateStore>(new SingleCertificateStore());
                configure?.Invoke(services);
            })
        {
            DefaultAccount = account,
            DefaultSids = sids,
        };
        factory.UseKestrel();
        factory.StartServer();
        var address = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new WatchdogClientServer(factory, new Uri(address));
    }

    public WatchdogHttpClient Client(TimeProvider? timeProvider = null, bool pinned = true, TimeSpan? timeout = null)
    {
        var client = new WatchdogHttpClient(timeProvider ?? TimeProvider.System, NullLogger<WatchdogHttpClient>.Instance);
        client.Configure(new WatchdogClientOptions
        {
            BaseAddress = Address,
            PinnedCertificateSha256 = pinned ? TestCertificates.Sha256(Certificate) : null,
            RequestTimeout = timeout ?? TimeSpan.FromSeconds(30),
        });
        return client;
    }

    public async ValueTask DisposeAsync() => await Factory.DisposeAsync();

    private sealed class SingleCertificateStore : ICertificateStore
    {
        public IReadOnlyList<X509Certificate2> GetCertificates(StoreName storeName, StoreLocation location) => [Certificate];
    }
}
