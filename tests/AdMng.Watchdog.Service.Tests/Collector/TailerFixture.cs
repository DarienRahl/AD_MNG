using System.Net;
using System.Runtime.Versioning;
using System.Text;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Service.Attribution;
using AdMng.Watchdog.Service.Directory;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using AdMng.Watchdog.Testing.Fixtures.OpenWec;
using AdMng.Watchdog.Testing.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Watchdog.Service.Tests.Collector;

/// <summary>
/// The WEF tailer over real files and the SQLite store: an OpenWEC output folder with one client directory per DC, the
/// progress file next to it, and a new job (a new process: fresh in-memory state) for every <see cref="NewJob"/> – the
/// store survives, so a "restart" after an injected crash resumes from the committed cursor.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class TailerFixture : IAsyncDisposable
{
    public const string Dc1 = "dc01.contoso.test";
    public const string Dc2 = "dc02.contoso.test";
    public const string Client1 = "DC01$@CONTOSO.TEST";
    public const string Client2 = "DC02$@CONTOSO.TEST";
    public const string Ip1 = "10.0.1.10";
    public const string Ip2 = "10.0.1.11";
    public const string AdminSid = "S-1-5-21-1004336348-1177238915-682003330-1104";

    private readonly List<ServiceProvider> _providers = [];
    private long _recordId = 100;

    public TailerFixture(WatchdogConfiguration? configuration = null)
    {
        Store = TestStore.Create();
        Output = Path.Combine(Store.Directory.Path, "openwec-out");
        System.IO.Directory.CreateDirectory(Output);
        ProgressFile = Path.Combine(Store.Directory.Path, "run", "wec-progress.json");
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(ProgressFile)!);
        Clock.SetUtcNow(TestStore.Start);
        Config = new ScenarioConfigProvider(configuration ?? new WatchdogConfiguration());
    }

    public TestStore Store { get; }

    public FakeTimeProvider Clock => Store.Clock;

    public string Output { get; }

    public string ProgressFile { get; }

    public ScenarioConfigProvider Config { get; }

    public IReadOnlyList<CollectorDomainController> DomainControllers { get; set; } =
    [
        new(Dc1, "DC01$", [IPAddress.Parse(Ip1)]),
        new(Dc2, "DC02$", [IPAddress.Parse(Ip2)]),
    ];

    public int MaxLogonRecordsPerDcPerMinute { get; set; } = 20_000;

    /// <summary>The services of the last job (health registry, tracker …).</summary>
    public IServiceProvider Services => _providers[^1];

    public IHealthRegistry Health => Services.GetRequiredService<IHealthRegistry>();

    public string Directory(string client) => Path.Combine(Output, WecRawJson.ClientDirectory(client));

    public string Active(string client) => Path.Combine(Directory(client), "security.jsonl");

    public long NextId() => Interlocked.Increment(ref _recordId);

    /// <summary>A 4724 by the admin on <paramref name="dc"/> – a change-related record with a unique record id.</summary>
    public string Change(string dc, long? id = null, DateTimeOffset? at = null) =>
        WindowsEventXml.Account(4724, id ?? NextId(), at ?? Clock.GetUtcNow().AddMinutes(-1), dc, "S-1-5-21-1004336348-1177238915-682003330-6609", "zofia.żak",
            WindowsEventXml.Subject(AdminSid, "adm.łukasz", "0x5a3f1"));

    public static string Line(string xml, string client = Client1, string ip = Ip1, DateTimeOffset? received = null) =>
        WecRawJson.Line(xml, client, ip, received ?? TestStore.Start.AddSeconds(-30));

    public void Append(string path, params string[] content)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, string.Concat(content), new UTF8Encoding(false));
    }

    /// <summary>What admng-wec-rotate does to the active file (rename in place) – returns the rotated path.</summary>
    public string Rotate(string client, DateTimeOffset at)
    {
        var rotated = Active(client) + "." + at.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        File.Move(Active(client), rotated);
        return rotated;
    }

    /// <summary>A new job over the same store and folder (a service restart).</summary>
    public WecFileIngestionJob NewJob(Action<string, IReadOnlyList<WecLine>>? fault = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddWatchdogCore();
        services.AddSingleton<IWatchdogConfigProvider>(Config);
        services.AddSingleton<IDetectionStore>(Store.Store);
        services.AddSingleton<IEventStore>(Store.Store);
        services.AddSingleton<ISecurityEventStore>(Store.Store);
        services.AddSingleton<INotificationQueueStore>(Store.Store);
        services.AddSingleton<IOutboxStore>(Store.Store);
        services.AddSingleton<IExpiryNoticeStore>(Store.Store);
        services.AddSingleton<IDigestRunStore>(Store.Store);
        services.AddSingleton<IConfigStore>(Store.Store);
        services.AddSingleton<ISecretStore>(Store.Store);
        services.AddSingleton<IStoreMaintenance>(Store.Store);
        services.AddSingleton<IDirectorySessionProvider, DisabledDirectorySessionProvider>();
        services.AddSingleton<ISecurityEventSource, NullSecurityEventSource>();
        services.AddSingleton<ICollectorDomainControllerSource>(new FixedDcSource(this));
        services.AddSingleton<IOptionsMonitor<ServiceOptions>>(new StaticOptions(new ServiceOptions
        {
            Collector = new CollectorBootstrapOptions
            {
                OutputDirectory = Output,
                ProgressFile = ProgressFile,
                MaxLogonRecordsPerDcPerMinute = MaxLogonRecordsPerDcPerMinute,
            },
            Kerberos = new KerberosBootstrapOptions { Realm = "CONTOSO.TEST" },
        }));
        services.AddSingleton<WecFileIngestionJob>();
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        var job = provider.GetRequiredService<WecFileIngestionJob>();
        job.FaultPoint = fault;
        return job;
    }

    /// <summary>Runs <paramref name="job"/> until it waits for new data (at most 50 runs).</summary>
    public async Task<JobResult> RunUntilIdleAsync(WecFileIngestionJob job, CancellationToken cancellationToken)
    {
        JobResult result = default;
        for (var i = 0; i < 50; i++)
        {
            result = await job.RunOnceAsync(cancellationToken);
            if (result.Failed || result.NextRunIn != TimeSpan.Zero)
            {
                return result;
            }
        }

        return result;
    }

    /// <summary>The stored records as (computer, record id), ordered.</summary>
    public async Task<IReadOnlyList<(string Computer, long RecordId)>> StoredAsync(CancellationToken cancellationToken) =>
        [.. (await Store.Store.FindAsync(new SecurityEventCriteria { From = DateTimeOffset.UnixEpoch, To = DateTimeOffset.MaxValue, Limit = 100_000 }, cancellationToken))
            .Select(e => (e.Computer.ToLowerInvariant(), e.RecordId)).Order()];

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers)
        {
            await provider.DisposeAsync();
        }

        await Store.DisposeAsync();
    }

    private sealed class FixedDcSource(TailerFixture fixture) : ICollectorDomainControllerSource
    {
        public Task<IReadOnlyList<CollectorDomainController>> GetAsync(CancellationToken cancellationToken) => Task.FromResult(fixture.DomainControllers);
    }

    private sealed class StaticOptions(ServiceOptions value) : IOptionsMonitor<ServiceOptions>
    {
        public ServiceOptions CurrentValue => value;

        public ServiceOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<ServiceOptions, string?> listener) => null;
    }
}
