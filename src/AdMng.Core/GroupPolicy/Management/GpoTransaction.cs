using System.Globalization;
using AdMng.Core.Batch;
using AdMng.Core.Directory;

namespace AdMng.Core.GroupPolicy.Management;

/// <summary>Reads and compare-and-swaps <c>versionNumber</c> of one group policy container (implemented by the batch executor).</summary>
public interface IGpoVersionStore
{
    /// <summary>The current <c>versionNumber</c> (null: not set).</summary>
    Task<int?> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Writes <paramref name="newValue"/> as delete(<paramref name="oldValue"/>) + add(new) in one modify - a conflict when the
    /// value changed meanwhile. Throws <see cref="DirectoryException"/>.
    /// </summary>
    Task WriteAsync(int? oldValue, int newValue, CancellationToken cancellationToken);
}

/// <summary>How a GPO transaction ended.</summary>
public enum GpoTransactionOutcome
{
    /// <summary>Content and version written; the version change was the commit point.</summary>
    Committed,

    /// <summary>A file or the version changed after the preview; nothing was changed.</summary>
    Conflict,

    /// <summary>A write failed; nothing was changed or every change was undone from memory.</summary>
    Reverted,

    /// <summary>A write failed and putting the old files back failed too: the GPO's files are inconsistent.</summary>
    Inconsistent,

    /// <summary>Cancelled before anything was switched; nothing was changed.</summary>
    Cancelled,
}

/// <summary>Result of <see cref="GpoTransaction.RunAsync"/>.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="ErrorKind">Kind of the first error (null when committed).</param>
/// <param name="Message">English, value-free message (prefixed with <see cref="BatchExecutor.ConflictMessagePrefix"/>,
/// <see cref="GpoTransaction.RevertedMessage"/> or <see cref="GpoTransaction.InconsistentMessage"/>).</param>
public sealed record GpoTransactionResult(GpoTransactionOutcome Outcome, DirectoryErrorKind? ErrorKind = null, string? Message = null)
{
    public static GpoTransactionResult Committed { get; } = new(GpoTransactionOutcome.Committed);

    public bool Succeeded => Outcome == GpoTransactionOutcome.Committed;
}

/// <summary>
/// The content transaction of one GPO (design note docs/dev/k-computers-and-gpo.md 3.3): the <see cref="ChangeAction.WriteGpoFile"/>,
/// <see cref="ChangeAction.DeleteGpoFile"/> and <see cref="ChangeAction.SetGpoVersion"/> steps of one object plan run as a unit.
/// <list type="number">
/// <item><b>Stage</b> - every new content is written to <see cref="TempName"/> beside its target; a failure deletes the temps.</item>
/// <item><b>Verify</b> - every target is read again and compared with the plan's old content (or "absent"), and the
/// <c>Version</c> of <c>GPT.INI</c> and <c>versionNumber</c> with the plan's old versions; a difference is a conflict and
/// nothing is changed.</item>
/// <item><b>Switch</b> - renames (temp → target) and deletes in plan order.</item>
/// <item><b>Commit</b> - <c>GPT.INI</c> with the new <c>Version=</c> line (staged and renamed), then the compare-and-swap of
/// <c>versionNumber</c>: the commit point.</item>
/// <item><b>Compensate</b> - a failure in 3 or 4 writes the old contents back from memory, deletes created files and puts
/// the old <c>GPT.INI</c> back; only when that fails is the GPO left inconsistent.</item>
/// </list>
/// Files written without a version change would be published by the next legitimate edit of anyone, so content is either
/// committed with its version or put back at once. Cancellation is honoured until the switch starts; from then on the
/// transaction finishes or compensates. Nothing is written to the local disk; messages carry no paths or content.
/// </summary>
public static class GpoTransaction
{
    /// <summary>Most entries of a GPO folder a plan copies into memory.</summary>
    public const int MaxEntriesPerGpo = 2000;

    /// <summary>Largest file a plan copies into memory.</summary>
    public const long MaxFileBytes = 32L * 1024 * 1024;

    /// <summary>Most bytes of in-memory copies of one plan.</summary>
    public const long MaxPlanBytes = 128L * 1024 * 1024;

