using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Rules;

/// <summary>Every rule that matched one event, in configuration order, and the event's evaluated severity.</summary>
public sealed record RuleEvaluationResult
{
    public required WatchEvent Event { get; init; }

    /// <summary><c>max(event.Severity, every matched rule's Severity override)</c> (12.1).</summary>
    public required Severity Severity { get; init; }

    public IReadOnlyList<RuleMatch> Matches { get; init; } = [];

    /// <summary>The store record of this evaluation (<c>IEventStore.CommitEvaluationAsync</c>).</summary>
    public EventEvaluation ToEvaluation() => new()
    {
        EventId = Event.Id,
        Severity = Severity,
        MatchedRules = [.. Matches.Select(m => m.Rule.Id)],
    };
}
