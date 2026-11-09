namespace AdMng.Core.Batch.Creation;

/// <summary>The accumulated results of every execution of one wizard window (docs/dev/phase3-bulk-create.md 2.5, 2.7; owner B2).</summary>
public static class BulkCreateResults
{
    /// <summary>
    /// Merges <paramref name="next"/> into <paramref name="accumulated"/>: objects keyed by <see cref="ObjectPlan.SourceRow"/>,
    /// the latest <see cref="ObjectResult"/> per row with the <see cref="ChangeResult"/>s of all executions concatenated;
    /// status <see cref="ObjectResultStatus.Succeeded"/> when the latest execution succeeded, else
    /// <see cref="ObjectResultStatus.PartiallySucceeded"/> when any step of any execution succeeded, else the latest status;
    /// plan = the first plan's header. A null <paramref name="accumulated"/> returns <paramref name="next"/>.
    /// </summary>
    public static BatchResult Merge(BatchResult? accumulated, BatchResult next) =>
        throw new NotImplementedException("F3 B2: BulkCreateResults.Merge (2.5).");
}
