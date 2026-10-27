using System.Collections.Concurrent;
using System.Text;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;

namespace AdMng.Testing;

/// <summary>
/// An in-memory SYSVOL for the privileged access review (U4c): files per GPO GUID and relative path (case-insensitive, as
/// SMB), per-file failures, an unavailable share, and a record of what was opened and read - which session (its DC) and
/// which files. Nothing touches the disk.
/// </summary>
public sealed class FakeSysvolFileReader : ISysvolFileReader
{
    private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SysvolFileStatus> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _reads = new();
    private readonly ConcurrentQueue<string> _servers = new();
    private int _open;

    /// <summary>When set, <see cref="OpenAsync"/> fails with this reason.</summary>
    public SysvolUnavailableReason? Unavailable { get; set; }

    /// <summary>The servers (session DCs) SYSVOL was opened on, in order.</summary>
    public IReadOnlyList<string> OpenedOn => [.. _servers];

    /// <summary>The files read: <c>{GUID}/relative/path</c>.</summary>
    public IReadOnlyList<string> Reads => [.. _reads];

    /// <summary>Connections opened and not yet disposed.</summary>
    public int OpenConnections => Volatile.Read(ref _open);

    /// <summary>Stores a file of a GPO folder.</summary>
    public void Put(Guid gpo, string relativePath, byte[] content) => _files[Key(gpo, relativePath)] = content;

    /// <summary>Stores GptTmpl.inf (UTF-16LE with BOM, CR LF - as secedit writes it).</summary>
    public void PutSecurityTemplate(Guid gpo, string text) => Put(gpo, SecurityTemplate.RelativePath, Utf16(text));

    /// <summary>Stores Groups.xml (UTF-8 with BOM - as the GPP editor writes it).</summary>
    public void PutGroups(Guid gpo, string xml) => Put(gpo, GppGroupsParser.RelativePath, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(xml)]);

    /// <summary>The read of this file fails with <paramref name="status"/>.</summary>
    public void Fail(Guid gpo, string relativePath, SysvolFileStatus status) => _failures[Key(gpo, relativePath)] = status;

    public static byte[] Utf16(string text) =>
        [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal))];

    public Task<ISysvolConnection> OpenAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        if (Unavailable is { } reason)
        {
            throw new SysvolUnavailableException(reason, "SYSVOL is not available (test).");
        }

        _servers.Enqueue(session.Info.Server);
        Interlocked.Increment(ref _open);
        return Task.FromResult<ISysvolConnection>(new Connection(this, session.Info.Server));
    }

    private static string Key(Guid gpo, string relativePath) =>
        SysvolPaths.FolderName(gpo) + "/" + (SysvolPaths.TryNormalize(relativePath, out var path) ? path : relativePath);

    private sealed class Connection(FakeSysvolFileReader owner, string server) : ISysvolConnection
    {
        private int _disposed;

        public string Server { get; } = server;

        public Task<SysvolFileResult> ReadGpoFileAsync(Guid gpoId, string relativePath, long maxBytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!SysvolPaths.TryNormalize(relativePath, out _))
            {
                throw new ArgumentException("Invalid relative path.", nameof(relativePath));
            }

            var key = Key(gpoId, relativePath);
            owner._reads.Enqueue(key);
            if (owner._failures.TryGetValue(key, out var status))
            {
                return Task.FromResult(new SysvolFileResult(status));
            }

            if (!owner._files.TryGetValue(key, out var content))
            {
                return Task.FromResult(SysvolFileResult.NotFound);
            }

            return Task.FromResult(content.Length > maxBytes ? SysvolFileResult.TooLarge : SysvolFileResult.Of([.. content]));
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref owner._open);
            }

            return ValueTask.CompletedTask;
        }
    }
}
