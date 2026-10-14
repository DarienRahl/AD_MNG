using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// One correlation attempt for one pending event (section 11.4): kinds without an actor become NotApplicable (lockouts
/// with their bad-password sources, 6b); privilege events inherit their cause's attribution; everything else is
/// correlated – resolved (actor, tags, logon source, attribution-dependent severity), ambiguous (candidates listed, no
/// actor), or retried at <c>DetectedAt + PendingRetries[i]</c> and after the last retry ended Unknown with a reason
/// (11.7).
/// </summary>
public sealed class PendingAttributionProcessor(
    AttributionCorrelator correlator,
    ActorResolver actors,
    LogonSourceResolver logonSources,
    LockoutSourceResolver lockoutSources,
    UnresolvedReasonResolver reasons,
    PrivilegeAttributionInheritor inheritor,
    DcLogHealthTracker tracker,
    TimeProvider timeProvider)
{
    /// <summary>Processes <paramref name="watchEvent"/> (attribution Pending).</summary>
    /// <param name="watchEvent">The pending event.</param>
    /// <param name="sources">Current attribution sources (normalized hosts).</param>
    /// <param name="settings">Attribution settings.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<ProcessedAttribution> ProcessAsync(WatchEvent watchEvent, IReadOnlyList<string> sources, AttributionSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(settings);
        if (watchEvent.Kind == WatchEventKind.AccountLockedOut)
        {
            var lockout = await lockoutSources.ResolveAsync(watchEvent, settings, cancellationToken).ConfigureAwait(false);
            return new ProcessedAttribution(watchEvent, new AttributionUpdate
            {
                Attribution = EventAttribution.NotApplicable with { Evidence = lockout.Evidence },
                Lockout = lockout.Lockout,
            });
        }

        if (!SecurityEventCatalog.IsAttributable(watchEvent.Kind))
        {
            return Final(watchEvent, EventAttribution.NotApplicable);
        }

        if (SecurityEventCatalog.InheritsAttribution(watchEvent.Kind))
        {
            return await InheritAsync(watchEvent, settings, cancellationToken).ConfigureAwait(false);
        }

        var plan = OriginatingDcResolver.Plan(watchEvent, sources, settings);
        var outcome = await correlator.CorrelateAsync(watchEvent, plan, cancellationToken).ConfigureAwait(false);
        return outcome.Status switch
        {
            CorrelationStatus.Resolved => await ResolveWithAsync(watchEvent, outcome.Best!, plan, settings, cancellationToken).ConfigureAwait(false) with { Outcome = outcome },
            CorrelationStatus.Ambiguous => Ambiguous(watchEvent, outcome, plan),
            _ => await RetryOrGiveUpAsync(watchEvent, plan, settings, cancellationToken).ConfigureAwait(false) with { Outcome = outcome },
        };
    }

    /// <summary>The resolved attribution of <paramref name="watchEvent"/> from <paramref name="candidate"/>.</summary>
    public async Task<ProcessedAttribution> ResolveWithAsync(WatchEvent watchEvent, ScoredCandidate candidate, AttributionSearchPlan plan,
        AttributionSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(settings);
        var actor = await actors.ResolveAsync(candidate.Event, watchEvent, cancellationToken).ConfigureAwait(false);
        var confidence = AttributionScorer.Confidence(candidate);
        var source = await logonSources.ResolveAsync(candidate.Event, confidence, settings, cancellationToken).ConfigureAwait(false);
        var attribution = new EventAttribution
        {
            Status = AttributionStatus.Resolved,
            Confidence = confidence,
            Reason = actor.Reason,
            Actor = actor.Actor,
            Workstation = source?.Workstation,
            IpAddress = source?.IpAddress,
            SourceMethod = source?.SourceMethod,
            SourceConfidence = source is { Reason: null } ? source.Confidence : AttributionConfidence.None,
            SourceReason = source?.Reason,
            LogonType = source?.LogonType,
            Evidence = [Evidence(candidate.Event)],
            ResolvedAt = timeProvider.GetUtcNow(),
        };
        tracker.RecordOutcome(HostNames.Normalize(candidate.Event.Computer), attributed: true);
        return Final(watchEvent, attribution, actor.AddedTags) with { Plan = plan };
    }

    /// <summary>The next attempt after a failed one at <paramref name="now"/>: the first <c>DetectedAt + step</c> in
    /// the future; null after the last step (the attribution becomes final).</summary>
    public static DateTimeOffset? NextAttempt(DateTimeOffset detectedAt, IReadOnlyList<TimeSpan> retries, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(retries);
        return retries.Select(step => detectedAt + step).Where(at => at > now).Order().Cast<DateTimeOffset?>().FirstOrDefault();
    }

    /// <summary>Attribution off or not available on this host (section 11.1): attributable kinds end Unknown with
    /// SOURCE_DISABLED, the others NotApplicable.</summary>
    public static AttributionUpdate Disabled(WatchEvent watchEvent, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        var attributable = SecurityEventCatalog.IsAttributable(watchEvent.Kind);
        return new AttributionUpdate
        {
            Attribution = attributable
                ? new EventAttribution { Status = AttributionStatus.Unknown, Reason = AttributionReasons.SourceDisabled, ResolvedAt = now }
                : EventAttribution.NotApplicable,
            Severity = attributable ? AttributionSeverityPolicy.Resolve(watchEvent, new EventAttribution { Status = AttributionStatus.Unknown }, watchEvent.Tags ?? []) : null,
        };
    }

    private async Task<ProcessedAttribution> InheritAsync(WatchEvent watchEvent, AttributionSettings settings, CancellationToken cancellationToken)
    {
        var cause = await inheritor.FindCauseAsync(watchEvent, cancellationToken).ConfigureAwait(false);
        if (cause is not null && cause.Attribution.Status != AttributionStatus.Pending)
        {
            return Final(watchEvent, PrivilegeAttributionInheritor.Inherit(cause.Attribution, timeProvider.GetUtcNow()));
        }

        var now = timeProvider.GetUtcNow();
        if (NextAttempt(watchEvent.DetectedAt, settings.PendingRetries, now) is { } next)
        {
            return new ProcessedAttribution(watchEvent, new AttributionUpdate { Attribution = watchEvent.Attribution, NextAttemptAt = next });
        }

        return Final(watchEvent, new EventAttribution { Status = AttributionStatus.Unknown, Reason = AttributionReasons.NoMatch, ResolvedAt = now });
    }

    private ProcessedAttribution Ambiguous(WatchEvent watchEvent, CorrelationOutcome outcome, AttributionSearchPlan plan)
    {
        var best = outcome.Best!;
        var close = outcome.Candidates.Where(c => c.Score >= best.Score - AttributionScorer.AmbiguityMargin).ToList();
        var attribution = new EventAttribution
        {
            Status = AttributionStatus.Ambiguous,
            Confidence = AttributionConfidence.Low,
            Reason = AttributionReasons.Ambiguous,
            Candidates = [.. close.DistinctBy(c => c.SubjectKey, StringComparer.OrdinalIgnoreCase)
                .Select(c => new PrincipalRef { Sid = c.Event.SubjectSid, Account = c.Event.SubjectAccount })],
            Evidence = [.. close.Select(c => Evidence(c.Event))],
            ResolvedAt = timeProvider.GetUtcNow(),
        };
        RecordUnresolved(plan);
        return Final(watchEvent, attribution) with { Outcome = outcome, Plan = plan };
    }

    private async Task<ProcessedAttribution> RetryOrGiveUpAsync(WatchEvent watchEvent, AttributionSearchPlan plan, AttributionSettings settings,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (NextAttempt(watchEvent.DetectedAt, settings.PendingRetries, now) is { } next)
        {
            return new ProcessedAttribution(watchEvent, new AttributionUpdate { Attribution = watchEvent.Attribution, NextAttemptAt = next }, Plan: plan);
        }

        var reason = await reasons.ResolveAsync(watchEvent, plan, cancellationToken).ConfigureAwait(false);
        RecordUnresolved(plan);
        return Final(watchEvent, new EventAttribution { Status = AttributionStatus.Unknown, Reason = reason, ResolvedAt = now }) with { Plan = plan };
    }

    private void RecordUnresolved(AttributionSearchPlan plan)
    {
        if (plan.OriginatingDc is { } dc)
        {
            tracker.RecordOutcome(dc, attributed: false);
        }
        else if (plan.DomainControllers.Count == 1)
        {
            tracker.RecordOutcome(plan.DomainControllers[0], attributed: false);
        }
    }

    private static ProcessedAttribution Final(WatchEvent watchEvent, EventAttribution attribution, IReadOnlyList<string>? addedTags = null)
    {
        var added = addedTags ?? [];
        var tags = (watchEvent.Tags ?? []).Concat(added).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new ProcessedAttribution(watchEvent, new AttributionUpdate
        {
            Attribution = attribution,
            AddedTags = added,
            Severity = AttributionSeverityPolicy.Resolve(watchEvent, attribution, tags),
        });
    }

    private static EvidenceRef Evidence(SecurityEvent record) =>
        new() { Dc = record.Computer, EventCode = record.EventCode, RecordId = record.RecordId, TimeCreated = record.TimeCreated };
}
