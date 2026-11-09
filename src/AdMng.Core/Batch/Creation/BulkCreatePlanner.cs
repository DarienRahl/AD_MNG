using AdMng.Core.Directory;
using AdMng.Core.Security;

namespace AdMng.Core.Batch.Creation;

/// <summary>
/// The planner of the bulk creation wizard (docs/dev/phase3-bulk-create.md 2.4, 2.5, owner B2; it may be split into
/// partials <c>BulkCreatePlanner.*.cs</c>). Stateless and thread-safe; caches live in <see cref="BulkCreateLookups"/>.
/// </summary>
/// <param name="passwords">Generates the per-account passwords (and the random machine passwords of computers).</param>
/// <param name="time">The clock of <see cref="ChangePlan.CreatedAt"/>, <see cref="BulkCreateComputation.ComputedAt"/> and <c>{$date}</c>.</param>
public sealed class BulkCreatePlanner(PasswordGenerator passwords, TimeProvider time) : IBulkCreatePlanner
{
    private readonly PasswordGenerator _passwords = passwords ?? throw new ArgumentNullException(nameof(passwords));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));

    public Task<BulkCreateComputation> ComputeAsync(
        IDirectorySession session,
        BulkCreateInput input,
        BulkCreateLookups lookups,
        IProgress<int>? progress,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException("F3 B2: BulkCreatePlanner.ComputeAsync (2.5).");

    public Task<CounterPreview> PreviewCounterAsync(
        IDirectorySession session,
        BulkCreateInput input,
        BulkCreateLookups lookups,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException("F3 B2: BulkCreatePlanner.PreviewCounterAsync (2.4).");

    public ChangePlan BuildPlan(BulkCreateComputation computation, string? sharedPassword, string title) =>
        throw new NotImplementedException("F3 B2: BulkCreatePlanner.BuildPlan (2.5).");

    public ChangePlan BuildRetryPlan(BulkCreateComputation computation, BatchResult accumulated, string? newSharedPassword, string title) =>
        throw new NotImplementedException("F3 B2: BulkCreatePlanner.BuildRetryPlan (2.5).");

    public Task<IReadOnlyList<CreatedObject>> ReadCreatedAsync(IDirectorySession session, BatchResult result, CancellationToken cancellationToken) =>
        throw new NotImplementedException("F3 B2: BulkCreatePlanner.ReadCreatedAsync (2.5).");

    public Task<ChangePlan> PlanUndoAsync(IDirectorySession session, IReadOnlyList<CreatedObject> created, string title, CancellationToken cancellationToken) =>
        throw new NotImplementedException("F3 B2: BulkCreatePlanner.PlanUndoAsync (2.5).");

    /// <summary>The generator and the clock are kept for B2 (the stubs do not use them yet).</summary>
    internal (PasswordGenerator Passwords, TimeProvider Time) Dependencies => (_passwords, _time);
}
