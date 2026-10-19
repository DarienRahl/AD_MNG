using System.Net;
using AdMng.Core.Model;
using AdMng.Core.Schema;

namespace AdMng.Core.Directory;

/// <summary>Creates authenticated sessions and discovers domain controllers.</summary>
public interface IDirectoryConnector
{
    /// <summary>DNS name of the domain this computer is joined to, or null (workgroup / non-Windows).</summary>
    string? GetComputerDomain();

    /// <summary>Lists domain controllers of a domain (site-aware ordering when the platform allows it).</summary>
    Task<IReadOnlyList<DomainControllerInfo>> FindDomainControllersAsync(string domain, CancellationToken cancellationToken);

    /// <summary>
    /// Locates a DC (unless <see cref="ConnectionOptions.Server"/> is set), binds, reads RootDSE and the schema
    /// and returns a session pinned to that DC. The password is only kept in memory for the session lifetime
    /// (needed to re-open pooled connections) and is never persisted.
    /// </summary>
    /// <exception cref="DirectoryException">Bind failed (wrong credentials, DC unreachable, policy).</exception>
    Task<IDirectorySession> ConnectAsync(ConnectionOptions options, NetworkCredential? credential, CancellationToken cancellationToken);

    /// <summary>
    /// "Change domain controller": opens a NEW session on <paramref name="server"/> with the connection options of
    /// <paramref name="current"/> (authentication, channel security, port, user name, timeouts) and the credential that
    /// session keeps in memory - the operator does not type the password again, and it never leaves the connector.
    /// <paramref name="current"/> stays open and unchanged (the caller closes it). Like <see cref="ConnectAsync"/> it
    /// binds and reads RootDSE and the schema; checking that the DC serves the same domain is up to the caller.
    /// </summary>
    /// <exception cref="DirectoryException">The DC is unreachable or refused the bind.</exception>
    /// <exception cref="ArgumentException">The server name is empty or contains a port.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="current"/> was closed (its credential is gone).</exception>
    /// <exception cref="NotSupportedException">The connector cannot reuse <paramref name="current"/> (default implementation).</exception>
    Task<IDirectorySession> ConnectToServerAsync(IDirectorySession current, string server, CancellationToken cancellationToken) =>
        Task.FromException<IDirectorySession>(new NotSupportedException("This connector cannot open a session with the credential of another session."));
}

/// <summary>
/// An authenticated connection to ONE domain controller. All reads and writes of a session go to the same DC,
/// which avoids replication-latency surprises (edit, then refresh on another DC shows old data).
/// Implementations are thread-safe and support concurrent requests.
/// Every call goes to the server - there is no client side cache of directory data.
/// </summary>
public interface IDirectorySession : IAsyncDisposable
{
    SessionInfo Info { get; }

    /// <summary>Schema metadata (attributes, classes, display names) loaded once per session, in memory.</summary>
    ISchemaCatalog Schema { get; }

    /// <summary>
    /// Streams search results page by page (paged results control). Results arrive as soon as the first page is
    /// received, so the UI can render progressively. Cancellation abandons the server side paging.
    /// </summary>
    IAsyncEnumerable<DirectoryObject> SearchAsync(DirectorySearch search, CancellationToken cancellationToken);

    /// <summary>
    /// Returns one window of a server sorted virtual list (VLV). Requires <see cref="DirectorySearch.Sort"/>.
    /// Use for very large containers where loading everything would be slow.
    /// </summary>
    Task<VirtualListPage> GetVirtualListPageAsync(DirectorySearch search, VirtualListRequest window, CancellationToken cancellationToken);

    /// <summary>Reads one object (base scope). Returns null when it does not exist.</summary>
    Task<DirectoryObject?> GetAsync(string distinguishedName, IReadOnlyList<string> attributes, CancellationToken cancellationToken);

    /// <summary>
    /// Reads all values of a large multi-valued attribute (e.g. group "member" beyond 1500 values)
    /// using ranged retrieval.
    /// </summary>
    IAsyncEnumerable<string> GetAllValuesAsync(string distinguishedName, string attribute, CancellationToken cancellationToken);

    /// <summary>Ranged retrieval with options. The default implementation supports only <see cref="AttributeReadOptions.Default"/>.</summary>
    IAsyncEnumerable<string> GetAllValuesAsync(string distinguishedName, string attribute, AttributeReadOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.ExtendedDn || options.IncludeDeleted
            ? throw new NotSupportedException("This session does not support extended DN / deleted-object value reads.")
            : GetAllValuesAsync(distinguishedName, attribute, cancellationToken);
    }

    /// <summary>Evaluates what the bound identity may do on the object (allowedAttributesEffective etc.).</summary>
    Task<EffectivePermissions> GetEffectivePermissionsAsync(string distinguishedName, CancellationToken cancellationToken);

    /// <summary>Applies modifications atomically (one LDAP modify request).</summary>
    Task ModifyAsync(string distinguishedName, IReadOnlyList<AttributeModification> modifications, ModifyOptions? options, CancellationToken cancellationToken);

    /// <summary>Creates an object. Attributes values are string or byte[].</summary>
    Task AddAsync(string distinguishedName, IReadOnlyDictionary<string, IReadOnlyList<object>> attributes, CancellationToken cancellationToken);

    /// <summary>Deletes an object; <paramref name="subtree"/> uses the tree delete control.</summary>
    Task DeleteAsync(string distinguishedName, bool subtree, CancellationToken cancellationToken);

    /// <summary>Moves and/or renames (ModifyDN). Returns the new DN.</summary>
    /// <param name="distinguishedName">Current DN.</param>
    /// <param name="newParentDn">New parent container, or null to keep the parent (pure rename).</param>
    /// <param name="newRdn">New RDN like <c>CN=New Name</c> (escaped), or null to keep the RDN (pure move).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<string> MoveAsync(string distinguishedName, string? newParentDn, string? newRdn, CancellationToken cancellationToken);

    /// <summary>
    /// Administrative password reset (unicodePwd replace). Requires an encrypted channel and the
    /// Reset Password extended right. Does not change pwdLastSet - use <see cref="ModifyAsync"/> for "must change".
    /// </summary>
    Task ResetPasswordAsync(string distinguishedName, string newPassword, CancellationToken cancellationToken);

    /// <summary>
    /// Restores a deleted object from the AD Recycle Bin (removes isDeleted, sets the new DN).
    /// </summary>
    /// <param name="deletedObjectDn">DN of the object in CN=Deleted Objects.</param>
    /// <param name="targetDn">New DN, normally msDS-LastKnownRDN under lastKnownParent.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task RestoreDeletedAsync(string deletedObjectDn, string targetDn, CancellationToken cancellationToken);

    /// <summary>
    /// Subscribes to change notifications for a container (one level). The callback receives changed DNs.
    /// Dispose the result to stop. Returns null when the server does not support notifications.
    /// </summary>
    Task<IAsyncDisposable?> WatchAsync(string containerDn, Action<string> onChanged, CancellationToken cancellationToken);
}
