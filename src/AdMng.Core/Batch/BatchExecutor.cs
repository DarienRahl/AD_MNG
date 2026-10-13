using AdMng.Core.ActiveDirectory;
using AdMng.Core.Audit;
using AdMng.Core.Directory;
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
/// is executed as two modify requests, because the permissive control would defeat the compare-and-swap.
/// The diagnostic log contains counts, actions and error kinds only - never DNs or values.
/// </remarks>
public sealed partial class BatchExecutor : IBatchExecutor
{
    /// <summary>Prefix of the error message of a step that failed because a value changed after the preview.</summary>
    public const string ConflictMessagePrefix = "Conflict: value changed since preview";

    private const int MaxParallelism = 16;

    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>Attributes that cannot be compared-and-swapped (write-only or with special write semantics).</summary>
    private static readonly HashSet<string> NonConcurrentAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        Attr.PwdLastSet, Attr.LockoutTime, Attr.UnicodePwd, "userPassword",
    };

    /// <summary>Attributes whose values are never written to the audit log.</summary>
    private static readonly HashSet<string> SecretAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        Attr.UnicodePwd, "userPassword", "ms-Mcs-AdmPwd", "msLAPS-Password", "msLAPS-EncryptedPassword",
    };

    private readonly IAuditLog _auditLog;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BatchExecutor>? _logger;

    public BatchExecutor(IAuditLog auditLog, TimeProvider timeProvider, ILogger<BatchExecutor>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _auditLog = auditLog;
        _timeProvider = timeProvider;
        _logger = logger;
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Batch finished in {Seconds:0.0} s: {Succeeded} succeeded, {Failed} failed, {Skipped} skipped, cancelled: {Cancelled}")]
    private static partial void LogFinished(ILogger logger, double seconds, int succeeded, int failed, int skipped, bool cancelled);

    /// <summary>Outcome of one LDAP request including retries.</summary>
    private readonly record struct Attempted<T>(T? Value, int Attempts, Exception? Error);

    /// <summary>Outcome of one step.</summary>
    private sealed record StepOutcome(ChangeResult Result, string? NewDn, bool Cancelled);

    /// <summary>State of one execution.</summary>
    private sealed class Run
    {
        private readonly BatchExecutor _owner;
        private readonly IDirectorySession _session;
        private readonly ChangePlan _plan;
        private readonly BatchOptions _options;
        private readonly IProgress<BatchProgress>? _progress;
        private readonly CancellationToken _cancellationToken;
        private readonly int _parallelism;
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
            _parallelism = Math.Clamp(options.MaxDegreeOfParallelism, 1, MaxParallelism);
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
            for (var i = 0; i < objects.Count; i++)
            {
                var objectPlan = objects[i];
                if (objectPlan.HasErrors || objectPlan.IsNoOp || _stopRequested)
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
                            Complete(results, index, await ExecuteObjectAsync(objectPlan).ConfigureAwait(false));
                        }
                        finally
                        {
                            gate.Release();
                        }
                    },
                    CancellationToken.None));
            }

            await Task.WhenAll(running).ConfigureAwait(false);
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

        private void Complete(ObjectResult[] results, int index, ObjectResult result)
        {
            results[index] = result;
            var failed = result.Status is ObjectResultStatus.Failed or ObjectResultStatus.PartiallySucceeded
                ? Interlocked.Increment(ref _failed)
                : Volatile.Read(ref _failed);
            var completed = Interlocked.Increment(ref _completed);
            _progress?.Report(new BatchProgress(completed, _plan.Objects.Count, failed, result));
        }

        private async Task<ObjectResult> ExecuteObjectAsync(ObjectPlan objectPlan)
        {
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
                        if (_options.StopOnFirstError)
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

        private async Task<StepOutcome> ExecuteStepAsync(ObjectPlan objectPlan, PlannedChange change, string dn)
        {
            switch (change.Action)
            {
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

                    return ToOutcome(change, await InvokeAsync(change.Action,
                        () => _session.MoveAsync(dn, null, change.TargetDn, CancellationToken.None)).ConfigureAwait(false), newDn: true);
                case ChangeAction.Move:
                    if (string.IsNullOrEmpty(change.TargetDn))
                    {
                        return Invalid(change, "The move step has no target container.");
                    }

                    return ToOutcome(change, await InvokeAsync(change.Action,
                        () => _session.MoveAsync(dn, change.TargetDn, null, CancellationToken.None)).ConfigureAwait(false), newDn: true);
                case ChangeAction.Delete:
                    return ToOutcome(change, await InvokeAsync(change.Action, async () =>
                    {
                        await _session.DeleteAsync(dn, change.DeleteSubtree, CancellationToken.None).ConfigureAwait(false);
                        return true;
                    }).ConfigureAwait(false));
                case ChangeAction.Create:
                    var attributes = change.Attributes
                        .Where(a => a.NewValues.Count > 0)
                        .ToDictionary(a => a.Attribute, a => (IReadOnlyList<object>)[.. a.NewValues], StringComparer.OrdinalIgnoreCase);
                    return ToOutcome(change, await InvokeAsync(change.Action, async () =>
                    {
                        await _session.AddAsync(objectPlan.DistinguishedName, attributes, CancellationToken.None).ConfigureAwait(false);
                        return true;
                    }).ConfigureAwait(false));
                default:
                    return Invalid(change, $"Unsupported action {change.Action}.");
            }
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

        private async Task<StepOutcome> ModifyAsync(PlannedChange change, string dn)
        {
            var strict = new List<AttributeModification>();
            var permissive = new List<AttributeModification>();
            var compareAndSwap = false;
            foreach (var attribute in change.Attributes)
            {
                switch (attribute.Kind)
                {
                    case AttributeChangeKind.AddValues when attribute.NewValues.Count > 0:
                        permissive.Add(new AttributeModification(attribute.Attribute, ModificationOperation.Add, [.. attribute.NewValues]));
                        break;
                    case AttributeChangeKind.RemoveValues when attribute.NewValues.Count > 0:
                        permissive.Add(new AttributeModification(attribute.Attribute, ModificationOperation.Delete, [.. attribute.NewValues]));
                        break;
                    case AttributeChangeKind.Replace or AttributeChangeKind.Clear:
                        var newValues = attribute.Kind == AttributeChangeKind.Clear ? [] : attribute.NewValues;
                        if (_options.OptimisticConcurrency && !NonConcurrentAttributes.Contains(attribute.Attribute))
                        {
                            if (attribute.OldValues.Count > 0)
                            {
                                strict.Add(new AttributeModification(attribute.Attribute, ModificationOperation.Delete, [.. attribute.OldValues]));
                                compareAndSwap = true;
                            }

                            if (newValues.Count > 0)
                            {
                                strict.Add(new AttributeModification(attribute.Attribute, ModificationOperation.Add, [.. newValues]));
                                compareAndSwap = true;
                            }
                        }
                        else
                        {
                            strict.Add(newValues.Count == 0
                                ? AttributeModification.Clear(attribute.Attribute)
                                : new AttributeModification(attribute.Attribute, ModificationOperation.Replace, [.. newValues]));
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
                attempts = attempted.Attempts;
                if (attempted.Error is not null)
                {
                    var outcome = ToOutcome(change, attempted);
                    if (compareAndSwap && attempted.Error is DirectoryException
                        {
                            Kind: DirectoryErrorKind.NoSuchAttribute or DirectoryErrorKind.AttributeOrValueExists or DirectoryErrorKind.ConstraintViolation,
                        })
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
                case ChangeAction.Modify or ChangeAction.Create:
                    if (change.Attributes.Count == 0)
                    {
                        _owner._auditLog.Record(Entry(change.Action.ToString()));
                        break;
                    }

                    foreach (var attribute in change.Attributes)
                    {
                        var secret = SecretAttributes.Contains(attribute.Attribute);
                        _owner._auditLog.Record(Entry(
                            change.Action.ToString(),
                            attribute.Attribute,
                            secret ? null : Join(attribute.OldValues),
                            secret ? null : Join(attribute.ResultValues)));
                    }

                    break;
                case ChangeAction.AddMember:
                    _owner._auditLog.Record(Entry(change.Action.ToString(), Attr.MemberOf, newValue: change.TargetDn));
                    break;
                case ChangeAction.RemoveMember:
                    _owner._auditLog.Record(Entry(change.Action.ToString(), Attr.MemberOf, oldValue: change.TargetDn));
                    break;
                case ChangeAction.Rename or ChangeAction.Move:
                    _owner._auditLog.Record(Entry(change.Action.ToString(), oldValue: dn, newValue: outcome.NewDn ?? change.TargetDn));
                    break;
                default:
                    // ResetPassword, Delete: no values (and never the password).
                    _owner._auditLog.Record(Entry(change.Action.ToString()));
                    break;
            }
        }

        private static string? Join(IReadOnlyList<string> values) => values.Count == 0 ? null : string.Join("; ", values);
    }
}
