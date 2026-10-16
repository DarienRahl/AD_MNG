using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Diagnostics;
using AdMng.Watchdog.Core.Model;
using Microsoft.Extensions.Logging;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// IWatchdogJob: one detection cycle (section 10.1). Reads the DC state, selects the mode (Baseline, Incremental,
/// Failback, Resync, Reconciliation, NoChange), runs the full scan or the incremental passes (containers first, then the
/// changed objects in pages, the configuration/PKI/schema partitions and the Show Deleted pass), emits settled creations,
/// re-evaluates privileged access when needed and moves the watermark in the final commit only. Pages are committed as
/// chunks without DC state, so a crash repeats the cycle without duplicating events.
/// </summary>
public sealed class ChangeDetectionEngine : IWatchdogJob
{
    public const string JobName = "detection";
    public const string HealthConnection = "directory.connection";
    public const string HealthNotSynchronized = "directory.notSynchronized";
    public const string HealthComputersNotRedirected = "directory.computersNotRedirected";
    public const string HealthSearchPermission = "permission.search";
    public const int PageSize = 1000;

    private static readonly TimeSpan PollPersistInterval = TimeSpan.FromMinutes(5);

    private readonly IDirectorySessionProvider _sessions;
    private readonly IWatchdogConfigProvider _configuration;
    private readonly IHealthRegistry _health;
    private readonly IWatchdogSignals _signals;
    private readonly ILogger<ChangeDetectionEngine> _logger;
    private readonly DetectionServices _services;
    private readonly DcStateReader _dcStateReader;
    private readonly DetectionModeSelector _modeSelector = new();
    private readonly ChangeNotificationWaker _waker;
    private readonly CreationSettler _settler;
    private readonly DeletedObjectsScanner _deletions;
    private readonly Reconciler _reconciler;
    private readonly ConfigurationPartitionWatcher _configurationPartition;
    private readonly PrivilegeEvaluator _privileges;
    private readonly SemaphoreSlim _cycleGate = new(1, 1);
    private bool _firstCycle = true;
    private DateTimeOffset? _lastPersistedPoll;
    private DateTimeOffset? _failingSince;
    private bool _unavailableReported;
    private bool _permissionGapReported;
    private bool _redirectHintReported;

    internal ChangeDetectionEngine(
        IDirectorySessionProvider sessions,
        IWatchdogConfigProvider configuration,
        IHealthRegistry health,
        IWatchdogSignals signals,
        ILogger<ChangeDetectionEngine> logger,
        DetectionServices services,
        DcStateReader dcStateReader,
        ChangeNotificationWaker waker)
    {
        _sessions = sessions;
        _configuration = configuration;
        _health = health;
        _signals = signals;
        _logger = logger;
        _services = services;
        _dcStateReader = dcStateReader;
        _waker = waker;
        _settler = new CreationSettler(services);
        _deletions = new DeletedObjectsScanner(services);
        _reconciler = new Reconciler(services, _deletions);
        _configurationPartition = new ConfigurationPartitionWatcher(services);
        _privileges = new PrivilegeEvaluator(services);
    }

    public string Name => JobName;

    /// <summary>Default poll interval; each run returns the configured Directory.PollInterval.</summary>
    public TimeSpan DefaultInterval => TimeSpan.FromSeconds(15);

    public IReadOnlyList<WatchdogSignal> WakeSignals { get; } = [WatchdogSignal.PollRequested, WatchdogSignal.ConfigChanged];

    /// <summary>The result of the last completed cycle (diagnostics).</summary>
    public DetectionCycleResult? LastResult { get; private set; }

