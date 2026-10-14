namespace AdMng.Watchdog.Core.Rules;

/// <summary>Result of matching one rule against one event (<see cref="RuleMatcher"/>, section 12.1).</summary>
public enum RuleMatchOutcome
{
    /// <summary>The rule does not apply.</summary>
    NoMatch,

    /// <summary>The rule applies.</summary>
    Match,

    /// <summary>The rule applies unless an actor condition excludes the event once attribution is final
    /// (12.1 #8): the planner holds the notification until attribution is final or the grace period ends, then
    /// re-matches.</summary>
    Provisional,
}
