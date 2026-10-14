namespace AdMng.Watchdog.Core.Attribution;

/// <summary>Decision of one correlation attempt (section 11.4 #4).</summary>
public enum CorrelationStatus
{
    /// <summary>No candidate reached <see cref="AttributionScorer.ResolveScore"/> – try again at the next retry.</summary>
    NoCandidate,

    /// <summary>The best candidate resolves the actor.</summary>
    Resolved,

    /// <summary>Two different subjects within <see cref="AttributionScorer.AmbiguityMargin"/> points.</summary>
    Ambiguous,
}
