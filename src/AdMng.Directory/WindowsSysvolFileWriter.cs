using System.Runtime.Versioning;
using System.Security.AccessControl;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;

namespace AdMng.Directory;

/// <summary>
/// <see cref="ISysvolFileWriter"/> of the WPF app (design note docs/dev/k-computers-and-gpo.md 3.2): writes
/// <c>\\&lt;session DC&gt;\SYSVOL\&lt;domain&gt;\Policies\{GUID}\…</c> - the DC of the session, never another one (CLAUDE.md
/// rule 4) - with the same logon as <see cref="WindowsSysvolFileReader"/>: the process identity for a Windows-account
/// session, otherwise a <c>LogonUser(NEW_CREDENTIALS)</c> token of the run's own with the session's in-memory credential
/// (rule 2), every file call impersonating it, the token closed with the connection. Only the batch executor calls it, for
/// the SYSVOL steps of a change plan (rule 5).
/// <list type="bullet">
/// <item>Temp files are created new (<c>FileMode.CreateNew</c>, <c>FileShare.None</c>), flushed to the server
/// (<c>Flush(true)</c>) and closed before <c>File.Move(temp, target, overwrite: true)</c> - an SMB2 rename with
/// ReplaceIfExists in the same folder, atomic on the DC's NTFS.</item>
/// <item>Folder security goes through <c>DirectoryInfo.SetAccessControl</c> with a <c>DirectorySecurity</c> built from the
/// binary form (protected DACL; the server propagates it to the children).</item>
/// <item>Every blocking call runs on the thread pool, never on the UI thread (rule 6). Errors become
/// <see cref="SysvolWriteException"/> without paths or content (rule 1).</item>
/// </list>
/// </summary>
public sealed class WindowsSysvolFileWriter : ISysvolFileWriter
{
    public Task<ISysvolWriteConnection> OpenForWriteAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!OperatingSystem.IsWindows())
        {
            throw new SysvolUnavailableException(SysvolUnavailableReason.NotSupported, "SYSVOL is written through Windows file sharing only.");
        }

        if (!SysvolPaths.TryNormalizeHost(session.Info.Server, out var server) || !SysvolPaths.TryNormalizeHost(session.Info.DomainDnsName, out var domain))
        {
            throw new SysvolUnavailableException(SysvolUnavailableReason.Failed, "The DC or domain name cannot be used in a UNC path.");
        }

        return OpenOnWindows(server, domain, SysvolLogon.Of(session), cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    private static Task<ISysvolWriteConnection> OpenOnWindows(string server, string domain, SysvolLogon logon, CancellationToken cancellationToken) =>
        Task.Run<ISysvolWriteConnection>(() =>
        {
            var share = WindowsSysvol.Share(server);
            return new WindowsSysvol.WriteConnection(server, domain, share, WindowsSysvol.LogOnForShare(share, domain, logon));
        }, cancellationToken);
}

/// <summary>Win32 errors of a SYSVOL write → <see cref="SysvolWriteError"/> (platform-neutral, testable).</summary>
internal static class SysvolWriteErrors
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorHandleDiskFull = 39;
    private const int ErrorFileExists = 80;
    private const int ErrorDiskFull = 112;
    private const int ErrorAlreadyExists = 183;

    internal static SysvolWriteError ErrorOf(int win32) => win32 switch
    {
        ErrorFileNotFound or ErrorPathNotFound => SysvolWriteError.NotFound,
        ErrorAccessDenied => SysvolWriteError.AccessDenied,
        ErrorFileExists or ErrorAlreadyExists => SysvolWriteError.AlreadyExists,
        ErrorHandleDiskFull or ErrorDiskFull => SysvolWriteError.DiskFull,
        _ => SysvolErrors.ReasonOf(win32) switch
        {
            SysvolUnavailableReason.LogonFailure => SysvolWriteError.LogonFailure,
            SysvolUnavailableReason.Unreachable => SysvolWriteError.Unreachable,
            _ => SysvolWriteError.Failed,
        },
    };

    /// <summary>The write error of an exception thrown by a file call (the message names the operation only).</summary>
    internal static SysvolWriteException From(Exception exception, string operation) => exception switch
    {
        SysvolWriteException written => written,
        UnauthorizedAccessException => new SysvolWriteException(SysvolWriteError.AccessDenied, operation + " was denied.", exception),
        FileNotFoundException or DirectoryNotFoundException => new SysvolWriteException(SysvolWriteError.NotFound, operation + " failed: not found.", exception),
        IOException io => new SysvolWriteException(ErrorOf(SysvolErrors.Win32Error(io)), operation + " failed.", exception),
        _ => new SysvolWriteException(SysvolWriteError.Failed, operation + " failed.", exception),
    };
}

internal static partial class WindowsSysvol
{
    /// <summary>A read/write connection: the reads of <see cref="Connection"/> plus the GPO folder writes.</summary>
    [SupportedOSPlatform("windows")]
    internal sealed class WriteConnection(string server, string domain, string share, Microsoft.Win32.SafeHandles.SafeAccessTokenHandle? token)
        : Connection(server, domain, share, token), ISysvolWriteConnection
    {
        public Task<IReadOnlyList<SysvolEntry>> ListGpoFolderAsync(Guid gpoId, SysvolListOptions options, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(options);
            var root = GpoPath(gpoId, null);
            return RunAsync<IReadOnlyList<SysvolEntry>>("Listing the GPO folder", () =>
            {
                var folder = new DirectoryInfo(root);
                if (!folder.Exists)
                {
                    throw new SysvolWriteException(SysvolWriteError.NotFound, "Listing the GPO folder failed: not found.");
                }

                var entries = new List<SysvolEntry>();
                var enumeration = new EnumerationOptions
                {
                    RecurseSubdirectories = options.Recursive,
                    AttributesToSkip = 0,
                    IgnoreInaccessible = false,
                    ReturnSpecialDirectories = false,
                };
                foreach (var info in folder.EnumerateFileSystemInfos("*", enumeration))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(root, info.FullName).Replace('\\', '/');
                    var isFolder = info is DirectoryInfo;
                    entries.Add(new SysvolEntry(relative, isFolder, isFolder ? 0 : ((FileInfo)info).Length, info.LastWriteTimeUtc));
                    if (entries.Count >= options.MaxEntries)
                    {
                        break;
                    }
                }

                return entries;
            }, cancellationToken);
        }

