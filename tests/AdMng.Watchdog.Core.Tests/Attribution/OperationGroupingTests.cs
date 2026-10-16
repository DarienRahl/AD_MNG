using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Core.Model;
using static AdMng.Watchdog.Core.Tests.Attribution.Records;
using static AdMng.Watchdog.Core.Tests.Rules.TestEvents;

namespace AdMng.Watchdog.Core.Tests.Attribution;

public class OperationGroupingTests
{
    private static readonly DateTimeOffset T = Now.AddMinutes(-1);
    private static readonly AttributionSearchPlan Plan = new() { DomainControllers = [Dc1, Dc2], From = T.AddMinutes(-15), To = T.AddMinutes(2), ReferenceTime = T };

    private static ProcessedAttribution Processed(WatchEvent e, CorrelationStatus status, params ScoredCandidate[] candidates) =>
        new(e, new AttributionUpdate { Attribution = e.Attribution, NextAttemptAt = Now }, new CorrelationOutcome
        {
            Status = status,
            Best = candidates.FirstOrDefault(),
            Candidates = candidates,
        }, Plan);

    [Fact]
    public void AnAmbiguousSibling_TakesTheCandidateOfTheResolvedOperation()
    {
        var changeSet = Guid.NewGuid();
        var created = Create(WatchEventKind.UserCreated) with { ChangeSetId = changeSet };
        var enabled = Create(WatchEventKind.AccountEnabled) with { ChangeSetId = changeSet };
        var resolved = new ScoredCandidate(Account(4720, T, Admin), 100, true, true);
        var ours = new ScoredCandidate(Account(4722, T.AddSeconds(1), Admin), 90, true, true);
        var theirs = new ScoredCandidate(Account(4722, T.AddSeconds(1), OtherAdmin, computer: Dc2), 90, true, true);

        var result = OperationGrouping.Disambiguate(
            [Processed(created, CorrelationStatus.Resolved, resolved), Processed(enabled, CorrelationStatus.Ambiguous, theirs, ours)]);

        var (ambiguous, candidate) = Assert.Single(result);
        Assert.Same(enabled, ambiguous.Event);
        Assert.Same(ours, candidate);
    }

    [Fact]
    public void OtherSessionsOrDistantRecords_DoNotDisambiguate()
    {
        var changeSet = Guid.NewGuid();
        var resolved = new ScoredCandidate(Account(4720, T, Admin), 100, true, true);
        var late = new ScoredCandidate(Account(4722, T.AddSeconds(3), Admin), 90, true, true);
        var other = new ScoredCandidate(Account(4722, T, OtherAdmin), 90, true, true);

        var result = OperationGrouping.Disambiguate(
        [
            Processed(Create(WatchEventKind.UserCreated) with { ChangeSetId = changeSet }, CorrelationStatus.Resolved, resolved),
            Processed(Create(WatchEventKind.AccountEnabled) with { ChangeSetId = changeSet }, CorrelationStatus.Ambiguous, other, late),
            Processed(Create(WatchEventKind.AccountEnabled) with { ChangeSetId = Guid.NewGuid() }, CorrelationStatus.Ambiguous,
                new ScoredCandidate(Account(4722, T, Admin), 90, true, true)),
        ]);

        Assert.Empty(result);
    }
}
