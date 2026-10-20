using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Core.Tests.Attribution.Fakes;
using AdMng.Watchdog.Core.Tests.Rules;
using AdMng.Watchdog.Core.Tests.Rules.Fakes;
using AdMng.Watchdog.Testing.Attribution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Watchdog.Core.Tests.Attribution;

/// <summary>The attribution object graph over in-memory fakes (CONTOSO, two writable DCs, time
/// <see cref="TestEvents.Now"/>).</summary>
internal sealed class AttributionHarness : IDisposable
{
    /// <param name="configuration">The runtime configuration (default: the record defaults – source OpenWec).</param>
    /// <param name="collector">Collector mode (stage B design 7.8): a WEF collector is available, the logon and failure
    /// lookups read the stored records; otherwise OpenWec falls back to the live DC-log queries of the fake source.</param>
    public AttributionHarness(WatchdogConfiguration? configuration = null, bool collector = false)
    {
        Config = new FakeConfigProvider(configuration ?? new WatchdogConfiguration());
        Collector = new FakeCollector { IsAvailable = collector };
        Lookup = new StoredSecurityEventLookup(SecurityStore, Collector);
        Tracker = new DcLogHealthTracker(Time);
        Gate = new SourceQueryGate(Tracker, Time);
        Actors = new ActorResolver(Detection, new FakeSyncAccounts(TestEvents.SyncSid));
        Processor = new PendingAttributionProcessor(
            new AttributionCorrelator(SecurityStore),
            Actors,
            new LogonSourceResolver(Source, Gate, Dns, Time, Tracker, Lookup),
            new LockoutSourceResolver(SecurityStore, Source, Gate, DomainControllers, new FixedLockoutPolicy(TimeSpan.FromMinutes(30)), Lookup),
            new UnresolvedReasonResolver(SecurityStore, Tracker, Time),
            new PrivilegeAttributionInheritor(Events),
            Tracker,
            Time,
            new AdMng.Watchdog.Core.Gpo.GpoAttributionInheritor(Events),
            GpoContent);
        Gaps = new EventLogGapDetector(Time);
        Reductions = new SecurityEventReductionFactory(new FakeSessionProvider(new AdMng.Testing.FakeDirectorySession()), Tokens,
            new AdMng.Watchdog.Core.Detection.WatchedAttributeCatalog(Config));
        var gaps = Gaps;
        Rules = new AdMng.Watchdog.Core.Rules.RuleEngine(Config, Tokens, Time);
        Engine = new AttributionEngine(
            Config,
            Source,
            Events,
            Queue,
            DomainControllers,
            new AttributionSourceResolver(DomainControllers, Health, Time, Collector),
            Reductions,
            Ingestor = new SecurityLogIngestor(Source, SecurityStore, Events, new SecurityEventParser(), gaps, new EventLogOnlyDetector(Actors, Time), Tracker, Health, Time,
                NullLogger<SecurityLogIngestor>.Instance),
            Processor,
            Tracker,
            new AttributionDegradedMonitor(Tracker, Time),
            new ActorConditionHealthMonitor(Time),
            Tokens,
            Health,
            Signals,
            [FollowUps],
            Rules,
            [Planner],
            Time,
            NullLogger<AttributionEngine>.Instance,
            Collector);
        CollectorIngestor = new CollectorIngestor(Ingestor, Events, gaps, Tracker);
    }

    /// <summary>The WEF collector (available in collector mode).</summary>
    public FakeCollector Collector { get; }

    /// <summary>The stored-record lookups of collector mode.</summary>
    public StoredSecurityEventLookup Lookup { get; }

    public EventLogGapDetector Gaps { get; }

    public SecurityLogIngestor Ingestor { get; }

    /// <summary>The Core part of the WEF tailer over the same stores.</summary>
    public CollectorIngestor CollectorIngestor { get; }

    public SecurityEventReductionFactory Reductions { get; }

    /// <summary>The rule engine of the re-evaluation after attribution (shared with a rules job in race tests).</summary>
    public AdMng.Watchdog.Core.Rules.RuleEngine Rules { get; }

    /// <summary>Stands in for the NotificationPlanner of the re-evaluation after attribution.</summary>
    public RecordingPlanner Planner { get; } = new();

    public FakeTimeProvider Time { get; } = new(TestEvents.Now);

    public FakeConfigProvider Config { get; }

    public FakeSecurityEventSource Source { get; } = new();

    public FakeSecurityEventStore SecurityStore { get; } = new();

    public FakeEventStore Events { get; } = new();

    /// <summary>GPO content snapshots (stage B design 8.7: the GPC version a content event was read for).</summary>
    public AdMng.Watchdog.Testing.Stores.InMemoryWatchdogStore GpoContent => _gpoContent ??= new AdMng.Watchdog.Testing.Stores.InMemoryWatchdogStore(Time);

    private AdMng.Watchdog.Testing.Stores.InMemoryWatchdogStore? _gpoContent;

    public FakeNotificationQueue Queue { get; } = new();

    public FakeDetectionStore Detection { get; } = new();

    public FakeDomainControllers DomainControllers { get; } = new(Records.Dc1, Records.Dc2);

    public FakeReverseDns Dns { get; } = new();

    public FakeHealthRegistry Health { get; } = new();

    public FakeSignals Signals { get; } = new();

    public FixedTokenSource Tokens { get; } = new(TestEvents.Tokens);

    public RecordingFollowUpPlanner FollowUps { get; } = new();

    public DcLogHealthTracker Tracker { get; }

    public SourceQueryGate Gate { get; }

    public ActorResolver Actors { get; }

    public PendingAttributionProcessor Processor { get; }

    public AttributionEngine Engine { get; }

    public void Dispose() => Gate.Dispose();
}
