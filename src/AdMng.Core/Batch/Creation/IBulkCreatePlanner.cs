using AdMng.Core.Directory;

namespace AdMng.Core.Batch.Creation;

/// <summary>
/// Plans the bulk creation of accounts, contacts, groups or computers into one container (docs/dev/phase3-bulk-create.md
/// 2.5): computes the check grid (reads only, rule 4), builds the change plan with the passwords (rule 5), the retry plan
/// over all executions, reads what was created and plans the undo of everything this wizard created.
/// </summary>
public interface IBulkCreatePlanner
{
    /// <summary>
    /// Computes every row (patterns, counters, uniqueness, references, validation) and the run checks. Reads only, on
    /// <paramref name="session"/> (and on <see cref="BulkCreateLookups.ForestReader"/> for the forest scope); never throws for
    /// a row – every finding is an issue. Progress: rows computed.
    /// </summary>
    Task<BulkCreateComputation> ComputeAsync(
        IDirectorySession session,
        BulkCreateInput input,
        BulkCreateLookups lookups,
        IProgress<int>? progress,
        CancellationToken cancellationToken);

    /// <summary>The first free number and the highest taken for the rules' constant counter prefix (the <em>Numeracja</em> sample, 2.6).</summary>
    Task<CounterPreview> PreviewCounterAsync(
        IDirectorySession session,
        BulkCreateInput input,
        BulkCreateLookups lookups,
        CancellationToken cancellationToken);

    /// <summary>
    /// The change plan of the computation (pure): one object per Ok / Warning / Error row with the Create, password,
    /// enable, manager and group steps; generated passwords, or <paramref name="sharedPassword"/> copied into the plan (the
    /// caller clears its copy).
    /// </summary>
    ChangePlan BuildPlan(BulkCreateComputation computation, string? sharedPassword, string title);

    /// <summary>
    /// The plan of the failed, partially succeeded and cancelled objects of <paramref name="accumulated"/> without the steps
    /// that succeeded in any execution (pure); a password step that did not succeed gets a new password.
    /// </summary>
    ChangePlan BuildRetryPlan(BulkCreateComputation computation, BatchResult accumulated, string? newSharedPassword, string title);

    /// <summary>objectGUID and whenCreated of every object whose Create succeeded in <paramref name="result"/> (memory only).</summary>
    Task<IReadOnlyList<CreatedObject>> ReadCreatedAsync(IDirectorySession session, BatchResult result, CancellationToken cancellationToken);

    /// <summary>
    /// The undo of everything this wizard created: one Delete per object, each re-read by objectGUID first (gone or
    /// replaced → not executable; in use or with children → warnings).
    /// </summary>
    Task<ChangePlan> PlanUndoAsync(IDirectorySession session, IReadOnlyList<CreatedObject> created, string title, CancellationToken cancellationToken);
}
