using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.Ldap;
using Microsoft.Extensions.Logging;

namespace AdMng.Core.Batch;

/// <summary>
/// SYSVOL steps of the executor (design note docs/dev/k-computers-and-gpo.md 3.3): one write connection per run to SYSVOL
/// of the session's DC (CLAUDE.md rule 4), opened at the first SYSVOL step and closed when the run ends; the content steps
/// of an object run as one <see cref="GpoTransaction"/>; the other SYSVOL steps run on their own. SYSVOL writes are not
/// retried (a half-done rename cannot be told from a done one) - the transaction verifies and compensates instead.
/// </summary>
public sealed partial class BatchExecutor
{
    /// <summary>Message of a SYSVOL step when SYSVOL of the session's DC cannot be opened for writing.</summary>
    public const string SysvolUnavailableMessage = "SYSVOL of the session's domain controller cannot be written.";

    /// <summary>Message of a guarded folder delete (rollback of a new GPO) when the folder changed since it was created.</summary>
    public const string FolderChangedMessage = "Conflict: value changed since preview. The GPO folder holds files the plan did not write; it was not deleted.";

    [LoggerMessage(Level = LogLevel.Information, Message = "SYSVOL step {Action} failed: {Error}")]
    private static partial void LogSysvolFailed(ILogger logger, ChangeAction action, string error);

    private sealed partial class Run
    {
        private readonly SemaphoreSlim _sysvolGate = new(1, 1);
        private readonly Dictionary<PlannedChange, StepOutcome> _transactionOutcomes = new(ReferenceEqualityComparer.Instance);
        private readonly Lock _transactionGate = new();
        private ISysvolWriteConnection? _sysvol;
        private Exception? _sysvolFailure;

