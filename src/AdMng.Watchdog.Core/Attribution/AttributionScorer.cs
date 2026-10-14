using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// The score table of section 11.4 #3: strong key +50, weak key +20; attribute confirmed +30; originating DC matches
/// +10; |Δt| ≤ 2 s +10, ≤ 30 s +5. Candidates below <see cref="MinimumScore"/> are discarded, the best one resolves
/// from <see cref="ResolveScore"/>, two different subjects within <see cref="AmbiguityMargin"/> points are ambiguous.
/// </summary>
public static class AttributionScorer
{
    public const int StrongKeyScore = 50;
    public const int WeakKeyScore = 20;
    public const int ConfirmedScore = 30;
    public const int OriginatingDcScore = 10;
    public const int CloseTimeScore = 10;
    public const int NearTimeScore = 5;
    public const int MinimumScore = 50;
    public const int ResolveScore = 60;
    public const int AmbiguityMargin = 5;

    private static readonly TimeSpan CloseTime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan NearTime = TimeSpan.FromSeconds(30);

    /// <summary>Scores <paramref name="candidate"/>; null when it contradicts the change or scores below
    /// <see cref="MinimumScore"/>.</summary>
    public static ScoredCandidate? Score(SecurityEvent candidate, WatchEvent watchEvent, bool strongKey, AttributionSearchPlan plan)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(watchEvent);
        ArgumentNullException.ThrowIfNull(plan);
        var fit = AttributionConfirmation.Assess(candidate, watchEvent);
        if (fit == CandidateFit.Contradicts)
        {
            return null;
        }

        var score = strongKey ? StrongKeyScore : WeakKeyScore;
        if (fit == CandidateFit.Confirmed)
        {
            score += ConfirmedScore;
        }

        if (plan.OriginatingDc is { } dc && HostNames.AreEqual(dc, candidate.Computer))
        {
            score += OriginatingDcScore;
        }

        var delta = (candidate.TimeCreated - plan.ReferenceTime).Duration();
        score += delta <= CloseTime ? CloseTimeScore : delta <= NearTime ? NearTimeScore : 0;
        return score < MinimumScore ? null : new ScoredCandidate(candidate, score, strongKey, fit == CandidateFit.Confirmed);
    }

    /// <summary>High (strong key + confirmed), Medium (strong key only), Low (weak key).</summary>
    public static AttributionConfidence Confidence(ScoredCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return !candidate.StrongKey ? AttributionConfidence.Low
            : candidate.Confirmed ? AttributionConfidence.High
            : AttributionConfidence.Medium;
    }
}