        public Task<bool> GpoFolderExistsAsync(Guid gpoId, CancellationToken cancellationToken)
        {
            var root = GpoPath(gpoId, null);
            return RunAsync("Checking the GPO folder", () => System.IO.Directory.Exists(root), cancellationToken);
        }

        public Task CreateGpoFolderAsync(Guid gpoId, CancellationToken cancellationToken)
        {
            var root = GpoPath(gpoId, null);
            return RunAsync("Creating the GPO folder", () =>
            {
                if (System.IO.Directory.Exists(root))
                {
                    throw new SysvolWriteException(SysvolWriteError.AlreadyExists, "Creating the GPO folder failed: it exists.");
                }

                System.IO.Directory.CreateDirectory(root);
                return true;
            }, cancellationToken);
        }

        public Task CreateFolderAsync(Guid gpoId, string relativeFolder, CancellationToken cancellationToken)
        {
            var root = GpoPath(gpoId, null);
            var path = GpoPath(gpoId, relativeFolder);
            return RunAsync("Creating a folder", () =>
            {
                RequireFolder(root);
                System.IO.Directory.CreateDirectory(path);
                return true;
            }, cancellationToken);
        }

        public Task WriteTempFileAsync(Guid gpoId, string tempRelativePath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            var root = GpoPath(gpoId, null);
            var path = GpoPath(gpoId, tempRelativePath);
            var bytes = content.ToArray();
            return RunAsync("Writing a file", () =>
            {
                RequireFolder(root);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var stream = new FileStream(path, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = 0,
                    PreallocationSize = bytes.Length,
                }))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                return true;
            }, cancellationToken);
        }

        public Task ReplaceAsync(Guid gpoId, string tempRelativePath, string targetRelativePath, CancellationToken cancellationToken)
        {
            var temp = GpoPath(gpoId, tempRelativePath);
            var target = GpoPath(gpoId, targetRelativePath);
            if (!string.Equals(Path.GetDirectoryName(temp), Path.GetDirectoryName(target), StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("A temp file must be in the folder of its target (atomic rename).", nameof(tempRelativePath));
            }

            return RunAsync("Replacing a file", () =>
            {
                File.Move(temp, target, overwrite: true);
                return true;
            }, cancellationToken);
        }

        public Task DeleteFileAsync(Guid gpoId, string relativePath, CancellationToken cancellationToken)
        {
            var path = GpoPath(gpoId, relativePath);
            return RunAsync("Deleting a file", () =>
            {
                if (!File.Exists(path))
                {
                    throw new SysvolWriteException(SysvolWriteError.NotFound, "Deleting a file failed: not found.");
                }

                File.Delete(path);
                return true;
            }, cancellationToken);
        }

        public Task DeleteEmptyFolderAsync(Guid gpoId, string relativeFolder, CancellationToken cancellationToken)
        {
            var path = GpoPath(gpoId, relativeFolder);
            return RunAsync("Deleting a folder", () =>
            {
                System.IO.Directory.Delete(path, recursive: false);
                return true;
            }, cancellationToken);
        }

        public Task DeleteGpoFolderAsync(Guid gpoId, CancellationToken cancellationToken)
        {
            var root = GpoPath(gpoId, null);
            return RunAsync("Deleting the GPO folder", () =>
            {
                RequireFolder(root);
                System.IO.Directory.Delete(root, recursive: true);
                return true;
            }, cancellationToken);
        }

        public Task<byte[]?> GetGpoFolderSecurityAsync(Guid gpoId, CancellationToken cancellationToken)
        {
            var root = GpoPath(gpoId, null);
            return RunAsync<byte[]?>("Reading the folder permissions", () =>
                new DirectoryInfo(root).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm(), cancellationToken);
        }

        public Task SetGpoFolderSecurityAsync(Guid gpoId, byte[] descriptor, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            var root = GpoPath(gpoId, null);
            return RunAsync("Writing the folder permissions", () =>
            {
                var security = new DirectorySecurity();
                security.SetSecurityDescriptorBinaryForm(descriptor, AccessControlSections.Access);
                new DirectoryInfo(root).SetAccessControl(security);
                return true;
            }, cancellationToken);
        }

        private static void RequireFolder(string root)
        {
            if (!System.IO.Directory.Exists(root))
            {
                throw new SysvolWriteException(SysvolWriteError.NotFound, "The GPO folder does not exist.");
            }
        }

        /// <summary>A blocking file call on the thread pool, as the run's logon session; errors as <see cref="SysvolWriteException"/>.</summary>
        private async Task<T> RunAsync<T>(string operation, Func<T> call, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            try
            {
                return await Task.Run(() => AsLogon(call), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SysvolWriteException or System.Security.SecurityException
                or PrivilegeNotHeldException)
            {
                throw SysvolWriteErrors.From(ex, operation);
            }
        }
    }
}
