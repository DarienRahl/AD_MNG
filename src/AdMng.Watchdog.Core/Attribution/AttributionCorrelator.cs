using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// Candidate selection and decision (section 11.4 #2–4): stored Security events of the kind's ids inside the plan's
/// window and DCs, first by the strong key (TargetSid = object SID – membership: group SID and MemberSid –, ObjectGUID),
/// when none by the weak keys (TargetUserName = sAMAccountName, ObjectDN = DN or previous DN); scored by
/// <see cref="AttributionScorer"/>; best ≥ 60 resolves, two different subjects within 5 points are ambiguous.
/// </summary>
public sealed class AttributionCorrelator(ISecurityEventStore store)
{
    /// <summary>Records read per store query.</summary>
    public const int CandidateLimit = 200;

    /// <summary>One correlation attempt for <paramref name="watchEvent"/>.</summary>
    public async Task<CorrelationOutcome> CorrelateAsync(WatchEvent watchEvent, AttributionSearchPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        ArgumentNullException.ThrowIfNull(plan);
        var accountCodes = SecurityEventCatalog.AccountCodes(watchEvent.Kind, watchEvent.Object.Kind);
        var directoryCodes = SecurityEventCatalog.DirectoryCodes(watchEvent.Kind);
        if (plan.DomainControllers.Count == 0 || (accountCodes.Count == 0 && directoryCodes.Count == 0))
        {
            return CorrelationOutcome.None;
        }

        var scored = Score(await StrongAsync(watchEvent, plan, accountCodes, directoryCodes, cancellationToken).ConfigureAwait(false), watchEvent, plan, strongKey: true);
        if (scored.Count == 0)
        {
            scored = Score(await WeakAsync(watchEvent, plan, accountCodes, directoryCodes, cancellationToken).ConfigureAwait(false), watchEvent, plan, strongKey: false);
        }

        return Decide(scored, plan);
    }

    /// <summary>The decision over scored candidates (best first).</summary>
    public static CorrelationOutcome Decide(IReadOnlyList<ScoredCandidate> scored, AttributionSearchPlan plan)
    {
        ArgumentNullException.ThrowIfNull(scored);
        ArgumentNullException.ThrowIfNull(plan);
        var ordered = scored
            .OrderByDescending(c => c.Score)
            .ThenBy(c => (c.Event.TimeCreated - plan.ReferenceTime).Duration())
            .ThenBy(c => c.Event.RecordId)
            .ToList();
        if (ordered.Count == 0 || ordered[0].Score < AttributionScorer.ResolveScore)
        {
            return new CorrelationOutcome { Status = CorrelationStatus.NoCandidate, Best = ordered.FirstOrDefault(), Candidates = ordered };
        }

        var best = ordered[0];
        var rival = ordered.Skip(1).Any(c =>
            c.Score >= best.Score - AttributionScorer.AmbiguityMargin
            && !string.Equals(c.SubjectKey, best.SubjectKey, StringComparison.OrdinalIgnoreCase));
        return new CorrelationOutcome
        {
            Status = rival ? CorrelationStatus.Ambiguous : CorrelationStatus.Resolved,
            Best = best,
            Candidates = ordered,
        };
    }

    private async Task<List<SecurityEvent>> StrongAsync(WatchEvent watchEvent, AttributionSearchPlan plan, IReadOnlyList<int> accountCodes,
        IReadOnlyList<int> directoryCodes, CancellationToken cancellationToken)
    {
        var found = new List<SecurityEvent>();
        if (accountCodes.Count > 0 && watchEvent.Object.Sid is { Length: > 0 } sid)
        {
            var membership = watchEvent.Kind is WatchEventKind.GroupMemberAdded or WatchEventKind.GroupMemberRemoved;
            found.AddRange(await FindAsync(new SecurityEventCriteria
            {
                EventCodes = accountCodes,
                From = plan.From,
                To = plan.To,
                TargetSid = sid,
                MemberSid = membership ? watchEvent.Member?.Sid : null,
                Limit = CandidateLimit,
            }, cancellationToken).ConfigureAwait(false));
        }

        if (directoryCodes.Count > 0 && watchEvent.Object.ObjectGuid is { } guid)
        {
            found.AddRange(await FindAsync(new SecurityEventCriteria
            {
                EventCodes = directoryCodes,
                From = plan.From,
                To = plan.To,
                ObjectGuid = guid,
                Limit = CandidateLimit,
            }, cancellationToken).ConfigureAwait(false));
        }

        return OnPlannedDcs(found, plan);
    }

    private async Task<List<SecurityEvent>> WeakAsync(WatchEvent watchEvent, AttributionSearchPlan plan, IReadOnlyList<int> accountCodes,
        IReadOnlyList<int> directoryCodes, CancellationToken cancellationToken)
    {
        var found = new List<SecurityEvent>();
        if (accountCodes.Count > 0 && watchEvent.Object.SamAccountName is { Length: > 0 } sam)
        {
            found.AddRange(await FindAsync(new SecurityEventCriteria
            {
                EventCodes = accountCodes, From = plan.From, To = plan.To, TargetUserName = sam, Limit = CandidateLimit,
            }, cancellationToken).ConfigureAwait(false));
        }

        if (directoryCodes.Count > 0)
        {
            foreach (var dn in new[] { watchEvent.Object.Dn, watchEvent.PreviousDn }.Where(d => !string.IsNullOrEmpty(d)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                found.AddRange(await FindAsync(new SecurityEventCriteria
                {
                    EventCodes = directoryCodes, From = plan.From, To = plan.To, ObjectDn = dn, Limit = CandidateLimit,
                }, cancellationToken).ConfigureAwait(false));
            }
        }

        return OnPlannedDcs(found.DistinctBy(e => (e.Computer.ToLowerInvariant(), e.RecordId)).ToList(), plan);
    }

    private Task<IReadOnlyList<SecurityEvent>> FindAsync(SecurityEventCriteria criteria, CancellationToken cancellationToken) =>
        store.FindAsync(criteria, cancellationToken);

    private static List<SecurityEvent> OnPlannedDcs(List<SecurityEvent> found, AttributionSearchPlan plan) =>
        [.. found.Where(e => plan.DomainControllers.Any(dc => HostNames.AreEqual(dc, e.Computer)))];

    private static List<ScoredCandidate> Score(List<SecurityEvent> found, WatchEvent watchEvent, AttributionSearchPlan plan, bool strongKey) =>
        [.. found.Select(e => AttributionScorer.Score(e, watchEvent, strongKey, plan)).OfType<ScoredCandidate>()];
}
