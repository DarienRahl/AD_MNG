namespace AdMng.Watchdog.Core.Attribution;

/// <summary>Result of <see cref="AttributionCorrelator.CorrelateAsync"/>.</summary>
public sealed record CorrelationOutcome
{
    public required CorrelationStatus Status { get; init; }

    /// <summary>The best candidate (null when none was kept).</summary>
    public ScoredCandidate? Best { get; init; }

    /// <summary>Every kept candidate, best first.</summary>
    public IReadOnlyList<ScoredCandidate> Candidates { get; init; } = [];

    public static CorrelationOutcome None { get; } = new() { Status = CorrelationStatus.NoCandidate };
}
