using System.Diagnostics;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Configuration;
using AdMng.Watchdog.Testing.Attribution;
using AdMng.Watchdog.Testing.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Watchdog.IntegrationTests.Infrastructure;

/// <summary>
/// The real watchdog engines against the lab (spec 22.4): <c>AddWatchdogCore()</c> with the in-memory store behind every
/// store interface, a <see cref="LabSessionProvider"/> bound as the engine's account and a fake clock that starts at the
/// real time (the DC's own times – whenCreated, lockoutTime – stay comparable). Engines are reached only through the
/// abstractions: jobs as <see cref="IWatchdogJob"/> by name, state through the store interfaces and
/// <see cref="IHealthRegistry"/>. The engine watches the whole lab domain; tests look only at events below their OU.
/// </summary>
public sealed class EngineHost : IAsyncDisposable
{
    public const string DetectionJob = "detection";
    public const string ScansJob = "scans";

    private readonly ServiceProvider _services;
    private readonly LabSessionProvider _sessions;
    private readonly Dictionary<string, IWatchdogJob> _jobs;

    private EngineHost(ServiceProvider services, LabSessionProvider sessions, FakeTimeProvider clock, CapturingLoggerProvider log)
    {
        _services = services;
        _sessions = sessions;
        Clock = clock;
        Log = log;
        _jobs = services.GetServices<IWatchdogJob>().ToDictionary(j => j.Name, StringComparer.Ordinal);
    }

    public FakeTimeProvider Clock { get; }

    public CapturingLoggerProvider Log { get; }

    public IServiceProvider Services => _services;

    public IEventStore Events => _services.GetRequiredService<IEventStore>();

    public IDetectionStore Snapshots => _services.GetRequiredService<IDetectionStore>();

    public IHealthRegistry Health => _services.GetRequiredService<IHealthRegistry>();

    public IDirectorySessionProvider Sessions => _sessions;

    /// <summary>Duration of the baseline cycle of <see cref="StartAsync"/>.</summary>
    public TimeSpan BaselineDuration { get; private set; }

