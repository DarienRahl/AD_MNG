using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>
/// One rule that matched an event, as the notification planner needs it (the rules area's <c>RuleMatch</c> has the same
/// shape: rule, notification severity <c>rule.Severity ?? event.Severity</c>, provisional actor match).
/// </summary>
/// <param name="Rule">The matching rule (effective configuration).</param>
/// <param name="NotificationSeverity">Severity of this rule's notifications.</param>
/// <param name="IsProvisional">The match depends on an actor condition while attribution is pending (12.1 #8).</param>
public sealed record PlannedMatch(RuleSettings Rule, Severity NotificationSeverity, bool IsProvisional = false);
