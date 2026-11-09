using AdMng.Core.Batch;
using AdMng.Core.Batch.Creation;

namespace AdMng.Presentation.BulkCreate;

/// <summary>
/// The change plan, the accumulated results and the undo of the bulk creation wizard (docs/dev/phase3-bulk-create.md 2.7,
/// owner B4): <see cref="PlanAsync"/> computes again with the check step's values frozen, reads the shared password last on
/// the UI thread and builds the plan; every execution of the window (canary and rest, retries, a second plan after
/// <em>Wstecz</em>) is merged into <see cref="Accumulated"/>; <see cref="PlanUndoAsync"/> deletes everything this window
/// created. The batch editor's create branches call these members.
/// </summary>
/// <remarks>
/// Contract stub: the state has its empty defaults (the complete host reads <see cref="CreatedRows"/> and
/// <see cref="ReleasedValues"/>), the methods throw until B4 writes them.
/// </remarks>
public sealed partial class BulkCreateViewModel
{
    /// <summary>Every execution of this window merged (<see cref="BulkCreateResults.Merge"/>); null before the first.</summary>
    public BatchResult? Accumulated { get; private set; }

    /// <summary>The objects this window created over all executions (GUIDs read after each execution; memory only).</summary>
    public IReadOnlyList<CreatedObject> CreatedObjects { get; private set; } = [];

    /// <summary>The password mode of the last plan (captured at plan time; <c>GeneratesPasswords</c> of the batch editor).</summary>
    public InitialPasswordMode? PasswordModeAtPlan { get; private set; }

    /// <summary>A created account with an initial password was neither exported, nor copied, nor handed out (the close guard).</summary>
    public bool HasUnhandedPasswords => false;

    /// <summary>Rows created by an earlier execution: row number → final DN (<see cref="BulkCreateInput.CreatedRows"/>).</summary>
    internal IReadOnlyDictionary<int, string> CreatedRows { get; private set; } = new Dictionary<int, string>();

    /// <summary>Identifiers this window created and its own undo deleted (<see cref="BulkCreateInput.ReleasedValues"/>).</summary>
    internal IReadOnlyCollection<string> ReleasedValues { get; private set; } = [];

    /// <summary>
    /// The create plan (preview in create mode): refreshed lookups and a computation with <see cref="BulkCreateInput.FrozenValues"/>
    /// on the thread pool, the shared password read last on the UI thread, <see cref="IBulkCreatePlanner.BuildPlan"/> on the
    /// thread pool, the boxes cleared only after the plan was built.
    /// </summary>
    public Task<ChangePlan> PlanAsync(IProgress<int> progress, CancellationToken cancellationToken) =>
        throw new NotImplementedException("F3 B4: BulkCreateViewModel.PlanAsync (2.7).");

    /// <summary>The retry plan of <see cref="Accumulated"/> (<see cref="IBulkCreatePlanner.BuildRetryPlan"/>).</summary>
    public Task<ChangePlan> PlanRetryAsync(CancellationToken cancellationToken) =>
        throw new NotImplementedException("F3 B4: BulkCreateViewModel.PlanRetryAsync (2.7).");

    /// <summary>The undo of everything this window created (<see cref="IBulkCreatePlanner.PlanUndoAsync"/>); null when nothing was created.</summary>
    public Task<ChangePlan?> PlanUndoAsync(CancellationToken cancellationToken) =>
        throw new NotImplementedException("F3 B4: BulkCreateViewModel.PlanUndoAsync (2.7).");

    /// <summary>An execution of a create or retry plan finished: merges it, marks the created rows, reads the GUIDs, updates the results.</summary>
    public void OnExecuted(BatchResult result) => throw new NotImplementedException("F3 B4: BulkCreateViewModel.OnExecuted (2.7).");

    /// <summary>The undo executed: clears the created marks of the deleted objects and releases their identifiers.</summary>
    public void OnUndone(BatchResult result) => throw new NotImplementedException("F3 B4: BulkCreateViewModel.OnUndone (2.7).");
}
