using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Core.Schema;

namespace AdMng.Testing;

/// <summary>
/// A session on ANOTHER domain controller of the same in-memory directory: every call goes to the shared
/// <see cref="FakeDirectorySession"/> (replication is instant), but the session is a distinct object with its own
/// <see cref="Info"/> (<see cref="SessionInfo.Server"/> = the other DC) and its own lifetime – disposing it does not
/// close the shared directory, and after disposal it refuses calls like a closed LDAP session. Used to test
/// "Change domain controller" (a new session replaces the current one).
/// </summary>
public sealed class FakeDomainControllerSession : IDirectorySession
{
    private readonly FakeDirectorySession _directory;
    private int _disposed;

    /// <param name="directory">The shared directory (its data and schema).</param>
    /// <param name="server">DNS host name of the DC this session is pinned to.</param>
    /// <param name="info">Optional changes of the session facts (e.g. another domain NC or identity).</param>
    public FakeDomainControllerSession(FakeDirectorySession directory, string server, Func<SessionInfo, SessionInfo>? info = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        _directory = directory;
        var facts = directory.Info with { Server = server.Trim() };
        Info = info is null ? facts : info(facts);
    }

    public SessionInfo Info { get; }

    public ISchemaCatalog Schema => _directory.Schema;

    /// <summary>The shared directory.</summary>
    public FakeDirectorySession Directory => _directory;

    public bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    public IAsyncEnumerable<DirectoryObject> SearchAsync(DirectorySearch search, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _directory.SearchAsync(search, cancellationToken);
    }

    public Task<VirtualListPage> GetVirtualListPageAsync(DirectorySearch search, VirtualListRequest window, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _directory.GetVirtualListPageAsync(search, window, cancellationToken);
    }

    public Task<DirectoryObject?> GetAsync(string distinguishedName, IReadOnlyList<string> attributes, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _directory.GetAsync(distinguishedName, attributes, cancellationToken);
    }

    public IAsyncEnumerable<string> GetAllValuesAsync(string distinguishedName, string attribute, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _directory.GetAllValuesAsync(distinguishedName, attribute, cancellationToken);
    }

    public IAsyncEnumerable<string> GetAllValuesAsync(string distinguishedName, string attribute, AttributeReadOptions options, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return ((IDirectorySession)_directory).GetAllValuesAsync(distinguishedName, attribute, options, cancellationToken);
    }

    public Task<EffectivePermissions> GetEffectivePermissionsAsync(string distinguishedName, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _directory.GetEffectivePermissionsAsync(distinguishedName, cancellationToken);
    }

    public Task ModifyAsync(string distinguishedName, IReadOnlyList<AttributeModification> modifications, ModifyOptions? options, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _directory.ModifyAsync(distinguishedName, modifications, options, cancellationToken);
    }

    public Task AddAsync(string distinguishedName, IReadOnlyDictionary<string, IReadOnlyList<object>> attributes, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _directory.AddAsync(distinguishedName, attributes, cancellationToken);
    }

    public Task DeleteAsync(string distinguishedName, bool subtree, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _directory.DeleteAsync(distinguishedName, subtree, cancellationToken);
    }

    public Task<string> MoveAsync(string distinguishedName, string? newParentDn, string? newRdn, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _directory.MoveAsync(distinguishedName, newParentDn, newRdn, cancellationToken);
    }

    public Task ResetPasswordAsync(string distinguishedName, string newPassword, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _directory.ResetPasswordAsync(distinguishedName, newPassword, cancellationToken);
    }

    public Task RestoreDeletedAsync(string deletedObjectDn, string targetDn, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _directory.RestoreDeletedAsync(deletedObjectDn, targetDn, cancellationToken);
    }

    public Task<IAsyncDisposable?> WatchAsync(string containerDn, Action<string> onChanged, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _directory.WatchAsync(containerDn, onChanged, cancellationToken);
    }

    /// <summary>Closes this session only; the shared directory stays open.</summary>
    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }

    public override string ToString() => $"{Info.Server} ({Info.DefaultNamingContext})";

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);
}
