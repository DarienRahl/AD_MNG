using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>One scored candidate record of a correlation (<see cref="AttributionScorer"/>).</summary>
/// <param name="Event">The Security event.</param>
/// <param name="Score">Total score (≥ <see cref="AttributionScorer.MinimumScore"/> for kept candidates).</param>
/// <param name="StrongKey">Matched by TargetSid / ObjectGUID (/ group SID + MemberSid) rather than by name or DN.</param>
/// <param name="Confirmed">The record confirmed the specific change.</param>
public sealed record ScoredCandidate(SecurityEvent Event, int Score, bool StrongKey, bool Confirmed)
{
    /// <summary>The subject identity used to tell candidates apart: SID, else DOMAIN\name.</summary>
    public string SubjectKey => Event.SubjectSid ?? Event.SubjectAccount ?? string.Empty;
}
