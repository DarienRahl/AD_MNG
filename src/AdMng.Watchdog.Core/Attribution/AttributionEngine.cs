using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Diagnostics;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Rules;
using Microsoft.Extensions.Logging;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// <see cref="IWatchdogJob"/> "attribution" (section 11): every <c>Attribution.PollInterval</c> (and on
/// <see cref="WatchdogSignal.EventsStored"/>) ingests the DC Security logs (<see cref="SecurityLogIngestor"/>, at most
/// <see cref="MaxParallelSources"/> sources in parallel), appends the event-log-only detections and log gaps, correlates
/// every pending event whose retry is due (<see cref="PendingAttributionProcessor"/>, operation grouping), stores the
/// updates, plans follow-ups for final attributions (<see cref="IAttributionFollowUpPlanner"/> →
/// <c>INotificationQueueStore.AddAsync</c>), raises <see cref="WatchdogSignal.AttributionUpdated"/>, and reports
/// <see cref="AttributionStatusDto"/>, HealthAttributionDegraded and <c>attribution.actorConditionsUnreliable</c>.
/// With attribution off, source None or an unavailable reader (non-Windows host), pending events older than
/// <see cref="DisabledGrace"/> end Unknown(SOURCE_DISABLED) (NotApplicable kinds NotApplicable).
/// </summary>
/// <remarks>
/// <para>Re-evaluation (11.4 #9): rules are matched once, when the event is stored. A final attribution that changes
/// the severity or adds tags (passwordReset, selfChange, syncAccount) can make further rules match – shadow credentials
/// on a user written by another account become High (10.6.4) and must reach <c>tier0-security</c>. Such an event is
/// matched again with its final state; rules that are not yet among its stored matched rules are planned
/// (<see cref="IRuleMatchPlanner.PlanReevaluationAsync"/> – targets that already got the final state are left out) and
/// added to the matched rules in one <c>CommitEvaluationAsync</c>, before the attribution update – a pass interrupted in
/// between leaves the event pending and the next pass finds the rules already stored (no duplicate). Rules that already
/// matched are never planned again (their rows may be delivered and gone); their waiting rows are dropped when they no
/// longer match, so the added rules can take their targets. The stored severity is the evaluated one (rule overrides
/// included), and NotificationsQueued is raised only after the attribution update.</para>
/// <para>The store phase of a pass (re-evaluations, attribution updates, follow-ups) runs without the stop token, and
/// the follow-ups collected so far are queued even when a later write fails, so an attribution that became final never
/// loses its follow-up.</para>
/// <para>Inside <c>AdMng.Watchdog.Core.*</c> the simple name <c>Attribution</c> binds to this namespace, not to
/// <c>AdMng.Watchdog.Contracts.Events.Attribution</c> – qualify the record or use an alias.</para>
/// </remarks>
public sealed partial class AttributionEngine(
    IWatchdogConfigProvider configProvider,
    ISecurityEventSource source,
    IEventStore events,
    INotificationQueueStore notificationQueue,
    IDomainControllerCatalog domainControllers,
    AttributionSourceResolver sourceResolver,
    SecurityEventReductionFactory reductionFactory,
    SecurityLogIngestor ingestor,
    PendingAttributionProcessor processor,
    DcLogHealthTracker tracker,
    AttributionDegradedMonitor degradedMonitor,
    ActorConditionHealthMonitor actorConditions,
    IConfigTokenSource tokens,
    IHealthRegistry health,
    IWatchdogSignals signals,
    IEnumerable<IAttributionFollowUpPlanner> followUpPlanners,
    RuleEngine ruleEngine,
    IEnumerable<IRuleMatchPlanner> ruleMatchPlanners,
    TimeProvider timeProvider,
    ILogger<AttributionEngine> logger) : IWatchdogJob
{
    /// <summary>Pending events correlated per run (the job runs again at once when the batch was full).</summary>
    public const int CorrelationBatchSize = 200;

    /// <summary>Sources ingested in parallel (11.1).</summary>
    public const int MaxParallelSources = 4;

    /// <summary>Pending events younger than this are left alone while attribution is disabled.</summary>
    public static readonly TimeSpan DisabledGrace = TimeSpan.FromSeconds(30);

    private static readonly WatchdogSignal[] Signals = [WatchdogSignal.EventsStored];

    private readonly IReadOnlyList<IAttributionFollowUpPlanner> _followUpPlanners = [.. followUpPlanners];
    private readonly IReadOnlyList<IRuleMatchPlanner> _ruleMatchPlanners = [.. ruleMatchPlanners];

    /// <inheritdoc/>
    public string Name => "attribution";

    /// <inheritdoc/>
    public TimeSpan DefaultInterval { get; } = TimeSpan.FromSeconds(30);

    /// <inheritdoc/>
    public IReadOnlyList<WatchdogSignal> WakeSignals => Signals;

    /// <inheritdoc/>
    public async Task<JobResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var configuration = configProvider.Current.Configuration;
        var settings = configuration.Attribution ?? new AttributionSettings();
        try
        {
            if (!settings.Enabled || settings.Source == AttributionSourceKind.None || !source.IsAvailable)
            {
                await DisabledAsync(settings, cancellationToken).ConfigureAwait(false);
                return new JobResult(settings.PollInterval);
            }

            var domain = Domain();
            var sources = await sourceResolver.ResolveAsync(settings, cancellationToken).ConfigureAwait(false);
            var appended = await IngestAsync(configuration, settings, sources, domain, cancellationToken).ConfigureAwait(false);
            var hosts = await sourceResolver.CorrelationHostsAsync(settings, sources, cancellationToken).ConfigureAwait(false);
            var (updated, full) = await CorrelateAsync(settings, hosts, cancellationToken).ConfigureAwait(false);

            foreach (var degraded in degradedMonitor.Check(hosts.Concat(sources.Select(s => s.Host)), domain))
            {
                await events.AppendAsync(degraded, cancellationToken).ConfigureAwait(false);
                LogDegraded(degraded.Object.Name ?? "-");
                appended++;
            }

            health.Report(actorConditions.Evaluate(configuration));
            health.ReportAttribution(new AttributionStatusDto
            {
                Enabled = true,
                Source = settings.Source.ToString(),
                DomainControllers = tracker.Snapshot(hosts.Concat(sources.Select(s => s.Host))),
            });
            if (appended > 0)
            {
                signals.Raise(WatchdogSignal.EventsStored);
            }

            if (updated > 0)
            {
                signals.Raise(WatchdogSignal.AttributionUpdated);
            }

            return new JobResult(full ? TimeSpan.Zero : settings.PollInterval);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SafeExceptionLog.LogError(logger, ex, "attribution cycle");
            return new JobResult(settings.PollInterval, Failed: true, FailureCode: SafeExceptionLog.ErrorCode(ex));
        }
    }

    private async Task<int> IngestAsync(WatchdogConfiguration configuration, AttributionSettings settings, IReadOnlyList<IngestionSource> sources,
        string? domain, CancellationToken cancellationToken)
    {
        if (sources.Count == 0)
        {
            return 0;
        }

        var known = (await domainControllers.GetKnownDomainControllersAsync(cancellationToken).ConfigureAwait(false))
            .Select(HostNames.Normalize).ToHashSet(StringComparer.Ordinal);
        var context = new IngestionContext
        {
            Settings = settings,
            Reduction = await reductionFactory.CreateAsync(configuration, cancellationToken).ConfigureAwait(false),
            IsKnownDomainController = computer => known.Contains(HostNames.Normalize(computer)),
            Domain = domain,
            DisabledKinds = configuration.Directory?.DisabledKinds ?? [],
        };

        // The ingestor appends its detections itself, before the cursor of their page is committed. One source failing
        // (a store error) neither stops nor discards the others.
        var results = new IngestionResult[sources.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, sources.Count),
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelSources, CancellationToken = cancellationToken },
            async (i, token) => results[i] = await IngestOneAsync(sources[i], context, token).ConfigureAwait(false)).ConfigureAwait(false);

        return results.Sum(r => r.Events.Count);
    }

    private async Task<IngestionResult> IngestOneAsync(IngestionSource ingestion, IngestionContext context, CancellationToken cancellationToken)
    {
        try
        {
            return await ingestor.IngestAsync(ingestion, context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var code = SafeExceptionLog.ErrorCode(ex);
            SafeExceptionLog.LogError(logger, ex, "security log ingestion");
            tracker.RecordFailure(ingestion.Host, code);
            health.Report(new HealthCheckDto
            {
                Code = "eventlog." + ingestion.Host,
                State = HealthState.Error,
                Detail = "The records of the Security log could not be stored.",
                Args = [ingestion.Host, code],
                CheckedAt = timeProvider.GetUtcNow(),
            });
            return new IngestionResult { Failed = true };
        }
    }

    private async Task<(int Updated, bool Full)> CorrelateAsync(AttributionSettings settings, IReadOnlyList<string> hosts, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var pending = await events.GetPendingAttributionAsync(now, CorrelationBatchSize, cancellationToken).ConfigureAwait(false);
        var processed = new List<ProcessedAttribution>(pending.Count);
        foreach (var watchEvent in pending)
        {
            processed.Add(await processor.ProcessAsync(watchEvent, hosts, settings, cancellationToken).ConfigureAwait(false));
        }

        foreach (var (ambiguous, candidate) in OperationGrouping.Disambiguate(processed))
        {
            var resolved = await processor.ResolveWithAsync(ambiguous.Event, candidate, ambiguous.Plan!, settings, cancellationToken).ConfigureAwait(false);
            processed[processed.IndexOf(ambiguous)] = resolved;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await StoreAsync(processed, recordOutcomes: true).ConfigureAwait(false);

        LogCorrelated(processed.Count, processed.Count(p => p.IsFinal));
        return (processed.Count, pending.Count >= CorrelationBatchSize);
    }

    private async Task DisabledAsync(AttributionSettings settings, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var pending = await events.GetPendingAttributionAsync(now, CorrelationBatchSize, cancellationToken).ConfigureAwait(false);
        var processed = pending.Where(e => now - e.DetectedAt >= DisabledGrace)
            .Select(e => new ProcessedAttribution(e, PendingAttributionProcessor.Disabled(e, now)))
            .ToList();
        await StoreAsync(processed, recordOutcomes: false).ConfigureAwait(false);
        var updated = processed.Count;

        health.Report(new HealthCheckDto
        {
            Code = "attribution.source",
            State = HealthState.Disabled,
            Detail = source.IsAvailable ? "Attribution is disabled in the configuration." : "Attribution needs the Windows event log reader.",
            CheckedAt = now,
        });
        health.ReportAttribution(new AttributionStatusDto { Enabled = false, Source = settings.Source.ToString() });
        if (updated > 0)
        {
            signals.Raise(WatchdogSignal.AttributionUpdated);
        }
    }

    /// <summary>
    /// The store phase of a pass, without the stop token (a few short writes – a stop in the middle would leave final
    /// attributions without their follow-ups): per event the re-evaluation of a final attribution that raised the
    /// severity or added tags, the attribution update, and the follow-ups of a final attribution; the follow-ups collected
    /// so far are queued (in one call, 13.2 #6) also when a later write fails.
    /// </summary>
    private async Task StoreAsync(IReadOnlyList<ProcessedAttribution> processed, bool recordOutcomes)
    {
        var followUps = new List<PendingNotification>();
        RuleMatchContext? rules = null;
        try
        {
            foreach (var result in processed)
            {
                var update = result.Update;
                var queued = false;

                // The re-evaluation and the update form one section with the rules job's read → commit (RuleEngine): the
                // job never commits a match made with the pending state after the final one was stored.
                using (await ruleEngine.EnterEvaluationAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    if (result.IsFinal && ChangesRuleInputs(result))
                    {
                        rules ??= ruleEngine.CreateContext(configProvider.Current.Configuration);
                        var reevaluation = await ReevaluateAsync(result, rules).ConfigureAwait(false);
                        queued = reevaluation.Queued;
                        if (reevaluation.Severity is { } severity)
                        {
                            update = update with { Severity = severity };
                        }
                    }

                    await events.UpdateAttributionAsync(result.Event.Id, update, CancellationToken.None).ConfigureAwait(false);
                }

                // Only now: a dispatch woken earlier would render the new rows with the attribution still pending.
                if (queued)
                {
                    signals.Raise(WatchdogSignal.NotificationsQueued);
                }

                if (result.IsFinal)
                {
                    if (recordOutcomes)
                    {
                        actorConditions.Record(result.Event.Kind, update.Attribution.Reason);
                    }

                    followUps.AddRange(await PlanFollowUpsAsync((result with { Update = update }).Updated, CancellationToken.None).ConfigureAwait(false));
                }
            }
        }
        finally
        {
            await QueueFollowUpsAsync(followUps, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>True when a final update can change the rule evaluation: an attribution-dependent kind (12.2 – its
    /// severity is re-resolved; "unchanged" is relative to the stored severity, which carries the rule overrides, so a
    /// rise of the event's own severity to an override's value reports no change), a changed severity, or a tag the event
    /// does not carry yet.</summary>
    private static bool ChangesRuleInputs(ProcessedAttribution result) =>
        AttributionSeverityPolicy.AttributionDependentKinds.Contains(result.Event.Kind)
        || (result.Update.Severity is { } severity && severity != result.Event.Severity)
        || result.Update.AddedTags.Any(tag => !(result.Event.Tags ?? []).Contains(tag, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Matches the event with its final state against the rules; plans and stores the rules that are not yet among its
    /// stored matched rules (an event the rules job has not evaluated yet has none, so every match is planned and the job
    /// skips it afterwards). Returns the evaluated severity – <c>max(final severity, overrides of the rules that match)</c>,
    /// as the rules job stores it – to store with the attribution (null when the event is gone), and whether rows were
    /// queued (announced by the caller once the attribution is stored).
    /// </summary>
    private async Task<(Severity? Severity, bool Queued)> ReevaluateAsync(ProcessedAttribution result, RuleMatchContext rules)
    {
        if (await events.GetAsync(result.Event.Id, CancellationToken.None).ConfigureAwait(false) is not { } current)
        {
            return (null, false);
        }

        var final = (result with { Event = current }).Updated;
        var now = timeProvider.GetUtcNow();
        var evaluation = RuleEngine.Evaluate(final, rules, now);
        var stored = (current.MatchedRules ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = evaluation.Matches.Where(m => !stored.Contains(m.Rule.Id)).ToList();
        if (added.Count == 0)
        {
            return (evaluation.Severity, false);
        }

        await DropSupersededRowsAsync(current.Id, evaluation).ConfigureAwait(false);
        var newMatches = evaluation with { Matches = added };
        var notifications = new List<PendingNotification>();
        foreach (var planner in _ruleMatchPlanners)
        {
            notifications.AddRange(await planner.PlanReevaluationAsync(newMatches, rules.Configuration, now, CancellationToken.None).ConfigureAwait(false));
        }

        var record = new EventEvaluation
        {
            EventId = current.Id,
            Severity = evaluation.Severity,
            MatchedRules = [.. (current.MatchedRules ?? []).Concat(added.Select(m => m.Rule.Id))],
        };
        await events.CommitEvaluationAsync([record], notifications, CancellationToken.None).ConfigureAwait(false);
        LogReevaluated(current.Id, added.Count, notifications.Count);
        return (evaluation.Severity, notifications.Count > 0);
    }

    /// <summary>
    /// Drops the event's waiting rows of rules that no longer match its final state – the dispatch job would drop them when
    /// it releases them (13.3) –, so a rule matched by the re-evaluation can take their target: the store keeps one row
    /// per event and target and ignores a second one.
    /// </summary>
    private async Task DropSupersededRowsAsync(long eventId, RuleEvaluationResult evaluation)
    {
        var matching = evaluation.Matches.Select(m => m.Rule.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var superseded = (await notificationQueue.GetAllAsync(CancellationToken.None).ConfigureAwait(false))
            .Where(p => p.EventId == eventId && !p.IsFollowUp && !matching.Contains(p.RuleId))
            .Select(p => p.Id)
            .ToList();
        if (superseded.Count > 0)
        {
            await notificationQueue.DropAsync(superseded, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<PendingNotification>> PlanFollowUpsAsync(WatchEvent attributed, CancellationToken cancellationToken)
    {
        var rows = new List<PendingNotification>();
        foreach (var planner in _followUpPlanners)
        {
            rows.AddRange(await planner.PlanAsync(attributed, cancellationToken).ConfigureAwait(false));
        }

        return rows;
    }

    /// <summary>Adds the follow-ups of one pass in one call, so the follow-ups of events that shared an alert (same
    /// message, same bucket) are released together as one follow-up message instead of racing the dispatch job.</summary>
    private async Task QueueFollowUpsAsync(IReadOnlyList<PendingNotification> rows, CancellationToken cancellationToken)
    {
        if (rows.Count > 0)
        {
            await notificationQueue.AddAsync(rows, cancellationToken).ConfigureAwait(false);
            signals.Raise(WatchdogSignal.NotificationsQueued);
        }
    }

    private string? Domain() =>
        DistinguishedName.TryParse(tokens.Current.DomainDn, out var dn) && !dn.IsRoot ? dn.DnsDomain : null;

    [LoggerMessage(EventId = WatchdogEventIds.AttributionDegraded, Level = LogLevel.Warning, Message = "Attribution from the Security log of {Host} is degraded.")]
    private partial void LogDegraded(string host);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Attribution: {Processed} pending events processed, {Final} final.")]
    private partial void LogCorrelated(int processed, int final);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Event {EventId} re-evaluated after attribution: {Rules} further rules matched, {Notifications} notifications queued.")]
    private partial void LogReevaluated(long eventId, int rules, int notifications);
}
