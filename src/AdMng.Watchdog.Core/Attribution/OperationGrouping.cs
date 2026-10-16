namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// Operation grouping (section 11.4 #7): events of one change set whose candidates come from the same logon session
/// (Computer + SubjectLogonId) within <see cref="Window"/> are one operation and share the actor – ADUC "new user" is
/// 4720 + 4724 + 4722 + 4738s. An ambiguous event is resolved with the candidate that belongs to the operation of a
/// resolved sibling.
/// </summary>
public static class OperationGrouping
{
    /// <summary>Maximum distance between the records of one operation.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(2);

    /// <summary>Ambiguous results that a resolved sibling of the same change set disambiguates, with the candidate of
    /// the shared operation.</summary>
    public static IReadOnlyList<(ProcessedAttribution Ambiguous, ScoredCandidate Candidate)> Disambiguate(IReadOnlyList<ProcessedAttribution> processed)
    {
        ArgumentNullException.ThrowIfNull(processed);
        var result = new List<(ProcessedAttribution, ScoredCandidate)>();
        foreach (var group in processed.Where(p => p.Event.ChangeSetId != Guid.Empty).GroupBy(p => p.Event.ChangeSetId))
        {
            var operations = group
                .Where(p => p.Outcome is { Status: CorrelationStatus.Resolved, Best: not null })
                .Select(p => p.Outcome!.Best!.Event)
                .Where(e => e.SubjectLogonId is not null)
                .ToList();
            if (operations.Count == 0)
            {
                continue;
            }

            foreach (var ambiguous in group.Where(p => p.Outcome?.Status == CorrelationStatus.Ambiguous))
            {
                var candidate = ambiguous.Outcome!.Candidates.FirstOrDefault(c => operations.Any(op =>
                    HostNames.AreEqual(op.Computer, c.Event.Computer)
                    && string.Equals(op.SubjectLogonId, c.Event.SubjectLogonId, StringComparison.Ordinal)
                    && (op.TimeCreated - c.Event.TimeCreated).Duration() <= Window));
                if (candidate is not null)
                {
                    result.Add((ambiguous, candidate));
                }
            }
        }

        return result;
    }
}
