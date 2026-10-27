using AdMng.Core.ActiveDirectory;
using AdMng.Core.Audit;
using AdMng.Core.Batch.Planning;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using Microsoft.Extensions.Logging;

namespace AdMng.Core.Batch;

/// <summary>
/// Default <see cref="IBatchExecutor"/>. Objects run in parallel (bounded), the steps of one object sequentially;
/// the current DN is tracked through renames and moves. Transient errors are retried with exponential back-off.
/// Every executed step is audited (passwords never).
/// </summary>
/// <remarks>
/// A Modify step whose changes mix replace/clear (sent strictly, as compare-and-swap with
/// <see cref="BatchOptions.OptimisticConcurrency"/>) and add/remove values (sent with the permissive modify control)
/// is executed as two modify requests, because the permissive control would defeat the compare-and-swap (the planners
/// therefore emit them as two steps, so that each request has its own result).
/// A transient error (timeout, lost connection) does not tell whether the DC applied the request, so a retry that
/// then fails is confirmed by reading the directory: noSuchObject after a rename, move or delete (the source is gone),
/// a conflict after a compare-and-swap modify (the new values are already there) and alreadyExists after a create
/// (the object exists with the requested values) mean that the attempt that seemed to fail went through.
/// Audit log failures (e.g. a throwing <see cref="IAuditLog.EntryRecorded"/> subscriber) are logged, never thrown.
/// The diagnostic log contains counts, actions and error kinds only - never DNs or values.
/// <see cref="ChangeAction.Restore"/>: an object restored into a container that an earlier object of the same plan restores
/// (<see cref="RecycleBin.PlanRestoreAsync"/> orders parents first) starts only after that object finished; when the parent
/// was not restored, the child fails with <see cref="ParentNotRestoredMessage"/> without a request to the DC.
/// </remarks>
public sealed partial class BatchExecutor : IBatchExecutor
{
    /// <summary>Prefix of the error message of a step that failed because a value changed after the preview.</summary>
    public const string ConflictMessagePrefix = "Conflict: value changed since preview";

    /// <summary>
    /// Error message of a restore that was not attempted because its parent container (restored first in the same plan)
    /// was not restored.
    /// </summary>
    public const string ParentNotRestoredMessage = "Not restored: the parent container was not restored.";

    private const int MaxParallelism = 16;