    /// <summary>Message prefix of a failed transaction whose changes were undone.</summary>
    public const string RevertedMessage = "SYSVOL write failed; the file changes were reverted.";

    /// <summary>Message prefix of a failed transaction that could not put the old files back.</summary>
    public const string InconsistentMessage = "SYSVOL write failed and the previous files could not be restored: the GPO is inconsistent.";

    private const string TempInfix = ".admng-";
    private const string TempSuffix = ".tmp";

    /// <summary>The steps the transaction runs (the others of a GPO plan run on their own).</summary>
    public static bool IsContentStep(ChangeAction action) =>
        action is ChangeAction.WriteGpoFile or ChangeAction.DeleteGpoFile or ChangeAction.SetGpoVersion;

    /// <summary>The temp name of a target: <c>&lt;target&gt;.admng-&lt;run&gt;.tmp</c> in the target's folder (same volume, atomic rename).</summary>
    public static string TempName(string relativePath, Guid runId) =>
        relativePath + TempInfix + runId.ToString("N", CultureInfo.InvariantCulture) + TempSuffix;

    /// <summary>True for a leftover of an interrupted write (<c>*.admng-&lt;32 hex&gt;.tmp</c>).</summary>
    public static bool IsTempName(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (!relativePath.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var infix = relativePath.LastIndexOf(TempInfix, StringComparison.OrdinalIgnoreCase);
        if (infix < 0)
        {
            return false;
        }

        var id = relativePath.AsSpan(infix + TempInfix.Length, relativePath.Length - infix - TempInfix.Length - TempSuffix.Length);
        return id.Length == 32 && Guid.TryParseExact(id, "N", out _);
    }

    /// <summary>Runs the transaction for the content steps of one GPO (see the class remarks).</summary>
    /// <param name="sysvol">SYSVOL of the session's DC, opened for writing.</param>
    /// <param name="gpoId">The GPO.</param>
    /// <param name="steps">The content steps, in plan order (<see cref="IsContentStep"/>); at most one <see cref="ChangeAction.SetGpoVersion"/>.</param>
    /// <param name="versions">The container's <c>versionNumber</c>.</param>
    /// <param name="cancellationToken">Honoured until the switch starts.</param>
    public static async Task<GpoTransactionResult> RunAsync(
        ISysvolWriteConnection sysvol,
        Guid gpoId,
        IReadOnlyList<PlannedChange> steps,
        IGpoVersionStore versions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sysvol);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(versions);
        var files = steps.Where(s => s.Action is ChangeAction.WriteGpoFile or ChangeAction.DeleteGpoFile).ToList();
        var version = steps.Where(s => s.Action == ChangeAction.SetGpoVersion).Select(s => s.GpoVersion).LastOrDefault();
        if (steps.Any(s => !IsContentStep(s.Action))
            || files.Any(f => f.Sysvol is not { RelativePath: { } path } sysvolChange || sysvolChange.GpoId != gpoId
                || !SysvolPaths.TryNormalize(path, out _) || (f.Action == ChangeAction.WriteGpoFile && sysvolChange.NewContent is null)
                || (f.Action == ChangeAction.DeleteGpoFile && sysvolChange.OldContent is null)
                || (version is not null && string.Equals(path, GptIni.RelativePath, StringComparison.OrdinalIgnoreCase)))
            || (steps.Any(s => s.Action == ChangeAction.SetGpoVersion) && version is null))
        {
            return new GpoTransactionResult(GpoTransactionOutcome.Reverted, DirectoryErrorKind.Unknown, RevertedMessage + " The steps are invalid.");
        }

        var run = Guid.NewGuid();
        var staged = new List<string>();

        // 1. Stage.
        try
        {
            foreach (var step in files.Where(f => f.Action == ChangeAction.WriteGpoFile))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var temp = TempName(step.Sysvol!.RelativePath!, run);
                await sysvol.WriteTempFileAsync(gpoId, temp, step.Sysvol.NewContent, cancellationToken).ConfigureAwait(false);
                staged.Add(temp);
            }

            // 2. Verify.
            foreach (var step in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = await ReadAsync(sysvol, gpoId, step.Sysvol!.RelativePath!, cancellationToken).ConfigureAwait(false);
                if (!SameContent(current, step.Sysvol.OldContent))
                {
                    await DeleteTempsAsync(sysvol, gpoId, staged).ConfigureAwait(false);
                    return Conflict("A file of the GPO changed after the preview.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            await DeleteTempsAsync(sysvol, gpoId, staged).ConfigureAwait(false);
            return new GpoTransactionResult(GpoTransactionOutcome.Cancelled, DirectoryErrorKind.Cancelled, "Cancelled before the GPO was changed.");
        }
        catch (Exception ex) when (ex is SysvolWriteException or DirectoryException or IOException or UnauthorizedAccessException)
        {
            await DeleteTempsAsync(sysvol, gpoId, staged).ConfigureAwait(false);
            return Failed(GpoTransactionOutcome.Reverted, ex);
        }

        byte[]? gptBefore = null;
        if (version is not null)
        {
            try
            {
                gptBefore = await ReadAsync(sysvol, gpoId, GptIni.RelativePath, cancellationToken).ConfigureAwait(false);
                int? gptVersion = gptBefore is null ? null : GptIni.ReadVersion(gptBefore);
                var directoryVersion = await versions.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (gptVersion != version.OldGptIni?.ToInt32() || directoryVersion != version.OldDirectory?.ToInt32())
                {
                    await DeleteTempsAsync(sysvol, gpoId, staged).ConfigureAwait(false);
                    return Conflict("The version of the GPO changed after the preview.");
                }
            }
            catch (GpoParseException)
            {
                await DeleteTempsAsync(sysvol, gpoId, staged).ConfigureAwait(false);
                return Conflict("GPT.INI of the GPO cannot be read.");
            }
            catch (OperationCanceledException)
            {
                await DeleteTempsAsync(sysvol, gpoId, staged).ConfigureAwait(false);
                return new GpoTransactionResult(GpoTransactionOutcome.Cancelled, DirectoryErrorKind.Cancelled, "Cancelled before the GPO was changed.");
            }
            catch (Exception ex) when (ex is SysvolWriteException or DirectoryException or IOException or UnauthorizedAccessException)
            {
                await DeleteTempsAsync(sysvol, gpoId, staged).ConfigureAwait(false);
                return Failed(GpoTransactionOutcome.Reverted, ex);
            }
        }

        // 3. Switch - from here on the transaction is not cancelled: it commits or compensates.
        var done = new List<PlannedChange>();
        var gptWritten = false;
        try
        {
            foreach (var step in files)
            {
                var path = step.Sysvol!.RelativePath!;
                if (step.Action == ChangeAction.WriteGpoFile)
                {
                    await sysvol.ReplaceAsync(gpoId, TempName(path, run), path, CancellationToken.None).ConfigureAwait(false);
                    staged.Remove(TempName(path, run));
                }
                else
                {
                    await sysvol.DeleteFileAsync(gpoId, path, CancellationToken.None).ConfigureAwait(false);
                }

                done.Add(step);
            }

            // 4. Commit: GPT.INI, then versionNumber (the commit point).
            if (version is not null)
            {
                var newGpt = GptIni.WithVersion(gptBefore ?? GptIni.Initial, version.New.ToInt32());
                var temp = TempName(GptIni.RelativePath, run);
                await sysvol.WriteTempFileAsync(gpoId, temp, newGpt, CancellationToken.None).ConfigureAwait(false);
                staged.Add(temp);
                await sysvol.ReplaceAsync(gpoId, temp, GptIni.RelativePath, CancellationToken.None).ConfigureAwait(false);
                staged.Remove(temp);
                gptWritten = true;
                await versions.WriteAsync(version.OldDirectory?.ToInt32(), version.New.ToInt32(), CancellationToken.None).ConfigureAwait(false);
            }

            return GpoTransactionResult.Committed;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 5. Compensate.
            var restored = await CompensateAsync(sysvol, gpoId, done, gptWritten, gptBefore).ConfigureAwait(false);
            await DeleteTempsAsync(sysvol, gpoId, staged).ConfigureAwait(false);
            if (!restored)
            {
                return Failed(GpoTransactionOutcome.Inconsistent, ex);
            }

            return ex is DirectoryException directory && IsConcurrentChange(directory)
                ? Conflict("The version of the GPO changed while it was written; the file changes were reverted.")
                : Failed(GpoTransactionOutcome.Reverted, ex);
        }
    }

    /// <summary>Puts the old files back (reverse order); false when anything could not be restored.</summary>
    private static async Task<bool> CompensateAsync(ISysvolWriteConnection sysvol, Guid gpoId, List<PlannedChange> done, bool gptWritten, byte[]? gptBefore)
    {
        var run = Guid.NewGuid();
        var ok = true;
        if (gptWritten)
        {
            ok &= await TryRestoreAsync(sysvol, gpoId, GptIni.RelativePath, gptBefore, run).ConfigureAwait(false);
        }

        for (var i = done.Count - 1; i >= 0; i--)
        {
            var change = done[i].Sysvol!;
            ok &= await TryRestoreAsync(sysvol, gpoId, change.RelativePath!, change.OldContent, run).ConfigureAwait(false);
        }

        return ok;
    }

    /// <summary>Writes <paramref name="content"/> back to <paramref name="path"/> (staged and renamed), or deletes the file when it did not exist.</summary>
    private static async Task<bool> TryRestoreAsync(ISysvolWriteConnection sysvol, Guid gpoId, string path, byte[]? content, Guid run)
    {
        try
        {
            if (content is null)
            {
                try
                {
                    await sysvol.DeleteFileAsync(gpoId, path, CancellationToken.None).ConfigureAwait(false);
                }
                catch (SysvolWriteException ex) when (ex.Error == SysvolWriteError.NotFound)
                {
                    // Already gone.
                }

                return true;
            }

            var temp = TempName(path, run);
            await sysvol.WriteTempFileAsync(gpoId, temp, content, CancellationToken.None).ConfigureAwait(false);
            await sysvol.ReplaceAsync(gpoId, temp, path, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static async Task DeleteTempsAsync(ISysvolWriteConnection sysvol, Guid gpoId, List<string> temps)
    {
        foreach (var temp in temps.ToList())
        {
            try
            {
                await sysvol.DeleteFileAsync(gpoId, temp, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A leftover is cleaned by the next write to this GPO (it is listed in that preview).
            }
        }

        temps.Clear();
    }

    /// <summary>The file's content, or null when it does not exist.</summary>
    private static async Task<byte[]?> ReadAsync(ISysvolWriteConnection sysvol, Guid gpoId, string path, CancellationToken cancellationToken)
    {
        var result = await sysvol.ReadGpoFileAsync(gpoId, path, MaxFileBytes, cancellationToken).ConfigureAwait(false);
        return result.Status switch
        {
            SysvolFileStatus.Read => result.Content,
            SysvolFileStatus.NotFound => null,
            SysvolFileStatus.AccessDenied => throw new SysvolWriteException(SysvolWriteError.AccessDenied, "A file of the GPO cannot be read."),
            _ => throw new SysvolWriteException(SysvolWriteError.Failed, "A file of the GPO cannot be read."),
        };
    }

    private static bool SameContent(byte[]? a, byte[]? b) =>
        a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);

    private static bool IsConcurrentChange(DirectoryException error) =>
        error.Kind is DirectoryErrorKind.NoSuchAttribute or DirectoryErrorKind.AttributeOrValueExists or DirectoryErrorKind.ConstraintViolation;

    private static GpoTransactionResult Conflict(string detail) =>
        new(GpoTransactionOutcome.Conflict, DirectoryErrorKind.NoSuchAttribute, BatchExecutor.ConflictMessagePrefix + ". " + detail);

    private static GpoTransactionResult Failed(GpoTransactionOutcome outcome, Exception error)
    {
        var kind = error switch
        {
            SysvolWriteException sysvol => sysvol.Kind,
            DirectoryException directory => directory.Kind,
            UnauthorizedAccessException => DirectoryErrorKind.AccessDenied,
            _ => DirectoryErrorKind.Unknown,
        };
        var prefix = outcome == GpoTransactionOutcome.Inconsistent ? InconsistentMessage : RevertedMessage;
        var detail = error is SysvolWriteException or DirectoryException ? " " + error.Message : string.Empty;
        return new GpoTransactionResult(outcome, kind, prefix + detail);
    }
}
