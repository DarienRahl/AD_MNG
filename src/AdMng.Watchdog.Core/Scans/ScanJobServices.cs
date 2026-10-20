using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Detection;

namespace AdMng.Watchdog.Core.Scans;

/// <summary>The collaborators of <see cref="ScheduledScanJob"/> (one instance per job).</summary>
internal sealed record ScanJobServices
{
    public required IDirectorySessionProvider Sessions { get; init; }
    public required IWatchdogConfigProvider Configuration { get; init; }
    public required IDetectionStore Store { get; init; }
    public required IEventStore Events { get; init; }
    public required IExpiryNoticeStore Notices { get; init; }
    public required IWatchdogSignals Signals { get; init; }
    public required TimeProvider Time { get; init; }
    public required DcStateReader DcStateReader { get; init; }
    public required SeverityResolver Severity { get; init; }
    public required LockoutPolicyResolver LockoutPolicies { get; init; }
    public required ExclusionSuggestionRegistry Suggestions { get; init; }
    public required DomainControllerDirectory DomainControllers { get; init; }
    public required IBusinessDayCalendar Calendar { get; init; }
    public required PermissionSelfCheck SelfCheck { get; init; }
    public DetectionHostOptions HostOptions { get; init; } = new();

    /// <summary>The service's own account (tag watchdogAccount on scan events, design 9.3).</summary>
    public IWatchdogAccountSource WatchdogAccount { get; init; } = NoWatchdogAccount.Instance;

    /// <summary>Null when the host registers no Security-log reader (password-spray detection unavailable).</summary>
    public ISecurityEventSource? SecurityEvents { get; init; }

    /// <summary>Collector mode (stage B design 7.8): the stored failure records the password-spray detector reads instead
    /// of querying the PDC's log.</summary>
    public ISecurityEventLookup? SecurityEventLookup { get; init; }
}
