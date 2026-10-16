namespace AdMng.Watchdog.Core.Rules;

/// <summary>Whether a rule exclusion applies to an event (<see cref="ExclusionMatcher"/>).</summary>
public enum ExclusionOutcome
{
    /// <summary>At least one condition does not hold (or the exclusion expired / sets no condition).</summary>
    DoesNotApply,

    /// <summary>Every condition the exclusion sets holds: the rule does not match.</summary>
    Applies,

    /// <summary>Every other condition holds, but the exclusion names an actor and attribution is still pending.</summary>
    AwaitingActor,
}
