using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Rules;

/// <summary>One rule that matched an event (<see cref="RuleEngine.Evaluate(WatchEvent)"/>).</summary>
public sealed record RuleMatch
{
    /// <summary>The matching rule (effective configuration, tokens unexpanded).</summary>
    public required RuleSettings Rule { get; init; }

    /// <summary>Severity of this rule's notifications: <c>rule.Severity ?? event.Severity</c> – a rule may deliberately
    /// lower its notifications (12.1).</summary>
    public required Severity NotificationSeverity { get; init; }

    /// <summary>The match depends on an actor condition while attribution is pending (12.1 #8).</summary>
    public bool IsProvisional { get; init; }
}
