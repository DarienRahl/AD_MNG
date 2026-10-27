using AdMng.Core.Directory;
using AdMng.Core.Schema;

namespace AdMng.Core.Batch;

/// <summary>What the operator asked for: target objects and the operations to apply to each of them.</summary>
public sealed record BatchRequest(IReadOnlyList<string> TargetDns, IReadOnlyList<BatchOperation> Operations)
{
    public string Title { get; init; } = string.Empty;
}

/// <summary>Options of the planning step.</summary>
public sealed record PlanningOptions
{
    /// <summary>Evaluate allowedAttributesEffective per object (one extra read per object).</summary>
    public bool CheckPermissions { get; init; } = true;

    /// <summary>Query the directory for uniqueness of sAMAccountName / userPrincipalName / mail values being set.</summary>
    public bool CheckUniqueness { get; init; } = true;

    /// <summary>Parallel reads while planning.</summary>
    public int MaxDegreeOfParallelism { get; init; } = 4;
}

/// <summary>Turns a <see cref="BatchRequest"/> into a <see cref="ChangePlan"/> (reads current values live, computes diffs, validates).</summary>
public interface IChangePlanner
{
    /// <summary>
    /// Builds the plan. For every target the current values of all involved attributes are read from the
    /// session's DC (never from a cache), templates are rendered, unchanged values are dropped (no-op) and
    /// every change is validated against the schema and the operator's effective permissions.
    /// Steps per object are ordered: ResetPassword, Modify, AddMember/RemoveMember, Rename, Move (Delete alone).
    /// </summary>
    Task<ChangePlan> PlanAsync(
        IDirectorySession session,
        BatchRequest request,
        PlanningOptions? options,
        IProgress<int>? progress,
        CancellationToken cancellationToken);
}

/// <summary>Execution options.</summary>
public sealed record BatchOptions
{
    /// <summary>Objects processed in parallel (each object's steps run sequentially). 1-16.</summary>
    public int MaxDegreeOfParallelism { get; init; } = 4;

    /// <summary>Retries for transient errors (busy / unavailable / timeout) with exponential back-off.</summary>
    public int MaxRetries { get; init; } = 3;

    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Stop scheduling new objects after the first failure.</summary>
    public bool StopOnFirstError { get; init; }

    /// <summary>
    /// Optimistic concurrency: replace operations are sent as delete(old values) + add(new values) in one
    /// atomic modify, so they fail with a conflict when somebody changed the attribute after planning.
    /// Not applied to write-only/special attributes (pwdLastSet, lockoutTime, unicodePwd).
    /// </summary>
    public bool OptimisticConcurrency { get; init; } = true;

    /// <summary>Optional pause between objects to limit DC load and replication bursts on very large batches.</summary>
    public TimeSpan DelayBetweenObjects { get; init; } = TimeSpan.Zero;
}

/// <summary>Outcome of one object.</summary>
public enum ObjectResultStatus
{
    Succeeded,

    /// <summary>Some steps succeeded, a later step failed (e.g. attributes set, move denied).</summary>
    PartiallySucceeded,
    Failed,

    /// <summary>Not executed: validation error, no-op, or stopped after an earlier error.</summary>
    Skipped,
    Cancelled,
}

/// <summary>Outcome of one step.</summary>
public sealed record ChangeResult(
    PlannedChange Change,
    bool Succeeded,
    DirectoryErrorKind? ErrorKind = null,
    string? ErrorMessage = null,
    int Attempts = 1)
{
    /// <summary>
    /// A succeeded <see cref="ChangeAction.ModifySecurity"/>: the part of the step's delta that changed the DACL as it was
    /// at execution (empty when everything was already there). The audit and the rollback use it, so an ACE another
    /// administrator added meanwhile is neither claimed nor removed. Null for other steps.
    /// </summary>
    public Security.AclChange? AppliedAcl { get; init; }
}

/// <summary>Outcome of one object.</summary>
public sealed record ObjectResult(
    ObjectPlan Plan,
    ObjectResultStatus Status,
    IReadOnlyList<ChangeResult> Changes,
    string FinalDn,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt);

/// <summary>Result of executing a plan - basis for the report, CSV export and rollback.</summary>
public sealed record BatchResult(
    ChangePlan Plan,
    IReadOnlyList<ObjectResult> Objects,
    string Server,
    string Operator,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    bool WasCancelled)
{
    public int SucceededCount => Objects.Count(o => o.Status == ObjectResultStatus.Succeeded);

    public int FailedCount => Objects.Count(o => o.Status is ObjectResultStatus.Failed or ObjectResultStatus.PartiallySucceeded);

    public int SkippedCount => Objects.Count(o => o.Status == ObjectResultStatus.Skipped);
}

/// <summary>Progress notification for the UI.</summary>
public sealed record BatchProgress(int Completed, int Total, int Failed, ObjectResult? Last);

/// <summary>Executes plans against one DC with bounded parallelism, retries, cancellation and auditing.</summary>
public interface IBatchExecutor
{
    /// <summary>
    /// Executes all objects without errors that are not no-ops. Never throws for per-object failures -
    /// they are reported in the result. Cancellation stops scheduling new objects; running objects finish
    /// their current step and are reported as Cancelled/PartiallySucceeded. Every executed step is written
    /// to the audit log (without passwords).
    /// </summary>
    Task<BatchResult> ExecuteAsync(
        IDirectorySession session,
        ChangePlan plan,
        BatchOptions? options,
        IProgress<BatchProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>Creates the inverse plan of an executed batch ("undo").</summary>
public interface IRollbackPlanner
{
    /// <summary>
    /// For each successfully executed step: Modify -> restore old values (with optimistic concurrency, so
    /// values changed by somebody else in the meantime are reported as conflicts), AddMember and RemoveMember swap,
    /// Rename/Move -> back to the original name/parent. ResetPassword, Delete and Restore (deleting the restored object again
    /// is not an undo the tool offers automatically) are reported with <see cref="IssueCodes.NotReversible"/>.
    /// </summary>
    ChangePlan CreateRollbackPlan(BatchResult result);
}

/// <summary>Generates an equivalent PowerShell (ActiveDirectory module) script - for review, documentation or automation.</summary>
public interface IPowerShellExporter
{
    /// <summary>
    /// Produces a script using Set-ADObject/-Replace/-Clear/-Add/-Remove, Move-ADObject, Rename-ADObject,
    /// Add-ADGroupMember/Remove-ADGroupMember, Unlock-ADAccount, Set-ADAccountPassword (password read with
    /// Read-Host -AsSecureString, never embedded), Remove-ADObject and Restore-ADObject, all with -Server pinned to the DC.
    /// Values of secret attributes (<see cref="SecretAttributes"/>, including the confidential attributes of
    /// <paramref name="schema"/>) are never embedded either: the script prompts for them with Read-Host -AsSecureString.
    /// Objects with errors are emitted as comments.
    /// </summary>
    string Export(ChangePlan plan, string? server, ISchemaCatalog? schema = null);
}