        private async Task<StepOutcome> SysvolStepAsync(ObjectPlan objectPlan, PlannedChange change, string dn)
        {
            lock (_transactionGate)
            {
                if (_transactionOutcomes.TryGetValue(change, out var decided))
                {
                    return decided;
                }
            }

            if ((change.Sysvol?.GpoId ?? GpoIdOf(dn)) is not { } gpoId || gpoId == Guid.Empty)
            {
                return Invalid(change, "The SYSVOL step names no GPO.");
            }

            var sysvol = await OpenSysvolAsync().ConfigureAwait(false);
            if (sysvol is null)
            {
                return _cancellationToken.IsCancellationRequested && _sysvolFailure is null
                    ? Failure(change, DirectoryErrorKind.Cancelled, "Cancelled before SYSVOL was opened.", cancelled: true)
                    : Failure(change, _sysvolFailure is SysvolUnavailableException unavailable
                        ? new SysvolWriteException(SysvolWriteException.FromUnavailable(unavailable.Reason), SysvolUnavailableMessage).Kind
                        : DirectoryErrorKind.Unknown, SysvolUnavailableMessage);
            }

            try
            {
                switch (change.Action)
                {
                    case ChangeAction.WriteGpoFile or ChangeAction.DeleteGpoFile or ChangeAction.SetGpoVersion:
                        return await TransactionAsync(objectPlan, change, dn, sysvol, gpoId).ConfigureAwait(false);
                    case ChangeAction.CreateGpoFolder:
                        await CreateGpoFolderAsync(sysvol, gpoId).ConfigureAwait(false);
                        return Success(change);
                    case ChangeAction.SyncGpoFolderSecurity:
                        var descriptor = await ReadContainerDescriptorAsync(dn).ConfigureAwait(false);
                        if (GpoFolderAcl.FromDirectoryDescriptor(descriptor) is not { } folderDescriptor)
                        {
                            return Failure(change, DirectoryErrorKind.Unknown, "The security descriptor of the GPO container cannot be read.");
                        }

                        await sysvol.SetGpoFolderSecurityAsync(gpoId, folderDescriptor, CancellationToken.None).ConfigureAwait(false);
                        return Success(change);
                    case ChangeAction.SetGpoFolderSecurity:
                        if (change.Sysvol?.NewFolderSecurity is not { } newDescriptor)
                        {
                            return Invalid(change, "The folder security step has no descriptor.");
                        }

                        await sysvol.SetGpoFolderSecurityAsync(gpoId, newDescriptor, CancellationToken.None).ConfigureAwait(false);
                        return Success(change);
                    case ChangeAction.DeleteGpoFolder:
                        if (change.Sysvol is { OnlyIfPristine: true } guard && !await IsPristineAsync(sysvol, gpoId, guard).ConfigureAwait(false))
                        {
                            return Failure(change, DirectoryErrorKind.NoSuchAttribute, FolderChangedMessage);
                        }

                        try
                        {
                            await sysvol.DeleteGpoFolderAsync(gpoId, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (SysvolWriteException ex) when (ex.Error == SysvolWriteError.NotFound)
                        {
                            // Already gone (a DFSR problem, the AD-only remnant of a failed create): the step's goal holds.
                        }

                        return Success(change);
                    default:
                        return Invalid(change, $"Unsupported action {change.Action}.");
                }
            }
            catch (SysvolWriteException ex)
            {
                LogFailure(change.Action, ex.Error.ToString());
                return Failure(change, ex.Kind, ex.Message);
            }
            catch (DirectoryException ex)
            {
                LogFailure(change.Action, ex.Kind.ToString());
                return Failure(change, ex.Kind, ex.Message);
            }
            catch (OperationCanceledException)
            {
                return Failure(change, DirectoryErrorKind.Cancelled, "Cancelled.", cancelled: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogFailure(change.Action, ex.GetType().Name);
                return Failure(change, ex is UnauthorizedAccessException ? DirectoryErrorKind.AccessDenied : DirectoryErrorKind.Unknown, "SYSVOL write failed.");
            }
        }

        /// <summary>Runs the content steps from <paramref name="change"/> on (the contiguous run of them) as one transaction.</summary>
        private async Task<StepOutcome> TransactionAsync(ObjectPlan objectPlan, PlannedChange change, string dn, ISysvolWriteConnection sysvol, Guid gpoId)
        {
            var changes = objectPlan.Changes;
            var start = -1;
            for (var i = 0; i < changes.Count; i++)
            {
                if (ReferenceEquals(changes[i], change))
                {
                    start = i;
                    break;
                }
            }

            var group = new List<PlannedChange>();
            for (var i = Math.Max(start, 0); i < changes.Count && GpoTransaction.IsContentStep(changes[i].Action); i++)
            {
                group.Add(changes[i]);
            }

            if (start < 0 || group.Count == 0)
            {
                group = [change];
            }

            var result = await GpoTransaction.RunAsync(sysvol, gpoId, group, new VersionStore(this, dn), _cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded && _owner._logger is { } logger)
            {
                LogSysvolFailed(logger, change.Action, result.Outcome.ToString());
            }

            var outcome = result.Succeeded
                ? Success(change)
                : Failure(change, result.ErrorKind ?? DirectoryErrorKind.Unknown, result.Message ?? GpoTransaction.RevertedMessage,
                    cancelled: result.Outcome == GpoTransactionOutcome.Cancelled);
            lock (_transactionGate)
            {
                foreach (var step in group.Skip(1))
                {
                    _transactionOutcomes[step] = outcome with { Result = outcome.Result with { Change = step } };
                }
            }

            return outcome;
        }

        /// <summary>The folder, Machine, User and GPT.INI version 0; a failure removes what was created.</summary>
        private static async Task CreateGpoFolderAsync(ISysvolWriteConnection sysvol, Guid gpoId)
        {
            await sysvol.CreateGpoFolderAsync(gpoId, CancellationToken.None).ConfigureAwait(false);
            try
            {
                await sysvol.CreateFolderAsync(gpoId, "Machine", CancellationToken.None).ConfigureAwait(false);
                await sysvol.CreateFolderAsync(gpoId, "User", CancellationToken.None).ConfigureAwait(false);
                var temp = GpoTransaction.TempName(GptIni.RelativePath, Guid.NewGuid());
                await sysvol.WriteTempFileAsync(gpoId, temp, GptIni.Initial, CancellationToken.None).ConfigureAwait(false);
                await sysvol.ReplaceAsync(gpoId, temp, GptIni.RelativePath, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    await sysvol.DeleteGpoFolderAsync(gpoId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception cleanup) when (cleanup is not OutOfMemoryException)
                {
                    // The folder stays; the next attempt reports it as existing (the GPO's Stan tab shows it).
                }

                throw;
            }
        }

        /// <summary>
        /// The guard of a rollback's folder delete: the folder holds only Machine, User, GPT.INI with the version the plan
        /// wrote and the files the plan wrote (same content) - nothing anybody added since.
        /// </summary>
        private static async Task<bool> IsPristineAsync(ISysvolWriteConnection sysvol, Guid gpoId, SysvolChange guard)
        {
            var expected = guard.PristineFiles ?? new Dictionary<string, byte[]>();
            var entries = await sysvol.ListGpoFolderAsync(gpoId, SysvolListOptions.Default, CancellationToken.None).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                if (entry.IsFolder)
                {
                    continue;
                }

                if (string.Equals(entry.RelativePath, GptIni.RelativePath, StringComparison.OrdinalIgnoreCase))
                {
                    var gpt = await sysvol.ReadGpoFileAsync(gpoId, GptIni.RelativePath, GpoTransaction.MaxFileBytes, CancellationToken.None).ConfigureAwait(false);
                    int? version;
                    try
                    {
                        version = gpt.Content is null ? null : GptIni.ReadVersion(gpt.Content);
                    }
                    catch (GpoParseException)
                    {
                        return false;
                    }

                    if ((version ?? 0) != (guard.PristineVersion ?? GpoVersion.Zero).ToInt32())
                    {
                        return false;
                    }

                    continue;
                }

                var path = expected.Keys.FirstOrDefault(k => string.Equals(k, entry.RelativePath, StringComparison.OrdinalIgnoreCase));
                if (path is null)
                {
                    return false;
                }

                var file = await sysvol.ReadGpoFileAsync(gpoId, entry.RelativePath, GpoTransaction.MaxFileBytes, CancellationToken.None).ConfigureAwait(false);
                if (file.Content is null || !file.Content.AsSpan().SequenceEqual(expected[path]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>nTSecurityDescriptor of the container now (DACL, read with the SD flags control).</summary>
        private async Task<byte[]?> ReadContainerDescriptorAsync(string dn)
        {
            await foreach (var entry in _session.SearchAsync(new DirectorySearch
            {
                BaseDn = dn,
                Scope = SearchScope.Base,
                Attributes = [Attr.NtSecurityDescriptor],
                ReadSecurityDescriptor = true,
            }, CancellationToken.None).ConfigureAwait(false))
            {
                return entry.GetBytes(Attr.NtSecurityDescriptor);
            }

            return null;
        }

        private async Task<ISysvolWriteConnection?> OpenSysvolAsync()
        {
            await _sysvolGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (_sysvol is not null || _sysvolFailure is not null)
                {
                    return _sysvol;
                }

                if (_owner._sysvolWriter is not { } writer)
                {
                    _sysvolFailure = new SysvolUnavailableException(SysvolUnavailableReason.NotSupported, SysvolUnavailableMessage);
                    return null;
                }

                try
                {
                    _sysvol = await writer.OpenForWriteAsync(_session, _cancellationToken).ConfigureAwait(false);
                }
                catch (SysvolUnavailableException ex)
                {
                    LogFailure(ChangeAction.CreateGpoFolder, ex.Reason.ToString());
                    _sysvolFailure = ex;
                }
                catch (OperationCanceledException)
                {
                    // Reported as cancelled by the step.
                }

                return _sysvol;
            }
            finally
            {
                _sysvolGate.Release();
            }
        }

        private async Task CloseSysvolAsync()
        {
            if (_sysvol is { } sysvol)
            {
                _sysvol = null;
                await sysvol.DisposeAsync().ConfigureAwait(false);
            }
        }

        private void LogFailure(ChangeAction action, string error)
        {
            if (_owner._logger is { } logger)
            {
                LogSysvolFailed(logger, action, error);
            }
        }

        /// <summary>The GPO's GUID from the cn of a group policy container DN.</summary>
        private static Guid? GpoIdOf(string dn) =>
            DistinguishedName.TryParse(dn, out var parsed) && !parsed.IsRoot && GroupPolicyLinks.TryParseGpoId(parsed.Name, out var id) ? id : null;

        private static StepOutcome Success(PlannedChange change) => new(new ChangeResult(change, true), null, false);

        private static StepOutcome Failure(PlannedChange change, DirectoryErrorKind kind, string message, bool cancelled = false) =>
            new(new ChangeResult(change, false, kind, message), null, cancelled);

        /// <summary>
        /// Audit values of a SYSVOL step: <c>sysvol:&lt;path&gt;</c> with <c>sha256:&lt;hex&gt; (&lt;n&gt; B)</c> of the old and new
        /// content - never content; the version step as <c>versionNumber</c> old → new.
        /// </summary>
        private static IEnumerable<(string? Attribute, string? OldValue, string? NewValue)> SysvolAuditValues(PlannedChange change)
        {
            var sysvol = change.Sysvol;
            var folder = sysvol is null ? null : "sysvol:" + SysvolPaths.FolderName(sysvol.GpoId);
            switch (change.Action)
            {
                case ChangeAction.WriteGpoFile or ChangeAction.DeleteGpoFile when sysvol is not null:
                    yield return ("sysvol:" + sysvol.RelativePath, Fingerprint(sysvol.OldContent),
                        change.Action == ChangeAction.DeleteGpoFile ? null : Fingerprint(sysvol.NewContent));
                    break;
                case ChangeAction.SetGpoVersion when change.GpoVersion is { } version:
                    yield return (GroupPolicyAttributes.VersionNumber, version.OldDirectory?.ToInt32().ToString(CultureInfo.InvariantCulture),
                        version.New.ToInt32().ToString(CultureInfo.InvariantCulture));
                    break;
                case ChangeAction.SyncGpoFolderSecurity or ChangeAction.SetGpoFolderSecurity:
                    yield return (folder + " (security)", Fingerprint(sysvol?.OldFolderSecurity), Fingerprint(sysvol?.NewFolderSecurity));
                    break;
                default:
                    yield return (folder, null, null);
                    break;
            }
        }

        /// <summary><c>sha256:&lt;hex&gt; (&lt;n&gt; B)</c>, null for absent content.</summary>
        private static string? Fingerprint(byte[]? content) => content is null
            ? null
            : string.Create(CultureInfo.InvariantCulture, $"sha256:{Convert.ToHexStringLower(SHA256.HashData(content))} ({content.Length} B)");

        /// <summary><c>versionNumber</c> of one container, through the executor's retries.</summary>
        private sealed class VersionStore(Run run, string dn) : IGpoVersionStore
        {
            public async Task<int?> ReadAsync(CancellationToken cancellationToken)
            {
                var gpc = await run._session.GetAsync(dn, [GroupPolicyAttributes.VersionNumber], cancellationToken).ConfigureAwait(false)
                    ?? throw new DirectoryException(DirectoryErrorKind.NoSuchObject, "The group policy container does not exist.");
                return gpc.GetInt32(GroupPolicyAttributes.VersionNumber);
            }

            public async Task WriteAsync(int? oldValue, int newValue, CancellationToken cancellationToken)
            {
                var modifications = new List<AttributeModification>();
                if (oldValue is { } old)
                {
                    modifications.Add(AttributeModification.Delete(GroupPolicyAttributes.VersionNumber, old.ToString(CultureInfo.InvariantCulture)));
                }

                modifications.Add(AttributeModification.Add(GroupPolicyAttributes.VersionNumber, newValue.ToString(CultureInfo.InvariantCulture)));
                var attempted = await run.InvokeAsync(ChangeAction.SetGpoVersion, async () =>
                {
                    await run._session.ModifyAsync(dn, modifications, ModifyOptions.Default, CancellationToken.None).ConfigureAwait(false);
                    return true;
                }).ConfigureAwait(false);
                if (attempted.Error is null)
                {
                    return;
                }

                // A conflict on a retry may be the value written by the attempt that timed out.
                if (attempted.Attempts > 1 && attempted.Error is DirectoryException retried && IsConcurrentChange(retried))
                {
                    try
                    {
                        if (await ReadAsync(CancellationToken.None).ConfigureAwait(false) == newValue)
                        {
                            return;
                        }
                    }
                    catch (DirectoryException)
                    {
                        // Keep the original error.
                    }
                }

                ExceptionDispatchInfo.Capture(attempted.Error).Throw();
            }
        }
    }
}