    /// <summary>
    /// Builds the engines, stores the configuration (defaults with <c>CreationSettleWindow = 0</c>, then
    /// <paramref name="configure"/>) and takes the baseline of the whole lab domain.
    /// </summary>
    public static async Task<EngineHost> StartAsync(EngineAccount account, Func<WatchdogConfiguration, WatchdogConfiguration>? configure,
        CancellationToken cancellationToken)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var log = new CapturingLoggerProvider();
        var sessions = new LabSessionProvider(account);
        var store = new InMemoryWatchdogStore(clock);
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug).AddProvider(log));
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IDirectorySessionProvider>(sessions);
        services.AddSingleton<ISecurityEventSource>(new FakeSecurityEventSource(isAvailable: false));
        services.AddSingleton(store);
        services.AddSingleton<IDetectionStore>(store);
        services.AddSingleton<IEventStore>(store);
        services.AddSingleton<INotificationQueueStore>(store);
        services.AddSingleton<IOutboxStore>(store);
        services.AddSingleton<ISecurityEventStore>(store);
        services.AddSingleton<IExpiryNoticeStore>(store);
        services.AddSingleton<IDigestRunStore>(store);
        services.AddSingleton<IConfigStore>(store);
        services.AddSingleton<ISecretStore>(store);
        services.AddSingleton<IStoreMaintenance>(store);
        services.AddWatchdogCore();

        var host = new EngineHost(services.BuildServiceProvider(), sessions, clock, log);
        try
        {
            await host.SaveConfigurationAsync(c => (configure ?? (x => x))(c with
            {
                Directory = c.Directory with { CreationSettleWindow = TimeSpan.Zero },
            }), cancellationToken);
            var watch = Stopwatch.StartNew();
            await host.RunDetectionAsync(cancellationToken);
            host.BaselineDuration = watch.Elapsed;
            var state = await host.Snapshots.GetDcStateAsync(WatchdogLab.DomainDns, cancellationToken);
            Assert.True(state?.BaselineCompletedAt is not null, "the first cycle did not complete the baseline");
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    /// <summary>Changes and stores the configuration (a new version) and reloads the provider.</summary>
    public async Task SaveConfigurationAsync(Func<WatchdogConfiguration, WatchdogConfiguration> change, CancellationToken cancellationToken)
    {
        var provider = _services.GetRequiredService<IWatchdogConfigProvider>();
        var current = await provider.ReloadAsync(cancellationToken);
        var store = _services.GetRequiredService<IConfigStore>();
        await store.SaveAsync(change(current.Configuration), current.Version, new PrincipalRef { Account = @"CONTOSO\integration.test" },
            "integration test", [], cancellationToken);
        await provider.ReloadAsync(cancellationToken);
    }

    /// <summary>One detection cycle; fails the test when the job reports a failure.</summary>
    public Task RunDetectionAsync(CancellationToken cancellationToken) => RunAsync(DetectionJob, cancellationToken);

    /// <summary>One run of the scheduled scans (self-check, expiry, lockout monitor, bursts – each when due).</summary>
    public Task RunScansAsync(CancellationToken cancellationToken) => RunAsync(ScansJob, cancellationToken);

    /// <summary>Runs one iteration of a job. The fake clock first catches up with the real time (never goes back): the DC
    /// stamps its own times (whenCreated, lockoutTime) and an engine clock behind them would see the future.</summary>
    public async Task RunAsync(string job, CancellationToken cancellationToken)
    {
        CatchUpWithRealTime();
        var result = await _jobs[job].RunOnceAsync(cancellationToken);
        Assert.False(result.Failed, $"job {job} failed: {result.FailureCode}");
    }

    /// <summary>
    /// Runs the daily reconciliation now (section 10.4): <c>Directory.FullReconciliationTime</c> is set to the current local
    /// time (configured time zone) and the clock moves 30 s past it, so the next cycle is a full scan that compares the
    /// directory with every stored snapshot. Near local midnight the clock first moves into the next day.
    /// </summary>
    public async Task RunReconciliationAsync(CancellationToken cancellationToken)
    {
        CatchUpWithRealTime();
        var provider = _services.GetRequiredService<IWatchdogConfigProvider>();
        var timeZone = TimeZoneResolver.Resolve(provider.Current.Configuration.General.TimeZone);
        if (TimeZoneInfo.ConvertTime(Clock.GetUtcNow(), timeZone).TimeOfDay > new TimeSpan(23, 55, 0))
        {
            Clock.Advance(TimeSpan.FromMinutes(10));
        }

        var local = TimeZoneInfo.ConvertTime(Clock.GetUtcNow(), timeZone);
        var at = new TimeOnly(local.Hour, local.Minute, local.Second);
        await SaveConfigurationAsync(c => c with { Directory = c.Directory with { FullReconciliationTime = at } }, cancellationToken);
        Clock.Advance(TimeSpan.FromSeconds(30));
        var before = (await Snapshots.GetDcStateAsync(WatchdogLab.DomainDns, cancellationToken))?.LastFullSyncAt;
        await RunAsync(DetectionJob, cancellationToken);
        var after = (await Snapshots.GetDcStateAsync(WatchdogLab.DomainDns, cancellationToken))?.LastFullSyncAt;
        Assert.True(after > before, $"the cycle was not a reconciliation (last full sync {before:O} → {after:O})");
    }

    /// <summary>Moves the fake clock forward to the real time when it is behind (a test may have moved it further).</summary>
    public void CatchUpWithRealTime()
    {
        var now = DateTimeOffset.UtcNow;
        if (now > Clock.GetUtcNow())
        {
            Clock.SetUtcNow(now);
        }
    }

    /// <summary>The id of the newest stored event (events of a step are those after it).</summary>
    public Task<long> MarkAsync(CancellationToken cancellationToken) => Events.GetLatestIdAsync(cancellationToken);

    /// <summary>Events after <paramref name="mark"/> whose object (or previous DN) is <paramref name="subtree"/> or below it.</summary>
    public Task<IReadOnlyList<WatchEvent>> EventsAsync(string subtree, long mark, CancellationToken cancellationToken) =>
        QueryAllAsync(new EventQuery { After = mark, Subtree = subtree, Limit = 500 }, cancellationToken);

    /// <summary>All events after <paramref name="mark"/> (health events have no DN).</summary>
    public Task<IReadOnlyList<WatchEvent>> AllEventsAsync(long mark, CancellationToken cancellationToken) =>
        QueryAllAsync(new EventQuery { After = mark, Limit = 500 }, cancellationToken);

    /// <summary>Every page of an ascending (<see cref="EventQuery.After"/>) query.</summary>
    private async Task<IReadOnlyList<WatchEvent>> QueryAllAsync(EventQuery query, CancellationToken cancellationToken)
    {
        var events = new List<WatchEvent>();
        while (true)
        {
            var page = await Events.QueryAsync(query, cancellationToken);
            events.AddRange(page.Items);
            if (page.Items.Count < query.Limit)
            {
                return events;
            }

            query = query with { After = page.Items.Max(e => e.Id) };
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _sessions.DisposeAsync();
    }
}
