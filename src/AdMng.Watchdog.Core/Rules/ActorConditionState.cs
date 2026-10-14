namespace AdMng.Watchdog.Core.Rules;

/// <summary>State of one actor condition (<see cref="ActorCondition"/>).</summary>
internal enum ActorConditionState
{
    /// <summary>The resolved actor is the configured SID.</summary>
    Matches,

    /// <summary>Another actor, or no resolved actor (Unknown, Ambiguous, NotApplicable, grace period over).</summary>
    NoMatch,

    /// <summary>Attribution is pending and the grace period is not over.</summary>
    Pending,
}
