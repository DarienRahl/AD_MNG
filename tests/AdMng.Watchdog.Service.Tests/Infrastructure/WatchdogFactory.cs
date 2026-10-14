using AdMng.Core.Directory;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Service.Auth;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Notifications;
using AdMng.Watchdog.Service.Storage;
using AdMng.Watchdog.Service.Tests.Fakes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdMng.Watchdog.Service.Tests.Infrastructure;

/// <summary>
/// The service in-process (spec 22.2): its own temporary data folder, <c>Watchdog:Authentication=Test</c> with
/// <see cref="TestAuthenticationHandler"/>, ephemeral Data Protection, a <see cref="TestClock"/>, a fake process
/// terminator, a fake token client and a delivery HTTP client that trusts the test certificate, the remote address from
/// <c>X-Test-Remote-Ip</c>, every log line captured in <see cref="Log"/> (through the production redaction wrapper).
/// Settings go in as host configuration, i.e. as command-line arguments the composition root reads before it registers
/// anything. The service's own jobs (detection, attribution, rules, dispatch, outbox, …) are not hosted unless
/// <see cref="RunServiceJobs"/> is set, so API tests see exactly the store state they arrange; jobs added through the
/// <c>services</c> callback are hosted.
/// </summary>
public sealed class WatchdogFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _settings;
    private readonly Action<IServiceCollection>? _services;
    private readonly string _environment;

    public WatchdogFactory(
        IDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? services = null,
        IDirectorySession? directory = null,
        string environment = "Development")
    {
        DataDirectory = new TestDataDirectory();
        Directory = directory;
        _environment = environment;
        _services = services;
        _settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Watchdog:DataDirectory"] = DataDirectory.Path,
            ["Watchdog:Authentication"] = "Test",
            ["Watchdog:Hosting:Server"] = "Kestrel",
            ["Watchdog:Hosting:Urls"] = "http://127.0.0.1:18443",
            ["Watchdog:Directory:Disabled"] = "true",
            ["Watchdog:Authorization:ViewerSids:0"] = TestRoles.ViewerGroup,
            ["Watchdog:Authorization:AdminSids:0"] = TestRoles.AdminGroup,
            ["Watchdog:Authorization:DeniedSids:0"] = TestRoles.DeniedUser,
            ["Watchdog:Notifications:OAuthSmtpHosts:0"] = "127.0.0.1",
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            _settings[key] = value;
        }
    }

    public TestDataDirectory DataDirectory { get; }

    public IDirectorySession? Directory { get; }

    public TestClock Clock { get; } = new(TestStore.Start);

    public FakeProcessTerminator Terminator { get; } = new();

    public FakeEntraTokenClient Tokens { get; } = new();

    public TestHttpClientFactory HttpClients { get; } = new();

    public TestLog Log { get; } = new();

    /// <summary>Identity of requests without test headers (client tests); null = anonymous.</summary>
    public string? DefaultAccount { get; init; }

    public IReadOnlyList<string> DefaultSids { get; init; } = [];

    /// <summary>Host the service's registered jobs in the background (as in production).</summary>
    public bool RunServiceJobs { get; init; }

    public SqliteWatchdogStore Store => Services.GetRequiredService<SqliteWatchdogStore>();

    /// <summary>A client calling as <paramref name="account"/> with the group SIDs (no account = anonymous).</summary>
    public HttpClient Client(string? account, params string[] groupSids)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (account is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthenticationHandler.AccountHeader, Uri.EscapeDataString(account));
            client.DefaultRequestHeaders.Add(TestAuthenticationHandler.SidsHeader, string.Join(',', groupSids));
        }

        client.DefaultRequestHeaders.Add(Contracts.WatchdogRoutes.ClientHeader, "1");
        return client;
    }

    public HttpClient Viewer() => Client(TestRoles.ViewerAccount, TestRoles.ViewerGroup);

    public HttpClient Admin() => Client(TestRoles.AdminAccount, TestRoles.AdminGroup);

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(_settings));
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication().AddScheme<TestAuthenticationOptions, TestAuthenticationHandler>(AuthenticationSetup.TestScheme, options =>
            {
                options.DefaultAccount = DefaultAccount;
                options.DefaultSids = DefaultSids;
            });
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<IProcessTerminator>(Terminator);
            services.AddSingleton<IEntraTokenClient>(Tokens);
            services.AddSingleton<IHttpClientFactory>(HttpClients);
            services.AddSingleton<IStartupFilter, RemoteAddressStartupFilter>();
            services.AddSingleton<ILoggerProvider>(new RedactingLoggerProvider(Log));
            if (Directory is not null)
            {
                services.AddSingleton<IDirectorySessionProvider>(new FakeDirectorySessionProvider(Directory));
            }

            if (!RunServiceJobs)
            {
                services.RemoveAll<IWatchdogJob>();
            }

            _services?.Invoke(services);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            DataDirectory.Dispose();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        DataDirectory.Dispose();
    }
}
