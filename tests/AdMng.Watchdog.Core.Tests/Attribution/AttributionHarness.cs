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
    public AttributionHarness(WatchdogConfiguration? configuration = null)
    {
        Config = new FakeConfigProvider(configuration ?? new WatchdogConfiguration());
        Tracker = new DcLogHealthTracker(Time);
        Gate = new SourceQueryGate(Tracker, Time);
        Actors = new ActorResolver(Detection, new FakeSyncAccounts(TestEvents.SyncSid));
        Processor = new PendingAttributionProcessor(
            new AttributionCorrelator(SecurityStore),
            Actors,
            new LogonSourceResolver(Source, Gate, Dns, Time),
            new LockoutSourceResolver(SecurityStore, Source, Gate, DomainControllers, new FixedLockoutPolicy(TimeSpan.FromMinutes(30))),
            new UnresolvedReasonResolver(SecurityStore, Tracker, Time),
            new PrivilegeAttributionInheritor(Events),
            Tracker,
            Time);
        var gaps = new EventLogGapDetector(Time);
        Engine = new AttributionEngine(
            Config,
            Source,
            Events,
            Queue,
            DomainControllers,
            new AttributionSourceResolver(DomainControllers, Health, Time),
            new SecurityEventReductionFactory(new FakeSessionProvider(new AdMng.Testing.FakeDirectorySession()), Tokens, new AdMng.Watchdog.Core.Detection.WatchedAttributeCatalog(Config)),
            new SecurityLogIngestor(Source, SecurityStore, new SecurityEventParser(), gaps, new EventLogOnlyDetector(Actors, Time), Tracker, Health, Time,
                NullLogger<SecurityLogIngestor>.Instance),
            Processor,
            Tracker,
            new AttributionDegradedMonitor(Tracker, Time),
            new ActorConditionHealthMonitor(Time),
            Tokens,
            Health,
            Signals,
            [FollowUps],
            Time,
            NullLogger<AttributionEngine>.Instance);
    }

    public FakeTimeProvider Time { get; } = new(TestEvents.Now);

    public FakeConfigProvider Config { get; }

    public FakeSecurityEventSource Source { get; } = new();

    public FakeSecurityEventStore SecurityStore { get; } = new();

    public FakeEventStore Events { get; } = new();

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