    public async Task<JobResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var interval = _configuration.Current.Configuration.Directory.PollInterval;
        try
        {
            await RunCycleAsync(cancellationToken).ConfigureAwait(false);
            return new JobResult(interval);
        }
        catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.AccessDenied)
        {
            SafeExceptionLog.LogWarning(_logger, ex, "detection cycle");
            await ReportPermissionGapAsync(cancellationToken).ConfigureAwait(false);
            return new JobResult(interval, Failed: true, FailureCode: "permission");
        }
        catch (DirectoryException ex)
        {
            SafeExceptionLog.LogWarning(_logger, ex, "detection cycle");
            await ReportFailureAsync(ex, cancellationToken).ConfigureAwait(false);
            return new JobResult(interval, Failed: true, FailureCode: "directory");
        }
    }

    /// <summary>
    /// Runs one cycle. Directory errors propagate (transient ones are reported to the session provider first) –
    /// <see cref="RunOnceAsync"/> turns them into a failed job result and health.
    /// </summary>
    public async Task<DetectionCycleResult> RunCycleAsync(CancellationToken cancellationToken)
    {
        await _cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await _sessions.GetSessionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await RunCycleCoreAsync(session, cancellationToken).ConfigureAwait(false);
                _failingSince = null;
                _unavailableReported = false;
                _permissionGapReported = false;
                LastResult = result;
                return result;
            }
            catch (DirectoryException ex) when (ex.IsTransient)
            {
                _sessions.ReportFailure(session, ex);
                throw;
            }
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    private async Task<DetectionCycleResult> RunCycleCoreAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        var started = _services.Time.GetTimestamp();
        var now = _services.Time.GetUtcNow();
        var configuration = _configuration.Current.Configuration;
        var dc = await _dcStateReader.ReadAsync(session, cancellationToken).ConfigureAwait(false);
        var settings = new DetectionSettings(configuration, dc.Facts, session.Schema, _services.HostOptions);
        var state = await _services.Store.GetDcStateAsync(dc.Facts.DomainDnsName, cancellationToken).ConfigureAwait(false);
        var known = state is null || DistinguishedName.AreEqual(state.DsServiceName, dc.DsServiceName)
            ? null
            : await _services.Store.GetWatermarkAsync(dc.DsServiceName, dc.InvocationId, cancellationToken).ConfigureAwait(false);
        var decision = _modeSelector.Select(state, known, dc, now, settings.TimeZone, configuration.Directory.FullReconciliationTime);
        await EnsureWakeUpAsync(session, settings, cancellationToken).ConfigureAwait(false);
        ReportHints(dc, now);
        if (decision.Skip)
        {
            Report(HealthNotSynchronized, HealthState.Warning, now, "The domain controller reports isSynchronized = FALSE.", dc.DnsHostName);
            await ReportDirectoryAsync(state, dc, HealthState.Warning, cancellationToken).ConfigureAwait(false);
            return new DetectionCycleResult { Mode = DetectionMode.NoChange, HighestUsn = state?.HighestUsn ?? 0, Warnings = [DetectionModeSelector.ReasonNotSynchronized] };
        }

        Report(HealthNotSynchronized, HealthState.Ok, now, null);
        var cycle = new DetectionCycle(session, settings, dc, state, decision, now);
        cycle.UsePrivileges(await _services.Store.GetPrivilegeStateAsync(cancellationToken).ConfigureAwait(false));
        var processor = new ObjectProcessor(_services, cycle);
        switch (decision.Mode)
        {
            case DetectionMode.Baseline or DetectionMode.Resync or DetectionMode.Reconciliation:
                await FullScanAsync(processor, cycle, cancellationToken).ConfigureAwait(false);
                break;
            case DetectionMode.Incremental or DetectionMode.Failback:
                await IncrementalAsync(processor, cycle, cancellationToken).ConfigureAwait(false);
                break;
        }

        if (cycle.IsFullScan || !_services.SyncAccounts.HasRun)
        {
            await RefreshSyncAccountsAsync(session, cancellationToken).ConfigureAwait(false);
        }

        await _settler.SettleDueAsync(processor, cycle, cancellationToken).ConfigureAwait(false);
        await cycle.CommitChunkAsync(_services.Store, cancellationToken).ConfigureAwait(false);
        var privileges = await EvaluatePrivilegesAsync(processor, cycle, cancellationToken).ConfigureAwait(false);
        AddFinalHealthEvents(processor, cycle, decision);
        var newState = NewState(cycle, state, dc, now);
        var final = cycle.Chunk.ToCommit(cycle.PendingRewrites) with
        {
            NewState = newState,
            Privileges = privileges,
            IsBaseline = decision.Mode == DetectionMode.Baseline,
        };
        if (newState is not null || privileges is not null || !cycle.Chunk.IsEmpty || cycle.PendingRewrites.Count > 0)
        {
            cycle.StoredEvents.AddRange(await _services.Store.CommitAsync(final, cancellationToken).ConfigureAwait(false));
            cycle.MarkRewritesCommitted();
        }

        if (newState is not null)
        {
            _lastPersistedPoll = newState.LastPollAt;
        }

        _firstCycle = false;
        if (cycle.StoredEvents.Count > 0)
        {
            _signals.Raise(WatchdogSignal.EventsStored);
        }

        Report(HealthConnection, HealthState.Ok, now, null, dc.DnsHostName);
        await ReportDirectoryAsync(newState ?? state, dc, HealthState.Ok, cancellationToken).ConfigureAwait(false);
        return new DetectionCycleResult
        {
            Mode = decision.Mode,
            ObjectsExamined = cycle.ObjectsExamined,
            EventsCreated = cycle.StoredEvents.Count,
            HighestUsn = newState?.HighestUsn ?? state?.HighestUsn ?? dc.HighestUsn,
            Duration = _services.Time.GetElapsedTime(started),
            Warnings = cycle.Warnings.Distinct(StringComparer.Ordinal).ToList(),
        };
    }

    /// <summary>Baseline / Resync / Reconciliation (section 10.4).</summary>
    private async Task FullScanAsync(ObjectProcessor processor, DetectionCycle cycle, CancellationToken cancellationToken)
    {
        var containers = await CollectAsync(cycle, DetectionFilters.ContainerClasses, cancellationToken).ConfigureAwait(false);
        await processor.ProcessPageAsync(containers, containerPass: true, cancellationToken).ConfigureAwait(false);
        await ProcessPagedAsync(processor, cycle, DetectionFilters.NonContainerScope, cancellationToken).ConfigureAwait(false);
        await _configurationPartition.RunAsync(processor, cycle, fullSync: true, cancellationToken).ConfigureAwait(false);
        if (cycle.Mode != DetectionMode.Baseline)
        {
            await _reconciler.ReconcileAsync(processor, cycle, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Incremental / Failback (section 10.5).</summary>
    private async Task IncrementalAsync(ObjectProcessor processor, DetectionCycle cycle, CancellationToken cancellationToken)
    {
        var tombstones = await _deletions.ReadAsync(cycle, cancellationToken).ConfigureAwait(false);
        var containers = await CollectAsync(cycle, DetectionFilters.ChangedContainers(cycle.SearchFrom), cancellationToken).ConfigureAwait(false);
        await processor.ProcessPageAsync(containers, containerPass: true, cancellationToken).ConfigureAwait(false);
        await ProcessPagedAsync(processor, cycle, DetectionFilters.ChangedObjects(cycle.SearchFrom), cancellationToken).ConfigureAwait(false);
        await _configurationPartition.RunAsync(processor, cycle, fullSync: false, cancellationToken).ConfigureAwait(false);
        await _deletions.ProcessAsync(processor, cycle, tombstones, cancellationToken).ConfigureAwait(false);
        await cycle.CommitChunkAsync(_services.Store, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<List<DirectoryObject>> CollectAsync(DetectionCycle cycle, string filter, CancellationToken cancellationToken)
    {
        var result = new List<DirectoryObject>();
        await foreach (var entry in cycle.Session.SearchAsync(Search(cycle, filter), cancellationToken).ConfigureAwait(false))
        {
            result.Add(entry);
        }

        return result;
    }

    private static async Task ProcessPagedAsync(ObjectProcessor processor, DetectionCycle cycle, string filter, CancellationToken cancellationToken)
    {
        var page = new List<DirectoryObject>(PageSize);
        await foreach (var entry in cycle.Session.SearchAsync(Search(cycle, filter), cancellationToken).ConfigureAwait(false))
        {
            page.Add(entry);
            if (page.Count == PageSize)
            {
                await processor.ProcessPageAsync(page, containerPass: false, cancellationToken).ConfigureAwait(false);
                page = new List<DirectoryObject>(PageSize);
            }
        }

        if (page.Count > 0)
        {
            await processor.ProcessPageAsync(page, containerPass: false, cancellationToken).ConfigureAwait(false);
        }
    }

    private static DirectorySearch Search(DetectionCycle cycle, string filter) => new()
    {
        BaseDn = cycle.Settings.Facts.DomainDn,
        Filter = filter,
        Attributes = cycle.Settings.RequestAttributes,
        ExtendedDn = true,
        PageSize = PageSize,
    };

    /// <summary>PrivilegeEvaluator triggers (10.8): relevant changes, full scans/failback, first cycle after start, crash safety.</summary>
    private async Task<PrivilegeState?> EvaluatePrivilegesAsync(ObjectProcessor processor, DetectionCycle cycle, CancellationToken cancellationToken)
    {
        var stored = cycle.StoredPrivileges;
        var crashed = stored is not null && cycle.State?.MembershipsChangedAt is { } changedAt && stored.ComputedAt < changedAt;
        var evaluate = cycle.PrivilegeRelevant || cycle.MembershipRelevant || _firstCycle || stored is null || crashed
            || cycle.Mode is DetectionMode.Baseline or DetectionMode.Resync or DetectionMode.Failback or DetectionMode.Reconciliation;
        if (!evaluate)
        {
            return null;
        }

        var evaluation = await _privileges.EvaluateAsync(cycle, emit: cycle.Mode != DetectionMode.Baseline, cancellationToken).ConfigureAwait(false);
        foreach (var draft in evaluation.Drafts)
        {
            processor.Emit([draft], null, draft.Object!, ReplicationMetadata.Empty, null);
        }

        return evaluation.State;
    }

    private void AddFinalHealthEvents(ObjectProcessor processor, DetectionCycle cycle, ModeDecision decision)
    {
        var watchdog = new ObjectRef { Dn = string.Empty, Kind = WatchObjectKind.Watchdog, Name = cycle.Dc.DnsHostName };
        if (decision.Mode == DetectionMode.Resync)
        {
            var changes = new List<AttributeChange>
            {
                processor.Changes.Text("reason", [decision.Reason ?? string.Empty]),
                processor.Changes.Text("dc", [cycle.Dc.DnsHostName], cycle.State is null ? null : [cycle.State.DcHostName]),
            };
            AddHealthEvent(processor, cycle, WatchEventKind.HealthResync, watchdog, changes);
        }

        if (decision.Mode == DetectionMode.Baseline)
        {
            var count = cycle.ObjectsExamined.ToString(CultureInfo.InvariantCulture);
            AddHealthEvent(processor, cycle, WatchEventKind.HealthBaselineCompleted, watchdog, [processor.Changes.Text("objects", [count])]);
        }
    }

    private void AddHealthEvent(ObjectProcessor processor, DetectionCycle cycle, WatchEventKind kind, ObjectRef watchdog, IReadOnlyList<AttributeChange> changes)
    {
        var draft = new EventDraft { Kind = kind, Changes = changes, Attribution = AdMng.Watchdog.Contracts.Events.Attribution.NotApplicable };
        var context = new EventContext { Source = DetectionSource.Health, Domain = cycle.Settings.Facts.DomainDnsName, Severity = cycle.Settings.Severity };
        cycle.Chunk.Add(processor.Factory.Create(draft, watchdog, context, [], processor.Factory.NewChangeSetId()));
    }

    private DcState? NewState(DetectionCycle cycle, DcState? state, DcSnapshot dc, DateTimeOffset now)
    {
        var changed = cycle.StoredEvents.Count > 0 || !cycle.Chunk.IsEmpty;
        var lastChange = changed ? now : state?.LastChangeAt;
        DcState Identity(DcState? previous) => new()
        {
            Domain = dc.Facts.DomainDnsName,
            DcHostName = dc.DnsHostName,
            DsServiceName = dc.DsServiceName,
            InvocationId = dc.InvocationId,
            HighestUsn = dc.HighestUsn,
            LastPollAt = now,
            LastChangeAt = lastChange,
            LastFullSyncAt = previous?.LastFullSyncAt,
            BaselineCompletedAt = previous?.BaselineCompletedAt,
            MembershipsChangedAt = previous?.MembershipsChangedAt,
        };

        switch (cycle.Mode)
        {
            case DetectionMode.Baseline:
                return Identity(state) with { LastFullSyncAt = now, BaselineCompletedAt = now };
            case DetectionMode.Resync:
                return Identity(state) with { LastFullSyncAt = now };
            case DetectionMode.Failback:
                return Identity(state);
            case DetectionMode.Reconciliation:
                return state! with { HighestUsn = dc.HighestUsn, LastPollAt = now, LastChangeAt = lastChange, LastFullSyncAt = now, DcHostName = dc.DnsHostName };
            case DetectionMode.Incremental:
                return state! with { HighestUsn = dc.HighestUsn, LastPollAt = now, LastChangeAt = lastChange, DcHostName = dc.DnsHostName };
            default:
                var lastPersisted = _lastPersistedPoll ?? state?.LastPollAt;
                return state is not null && (lastPersisted is null || now - lastPersisted >= PollPersistInterval)
                    ? state with { LastPollAt = now }
                    : null;
        }
    }

    private async Task RefreshSyncAccountsAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        try
        {
            await _services.SyncAccounts.RefreshAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryException ex) when (!ex.IsTransient)
        {
            SafeExceptionLog.LogWarning(_logger, ex, "sync account detection");
        }
    }

    private async Task EnsureWakeUpAsync(IDirectorySession session, DetectionSettings settings, CancellationToken cancellationToken)
    {
        var facts = settings.Facts;
        var containers = settings.Directory.WakeUpContainers
            .Select(c => DetectionTokens.Expand(c, facts.DomainDn, facts.DomainSid, facts.RootDomainSid))
            .ToList();
        await _waker.EnsureAsync(session, containers, cancellationToken).ConfigureAwait(false);
    }

    private void ReportHints(DcSnapshot dc, DateTimeOffset now)
    {
        if (_redirectHintReported)
        {
            return;
        }

        _redirectHintReported = true;
        if (!dc.Containers.ComputersRedirected)
        {
            Report(HealthComputersNotRedirected, HealthState.Ok, now,
                "New computers are created in the default CN=Computers container (redircmp not used).");
        }
    }

    private async Task ReportFailureAsync(DirectoryException error, CancellationToken cancellationToken)
    {
        var now = _services.Time.GetUtcNow();
        _failingSince ??= now;
        Report(HealthConnection, HealthState.Error, now, "The domain controller is not reachable.", error.Kind.ToString());
        // The status keeps the last known DC state (baseline time, USN) and shows the directory as failing.
        _health.ReportDirectory(_health.Directory with { State = HealthState.Error });
        var failoverAfter = _configuration.Current.Configuration.Directory.FailoverAfter;
        if (!_unavailableReported && now - _failingSince >= failoverAfter)
        {
            _unavailableReported = true;
            await AppendHealthEventAsync(WatchEventKind.HealthDirectoryUnavailable, error.Kind.ToString(), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReportPermissionGapAsync(CancellationToken cancellationToken)
    {
        Report(HealthSearchPermission, HealthState.Error, _services.Time.GetUtcNow(), "The change search was denied; the watermark was not moved.");
        _health.ReportDirectory(_health.Directory with { State = HealthState.Error });
        if (!_permissionGapReported)
        {
            _permissionGapReported = true;
            await AppendHealthEventAsync(WatchEventKind.HealthPermissionGap, HealthSearchPermission, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task AppendHealthEventAsync(WatchEventKind kind, string code, CancellationToken cancellationToken)
    {
        var now = _services.Time.GetUtcNow();
        var watchEvent = new WatchEvent
        {
            EventId = Guid.CreateVersion7(now),
            ChangeSetId = Guid.CreateVersion7(now),
            Kind = kind,
            Severity = EventKindCatalog.Get(kind).DefaultSeverity,
            Object = new ObjectRef { Dn = string.Empty, Kind = WatchObjectKind.Watchdog, Name = _sessions.CurrentServer },
            Changes = [new AttributeChange { Attribute = "code", NewValues = [code], NewCount = 1 }],
            DetectedAt = now,
            Source = DetectionSource.Health,
            Attribution = AdMng.Watchdog.Contracts.Events.Attribution.NotApplicable,
        };
        try
        {
            await _services.Events.AppendAsync(watchEvent, cancellationToken).ConfigureAwait(false);
            _signals.Raise(WatchdogSignal.EventsStored);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SafeExceptionLog.LogError(_logger, ex, "health event append");
        }
    }

    private void Report(string code, HealthState state, DateTimeOffset now, string? detail, params string[] args) =>
        _health.Report(new HealthCheckDto { Code = code, State = state, Detail = detail, Args = args, CheckedAt = now });

    private async Task ReportDirectoryAsync(DcState? state, DcSnapshot dc, HealthState health, CancellationToken cancellationToken)
    {
        var objects = await _services.Store.CountLiveObjectsAsync(cancellationToken).ConfigureAwait(false);
        _health.ReportDirectory(new DirectoryStatusDto
        {
            State = health,
            DcHostName = dc.DnsHostName,
            IsPdc = dc.IsPdc,
            InvocationId = dc.InvocationId,
            HighestUsn = state?.HighestUsn ?? 0,
            LastPollAt = state?.LastPollAt,
            LastChangeAt = state?.LastChangeAt,
            LastFullSyncAt = state?.LastFullSyncAt,
            BaselineCompletedAt = state?.BaselineCompletedAt,
            SnapshotObjects = objects,
        });
    }
}
