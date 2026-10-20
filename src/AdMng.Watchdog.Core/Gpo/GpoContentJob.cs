using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Core.Diagnostics;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Notifications;
using Microsoft.Extensions.Logging;

namespace AdMng.Watchdog.Core.Gpo;

/// <summary>
/// GPO content tracking (stage B design 8.1–8.8), job <c>gpo-content</c>:
/// <list type="number">
/// <item>Triggers: stored GroupPolicyChanged events with a <c>versionNumber</c> or <c>gPCFileSysPath</c> change (creation
/// included), ObjectRestored of a GPO → the read queue (idempotent by GUID); ObjectDeleted of a GPO marks its snapshot
/// deleted.</item>
/// <item>Baseline: once the detection baseline exists, every GPO without a snapshot is read silently.</item>
/// <item>Queue: one GPO at a time, at most <see cref="MaxReadsPerRun"/> per run. <c>gPCFileSysPath</c> is validated first
/// (8.2) – a redirected path is reported (GroupPolicyPathRedirected, Critical) and nothing is read, from any host; the
/// content is read only from <c>\\&lt;session DC&gt;\SYSVOL\&lt;domain&gt;\Policies\{GUID}</c>. SYSVOL lag is retried every
/// 2 minutes up to <c>GpoContent.SysvolLagTolerance</c>. The attempt counter is stored before a read, so a GPO that keeps
/// failing – or crashes the process – is parked after <see cref="MaxAttempts"/> attempts (health
/// <c>gpo.&lt;guid&gt;.parseError</c>) until its next change.</item>
/// <item>Daily reconciliation: the listing of every read GPO is compared with the stored inventory; a difference without a
/// <c>versionNumber</c> change is read and reported as GroupPolicyContentOutOfBand when the settings differ.</item>
/// </list>
/// Health: <c>gpo.&lt;guid&gt;.tooLarge|parseError|sysvolLag</c>, <c>sysvol.accessDenied</c> (+ one HealthPermissionGap event),
/// <see cref="ConnectionHealthCode"/>, and the Status section „Zawartość GPO” (<see cref="GpoContentStatusDto"/>).
/// </summary>
public sealed partial class GpoContentJob : IWatchdogJob
{
    public const string JobName = "gpo-content";

    /// <summary>Started reads before a GPO is parked (poison-input guard).</summary>
    public const int MaxAttempts = 3;

    /// <summary>GPO reads per run (the baseline throttle of 8.1: one GPO at a time, at most 20 per cycle).</summary>
    public const int MaxReadsPerRun = 20;

    /// <summary>Reconciliation listings per run (the rest follows in the next runs).</summary>
    public const int MaxListingsPerRun = 25;

    /// <summary>Health check of SYSVOL reading as a whole (Kerberos, DC unreachable): GUI prefix <c>sysvol</c>.</summary>
    public const string ConnectionHealthCode = "sysvol.connection";

    public static readonly TimeSpan ReconciliationInterval = TimeSpan.FromDays(1);

