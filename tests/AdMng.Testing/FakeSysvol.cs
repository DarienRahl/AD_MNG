using System.Text;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;

namespace AdMng.Testing;

/// <summary>One call to <see cref="FakeSysvol"/>.</summary>
/// <param name="Server">The DC (session server) the connection was opened on.</param>
/// <param name="Operation">Name of the method without "Async" (Read, List, CreateGpoFolder, WriteTemp, Replace, DeleteFile …).</param>
/// <param name="GpoId">The GPO.</param>
/// <param name="Path">The relative path (the target of a replace), or null.</param>
public sealed record FakeSysvolCall(string Server, string Operation, Guid GpoId, string? Path);

/// <summary>
/// A writable in-memory SYSVOL for the GPO management tests (K2, design note docs/dev/k-computers-and-gpo.md 10): files and
/// folders per GPO (case-insensitive, as SMB), folder descriptors, every call recorded with the DC it went to, failures of
/// any operation and a hook that runs before each call (to change things behind a plan's back). Implements the reader and
/// the writer, so the same instance serves the privileged access review, the GPO views and the executor. Nothing touches
/// the disk.
/// </summary>
public sealed class FakeSysvol : ISysvolFileReader, ISysvolFileWriter
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, byte[]> _security = [];
    private readonly List<FakeSysvolCall> _calls = [];
    private readonly List<(string Operation, string? PathPart, SysvolWriteError Error, int Remaining)> _failures = [];
    private int _open;

    /// <summary>When set, opening fails with this reason.</summary>
    public SysvolUnavailableReason? Unavailable { get; set; }

    /// <summary>Runs before every call (operation, GPO, path) - e.g. to change a file or the version behind the plan's back.</summary>
    public Action<string, Guid, string?>? BeforeCall { get; set; }

    /// <summary>Last write time reported by listings.</summary>
    public DateTimeOffset Now { get; set; } = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Every call in order.</summary>
    public IReadOnlyList<FakeSysvolCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>The DCs connections were opened on.</summary>
    public IReadOnlyList<string> OpenedOn => [.. Calls.Where(c => c.Operation == "Open").Select(c => c.Server)];

    /// <summary>Connections opened and not yet disposed.</summary>
    public int OpenConnections => Volatile.Read(ref _open);

    /// <summary>The write calls (everything but Open, Read, List, Exists and GetSecurity).</summary>
    public IReadOnlyList<FakeSysvolCall> Writes =>
        [.. Calls.Where(c => c.Operation is not ("Open" or "Read" or "List" or "Exists" or "GetSecurity"))];

    /// <summary>Creates a GPO folder as <c>samba-tool gpo create</c> does: Machine, User and GPT.INI version 0.</summary>
    public void AddGpoFolder(Guid gpoId, int version = 0)
    {
        lock (_gate)
        {
            var root = SysvolPaths.FolderName(gpoId);
            _folders.Add(root);
            _folders.Add(root + "/Machine");
            _folders.Add(root + "/User");
            _files[root + "/GPT.INI"] = Encoding.ASCII.GetBytes($"[General]\r\nVersion={version}\r\n");
        }
    }

    /// <summary>Stores a file (its folders are created).</summary>
    public void Put(Guid gpoId, string relativePath, byte[] content)
    {
        lock (_gate)
        {
            var key = Key(gpoId, relativePath);
            AddParents(key);
            _files[key] = [.. content];
        }
    }

    /// <summary>Stores a text file (UTF-8 without BOM).</summary>
    public void PutText(Guid gpoId, string relativePath, string text) => Put(gpoId, relativePath, Encoding.UTF8.GetBytes(text));

    /// <summary>The file's content, or null.</summary>
    public byte[]? Get(Guid gpoId, string relativePath)
    {
        lock (_gate)
        {
            return _files.TryGetValue(Key(gpoId, relativePath), out var content) ? [.. content] : null;
        }
    }

    /// <summary>The file as text (UTF-8 / ASCII), or null.</summary>
    public string? GetText(Guid gpoId, string relativePath) => Get(gpoId, relativePath) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    /// <summary>The paths of a GPO folder's files ('/'-separated, relative), sorted.</summary>
    public IReadOnlyList<string> Files(Guid gpoId)
    {
        lock (_gate)
        {
            var prefix = SysvolPaths.FolderName(gpoId) + "/";
            return [.. _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(k => k[prefix.Length..]).Order(StringComparer.OrdinalIgnoreCase)];
        }
    }

    public bool FolderExists(Guid gpoId)
    {
        lock (_gate)
        {
            return _folders.Contains(SysvolPaths.FolderName(gpoId));
        }
    }

    public byte[]? FolderSecurity(Guid gpoId)
    {
        lock (_gate)
        {
            return _security.TryGetValue(gpoId, out var sd) ? [.. sd] : null;
        }
    }

    public void SetFolderSecurity(Guid gpoId, byte[] descriptor)
    {
        lock (_gate)
        {
            _security[gpoId] = [.. descriptor];
        }
    }

    /// <summary>The next <paramref name="times"/> calls of <paramref name="operation"/> (whose path contains <paramref name="pathPart"/>) fail.</summary>
    public void Fail(string operation, SysvolWriteError error, string? pathPart = null, int times = 1)
    {
        lock (_gate)
        {
            _failures.Add((operation, pathPart, error, times));
        }
    }

    public void ClearCalls()
    {
        lock (_gate)
        {
            _calls.Clear();
        }
    }

    public Task<ISysvolConnection> OpenAsync(IDirectorySession session, CancellationToken cancellationToken) =>
        OpenCoreAsync<ISysvolConnection>(session, cancellationToken);

    public Task<ISysvolWriteConnection> OpenForWriteAsync(IDirectorySession session, CancellationToken cancellationToken) =>
        OpenCoreAsync<ISysvolWriteConnection>(session, cancellationToken);

    private Task<T> OpenCoreAsync<T>(IDirectorySession session, CancellationToken cancellationToken)
        where T : class, ISysvolConnection
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        if (Unavailable is { } reason)
        {
            throw new SysvolUnavailableException(reason, "SYSVOL is not available (test).");
        }

        lock (_gate)
        {
            _calls.Add(new FakeSysvolCall(session.Info.Server, "Open", Guid.Empty, null));
        }

        Interlocked.Increment(ref _open);
        return Task.FromResult((T)(object)new Connection(this, session.Info.Server));
    }

    private static string Key(Guid gpoId, string relativePath)
    {
        if (!SysvolPaths.TryNormalize(relativePath, out var path))
        {
            throw new ArgumentException("Invalid relative path.", nameof(relativePath));
        }

        return SysvolPaths.FolderName(gpoId) + "/" + path;
    }

    private void AddParents(string key)
    {
        for (var slash = key.LastIndexOf('/'); slash > 0; slash = key.LastIndexOf('/', slash - 1))
        {
            _folders.Add(key[..slash]);
        }
    }

    private void Enter(string server, string operation, Guid gpoId, string? path)
    {
        BeforeCall?.Invoke(operation, gpoId, path);
        lock (_gate)
        {
            _calls.Add(new FakeSysvolCall(server, operation, gpoId, path));
            for (var i = 0; i < _failures.Count; i++)
            {
                var failure = _failures[i];
                if (failure.Operation == operation && (failure.PathPart is null || (path?.Contains(failure.PathPart, StringComparison.OrdinalIgnoreCase) ?? false)))
                {
                    if (failure.Remaining <= 1)
                    {
                        _failures.RemoveAt(i);
                    }
                    else
                    {
                        _failures[i] = failure with { Remaining = failure.Remaining - 1 };
                    }

                    throw new SysvolWriteException(failure.Error, $"{operation} failed (test).");
                }
            }
        }
    }

    private sealed class Connection(FakeSysvol owner, string server) : ISysvolWriteConnection
    {
        private int _disposed;

        public string Server { get; } = server;

        public Task<SysvolFileResult> ReadGpoFileAsync(Guid gpoId, string relativePath, long maxBytes, CancellationToken cancellationToken)
        {
            Check(cancellationToken);
            var key = Key(gpoId, relativePath);
            try
            {
                owner.Enter(Server, "Read", gpoId, relativePath);
            }
            catch (SysvolWriteException ex)
            {
                return Task.FromResult(ex.Error == SysvolWriteError.AccessDenied ? SysvolFileResult.AccessDenied : SysvolFileResult.Failed);
            }

            lock (owner._gate)
            {
                if (!owner._files.TryGetValue(key, out var content))
                {
                    return Task.FromResult(SysvolFileResult.NotFound);
                }

                return Task.FromResult(content.Length > maxBytes ? SysvolFileResult.TooLarge : SysvolFileResult.Of([.. content]));
            }
        }

        public Task<IReadOnlyList<SysvolEntry>> ListGpoFolderAsync(Guid gpoId, SysvolListOptions options, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(options);
            Check(cancellationToken);
            owner.Enter(Server, "List", gpoId, null);
            lock (owner._gate)
            {
                var root = SysvolPaths.FolderName(gpoId);
                if (!owner._folders.Contains(root))
                {
                    throw new SysvolWriteException(SysvolWriteError.NotFound, "The GPO folder does not exist (test).");
                }

                var prefix = root + "/";
                var entries = owner._folders.Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Select(f => new SysvolEntry(f[prefix.Length..], true, 0, owner.Now))
                    .Concat(owner._files.Where(f => f.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        .Select(f => new SysvolEntry(f.Key[prefix.Length..], false, f.Value.Length, owner.Now)))
                    .Where(e => options.Recursive || !e.RelativePath.Contains('/'))
                    .OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .Take(options.MaxEntries)
                    .ToList();
                return Task.FromResult<IReadOnlyList<SysvolEntry>>(entries);
            }
        }

        public Task<bool> GpoFolderExistsAsync(Guid gpoId, CancellationToken cancellationToken)
        {
            Check(cancellationToken);
            owner.Enter(Server, "Exists", gpoId, null);
            return Task.FromResult(owner.FolderExists(gpoId));
        }

        public Task CreateGpoFolderAsync(Guid gpoId, CancellationToken cancellationToken)
        {
            Check(cancellationToken);
            owner.Enter(Server, "CreateGpoFolder", gpoId, null);
            lock (owner._gate)
            {
                if (!owner._folders.Add(SysvolPaths.FolderName(gpoId)))
                {
                    throw new SysvolWriteException(SysvolWriteError.AlreadyExists, "The GPO folder exists (test).");
                }
            }

            return Task.CompletedTask;
        }

        public Task CreateFolderAsync(Guid gpoId, string relativeFolder, CancellationToken cancellationToken)
        {
            Check(cancellationToken);
            var key = Key(gpoId, relativeFolder);
            owner.Enter(Server, "CreateFolder", gpoId, relativeFolder);
            lock (owner._gate)
            {
                RequireRoot(gpoId);
                owner.AddParents(key);
                owner._folders.Add(key);
            }

            return Task.CompletedTask;
        }

        public Task WriteTempFileAsync(Guid gpoId, string tempRelativePath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            Check(cancellationToken);
            var key = Key(gpoId, tempRelativePath);
            owner.Enter(Server, "WriteTemp", gpoId, tempRelativePath);
            lock (owner._gate)
            {
                RequireRoot(gpoId);
                if (owner._files.ContainsKey(key))
                {
                    throw new SysvolWriteException(SysvolWriteError.AlreadyExists, "The temp file exists (test).");
                }

                owner.AddParents(key);
                owner._files[key] = content.ToArray();
            }

            return Task.CompletedTask;
        }

        public Task ReplaceAsync(Guid gpoId, string tempRelativePath, string targetRelativePath, CancellationToken cancellationToken)
        {
            Check(cancellationToken);
            var temp = Key(gpoId, tempRelativePath);
            var target = Key(gpoId, targetRelativePath);
            owner.Enter(Server, "Replace", gpoId, targetRelativePath);
            lock (owner._gate)
            {
                if (!owner._files.Remove(temp, out var content))
                {
                    throw new SysvolWriteException(SysvolWriteError.NotFound, "The temp file does not exist (test).");
                }

                owner._files[target] = content;
            }

            return Task.CompletedTask;
        }

        public Task DeleteFileAsync(Guid gpoId, string relativePath, CancellationToken cancellationToken)
        {
            Check(cancellationToken);
            var key = Key(gpoId, relativePath);
            owner.Enter(Server, "DeleteFile", gpoId, relativePath);
            lock (owner._gate)
            {
                if (!owner._files.Remove(key))
                {
                    throw new SysvolWriteException(SysvolWriteError.NotFound, "The file does not exist (test).");
                }
            }

            return Task.CompletedTask;
        }

        public Task DeleteEmptyFolderAsync(Guid gpoId, string relativeFolder, CancellationToken cancellationToken)
        {
            Check(cancellationToken);
            var key = Key(gpoId, relativeFolder);
            owner.Enter(Server, "DeleteFolder", gpoId, relativeFolder);
            lock (owner._gate)
            {
                if (owner._files.Keys.Concat(owner._folders).Any(k => k.StartsWith(key + "/", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new SysvolWriteException(SysvolWriteError.Failed, "The folder is not empty (test).");
                }

                if (!owner._folders.Remove(key))
                {
                    throw new SysvolWriteException(SysvolWriteError.NotFound, "The folder does not exist (test).");
                }
            }

            return Task.CompletedTask;
        }

        public Task DeleteGpoFolderAsync(Guid gpoId, CancellationToken cancellationToken)
        {
            Check(cancellationToken);
            owner.Enter(Server, "DeleteGpoFolder", gpoId, null);
            lock (owner._gate)
            {
                var root = SysvolPaths.FolderName(gpoId);
                if (!owner._folders.Contains(root))
                {
                    throw new SysvolWriteException(SysvolWriteError.NotFound, "The GPO folder does not exist (test).");
                }

                owner._folders.RemoveWhere(f => f.Equals(root, StringComparison.OrdinalIgnoreCase) || f.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase));
                foreach (var file in owner._files.Keys.Where(k => k.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    owner._files.Remove(file);
                }

                owner._security.Remove(gpoId);
            }

            return Task.CompletedTask;
        }

        public Task<byte[]?> GetGpoFolderSecurityAsync(Guid gpoId, CancellationToken cancellationToken)
        {
            Check(cancellationToken);
            owner.Enter(Server, "GetSecurity", gpoId, null);
            return Task.FromResult(owner.FolderSecurity(gpoId));
        }

        public Task SetGpoFolderSecurityAsync(Guid gpoId, byte[] descriptor, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            Check(cancellationToken);
            owner.Enter(Server, "SetSecurity", gpoId, null);
            lock (owner._gate)
            {
                RequireRoot(gpoId);
                owner._security[gpoId] = [.. descriptor];
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref owner._open);
            }

            return ValueTask.CompletedTask;
        }

        private void RequireRoot(Guid gpoId)
        {
            if (!owner._folders.Contains(SysvolPaths.FolderName(gpoId)))
            {
                throw new SysvolWriteException(SysvolWriteError.NotFound, "The GPO folder does not exist (test).");
            }
        }

        private void Check(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        }
    }
}
