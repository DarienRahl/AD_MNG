using System.Security.Cryptography.X509Certificates;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.IntegrationTests.Infrastructure;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Testing.Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdMng.Watchdog.IntegrationTests.EndToEnd;

/// <summary>
/// The real watchdog service in-process (spec 22.4 end to end): Kestrel on an ephemeral loopback port with https and the
/// test certificate, Development authentication (an Admin identity for loopback callers – the lab profile), the real
/// LDAP connection to the lab as <c>odczyt.test</c> (simple bind, lab only; the password only through
/// <c>ADMNG_WATCHDOG_LDAP_PASSWORD</c>), SQLite in a temporary data folder, the real job hosts in the background,
/// ephemeral Data Protection keys and every log line captured through the production redaction wrapper.
/// </summary>
public sealed class LabServiceFactory : WebApplicationFactory<Program>
{
    public const string AdminSid = "S-1-5-21-1111111111-2222222222-3333333333-1302";
    public const string Account = @"CONTOSO\integracja.e2e";

    private readonly Dictionary<string, string?> _settings;
    private readonly Action<IServiceCollection>? _services;

    /// <param name="settings">Bootstrap settings that replace the defaults below (e.g. Negotiate authentication).</param>
    /// <param name="services">Extra registrations after the service's own (they win).</param>
    public LabServiceFactory(IDictionary<string, string?>? settings = null, Action<IServiceCollection>? services = null)
    {
        _services = services;
        DataDirectory = Path.Combine(Path.GetTempPath(), "admng-watchdog-it", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(DataDirectory);
        _settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Watchdog:DataDirectory"] = DataDirectory,
            ["Watchdog:Authentication"] = "Development",
            ["Watchdog:Hosting:Server"] = "Kestrel",
            ["Watchdog:Hosting:Urls"] = "https://127.0.0.1:0",
            ["Watchdog:Hosting:Certificate:Thumbprint"] = TestCertificates.Localhost.Thumbprint,
            ["Watchdog:Authorization:AdminSids:0"] = AdminSid,
            ["Watchdog:Development:Sids:0"] = AdminSid,
            ["Watchdog:Development:Account"] = Account,
            ["Watchdog:Directory:Domain"] = WatchdogLab.DomainDns,
            ["Watchdog:Directory:Server"] = WatchdogLab.Server,
            ["Watchdog:Directory:PreferPdc"] = "false",
            ["Watchdog:Directory:Authentication"] = "SimpleBind",
            ["Watchdog:Directory:Security"] = "None",
            ["Watchdog:Directory:LabSimpleBindUser"] = WatchdogLab.ReadOnlyUser,
            ["Watchdog:Directory:AllowInsecureLab"] = "true",
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            _settings[key] = value;
        }
    }

    public string DataDirectory { get; }

    public CapturingLoggerProvider Log { get; } = new();

    /// <summary>The https address Kestrel listens on (after <see cref="Start"/>).</summary>
    public Uri Address { get; private set; } = new("https://127.0.0.1/");

    /// <summary>Starts Kestrel and returns the address.</summary>
    public Uri Start()
    {
        UseKestrel();
        StartServer();
        Address = new Uri(Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
        return Address;
    }

    /// <summary>The AD_MNG client of the service (the GUI's client), pinned to the test certificate.</summary>
    public WatchdogHttpClient Client()
    {
        var client = new WatchdogHttpClient(TimeProvider.System, NullLogger<WatchdogHttpClient>.Instance);
        client.Configure(new WatchdogClientOptions
        {
            BaseAddress = Address,
            PinnedCertificateSha256 = TestCertificates.Sha256(TestCertificates.Localhost),
            RequestTimeout = TimeSpan.FromSeconds(60),
        });
        return client;
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(_settings));
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            services.AddSingleton<ICertificateStore>(new TestCertificateStore());
            services.AddSingleton<ILoggerProvider>(new RedactingLoggerProvider(Log));
            _services?.Invoke(services);
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            System.IO.Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A file still held by a background thread; the OS temp cleanup removes it.
        }
    }

    /// <summary>LocalMachine\My stand-in holding only the test certificate.</summary>
    private sealed class TestCertificateStore : ICertificateStore
    {
        public IReadOnlyList<X509Certificate2> GetCertificates(StoreName storeName, StoreLocation location) => [TestCertificates.Localhost];
    }
}