    /// <summary>Delay after a failed attempt (index = attempts − 1).</summary>
    public static readonly IReadOnlyList<TimeSpan> RetryDelays = [TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10)];

    private readonly IGpoContentStore _store;
    private readonly IEventStore _events;
    private readonly IDetectionStore _detection;
    private readonly ISysvolReader _reader;
    private readonly IDirectorySessionProvider _sessions;
    private readonly IWatchdogConfigProvider _config;
    private readonly IHealthRegistry _health;
    private readonly IWatchdogSignals _signals;
    private readonly TimeProvider _time;
    private readonly ILogger<GpoContentJob> _logger;
    private readonly HashSet<string> _reportedProblems = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _lagging = [];
    private Queue<Guid>? _reconciliation;
    private volatile bool _forceReconciliation;
    private bool _accessDeniedEventSent;
    private HealthState _connection = HealthState.Ok;

    public GpoContentJob(IGpoContentStore store, IEventStore events, IDetectionStore detection, ISysvolReader reader,
        IDirectorySessionProvider sessions, IWatchdogConfigProvider config, IHealthRegistry health, IWatchdogSignals signals,
        TimeProvider time, ILogger<GpoContentJob> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(detection);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _events = events;
        _detection = detection;
        _reader = reader;
        _sessions = sessions;
        _config = config;
        _health = health;
        _signals = signals;
        _time = time;
        _logger = logger;
    }

    public string Name => JobName;

    public TimeSpan DefaultInterval => SysvolLagPolicy.RetryInterval;

    public IReadOnlyList<WatchdogSignal> WakeSignals { get; } = [WatchdogSignal.EventsStored, WatchdogSignal.ConfigChanged];

    /// <summary>Starts a reconciliation in the next run regardless of the last one (tests, lab, operator tooling).</summary>
    public void ForceReconciliation() => _forceReconciliation = true;

    public async Task<JobResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = _config.Current.Configuration.Directory.GpoContent ?? new GpoContentSettings();
        if (!settings.Enabled)
        {
            _health.ReportGpoContent(new GpoContentStatusDto { State = HealthState.Disabled, Enabled = false });
            return new JobResult(TimeSpan.FromMinutes(5));
        }

        GpoRunContext context;
        try
        {
            var session = await _sessions.GetSessionAsync(cancellationToken).ConfigureAwait(false);
            context = new GpoRunContext(session.Info.Server, session.Info.DomainDnsName, session.Info.DefaultNamingContext, settings,
                GpoReadLimits.From(settings), _time.GetUtcNow());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SafeExceptionLog.LogWarning(_logger, ex, "gpo content session", WatchdogEventIds.GpoContentProblem);
            return new JobResult(null, Failed: true, FailureCode: "directory");
        }

        var dcState = await _detection.GetDcStateAsync(context.Domain, cancellationToken).ConfigureAwait(false);
        if (dcState?.BaselineCompletedAt is null)
        {
            // The GPCs come from the detection snapshot: wait for its baseline.
            await ReportStatusAsync(context, new Dictionary<Guid, SnapshotObject>(), cancellationToken).ConfigureAwait(false);
            return new JobResult(null);
        }

        var gpos = await LoadGposAsync(cancellationToken).ConfigureAwait(false);
        var state = await _store.GetGpoContentStateAsync(cancellationToken).ConfigureAwait(false);
        state = await ScanTriggersAsync(state, context, cancellationToken).ConfigureAwait(false);
        state = await EnqueueBaselineAsync(state, gpos, context, cancellationToken).ConfigureAwait(false);

        var outcome = await ProcessQueueAsync(gpos, context, cancellationToken).ConfigureAwait(false);
        if (outcome.Reads > 0)
        {
            state = state with { LastReadAt = context.Now };
        }

        if (!outcome.Stopped)
        {
            state = await ReconcileAsync(state, gpos, context, cancellationToken).ConfigureAwait(false);
        }

        await _store.SaveGpoContentStateAsync(state, cancellationToken).ConfigureAwait(false);
        if (outcome.Events > 0)
        {
            _signals.Raise(WatchdogSignal.EventsStored);
        }

        await ReportStatusAsync(context, gpos, cancellationToken).ConfigureAwait(false);
        if (outcome.Stopped)
        {
            return new JobResult(null, Failed: true, FailureCode: "sysvol");
        }

        // More due work (a full batch, a reconciliation in progress): run again soon.
        return outcome.Reads >= MaxReadsPerRun || _reconciliation is { Count: > 0 } ? new JobResult(TimeSpan.FromSeconds(5)) : new JobResult(null);
    }

    // ---- GPOs of the detection snapshot ------------------------------------------------------------------------------------

    /// <summary>Live GPCs of the detection snapshot by the GUID of their <c>cn</c>.</summary>
    private async Task<Dictionary<Guid, SnapshotObject>> LoadGposAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, SnapshotObject>();
        foreach (var gpc in await _detection.GetByKindAsync(WatchObjectKind.GroupPolicy, cancellationToken).ConfigureAwait(false))
        {
            if (!gpc.IsDeleted && TryGetGpoGuid(gpc.Dn, out var guid))
            {
                result[guid] = gpc;
            }
        }

        return result;
    }

    /// <summary>The GPO GUID of a GPC DN (<c>CN={GUID},CN=Policies,…</c>); also of a deleted-object DN
    /// (<c>CN={GUID}\0ADEL:…</c>).</summary>
    public static bool TryGetGpoGuid(string? dn, out Guid guid)
    {
        guid = Guid.Empty;
        if (string.IsNullOrEmpty(dn) || !DistinguishedName.TryParse(dn, out var parsed) || parsed.IsRoot)
        {
            return false;
        }

        var name = parsed.Name;
        var cut = name.IndexOfAny(['\n', '\0']);
        return GpoPathValidator.TryParseGpcCn(cut >= 0 ? name[..cut] : name, out guid);
    }

    private static string? Single(SnapshotObject gpc, string attribute) =>
        gpc.Attributes.TryGetValue(attribute, out var value) && value.Values.Count > 0 ? value.Values[0] : null;

    private static int? GpcVersion(SnapshotObject gpc) =>
        long.TryParse(Single(gpc, WatchdogAttributes.VersionNumber), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)
            ? unchecked((int)n)
            : null;

    // ---- 8.1 triggers ------------------------------------------------------------------------------------------------------

    private async Task<GpoContentJobState> ScanTriggersAsync(GpoContentJobState state, GpoRunContext context, CancellationToken cancellationToken)
    {
        if (state.EventCursor is not { } cursor)
        {
            // First run: older changes are covered by the baseline.
            return state with { EventCursor = await _events.GetLatestIdAsync(cancellationToken).ConfigureAwait(false) };
        }

        while (true)
        {
            var page = await _events.QueryAsync(new EventQuery
            {
                After = cursor,
                Limit = 500,
                Kinds = [WatchEventKind.GroupPolicyChanged, WatchEventKind.ObjectRestored, WatchEventKind.ObjectDeleted],
                ObjectKinds = [WatchObjectKind.GroupPolicy],
            }, cancellationToken).ConfigureAwait(false);
            foreach (var e in page.Items)
            {
                await TriggerAsync(e, context, cancellationToken).ConfigureAwait(false);
                cursor = Math.Max(cursor, e.Id);
            }

            if (!page.HasMore)
            {
                return state with { EventCursor = Math.Max(cursor, page.LatestId) };
            }
        }
    }

    private async Task TriggerAsync(WatchEvent e, GpoRunContext context, CancellationToken cancellationToken)
    {
        if (e.Object.Kind != WatchObjectKind.GroupPolicy)
        {
            return;
        }

        if (e.Kind == WatchEventKind.ObjectDeleted)
        {
            if (TryGetGpoGuid(e.PreviousDn, out var deleted) || TryGetGpoGuid(e.Object.Dn, out deleted))
            {
                await _store.MarkGpoContentDeletedAsync(deleted, e.DetectedAt, cancellationToken).ConfigureAwait(false);
                ClearGpoHealth(deleted, context.Now);
            }

            return;
        }

        if (!TryGetGpoGuid(e.Object.Dn, out var guid))
        {
            return;
        }

        GpoQueueReason? reason = e.Kind switch
        {
            WatchEventKind.ObjectRestored => GpoQueueReason.Restored,
            _ when Has(e, "operation") => GpoQueueReason.Created,
            _ when Has(e, WatchdogAttributes.GpcFileSysPath) => GpoQueueReason.PathChanged,
            _ when Has(e, WatchdogAttributes.VersionNumber) => GpoQueueReason.Changed,
            _ => null,
        };
        if (reason is { } r)
        {
            await _store.EnqueueGpoContentAsync(new GpoQueueEntry
            {
                GpoGuid = guid,
                ObjectGuid = e.Object.ObjectGuid,
                Reason = r,
                DueAt = context.Now,
                ChangeSetId = e.ChangeSetId,
                EnqueuedAt = context.Now,
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool Has(WatchEvent e, string attribute) =>
        e.Changes.Any(c => c.Attribute.Equals(attribute, StringComparison.OrdinalIgnoreCase));

    // ---- baseline ---------------------------------------------------------------------------------------------------------

    private async Task<GpoContentJobState> EnqueueBaselineAsync(GpoContentJobState state, Dictionary<Guid, SnapshotObject> gpos,
        GpoRunContext context, CancellationToken cancellationToken)
    {
        var stored = (await _store.ListGpoContentAsync(includeSettings: false, cancellationToken).ConfigureAwait(false))
            .Where(s => s.DeletedAt is null).Select(s => s.Snapshot.GpoGuid).ToHashSet();
        var queue = await _store.GetGpoQueueAsync(cancellationToken).ConfigureAwait(false);
        var queued = queue.Select(q => q.GpoGuid).ToHashSet();
        var parked = queue.Where(q => q.Attempts >= MaxAttempts).Select(q => q.GpoGuid).ToHashSet();
        var missing = gpos.Where(g => !stored.Contains(g.Key) && !queued.Contains(g.Key)).ToList();

        // A GPO whose path is missing or redirected is never read (8.2), so it never gets a snapshot: it does not block the
        // baseline, and it is queued only for the 511 of a hijack that exists already – once per path, not in every run.
        var unreadable = gpos.Where(g => PathCheck(g.Key, g.Value, context) != GpoPathCheck.Valid).Select(g => g.Key).ToHashSet();
        foreach (var (guid, gpc) in missing)
        {
            if (unreadable.Contains(guid)
                && (PathCheck(guid, gpc, context) == GpoPathCheck.Missing || await RedirectReportedAsync(gpc, cancellationToken).ConfigureAwait(false)))
            {
                continue;
            }

            await _store.EnqueueGpoContentAsync(new GpoQueueEntry
            {
                GpoGuid = guid,
                ObjectGuid = gpc.ObjectGuid,
                Reason = GpoQueueReason.Baseline,
                DueAt = context.Now,
                EnqueuedAt = context.Now,
            }, cancellationToken).ConfigureAwait(false);
        }

        if (state.BaselineCompletedAt is null && gpos.Keys.All(g => stored.Contains(g) || parked.Contains(g) || unreadable.Contains(g)))
        {
            // The first reconciliation follows a day after the baseline.
            return state with { BaselineCompletedAt = context.Now, LastReconciliationAt = state.LastReconciliationAt ?? context.Now };
        }

        return state;
    }

    private static GpoPathCheck PathCheck(Guid guid, SnapshotObject gpc, GpoRunContext context) =>
        GpoPathValidator.Validate(Single(gpc, WatchdogAttributes.GpcFileSysPath), context.Domain, "{" + guid.ToString("D", CultureInfo.InvariantCulture) + "}");

    /// <summary>True when the newest GroupPolicyPathRedirected of the GPO names its current path (already reported).</summary>
    private async Task<bool> RedirectReportedAsync(SnapshotObject gpc, CancellationToken cancellationToken)
    {
        var path = Single(gpc, WatchdogAttributes.GpcFileSysPath);
        var page = await _events.QueryAsync(new EventQuery
        {
            Kinds = [WatchEventKind.GroupPolicyPathRedirected],
            ObjectGuid = gpc.ObjectGuid,
            Limit = 1,
        }, cancellationToken).ConfigureAwait(false);
        return page.Items is [var last] && last.Changes.Any(c => c.NewValues.Contains(path, StringComparer.OrdinalIgnoreCase));
    }

    // ---- queue ------------------------------------------------------------------------------------------------------------

    private async Task<QueueOutcome> ProcessQueueAsync(Dictionary<Guid, SnapshotObject> gpos, GpoRunContext context, CancellationToken cancellationToken)
    {
        var queue = await _store.GetGpoQueueAsync(cancellationToken).ConfigureAwait(false);
        var outcome = new QueueOutcome();
        foreach (var entry in queue.Where(q => q.DueAt <= context.Now && q.Attempts < MaxAttempts))
        {
            if (outcome.Reads >= MaxReadsPerRun)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!gpos.TryGetValue(entry.GpoGuid, out var gpc))
            {
                // The GPC is gone (deleted, out of scope): nothing to read.
                await _store.MarkGpoContentDeletedAsync(entry.GpoGuid, context.Now, cancellationToken).ConfigureAwait(false);
                await _store.RemoveGpoQueueEntryAsync(entry.GpoGuid, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var step = await ProcessAsync(entry, gpc, context, cancellationToken).ConfigureAwait(false);
            outcome.Reads += step.Read ? 1 : 0;
            outcome.Events += step.Events;
            if (step.Stop)
            {
                outcome.Stopped = true;
                break;
            }
        }

        if (!outcome.Stopped && outcome.Reads > 0)
        {
            SetConnection(HealthState.Ok, null, context.Now);
        }

        return outcome;
    }

    private async Task<StepResult> ProcessAsync(GpoQueueEntry entry, SnapshotObject gpc, GpoRunContext context, CancellationToken cancellationToken)
    {
        // 8.2 first: a redirected path is never read, from any host.
        var path = Single(gpc, WatchdogAttributes.GpcFileSysPath);
        switch (GpoPathValidator.Validate(path, context.Domain, "{" + entry.GpoGuid.ToString("D", CultureInfo.InvariantCulture) + "}"))
        {
            case GpoPathCheck.Missing:
                await _store.RemoveGpoQueueEntryAsync(entry.GpoGuid, cancellationToken).ConfigureAwait(false);
                return default;
            case GpoPathCheck.Redirected:
                return await RedirectedAsync(entry, gpc, path!, context, cancellationToken).ConfigureAwait(false);
        }

        // Poison-input guard: the attempt is stored before anything is read.
        var attempt = entry with { Attempts = entry.Attempts + 1, FirstAttemptAt = entry.FirstAttemptAt ?? context.Now };
        await _store.UpdateGpoQueueEntryAsync(attempt, cancellationToken).ConfigureAwait(false);
        var folder = SysvolGpoFolder.Create(context.Dc, context.Domain, entry.GpoGuid);
        var stored = await _store.GetGpoContentAsync(entry.GpoGuid, cancellationToken).ConfigureAwait(false);
        var gpcVersion = GpcVersion(gpc);
        GpoReadResult result;
        try
        {
            var listing = await _reader.ListAsync(folder, cancellationToken).ConfigureAwait(false);
            var plan = GpoReadPlanner.Plan(listing, context.Limits);
            var files = plan.Read.Count == 0
                ? []
                : await _reader.ReadAsync(folder, [.. plan.Read.Select(f => f.Path)], context.Limits.MaxFileBytes, cancellationToken).ConfigureAwait(false);
            result = GpoSnapshotBuilder.Build(new GpoSnapshotInput
            {
                GpoGuid = entry.GpoGuid,
                Dc = context.Dc,
                GpcVersion = gpcVersion,
                ReadAt = context.Now,
                Plan = plan,
                Files = files,
                Previous = stored?.Snapshot,
                Limits = context.Limits,
            });
        }
        catch (SysvolReadException ex)
        {
            return await ReadFailedAsync(entry, attempt, ex, context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SafeExceptionLog.LogWarning(_logger, ex, "gpo content read", WatchdogEventIds.GpoContentProblem);
            await FailAttemptAsync(attempt, context, cancellationToken).ConfigureAwait(false);
            return new StepResult(Read: true);
        }

        _accessDeniedEventSent = false;
        Report(HealthCheckCodes.SysvolAccessDenied, HealthState.Ok, null, [], context.Now, onlyIfReported: true);

        // 8.7: wait for SYSVOL to reach the GPC version of a change (not for the baseline and the reconciliation).
        if (gpcVersion is { } version && entry.Reason is GpoQueueReason.Changed or GpoQueueReason.Created or GpoQueueReason.Restored or GpoQueueReason.PathChanged)
        {
            switch (SysvolLagPolicy.Decide(result.Snapshot.GptVersion, version, attempt.FirstAttemptAt!.Value, context.Now, context.Settings.SysvolLagTolerance))
            {
                case SysvolLagDecision.Retry:
                    _lagging.Add(entry.GpoGuid);
                    await _store.UpdateGpoQueueEntryAsync(attempt with { Attempts = entry.Attempts, DueAt = context.Now + SysvolLagPolicy.RetryInterval },
                        cancellationToken).ConfigureAwait(false);
                    return new StepResult(Read: true);
                case SysvolLagDecision.ReadWithLagWarning:
                    _lagging.Remove(entry.GpoGuid);
                    Report(SysvolLagPolicy.HealthCode(entry.GpoGuid), HealthState.Warning,
                        "GPT.INI stayed older than the GPC version for the SYSVOL lag tolerance.", [Id(entry.GpoGuid)], context.Now);
                    break;
                default:
                    _lagging.Remove(entry.GpoGuid);
                    Report(SysvolLagPolicy.HealthCode(entry.GpoGuid), HealthState.Ok, null, [Id(entry.GpoGuid)], context.Now, onlyIfReported: true);
                    break;
            }
        }

        var events = new List<WatchEvent>();
        var silent = entry.Reason == GpoQueueReason.Baseline
                     || (stored is null && entry.Reason is GpoQueueReason.Changed or GpoQueueReason.OutOfBand);
        var diff = GpoSettingsDiffer.Diff(stored?.Snapshot.Settings ?? [], result.Snapshot.Settings);
        if (!silent && !diff.IsEmpty)
        {
            var eventContext = new GpoEventContext
            {
                Gpo = gpc.ToRef(),
                Domain = context.Domain,
                DetectedAt = context.Now,
                ChangeSetId = entry.ChangeSetId,
                Source = entry.Reason == GpoQueueReason.OutOfBand ? DetectionSource.Reconciliation : DetectionSource.IncrementalPoll,
                Scope = await LinkScopeAsync(entry.GpoGuid, context, cancellationToken).ConfigureAwait(false),
            };
            events.Add(entry.Reason == GpoQueueReason.OutOfBand
                ? GpoContentEvents.ContentOutOfBand(eventContext, diff)
                : GpoContentEvents.SettingsChanged(eventContext, diff));
        }

        var committed = await _store.CommitGpoContentAsync(result.Snapshot, events, cancellationToken).ConfigureAwait(false);
        ReportReadHealth(entry.GpoGuid, result, context.Now);
        return new StepResult(Read: true, Events: committed.Count);
    }

    private async Task<StepResult> RedirectedAsync(GpoQueueEntry entry, SnapshotObject gpc, string path, GpoRunContext context,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<WatchEvent> events = [];
        if (entry.Reason is GpoQueueReason.PathChanged or GpoQueueReason.Created or GpoQueueReason.Restored or GpoQueueReason.Baseline)
        {
            events =
            [
                GpoContentEvents.PathRedirected(new GpoEventContext
                {
                    Gpo = gpc.ToRef(),
                    Domain = context.Domain,
                    DetectedAt = context.Now,
                    ChangeSetId = entry.ChangeSetId,
                    Scope = await LinkScopeAsync(entry.GpoGuid, context, cancellationToken).ConfigureAwait(false),
                }, path),
            ];
        }

        Log.PathRedirected(_logger, entry.GpoGuid);
        var committed = await _store.CompleteGpoQueueEntryAsync(entry.GpoGuid, events, cancellationToken).ConfigureAwait(false);
        return new StepResult(Events: committed.Count);
    }

    private async Task<StepResult> ReadFailedAsync(GpoQueueEntry entry, GpoQueueEntry attempt, SysvolReadException ex, GpoRunContext context,
        CancellationToken cancellationToken)
    {
        Log.ReadFailed(_logger, entry.GpoGuid, ex.Kind.ToString(), ex.NtStatus ?? "-");
        switch (ex.Kind)
        {
            case SysvolErrorKind.Authentication or SysvolErrorKind.Unreachable:
                // Not this GPO's fault: the attempt does not count, the queue waits for the next run.
                await _store.UpdateGpoQueueEntryAsync(attempt with { Attempts = entry.Attempts }, cancellationToken).ConfigureAwait(false);
                SetConnection(HealthState.Error, ex.Kind == SysvolErrorKind.Authentication
                    ? "smbclient could not authenticate with Kerberos (TGT of the service account, clock, SPN of the DC)."
                    : "The SYSVOL share of the DC is not reachable.", context.Now);
                return new StepResult(Stop: true);
            case SysvolErrorKind.AccessDenied:
                Report(HealthCheckCodes.SysvolAccessDenied, HealthState.Error, "A GPO folder is not readable for the service account.", [Id(entry.GpoGuid)], context.Now);
                if (!_accessDeniedEventSent)
                {
                    _accessDeniedEventSent = true;
                    await _events.AppendAsync(HealthEvents.Create(WatchEventKind.HealthPermissionGap, context.Now,
                        ("code", HealthCheckCodes.SysvolAccessDenied)), cancellationToken).ConfigureAwait(false);
                    _signals.Raise(WatchdogSignal.EventsStored);
                }

                break;
            case SysvolErrorKind.NotFound when entry.Reason is not GpoQueueReason.Baseline and not GpoQueueReason.OutOfBand
                                              && context.Now - attempt.FirstAttemptAt!.Value < context.Settings.SysvolLagTolerance:
                // A new GPO whose folder has not replicated yet: SYSVOL lag (8.7), not a failure.
                _lagging.Add(entry.GpoGuid);
                await _store.UpdateGpoQueueEntryAsync(attempt with { Attempts = entry.Attempts, DueAt = context.Now + SysvolLagPolicy.RetryInterval },
                    cancellationToken).ConfigureAwait(false);
                return new StepResult(Read: true);
        }

        await FailAttemptAsync(attempt, context, cancellationToken).ConfigureAwait(false);
        return new StepResult(Read: true);
    }

    /// <summary>A failed attempt: retried after <see cref="RetryDelays"/>, parked after <see cref="MaxAttempts"/>.</summary>
    private async Task FailAttemptAsync(GpoQueueEntry attempt, GpoRunContext context, CancellationToken cancellationToken)
    {
        _lagging.Remove(attempt.GpoGuid);
        if (attempt.Attempts >= MaxAttempts)
        {
            Report(HealthCheckCodes.Gpo(attempt.GpoGuid, HealthCheckCodes.GpoParseError), HealthState.Warning,
                "The GPO was skipped after 3 failed read attempts; it is read again after its next change.", [Id(attempt.GpoGuid), "attempts"], context.Now);
            Log.Parked(_logger, attempt.GpoGuid, attempt.Attempts);
            await _store.UpdateGpoQueueEntryAsync(attempt with { DueAt = DateTimeOffset.MaxValue }, cancellationToken).ConfigureAwait(false);
            return;
        }

        var delay = RetryDelays[Math.Clamp(attempt.Attempts - 1, 0, RetryDelays.Count - 1)];
        await _store.UpdateGpoQueueEntryAsync(attempt with { DueAt = context.Now + delay }, cancellationToken).ConfigureAwait(false);
    }

    private void ReportReadHealth(Guid gpoGuid, GpoReadResult result, DateTimeOffset now)
    {
        var id = Id(gpoGuid);
        if (result.TooLarge)
        {
            Report(HealthCheckCodes.Gpo(gpoGuid, HealthCheckCodes.GpoTooLarge), HealthState.Warning,
                "A read limit of Directory.GpoContent was exceeded; the inventory is kept.",
                [id, .. result.Problems.Where(p => p != GpoReadProblems.InvalidPath)], now);
        }
        else
        {
            Report(HealthCheckCodes.Gpo(gpoGuid, HealthCheckCodes.GpoTooLarge), HealthState.Ok, null, [id], now, onlyIfReported: true);
        }

        if (result.HasParseErrors)
        {
            Report(HealthCheckCodes.Gpo(gpoGuid, HealthCheckCodes.GpoParseError), HealthState.Warning,
                "A file of the GPO could not be read or parsed; its previous settings are kept.",
                [id, result.UnreadableFiles.Count.ToString(CultureInfo.InvariantCulture)], now);
        }
        else
        {
            Report(HealthCheckCodes.Gpo(gpoGuid, HealthCheckCodes.GpoParseError), HealthState.Ok, null, [id], now, onlyIfReported: true);
        }
    }

    private void ClearGpoHealth(Guid gpoGuid, DateTimeOffset now)
    {
        _lagging.Remove(gpoGuid);
        foreach (var check in new[] { HealthCheckCodes.GpoTooLarge, HealthCheckCodes.GpoParseError, HealthCheckCodes.GpoSysvolLag })
        {
            Report(HealthCheckCodes.Gpo(gpoGuid, check), HealthState.Ok, null, [Id(gpoGuid)], now, onlyIfReported: true);
        }
    }

    private async Task<GpoLinkScope> LinkScopeAsync(Guid gpoGuid, GpoRunContext context, CancellationToken cancellationToken)
    {
        if (context.Links is null)
        {
            var containers = new List<(string, string?)>();
            foreach (var kind in new[] { WatchObjectKind.Domain, WatchObjectKind.OrganizationalUnit })
            {
                foreach (var container in await _detection.GetByKindAsync(kind, cancellationToken).ConfigureAwait(false))
                {
                    if (!container.IsDeleted && Single(container, Attr.GpLink) is { Length: > 0 } gpLink)
                    {
                        containers.Add((container.Dn, gpLink));
                    }
                }
            }

            context.Links = containers;
        }

        return GpoLinkScope.From(gpoGuid, context.DomainDn, context.Links);
    }

    // ---- 8.1 daily reconciliation ----------------------------------------------------------------------------------------

    private async Task<GpoContentJobState> ReconcileAsync(GpoContentJobState state, Dictionary<Guid, SnapshotObject> gpos, GpoRunContext context,
        CancellationToken cancellationToken)
    {
        if (_reconciliation is null)
        {
            var due = _forceReconciliation
                      || (state.BaselineCompletedAt is not null && context.Now - (state.LastReconciliationAt ?? state.BaselineCompletedAt.Value) >= ReconciliationInterval);
            if (!due)
            {
                return state;
            }

            _forceReconciliation = false;
            var stored = await _store.ListGpoContentAsync(includeSettings: false, cancellationToken).ConfigureAwait(false);
            _reconciliation = new Queue<Guid>(stored.Where(s => s.DeletedAt is null).Select(s => s.Snapshot.GpoGuid));
        }

        var queued = (await _store.GetGpoQueueAsync(cancellationToken).ConfigureAwait(false)).Select(q => q.GpoGuid).ToHashSet();
        var listings = 0;
        while (listings < MaxListingsPerRun && _reconciliation.TryDequeue(out var guid))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (queued.Contains(guid))
            {
                continue; // read anyway
            }

            if (!gpos.TryGetValue(guid, out var gpc))
            {
                await _store.MarkGpoContentDeletedAsync(guid, context.Now, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (GpoPathValidator.Validate(Single(gpc, WatchdogAttributes.GpcFileSysPath), context.Domain,
                    "{" + guid.ToString("D", CultureInfo.InvariantCulture) + "}") != GpoPathCheck.Valid
                || await _store.GetGpoContentAsync(guid, cancellationToken).ConfigureAwait(false) is not { DeletedAt: null } stored)
            {
                continue; // a redirected path is never listed (8.2); reported when it changed
            }

            listings++;
            IReadOnlyList<GpoInventoryEntry> inventory;
            try
            {
                var listing = await _reader.ListAsync(SysvolGpoFolder.Create(context.Dc, context.Domain, guid), cancellationToken).ConfigureAwait(false);
                inventory = GpoReadPlanner.Inventory(listing, context.Limits);
            }
            catch (SysvolReadException ex)
            {
                Log.ReadFailed(_logger, guid, ex.Kind.ToString(), ex.NtStatus ?? "-");
                if (ex.Kind is SysvolErrorKind.Authentication or SysvolErrorKind.Unreachable)
                {
                    SetConnection(HealthState.Error, "SYSVOL could not be listed for the reconciliation.", context.Now);
                    _reconciliation.Enqueue(guid);
                    return state;
                }

                continue;
            }

            if (!GpoInventoryComparer.Differs(stored.Snapshot.Inventory, inventory))
            {
                continue;
            }

            // A listing difference: without a versionNumber change it is an out-of-band SYSVOL edit, with one a missed trigger.
            var outOfBand = GpcVersion(gpc) == stored.Snapshot.GpcVersion;
            await _store.EnqueueGpoContentAsync(new GpoQueueEntry
            {
                GpoGuid = guid,
                ObjectGuid = gpc.ObjectGuid,
                Reason = outOfBand ? GpoQueueReason.OutOfBand : GpoQueueReason.Changed,
                DueAt = context.Now,
                EnqueuedAt = context.Now,
            }, cancellationToken).ConfigureAwait(false);
        }

        if (_reconciliation.Count > 0)
        {
            return state;
        }

        _reconciliation = null;
        Log.Reconciled(_logger, gpos.Count);
        return state with { LastReconciliationAt = context.Now };
    }

    // ---- health and status ------------------------------------------------------------------------------------------------

    private void SetConnection(HealthState state, string? detail, DateTimeOffset now)
    {
        if (state == HealthState.Ok && _connection == HealthState.Ok)
        {
            return;
        }

        _connection = state;
        _health.Report(new HealthCheckDto { Code = ConnectionHealthCode, State = state, Detail = detail, CheckedAt = now });
    }

    /// <summary>Reports a check; with <paramref name="onlyIfReported"/> only when it was reported before in another state
    /// (an Ok row per healthy GPO would flood the registry).</summary>
    private void Report(string code, HealthState state, string? detail, IReadOnlyList<string> args, DateTimeOffset now, bool onlyIfReported = false)
    {
        if (state == HealthState.Ok)
        {
            if (!_reportedProblems.Remove(code) && onlyIfReported)
            {
                return;
            }
        }
        else
        {
            if (_reportedProblems.Add(code))
            {
                Log.Problem(_logger, code, state.ToString());
            }
        }

        _health.Report(new HealthCheckDto { Code = code, State = state, Detail = detail, Args = args, CheckedAt = now });
    }

    private async Task ReportStatusAsync(GpoRunContext context, Dictionary<Guid, SnapshotObject> gpos, CancellationToken cancellationToken)
    {
        var state = await _store.GetGpoContentStateAsync(cancellationToken).ConfigureAwait(false);
        var stored = await _store.ListGpoContentAsync(includeSettings: false, cancellationToken).ConfigureAwait(false);
        var queue = await _store.GetGpoQueueAsync(cancellationToken).ConfigureAwait(false);
        var live = stored.Where(s => s.DeletedAt is null && gpos.ContainsKey(s.Snapshot.GpoGuid)).ToList();
        var parked = queue.Count(q => q.Attempts >= MaxAttempts);
        var failed = parked + live.Count(s => s.Snapshot.State == GpoContentState.Partial && !queue.Any(q => q.GpoGuid == s.Snapshot.GpoGuid && q.Attempts >= MaxAttempts));
        var health = _connection != HealthState.Ok || _reportedProblems.Contains(HealthCheckCodes.SysvolAccessDenied)
            ? HealthState.Error
            : failed > 0 || _lagging.Count > 0 || _reportedProblems.Any(c => c.StartsWith(HealthCheckCodes.GpoPrefix, StringComparison.OrdinalIgnoreCase))
                ? HealthState.Warning
                : HealthState.Ok;
        _health.ReportGpoContent(new GpoContentStatusDto
        {
            State = health,
            Enabled = true,
            Dc = context.Dc,
            TotalGpos = gpos.Count,
            ReadGpos = live.Count,
            BaselineCompletedAt = state.BaselineCompletedAt,
            QueuedGpos = queue.Count - parked,
            LastReadAt = state.LastReadAt,
            LastReconciliationAt = state.LastReconciliationAt,
            FailedGpos = failed,
            SysvolLaggingGpos = _lagging.Count,
        });
    }

    private static string Id(Guid gpoGuid) => gpoGuid.ToString("B", CultureInfo.InvariantCulture).ToUpperInvariant();

    private sealed class GpoRunContext(string dc, string domain, string domainDn, GpoContentSettings settings, GpoReadLimits limits, DateTimeOffset now)
    {
        public string Dc { get; } = dc;
        public string Domain { get; } = domain;
        public string DomainDn { get; } = domainDn;
        public GpoContentSettings Settings { get; } = settings;
        public GpoReadLimits Limits { get; } = limits;
        public DateTimeOffset Now { get; } = now;
        public List<(string ContainerDn, string? GpLink)>? Links { get; set; }
    }

    private sealed class QueueOutcome
    {
        public int Reads { get; set; }
        public int Events { get; set; }
        public bool Stopped { get; set; }
    }

    private readonly record struct StepResult(bool Read = false, int Events = 0, bool Stop = false);

    private static partial class Log
    {
        [LoggerMessage(EventId = WatchdogEventIds.GpoContentProblem, Level = LogLevel.Warning, Message = "GPO content: {Code} is {State}.")]
        public static partial void Problem(ILogger logger, string code, string state);

        [LoggerMessage(EventId = WatchdogEventIds.GpoContentProblem, Level = LogLevel.Warning, Message = "GPO content: reading GPO {GpoGuid} failed ({Kind}, {NtStatus}).")]
        public static partial void ReadFailed(ILogger logger, Guid gpoGuid, string kind, string ntStatus);

        [LoggerMessage(EventId = WatchdogEventIds.GpoContentProblem, Level = LogLevel.Warning, Message = "GPO content: GPO {GpoGuid} was skipped after {Attempts} failed read attempts.")]
        public static partial void Parked(ILogger logger, Guid gpoGuid, int attempts);

        [LoggerMessage(EventId = WatchdogEventIds.GpoContentProblem, Level = LogLevel.Error, Message = "GPO content: gPCFileSysPath of GPO {GpoGuid} does not point to its SYSVOL folder; nothing is read.")]
        public static partial void PathRedirected(ILogger logger, Guid gpoGuid);

        [LoggerMessage(EventId = 2310, Level = LogLevel.Information, Message = "GPO content: reconciliation of {Count} GPO(s) finished.")]
        public static partial void Reconciled(ILogger logger, int count);
    }
}