    /// <summary>ERROR_DS_SINGLE_VALUE_CONSTRAINT: a single-valued attribute got a second value (constraintViolation).</summary>
    private const int ErrorDsSingleValueConstraint = 0x2081;

    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>Attributes that cannot be compared-and-swapped (write-only or with special write semantics).</summary>
    private static readonly HashSet<string> NonConcurrentAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        Attr.PwdLastSet, Attr.LockoutTime, Attr.UnicodePwd, "userPassword",
    };

    private readonly IAuditLog _auditLog;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BatchExecutor>? _logger;
    private readonly ISysvolFileWriter? _sysvolWriter;

    /// <param name="auditLog">Audit log of the connection window.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="logger">Diagnostics (value-free).</param>
    /// <param name="sysvolWriter">SYSVOL writer for the GPO steps (design note docs/dev/k-computers-and-gpo.md 3.3); without
    /// it SYSVOL steps fail.</param>
    public BatchExecutor(IAuditLog auditLog, TimeProvider timeProvider, ILogger<BatchExecutor>? logger = null, ISysvolFileWriter? sysvolWriter = null)
    {
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _auditLog = auditLog;
        _timeProvider = timeProvider;
        _logger = logger;
        _sysvolWriter = sysvolWriter;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The plan was made on another domain controller than the session's.</exception>
    public Task<BatchResult> ExecuteAsync(
        IDirectorySession session,
        ChangePlan plan,
        BatchOptions? options,
        IProgress<BatchProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Server is not null && !string.Equals(plan.Server, session.Info.Server, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The plan was created from values read on '{plan.Server}' but the session is connected to '{session.Info.Server}'. Re-create the preview.");
        }

        var run = new Run(this, session, plan, options ?? new BatchOptions(), progress, cancellationToken);
        return run.ExecuteAsync();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Executing batch: {Objects} objects, {Executable} executable, parallelism {Parallelism}")]
    private static partial void LogStarting(ILogger logger, int objects, int executable, int parallelism);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Transient {ErrorKind} during {Action}, attempt {Attempt} of {MaxAttempts}; retrying in {DelayMs} ms")]
    private static partial void LogRetry(ILogger logger, DirectoryErrorKind errorKind, ChangeAction action, int attempt, int maxAttempts, double delayMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Action} failed: {ErrorKind}")]
    private static partial void LogStepFailed(ILogger logger, ChangeAction action, DirectoryErrorKind errorKind);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected {ExceptionType} during {Action}: {StackTrace}")]
    private static partial void LogUnexpected(ILogger logger, string exceptionType, ChangeAction action, string? stackTrace);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recording the audit entry of {Action} failed with {ExceptionType}")]
    private static partial void LogAuditFailed(ILogger logger, ChangeAction action, string exceptionType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Batch finished in {Seconds:0.0} s: {Succeeded} succeeded, {Failed} failed, {Skipped} skipped, cancelled: {Cancelled}")]
    private static partial void LogFinished(ILogger logger, double seconds, int succeeded, int failed, int skipped, bool cancelled);

    /// <summary>Outcome of one LDAP request including retries.</summary>
    private readonly record struct Attempted<T>(T? Value, int Attempts, Exception? Error);

    /// <summary>Outcome of one step.</summary>
    private sealed record StepOutcome(ChangeResult Result, string? NewDn, bool Cancelled)
    {
        /// <summary>The ACE delta a <see cref="Security.AclChange.ReplacesExplicit"/> step applied (audited instead of the planned one).</summary>
        public Security.AclChange? AppliedAcl { get; init; }
    }

    /// <summary>State of one execution.</summary>
    private sealed partial class Run
    {
        private readonly BatchExecutor _owner;
        private readonly IDirectorySession _session;
        private readonly ChangePlan _plan;
        private readonly BatchOptions _options;
        private readonly IProgress<BatchProgress>? _progress;
        private readonly CancellationToken _cancellationToken;
        private readonly int _parallelism;

        /// <summary>
        /// Per object: the index of the earlier object whose restore creates the container this object is restored into,
        /// or -1. Null when the plan has no such dependencies.
        /// </summary>
        private readonly int[]? _dependencies;

        /// <summary>Per object (only with <see cref="_dependencies"/>): completes with "restored" when the object finished.</summary>
        private readonly TaskCompletionSource<bool>[]? _restored;
        private int _completed;
        private int _failed;
        private volatile bool _stopRequested;

        public Run(BatchExecutor owner, IDirectorySession session, ChangePlan plan, BatchOptions options, IProgress<BatchProgress>? progress, CancellationToken cancellationToken)
        {
            _owner = owner;
            _session = session;
            _plan = plan;
            _options = options;
            _progress = progress;
            _cancellationToken = cancellationToken;
            // A sequential plan (every GPO plan) runs its objects one by one in plan order.
            _parallelism = plan.Sequential ? 1 : Math.Clamp(options.MaxDegreeOfParallelism, 1, MaxParallelism);
            _dependencies = RestoreDependencies(plan.Objects);
            if (_dependencies is not null)
            {
                _restored = [.. plan.Objects.Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously))];
            }
        }

        private DateTimeOffset Now => _owner._timeProvider.GetUtcNow();

        public async Task<BatchResult> ExecuteAsync()
        {
            var startedAt = Now;
            var objects = _plan.Objects;
            if (_owner._logger is { } logger)
            {
                LogStarting(logger, objects.Count, _plan.ExecutableCount, _parallelism);
            }

            var results = new ObjectResult[objects.Count];
            var running = new List<Task>();
            using var gate = new SemaphoreSlim(_parallelism, _parallelism);
            var hasScheduled = false;
            var blocked = _plan.IsBlocked;
            for (var i = 0; i < objects.Count; i++)
            {
                var objectPlan = objects[i];
                if (blocked || objectPlan.HasErrors || objectPlan.IsNoOp || _stopRequested)
                {
                    Complete(results, i, NotExecuted(objectPlan, ObjectResultStatus.Skipped));
                    continue;
                }

                if (hasScheduled && _options.DelayBetweenObjects > TimeSpan.Zero && !_cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(_options.DelayBetweenObjects, _owner._timeProvider, _cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Reported as cancelled below.
                    }
                }

                if (!await TryEnterAsync(gate).ConfigureAwait(false))
                {
                    Complete(results, i, NotExecuted(objectPlan, ObjectResultStatus.Cancelled));
                    continue;
                }

                if (_stopRequested)
                {
                    gate.Release();
                    Complete(results, i, NotExecuted(objectPlan, ObjectResultStatus.Skipped));
                    continue;
                }

                hasScheduled = true;
                var index = i;
                running.Add(Task.Run(
                    async () =>
                    {
                        try
                        {
                            Complete(results, index, await ExecuteObjectAsync(objectPlan, index).ConfigureAwait(false));
                        }
                        finally
                        {
                            // Should the object fail unexpectedly, objects restored into it must not wait for it forever.
                            _restored?[index].TrySetResult(false);
                            gate.Release();
                        }
                    },
                    CancellationToken.None));
            }

            await Task.WhenAll(running).ConfigureAwait(false);
            await CloseSysvolAsync().ConfigureAwait(false);
            var finishedAt = Now;
            var wasCancelled = _cancellationToken.IsCancellationRequested
                || results.Any(r => r.Status == ObjectResultStatus.Cancelled);
            var result = new BatchResult(_plan, results, _session.Info.Server, _session.Info.BoundIdentity, startedAt, finishedAt, wasCancelled);
            if (_owner._logger is { } finishedLogger)
            {
                LogFinished(finishedLogger, (finishedAt - startedAt).TotalSeconds, result.SucceededCount, result.FailedCount, result.SkippedCount, wasCancelled);
            }

            return result;
        }

        private async Task<bool> TryEnterAsync(SemaphoreSlim gate)
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                await gate.WaitAsync(_cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        private ObjectResult NotExecuted(ObjectPlan objectPlan, ObjectResultStatus status)
        {
            var now = Now;
            return new ObjectResult(objectPlan, status, [], objectPlan.DistinguishedName, now, now);
        }

        /// <summary>
        /// For each object with a <see cref="ChangeAction.Restore"/> step whose target container is the restore target of an
        /// EARLIER object: that object's index. Only earlier ones count - objects are scheduled in plan order, so waiting for
        /// them cannot dead-lock the bounded parallelism. Null when no object depends on another.
        /// </summary>
        private static int[]? RestoreDependencies(IReadOnlyList<ObjectPlan> objects)
        {
            int[]? dependencies = null;
            Dictionary<DistinguishedName, int>? restoredTargets = null;
            for (var i = 0; i < objects.Count; i++)
            {
                var restore = objects[i].Changes.FirstOrDefault(c => c.Action == ChangeAction.Restore);
                if (restore is null || !DistinguishedName.TryParse(restore.TargetDn, out var target) || target.IsRoot)
                {
                    continue;
                }

                if (restoredTargets is not null && target.Parent is { } parent && restoredTargets.TryGetValue(parent, out var parentIndex))
                {
                    dependencies ??= [.. Enumerable.Repeat(-1, objects.Count)];
                    dependencies[i] = parentIndex;
                }

                (restoredTargets ??= []).TryAdd(target, i);
            }

            return dependencies;
        }

        private void Complete(ObjectResult[] results, int index, ObjectResult result)
        {
            results[index] = result;
            _restored?[index].TrySetResult(result.Changes.Any(c => c.Succeeded && c.Change.Action == ChangeAction.Restore));
            var failed = result.Status is ObjectResultStatus.Failed or ObjectResultStatus.PartiallySucceeded
                ? Interlocked.Increment(ref _failed)
                : Volatile.Read(ref _failed);
            var completed = Interlocked.Increment(ref _completed);
            _progress?.Report(new BatchProgress(completed, _plan.Objects.Count, failed, result));
        }

        private async Task<ObjectResult> ExecuteObjectAsync(ObjectPlan objectPlan, int index)
        {
            if (_dependencies is { } dependencies && dependencies[index] >= 0
                && !await _restored![dependencies[index]].Task.ConfigureAwait(false))
            {
                return ParentNotRestored(objectPlan);
            }

            var startedAt = Now;
            var currentDn = objectPlan.DistinguishedName;
            var results = new List<ChangeResult>();
            var anySucceeded = false;
            var status = ObjectResultStatus.Succeeded;
            foreach (var change in objectPlan.Changes)
            {
                if (change.Action == ChangeAction.Modify && change.Attributes.Count == 0)
                {
                    continue;
                }

                if (_cancellationToken.IsCancellationRequested)
                {
                    status = anySucceeded ? ObjectResultStatus.PartiallySucceeded : ObjectResultStatus.Cancelled;
                    break;
                }

                var outcome = await ExecuteStepAsync(objectPlan, change, currentDn).ConfigureAwait(false);
                results.Add(outcome.Result);
                Audit(change, currentDn, outcome);
                if (!outcome.Result.Succeeded)
                {
                    if (outcome.Cancelled)
                    {
                        status = anySucceeded ? ObjectResultStatus.PartiallySucceeded : ObjectResultStatus.Cancelled;
                    }
                    else
                    {
                        status = anySucceeded ? ObjectResultStatus.PartiallySucceeded : ObjectResultStatus.Failed;
                        if (_options.StopOnFirstError || _plan.Sequential)
                        {
                            _stopRequested = true;
                        }
                    }

                    break;
                }

                anySucceeded = true;
                if (outcome.NewDn is not null)
                {
                    currentDn = outcome.NewDn;
                }
            }

            return new ObjectResult(objectPlan, status, results, currentDn, startedAt, Now);
        }

        /// <summary>The container this object is restored into was not restored by its (earlier) object: nothing is sent to the DC.</summary>
        private ObjectResult ParentNotRestored(ObjectPlan objectPlan)
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                return NotExecuted(objectPlan, ObjectResultStatus.Cancelled);
            }

            var now = Now;
            var restore = objectPlan.Changes.First(c => c.Action == ChangeAction.Restore);
            var failed = new ChangeResult(restore, false, DirectoryErrorKind.NoSuchObject, ParentNotRestoredMessage, 0);
            return new ObjectResult(objectPlan, ObjectResultStatus.Failed, [failed], objectPlan.DistinguishedName, now, now);
        }

        private async Task<StepOutcome> ExecuteStepAsync(ObjectPlan objectPlan, PlannedChange change, string dn)
        {
            switch (change.Action)
            {
                case ChangeAction.CreateGpoFolder or ChangeAction.WriteGpoFile or ChangeAction.DeleteGpoFile or ChangeAction.SyncGpoFolderSecurity
                    or ChangeAction.SetGpoFolderSecurity or ChangeAction.SetGpoVersion or ChangeAction.DeleteGpoFolder:
                    return await SysvolStepAsync(objectPlan, change, dn).ConfigureAwait(false);
                case ChangeAction.Modify:
                    return await ModifyAsync(change, dn).ConfigureAwait(false);
                case ChangeAction.AddMember or ChangeAction.RemoveMember:
                    if (string.IsNullOrEmpty(change.TargetDn))
                    {
                        return Invalid(change, "The membership step has no group.");
                    }

                    var modification = change.Action == ChangeAction.AddMember
                        ? AttributeModification.Add(Attr.Member, dn)
                        : AttributeModification.Delete(Attr.Member, dn);
                    return ToOutcome(change, await InvokeAsync(change.Action, async () =>
                    {
                        await _session.ModifyAsync(change.TargetDn, [modification], ModifyOptions.Idempotent, CancellationToken.None).ConfigureAwait(false);
                        return true;
                    }).ConfigureAwait(false));
                case ChangeAction.ResetPassword:
                    if (string.IsNullOrEmpty(change.Password))
                    {
                        return Invalid(change, "The password reset step has no password.");
                    }

                    return ToOutcome(change, await InvokeAsync(change.Action, async () =>
                    {
                        await _session.ResetPasswordAsync(dn, change.Password, CancellationToken.None).ConfigureAwait(false);
                        return true;
                    }).ConfigureAwait(false));
                case ChangeAction.Rename:
                    if (string.IsNullOrEmpty(change.TargetDn))
                    {
                        return Invalid(change, "The rename step has no new name.");
                    }

                    var renamed = await InvokeAsync(change.Action,
                        () => _session.MoveAsync(dn, null, change.TargetDn, CancellationToken.None)).ConfigureAwait(false);
                    return ToOutcome(change, await ConfirmMoveAsync(renamed, dn, ExpectedDn(dn, change)).ConfigureAwait(false), newDn: true);
                case ChangeAction.Move:
                    if (string.IsNullOrEmpty(change.TargetDn))
                    {
                        return Invalid(change, "The move step has no target container.");
                    }

                    var moved = await InvokeAsync(change.Action,
                        () => _session.MoveAsync(dn, change.TargetDn, null, CancellationToken.None)).ConfigureAwait(false);
                    return ToOutcome(change, await ConfirmMoveAsync(moved, dn, ExpectedDn(dn, change)).ConfigureAwait(false), newDn: true);
                case ChangeAction.Delete:
                    var deleted = await InvokeAsync(change.Action, async () =>
                    {
                        await _session.DeleteAsync(dn, change.DeleteSubtree, CancellationToken.None).ConfigureAwait(false);
                        return true;
                    }).ConfigureAwait(false);

                    // noSuchObject on a retry: the attempt that timed out has already deleted the object.
                    return ToOutcome(change, IsAmbiguousNoSuchObject(deleted) ? deleted with { Value = true, Error = null } : deleted);
                case ChangeAction.Create:
                    var created = await InvokeAsync(change.Action, async () =>
                    {
                        // Built inside the request so that an invalid step (e.g. an attribute listed twice) fails this
                        // object only instead of aborting the whole batch.
                        var attributes = change.Attributes
                            .Where(a => a.NewValues.Count > 0)
                            .ToDictionary(a => a.Attribute, a => Wire(a.Attribute, a.NewValues), StringComparer.OrdinalIgnoreCase);
                        await _session.AddAsync(objectPlan.DistinguishedName, attributes, CancellationToken.None).ConfigureAwait(false);
                        return true;
                    }).ConfigureAwait(false);
                    return ToOutcome(change, await ConfirmCreateAsync(created, objectPlan.DistinguishedName, change).ConfigureAwait(false));
                case ChangeAction.Restore:
                    if (!DistinguishedName.TryParse(change.TargetDn, out var restoreTarget) || restoreTarget.IsRoot)
                    {
                        return Invalid(change, "The restore step has no valid target DN.");
                    }

                    var targetDn = change.TargetDn!;
                    var restored = await InvokeAsync(change.Action, async () =>
                    {
                        await _session.RestoreDeletedAsync(dn, targetDn, CancellationToken.None).ConfigureAwait(false);
                        return targetDn;
                    }).ConfigureAwait(false);

                    // noSuchObject on a retry: the attempt that timed out restored it (the deleted object is gone and the
                    // object at the target DN is this one, not somebody else's object with the same name).
                    return ToOutcome(change, await ConfirmRestoreAsync(restored, targetDn, objectPlan.ObjectGuid).ConfigureAwait(false), newDn: true);
                case ChangeAction.ModifySecurity:
                    return await ModifySecurityAsync(change, dn).ConfigureAwait(false);
                default:
                    return Invalid(change, $"Unsupported action {change.Action}.");
            }
        }

        /// <summary>
        /// A transient error (timeout, lost connection) does not tell whether the DC applied the request. When the retry
        /// then fails with noSuchObject, the earlier attempt most likely went through; reporting a failure would lose
        /// the DN tracking of the following steps and the rollback of a change that was made.
        /// </summary>
        private static bool IsAmbiguousNoSuchObject<T>(Attempted<T> attempted) =>
            attempted.Attempts > 1 && attempted.Error is DirectoryException { Kind: DirectoryErrorKind.NoSuchObject };

        /// <summary>Confirms an ambiguous rename/move by reading the directory: the source is gone and the target exists.</summary>
        private async Task<Attempted<string>> ConfirmMoveAsync(Attempted<string> attempted, string dn, string? expectedDn)
        {
            if (!IsAmbiguousNoSuchObject(attempted) || expectedDn is null)
            {
                return attempted;
            }

            try
            {
                if (await _session.GetAsync(dn, [Attr.DistinguishedName], CancellationToken.None).ConfigureAwait(false) is null
                    && await _session.GetAsync(expectedDn, [Attr.DistinguishedName], CancellationToken.None).ConfigureAwait(false) is { } moved)
                {
                    return attempted with { Value = moved.DistinguishedName, Error = null };
                }
            }
            catch (DirectoryException)
            {
                // Keep the original error.
            }

            return attempted;
        }

        /// <summary>
        /// Confirms an ambiguous restore (noSuchObject on a retry): the object at the target DN is the restored one - it has
        /// the plan's objectGUID (when the plan knows it). Unlike <see cref="ConfirmMoveAsync"/> the source is not read: a
        /// DN in CN=Deleted Objects is never visible without the Show Deleted control, so its absence proves nothing.
        /// </summary>
        private async Task<Attempted<string>> ConfirmRestoreAsync(Attempted<string> attempted, string targetDn, Guid? objectGuid)
        {
            if (!IsAmbiguousNoSuchObject(attempted))
            {
                return attempted;
            }

            try
            {
                if (await _session.GetAsync(targetDn, [Attr.ObjectGuid], CancellationToken.None).ConfigureAwait(false) is { } restored
                    && (objectGuid is null || restored.ObjectGuid == objectGuid))
                {
                    return attempted with { Value = restored.DistinguishedName, Error = null };
                }
            }
            catch (DirectoryException)
            {
                // Keep the original error.
            }

            return attempted;
        }

        /// <summary>
        /// Confirms an ambiguous create (alreadyExists on a retry): the object exists and holds every value the create
        /// writes, so it is the one the timed-out attempt created and not somebody else's object with the same name.
        /// </summary>
        private async Task<Attempted<bool>> ConfirmCreateAsync(Attempted<bool> attempted, string dn, PlannedChange change)
        {
            if (attempted.Attempts <= 1 || attempted.Error is not DirectoryException { Kind: DirectoryErrorKind.AlreadyExists })
            {
                return attempted;
            }

            var written = change.Attributes.Where(a => a.NewValues.Count > 0 && !SecretAttributes.IsSecret(a.Attribute, _session.Schema)).ToList();
            try
            {
                var existing = await _session.GetAsync(dn, [.. written.Select(a => a.Attribute)], CancellationToken.None).ConfigureAwait(false);
                if (existing is not null && written.All(a => string.Equals(a.Attribute, Attr.ObjectClass, StringComparison.OrdinalIgnoreCase)
                        ? a.NewValues.All(c => existing.GetStrings(a.Attribute).Contains(c, StringComparer.OrdinalIgnoreCase))
                        : HasExactly(existing, a.Attribute, a.NewValues)))
                {
                    return attempted with { Value = true, Error = null };
                }
            }
            catch (DirectoryException)
            {
                // Keep the original error.
            }

            return attempted;
        }

        /// <summary>
        /// Confirms an ambiguous compare-and-swap modify (a conflict on a retry): every attribute already holds the new
        /// values, which the timed-out attempt wrote (a modify is atomic, so it applied the whole request).
        /// </summary>
        private async Task<Attempted<bool>> ConfirmModifyAsync(Attempted<bool> attempted, string dn, IReadOnlyList<AttributeChange> compared)
        {
            if (attempted.Attempts <= 1 || attempted.Error is not DirectoryException error || !IsConcurrentChange(error) || compared.Count == 0)
            {
                return attempted;
            }

            try
            {
                var current = await _session.GetAsync(dn, [.. compared.Select(a => a.Attribute)], CancellationToken.None).ConfigureAwait(false);
                if (current is not null && compared.All(a => HasExactly(current, a.Attribute, a.Kind == AttributeChangeKind.Clear ? [] : a.NewValues)))
                {
                    return attempted with { Value = true, Error = null };
                }
            }
            catch (DirectoryException)
            {
                // Keep the original error.
            }

            return attempted;
        }

        /// <summary>Whether the attribute holds exactly the expected values (order-insensitive, per the attribute's syntax).</summary>
        private bool HasExactly(DirectoryObject obj, string attribute, IReadOnlyList<string> expected)
        {
            var remaining = PipelineValues.Read(obj, attribute).ToList();
            if (remaining.Count != expected.Count)
            {
                return false;
            }

            var comparer = ValueComparers.ForIdentity(_session.Schema.FindAttribute(attribute));
            foreach (var value in expected)
            {
                var index = remaining.FindIndex(r => comparer.Equals(r, value));
                if (index < 0)
                {
                    return false;
                }

                remaining.RemoveAt(index);
            }

            return true;
        }

        /// <summary>The DN a rename (new RDN in TargetDn) or move (new parent in TargetDn) produces, or null.</summary>
        private static string? ExpectedDn(string dn, PlannedChange change)
        {
            if (!DistinguishedName.TryParse(dn, out var current) || current.IsRoot
                || !DistinguishedName.TryParse(change.TargetDn, out var target) || target.IsRoot)
            {
                return null;
            }

            return change.Action == ChangeAction.Rename
                ? current.Parent?.Child(target.Leaf.Type, target.Leaf.Value).ToString()
                : current.WithParent(target).ToString();
        }

        private static StepOutcome Invalid(PlannedChange change, string message) =>
            new(new ChangeResult(change, false, DirectoryErrorKind.Unknown, message, 0), null, false);

        private static StepOutcome ToOutcome<T>(PlannedChange change, Attempted<T> attempted, bool newDn = false)
        {
            if (attempted.Error is null)
            {
                return new StepOutcome(new ChangeResult(change, true, Attempts: attempted.Attempts), newDn ? attempted.Value as string : null, false);
            }

            var (kind, message) = Describe(attempted.Error);
            return new StepOutcome(
                new ChangeResult(change, false, kind, message, attempted.Attempts),
                null,
                attempted.Error is OperationCanceledException);
        }

        private static (DirectoryErrorKind Kind, string Message) Describe(Exception error) => error switch
        {
            DirectoryException directory => (directory.Kind, directory.Message),
            OperationCanceledException => (DirectoryErrorKind.Cancelled, error.Message),
            _ => (DirectoryErrorKind.Unknown, error.Message),
        };

        /// <summary>The values sent to the DC: bytes for hex-encoded binary attributes (logonHours), strings otherwise.</summary>
        private static IReadOnlyList<object> Wire(string attribute, IReadOnlyList<string> values) =>
            [.. values.Select(v => PipelineValues.ToWire(attribute, v))];

        private async Task<StepOutcome> ModifyAsync(PlannedChange change, string dn)
        {
            var strict = new List<AttributeModification>();
            var permissive = new List<AttributeModification>();
            var compareAndSwap = false;
            var compared = new List<AttributeChange>();
            if (change.Attributes.FirstOrDefault(a => a.OldValues.Concat(a.NewValues).Any(v => !PipelineValues.IsWireValue(a.Attribute, v))) is { } invalid)
            {
                // Only a hand-made plan can carry such a value: the planner validates the hex form of binary values.
                return Invalid(change, $"A value of '{invalid.Attribute}' is not valid hexadecimal text.");
            }

            foreach (var attribute in change.Attributes)
            {
                switch (attribute.Kind)
                {
                    case AttributeChangeKind.AddValues when attribute.NewValues.Count > 0:
                        permissive.Add(new AttributeModification(attribute.Attribute, ModificationOperation.Add, Wire(attribute.Attribute, attribute.NewValues)));
                        break;
                    case AttributeChangeKind.RemoveValues when attribute.NewValues.Count > 0:
                        permissive.Add(new AttributeModification(attribute.Attribute, ModificationOperation.Delete, Wire(attribute.Attribute, attribute.NewValues)));
                        break;
                    case AttributeChangeKind.Replace or AttributeChangeKind.Clear:
                        var newValues = attribute.Kind == AttributeChangeKind.Clear ? [] : attribute.NewValues;
                        if (_options.OptimisticConcurrency && !NonConcurrentAttributes.Contains(attribute.Attribute))
                        {
                            compared.Add(attribute);
                            if (attribute.OldValues.Count > 0)
                            {
                                strict.Add(new AttributeModification(attribute.Attribute, ModificationOperation.Delete, Wire(attribute.Attribute, attribute.OldValues)));
                                compareAndSwap = true;
                            }

                            if (newValues.Count > 0)
                            {
                                strict.Add(new AttributeModification(attribute.Attribute, ModificationOperation.Add, Wire(attribute.Attribute, newValues)));
                                compareAndSwap = true;
                            }
                        }
                        else
                        {
                            strict.Add(newValues.Count == 0
                                ? AttributeModification.Clear(attribute.Attribute)
                                : new AttributeModification(attribute.Attribute, ModificationOperation.Replace, Wire(attribute.Attribute, newValues)));
                        }

                        break;
                }
            }

            var attempts = 0;
            if (strict.Count > 0)
            {
                var attempted = await InvokeAsync(ChangeAction.Modify, async () =>
                {
                    await _session.ModifyAsync(dn, strict, ModifyOptions.Default, CancellationToken.None).ConfigureAwait(false);
                    return true;
                }).ConfigureAwait(false);
                if (compareAndSwap)
                {
                    // A conflict on a retry may be the values written by the attempt that timed out.
                    attempted = await ConfirmModifyAsync(attempted, dn, compared).ConfigureAwait(false);
                }

                attempts = attempted.Attempts;
                if (attempted.Error is not null)
                {
                    var outcome = ToOutcome(change, attempted);
                    if (compareAndSwap && attempted.Error is DirectoryException directoryError && IsConcurrentChange(directoryError))
                    {
                        outcome = outcome with
                        {
                            Result = outcome.Result with { ErrorMessage = $"{ConflictMessagePrefix}. {outcome.Result.ErrorMessage}" },
                        };
                    }

                    return outcome;
                }
            }

            if (permissive.Count > 0)
            {
                var attempted = await InvokeAsync(ChangeAction.Modify, async () =>
                {
                    await _session.ModifyAsync(dn, permissive, ModifyOptions.Idempotent, CancellationToken.None).ConfigureAwait(false);
                    return true;
                }).ConfigureAwait(false);
                if (attempted.Error is not null)
                {
                    return ToOutcome(change, attempted with { Attempts = Math.Max(attempts, attempted.Attempts) });
                }

                attempts = Math.Max(attempts, attempted.Attempts);
            }

            return new StepOutcome(new ChangeResult(change, true, Attempts: Math.Max(attempts, 1)), null, false);
        }

        /// <summary>
        /// Whether a failed compare-and-swap modify means that a value changed after the preview: the old value is gone
        /// (noSuchAttribute), the new one is already there (attributeOrValueExists) or a single-valued attribute got a
        /// value meanwhile (constraintViolation / ERROR_DS_SINGLE_VALUE_CONSTRAINT). Other constraint violations
        /// (range, size) are reported as they are.
        /// </summary>
        private static bool IsConcurrentChange(DirectoryException error) => error.Kind switch
        {
            DirectoryErrorKind.NoSuchAttribute or DirectoryErrorKind.AttributeOrValueExists => true,
            DirectoryErrorKind.ConstraintViolation => error.Win32Error is null or ErrorDsSingleValueConstraint,
            _ => false,
        };

        /// <summary>
        /// Runs one directory request. The request itself is not cancelled (a write that reached the DC must finish
        /// so that its outcome is known); cancellation only interrupts the back-off between retries.
        /// </summary>
        private async Task<Attempted<T>> InvokeAsync<T>(ChangeAction action, Func<Task<T>> request)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return new Attempted<T>(await request().ConfigureAwait(false), attempt, null);
                }
                catch (DirectoryException ex) when (ex.IsTransient && attempt <= _options.MaxRetries)
                {
                    var delay = RetryDelay(attempt);
                    if (_owner._logger is { } logger)
                    {
                        LogRetry(logger, ex.Kind, action, attempt, _options.MaxRetries + 1, delay.TotalMilliseconds);
                    }

                    try
                    {
                        await Task.Delay(delay, _owner._timeProvider, _cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException cancelled)
                    {
                        return new Attempted<T>(default, attempt, new OperationCanceledException("Cancelled while waiting to retry.", cancelled, _cancellationToken));
                    }
                }
                catch (DirectoryException ex)
                {
                    if (_owner._logger is { } logger)
                    {
                        LogStepFailed(logger, action, ex.Kind);
                    }

                    return new Attempted<T>(default, attempt, ex);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    if (_owner._logger is { } logger)
                    {
                        LogUnexpected(logger, ex.GetType().FullName ?? ex.GetType().Name, action, ex.StackTrace);
                    }

                    return new Attempted<T>(default, attempt, ex);
                }
            }
        }

        private TimeSpan RetryDelay(int attempt)
        {
            var initial = _options.InitialRetryDelay < TimeSpan.Zero ? TimeSpan.Zero : _options.InitialRetryDelay;
            var factor = Math.Pow(2, Math.Min(attempt - 1, 16));
            var delay = TimeSpan.FromTicks((long)Math.Min(initial.Ticks * factor, MaxRetryDelay.Ticks));
            return delay;
        }

        private void Audit(PlannedChange change, string dn, StepOutcome outcome)
        {
            var info = _session.Info;
            var now = Now;
            var succeeded = outcome.Result.Succeeded;
            var error = outcome.Result.ErrorMessage;

            AuditEntry Entry(string action, string? attribute = null, string? oldValue = null, string? newValue = null) =>
                new(now, info.BoundIdentity, info.Server, dn, action, attribute, oldValue, newValue, succeeded, error);

            switch (change.Action)
            {
                case ChangeAction.CreateGpoFolder or ChangeAction.WriteGpoFile or ChangeAction.DeleteGpoFile or ChangeAction.SyncGpoFolderSecurity
                    or ChangeAction.SetGpoFolderSecurity or ChangeAction.SetGpoVersion or ChangeAction.DeleteGpoFolder:
                    foreach (var (attribute, oldValue, newValue) in SysvolAuditValues(change))
                    {
                        Record(change.Action, Entry(change.Action.ToString(), attribute, oldValue, newValue));
                    }

                    break;
                case ChangeAction.Modify or ChangeAction.Create:
                    if (change.Attributes.Count == 0)
                    {
                        Record(change.Action, Entry(change.Action.ToString()));
                        break;
                    }

                    foreach (var attribute in change.Attributes)
                    {
                        var secret = SecretAttributes.IsSecret(attribute.Attribute, _session.Schema);
                        Record(change.Action, Entry(
                            change.Action.ToString(),
                            attribute.Attribute,
                            secret ? null : Join(attribute.OldValues),
                            secret ? null : Join(attribute.ResultValues)));
                    }

                    break;
                case ChangeAction.AddMember:
                    Record(change.Action, Entry(change.Action.ToString(), Attr.MemberOf, newValue: change.TargetDn));
                    break;
                case ChangeAction.RemoveMember:
                    Record(change.Action, Entry(change.Action.ToString(), Attr.MemberOf, oldValue: change.TargetDn));
                    break;
                case ChangeAction.Rename or ChangeAction.Move:
                    Record(change.Action, Entry(change.Action.ToString(), oldValue: dn, newValue: outcome.NewDn ?? change.TargetDn));
                    break;
                case ChangeAction.Restore:
                    // Recorded under the restored DN (the DN in CN=Deleted Objects carries the mangled name): old value = the
                    // DN in CN=Deleted Objects, new value = the DN the object got back.
                    var restoredDn = outcome.NewDn ?? change.TargetDn ?? dn;
                    Record(change.Action, new AuditEntry(now, info.BoundIdentity, info.Server, restoredDn, change.Action.ToString(), null, dn, restoredDn, succeeded, error));
                    break;
                case ChangeAction.ModifySecurity:
                    // The ACEs (rights and trustee SIDs, no names): old value = removed, new value = added.
                    var acl = outcome.AppliedAcl ?? change.Acl;
                    Record(change.Action, Entry(
                        change.Action.ToString(),
                        Attr.NtSecurityDescriptor,
                        Join([.. acl?.Removed.Select(a => a.Describe()) ?? []]),
                        Join([.. acl?.Added.Select(a => a.Describe()) ?? []])));
                    break;
                default:
                    // ResetPassword, Delete: no values (and never the password).
                    Record(change.Action, Entry(change.Action.ToString()));
                    break;
            }
        }

        /// <summary>
        /// Records one audit entry. A failing audit log (or a throwing EntryRecorded subscriber) must not abort the
        /// batch: the step already ran against the DC and its outcome has to reach the result.
        /// </summary>
        private void Record(ChangeAction action, AuditEntry entry)
        {
            try
            {
                _owner._auditLog.Record(entry);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (_owner._logger is { } logger)
                {
                    LogAuditFailed(logger, action, ex.GetType().FullName ?? ex.GetType().Name);
                }
            }
        }

        private static string? Join(IReadOnlyList<string> values) => values.Count == 0 ? null : string.Join("; ", values);
    }
}
