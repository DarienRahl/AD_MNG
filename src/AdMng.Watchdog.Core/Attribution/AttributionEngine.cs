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
/// <remarks>Inside <c>AdMng.Watchdog.Core.*</c> the simple name <c>Attribution</c> binds to this namespace, not to
/// <c>AdMng.Watchdog.Contracts.Events.Attribution</c> – qualify the record or use an alias.</remarks>
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

        var results = new IngestionResult[sources.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, sources.Count),
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelSources, CancellationToken = cancellationToken },
            async (i, token) => results[i] = await ingestor.IngestAsync(sources[i], context, token).ConfigureAwait(false)).ConfigureAwait(false);

        var appended = 0;
        foreach (var watchEvent in results.SelectMany(r => r.Events))
        {
            await events.AppendAsync(watchEvent, cancellationToken).ConfigureAwait(false);
            appended++;
        }

        return appended;
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

        foreach (var result in processed)
        {
            await events.UpdateAttributionAsync(result.Event.Id, result.Update, cancellationToken).ConfigureAwait(false);
            if (result.IsFinal)
            {
                actorConditions.Record(result.Event.Kind, result.Update.Attribution.Reason);
                await PlanFollowUpsAsync(result.Updated, cancellationToken).ConfigureAwait(false);
            }
        }

        LogCorrelated(processed.Count, processed.Count(p => p.IsFinal));
        return (processed.Count, pending.Count >= CorrelationBatchSize);
    }

    private async Task DisabledAsync(AttributionSettings settings, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var pending = await events.GetPendingAttributionAsync(now, CorrelationBatchSize, cancellationToken).ConfigureAwait(false);
        var updated = 0;
        foreach (var watchEvent in pending.Where(e => now - e.DetectedAt >= DisabledGrace))
        {
            var update = PendingAttributionProcessor.Disabled(watchEvent, now);
            await events.UpdateAttributionAsync(watchEvent.Id, update, cancellationToken).ConfigureAwait(false);
            await PlanFollowUpsAsync(new ProcessedAttribution(watchEvent, update).Updated, cancellationToken).ConfigureAwait(false);
            updated++;
        }

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

    private async Task PlanFollowUpsAsync(WatchEvent attributed, CancellationToken cancellationToken)
    {
        if (_followUpPlanners.Count == 0)
        {
            return;
        }

        var rows = new List<PendingNotification>();
        foreach (var planner in _followUpPlanners)
        {
            rows.AddRange(await planner.PlanAsync(attributed, cancellationToken).ConfigureAwait(false));
        }

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
}
