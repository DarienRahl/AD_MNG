using AdMng.Core.Batch;
using AdMng.Core.Directory;

namespace AdMng.Core.GroupPolicy.Management;

/// <summary>
/// The central store steps of one object plan (<see cref="ChangeAction.WritePolicyDefinitionsFile"/>,
/// <see cref="ChangeAction.DeletePolicyDefinitionsFile"/>, K3 "Zaktualizuj szablony") as one unit, like
/// <see cref="GpoTransaction"/> without version numbers: <b>stage</b> every new content beside its target, <b>verify</b>
/// every target against the plan's old content (a difference is a conflict and nothing changes), <b>switch</b> (renames
/// and deletes in plan order), and on a failure <b>compensate</b> from the old contents in memory. Cancellation is honoured
/// until the switch starts. Messages carry no paths or content.
/// </summary>
public static class PolicyDefinitionsTransaction
{
    /// <summary>The steps the transaction runs.</summary>
    public static bool IsStep(ChangeAction action) =>
        action is ChangeAction.WritePolicyDefinitionsFile or ChangeAction.DeletePolicyDefinitionsFile;

    /// <summary>Runs the transaction for the central store steps of one object (in plan order).</summary>
    public static async Task<GpoTransactionResult> RunAsync(ISysvolWriteConnection sysvol, IReadOnlyList<PlannedChange> steps, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sysvol);
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Any(s => !IsStep(s.Action) || s.Sysvol is not { RelativePath: { } path } change || !SysvolPaths.TryNormalize(path, out _)
            || (s.Action == ChangeAction.WritePolicyDefinitionsFile && change.NewContent is null)
            || (s.Action == ChangeAction.DeletePolicyDefinitionsFile && change.OldContent is null)))
        {
            return new GpoTransactionResult(GpoTransactionOutcome.Reverted, DirectoryErrorKind.Unknown, GpoTransaction.RevertedMessage + " The steps are invalid.");
        }

        var run = Guid.NewGuid();
        var staged = new List<string>();
        try
        {
            // 1. Stage.
            foreach (var step in steps.Where(s => s.Action == ChangeAction.WritePolicyDefinitionsFile))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var temp = GpoTransaction.TempName(step.Sysvol!.RelativePath!, run);
                await sysvol.WritePolicyDefinitionsTempFileAsync(temp, step.Sysvol.NewContent, cancellationToken).ConfigureAwait(false);
                staged.Add(temp);
            }

            // 2. Verify.
            foreach (var step in steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = await ReadAsync(sysvol, step.Sysvol!.RelativePath!, cancellationToken).ConfigureAwait(false);
                if (!SameContent(current, step.Sysvol.OldContent))
                {
                    await DeleteTempsAsync(sysvol, staged).ConfigureAwait(false);
                    return new GpoTransactionResult(GpoTransactionOutcome.Conflict, DirectoryErrorKind.NoSuchAttribute,
                        BatchExecutor.ConflictMessagePrefix + ". A file of the central store changed after the preview.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            await DeleteTempsAsync(sysvol, staged).ConfigureAwait(false);
            return new GpoTransactionResult(GpoTransactionOutcome.Cancelled, DirectoryErrorKind.Cancelled, "Cancelled before the central store was changed.");
        }
        catch (Exception ex) when (ex is SysvolWriteException or IOException or UnauthorizedAccessException)
        {
            await DeleteTempsAsync(sysvol, staged).ConfigureAwait(false);
            return Failed(GpoTransactionOutcome.Reverted, ex);
        }

        // 3. Switch - from here on it completes or compensates.
        var done = new List<PlannedChange>();
        try
        {
            foreach (var step in steps)
            {
                var path = step.Sysvol!.RelativePath!;
                if (step.Action == ChangeAction.WritePolicyDefinitionsFile)
                {
                    await sysvol.ReplacePolicyDefinitionsFileAsync(GpoTransaction.TempName(path, run), path, CancellationToken.None).ConfigureAwait(false);
                    staged.Remove(GpoTransaction.TempName(path, run));
                }
                else
                {
                    await sysvol.DeletePolicyDefinitionsFileAsync(path, CancellationToken.None).ConfigureAwait(false);
                }

                done.Add(step);
            }

            return GpoTransactionResult.Committed;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 4. Compensate.
            var restored = true;
            var back = Guid.NewGuid();
            for (var i = done.Count - 1; i >= 0; i--)
            {
                restored &= await TryRestoreAsync(sysvol, done[i].Sysvol!.RelativePath!, done[i].Sysvol!.OldContent, back).ConfigureAwait(false);
            }

            await DeleteTempsAsync(sysvol, staged).ConfigureAwait(false);
            return Failed(restored ? GpoTransactionOutcome.Reverted : GpoTransactionOutcome.Inconsistent, ex);
        }
    }

    private static async Task<bool> TryRestoreAsync(ISysvolWriteConnection sysvol, string path, byte[]? content, Guid run)
    {
        try
        {
            if (content is null)
            {
                try
                {
                    await sysvol.DeletePolicyDefinitionsFileAsync(path, CancellationToken.None).ConfigureAwait(false);
                }
                catch (SysvolWriteException ex) when (ex.Error == SysvolWriteError.NotFound)
                {
                    // Already gone.
                }

                return true;
            }

            var temp = GpoTransaction.TempName(path, run);
            await sysvol.WritePolicyDefinitionsTempFileAsync(temp, content, CancellationToken.None).ConfigureAwait(false);
            await sysvol.ReplacePolicyDefinitionsFileAsync(temp, path, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static async Task DeleteTempsAsync(ISysvolWriteConnection sysvol, List<string> temps)
    {
        foreach (var temp in temps.ToList())
        {
            try
            {
                await sysvol.DeletePolicyDefinitionsFileAsync(temp, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A leftover is deleted by the next update (it is listed in that preview).
            }
        }

        temps.Clear();
    }

    private static async Task<byte[]?> ReadAsync(ISysvolWriteConnection sysvol, string path, CancellationToken cancellationToken)
    {
        var result = await sysvol.ReadPolicyDefinitionsFileAsync(path, GpoTransaction.MaxFileBytes, cancellationToken).ConfigureAwait(false);
        return result.Status switch
        {
            SysvolFileStatus.Read => result.Content,
            SysvolFileStatus.NotFound => null,
            SysvolFileStatus.AccessDenied => throw new SysvolWriteException(SysvolWriteError.AccessDenied, "A file of the central store cannot be read."),
            _ => throw new SysvolWriteException(SysvolWriteError.Failed, "A file of the central store cannot be read."),
        };
    }

    private static bool SameContent(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);

    private static GpoTransactionResult Failed(GpoTransactionOutcome outcome, Exception error)
    {
        var kind = error switch
        {
            SysvolWriteException sysvol => sysvol.Kind,
            UnauthorizedAccessException => DirectoryErrorKind.AccessDenied,
            _ => DirectoryErrorKind.Unknown,
        };
        var prefix = outcome == GpoTransactionOutcome.Inconsistent ? GpoTransaction.InconsistentMessage : GpoTransaction.RevertedMessage;
        return new GpoTransactionResult(outcome, kind, prefix + (error is SysvolWriteException ? " " + error.Message : string.Empty));
    }
}
