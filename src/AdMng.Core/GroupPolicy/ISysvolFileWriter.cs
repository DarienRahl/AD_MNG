using AdMng.Core.Directory;

namespace AdMng.Core.GroupPolicy;

/// <summary>
/// Writes GPO folders on the SYSVOL share of the session's domain controller (CLAUDE.md rule 4: the DC of the session,
/// never another one) for the change pipeline (rule 5, design note docs/dev/k-computers-and-gpo.md 3.2). Writes happen only
/// as steps of a <see cref="Batch.ChangePlan"/> executed by the batch executor; nothing else in the application writes
/// SYSVOL. The Windows implementation (AdMng.Directory) logs on like <see cref="ISysvolFileReader"/> - the process identity,
/// or the session's in-memory credential in a logon session of the run's own (rule 2).
/// </summary>
public interface ISysvolFileWriter
{
    /// <summary>SYSVOL of the session's DC opened for reading and writing; dispose the result to release the logon.</summary>
    /// <exception cref="SysvolUnavailableException">SYSVOL cannot be opened with this session (the reason says why).</exception>
    Task<ISysvolWriteConnection> OpenForWriteAsync(IDirectorySession session, CancellationToken cancellationToken);
}

/// <summary>
/// SYSVOL of one DC, opened by <see cref="ISysvolFileWriter.OpenForWriteAsync"/>. Every relative path is validated by
/// <see cref="SysvolPaths.TryNormalize"/> and every GPO folder is <c>&lt;domain&gt;\Policies\{GUID}</c> named by
/// <see cref="SysvolPaths.FolderName"/>. Failures throw <see cref="SysvolWriteException"/>, whose message never contains a
/// path or content (rule 1: diagnostics stay value-free).
/// </summary>
public interface ISysvolWriteConnection : ISysvolConnection
{
    /// <summary>The files and folders of a GPO folder (paths relative to it, '/'-separated).</summary>
    Task<IReadOnlyList<SysvolEntry>> ListGpoFolderAsync(Guid gpoId, SysvolListOptions options, CancellationToken cancellationToken);

    Task<bool> GpoFolderExistsAsync(Guid gpoId, CancellationToken cancellationToken);

    /// <summary>Creates the <c>{GUID}</c> folder (only the folder); fails with <see cref="SysvolWriteError.AlreadyExists"/> when it exists.</summary>
    Task CreateGpoFolderAsync(Guid gpoId, CancellationToken cancellationToken);

    /// <summary>Creates a folder below the GPO folder (and its missing parents); an existing folder is no error.</summary>
    Task CreateFolderAsync(Guid gpoId, string relativeFolder, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a new file (it must not exist) with the content, flushed to the server, in the folder of its future target -
    /// missing parent folders are created. Used to stage content before <see cref="ReplaceAsync"/>.
    /// </summary>
    Task WriteTempFileAsync(Guid gpoId, string tempRelativePath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);

    /// <summary>Renames the temp file over the target (an existing target is replaced; same folder, so the rename is atomic).</summary>
    Task ReplaceAsync(Guid gpoId, string tempRelativePath, string targetRelativePath, CancellationToken cancellationToken);

    /// <summary>Deletes a file; a missing file is <see cref="SysvolWriteError.NotFound"/>.</summary>
    Task DeleteFileAsync(Guid gpoId, string relativePath, CancellationToken cancellationToken);

    /// <summary>Deletes an empty folder below the GPO folder.</summary>
    Task DeleteEmptyFolderAsync(Guid gpoId, string relativeFolder, CancellationToken cancellationToken);

    /// <summary>Deletes the whole <c>{GUID}</c> folder with everything in it.</summary>
    Task DeleteGpoFolderAsync(Guid gpoId, CancellationToken cancellationToken);

    /// <summary>The folder's security descriptor (self-relative, DACL only), null when it cannot be read.</summary>
    Task<byte[]?> GetGpoFolderSecurityAsync(Guid gpoId, CancellationToken cancellationToken);

    /// <summary>Writes the folder's DACL (protected, propagated to its children by the server).</summary>
    Task SetGpoFolderSecurityAsync(Guid gpoId, byte[] descriptor, CancellationToken cancellationToken);
}

/// <summary>A file or folder of a GPO folder.</summary>
/// <param name="RelativePath">'/'-separated path below the GPO folder.</param>
/// <param name="IsFolder">A folder.</param>
/// <param name="Length">Size in bytes (0 for folders).</param>
/// <param name="LastWrite">Last write time.</param>
public sealed record SysvolEntry(string RelativePath, bool IsFolder, long Length, DateTimeOffset LastWrite);

/// <summary>Options of <see cref="ISysvolWriteConnection.ListGpoFolderAsync"/>.</summary>
public sealed record SysvolListOptions
{
    /// <summary>Default: the whole tree, at most <see cref="Management.GpoTransaction.MaxEntriesPerGpo"/> + 1 entries.</summary>
    public static SysvolListOptions Default { get; } = new();

    /// <summary>List the subfolders too.</summary>
    public bool Recursive { get; init; } = true;

    /// <summary>Stop after this many entries (the caller sees more than its limit and refuses the GPO).</summary>
    public int MaxEntries { get; init; } = Management.GpoTransaction.MaxEntriesPerGpo + 1;
}

/// <summary>Why a SYSVOL write failed.</summary>
public enum SysvolWriteError
{
    AccessDenied,
    NotFound,
    AlreadyExists,
    Unreachable,
    LogonFailure,
    DiskFull,
    Failed,
}

/// <summary>A SYSVOL write failed (<see cref="Error"/>); the message names the operation only, never a path or content.</summary>
public sealed class SysvolWriteException : Exception
{
    public SysvolWriteException()
    {
    }

    public SysvolWriteException(string message)
        : base(message)
    {
    }

    public SysvolWriteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SysvolWriteException(SysvolWriteError error, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
    }

    public SysvolWriteError Error { get; } = SysvolWriteError.Failed;

    /// <summary>The directory error kind the pipeline reports for <see cref="Error"/>.</summary>
    public DirectoryErrorKind Kind => Error switch
    {
        SysvolWriteError.AccessDenied => DirectoryErrorKind.AccessDenied,
        SysvolWriteError.NotFound => DirectoryErrorKind.NoSuchObject,
        SysvolWriteError.AlreadyExists => DirectoryErrorKind.AlreadyExists,
        SysvolWriteError.Unreachable => DirectoryErrorKind.ServerUnavailable,
        SysvolWriteError.LogonFailure => DirectoryErrorKind.InvalidCredentials,
        SysvolWriteError.DiskFull => DirectoryErrorKind.UnwillingToPerform,
        _ => DirectoryErrorKind.Unknown,
    };

    /// <summary>The write error of an unavailable share.</summary>
    public static SysvolWriteError FromUnavailable(SysvolUnavailableReason reason) => reason switch
    {
        SysvolUnavailableReason.AccessDenied => SysvolWriteError.AccessDenied,
        SysvolUnavailableReason.LogonFailure => SysvolWriteError.LogonFailure,
        SysvolUnavailableReason.Unreachable => SysvolWriteError.Unreachable,
        _ => SysvolWriteError.Failed,
    };
}
