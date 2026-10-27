using AdMng.Core.Model;
using AdMng.Core.Schema;

namespace AdMng.Core.Directory;

/// <summary>
/// The read half of a directory session: session facts, schema, searches, base reads and ranged retrieval. Every
/// <see cref="IDirectorySession"/> is one. Code that must never write - the comparison of objects between two connection
/// windows - gets a reader through <see cref="ReadOnlyDirectoryReader"/>, which cannot be cast back to a session.
/// </summary>
public interface IDirectoryReader
{
    SessionInfo Info { get; }

    /// <summary>Schema metadata (attributes, classes, display names) loaded once per session, in memory.</summary>
    ISchemaCatalog Schema { get; }

    /// <summary>
    /// Streams search results page by page (paged results control). Results arrive as soon as the first page is
    /// received, so the UI can render progressively. Cancellation abandons the server side paging.
    /// </summary>
    IAsyncEnumerable<DirectoryObject> SearchAsync(DirectorySearch search, CancellationToken cancellationToken);

    /// <summary>Reads one object (base scope). Returns null when it does not exist.</summary>
    Task<DirectoryObject?> GetAsync(string distinguishedName, IReadOnlyList<string> attributes, CancellationToken cancellationToken);

    /// <summary>
    /// Reads all values of a large multi-valued attribute (e.g. group "member" beyond 1500 values)
    /// using ranged retrieval.
    /// </summary>
    IAsyncEnumerable<string> GetAllValuesAsync(string distinguishedName, string attribute, CancellationToken cancellationToken);
}

/// <summary>
/// A reader that forwards only the reads of <see cref="IDirectoryReader"/> to a session. It is not an
/// <see cref="IDirectorySession"/>, so a cast back to the session (and its write methods or its credential, through
/// <see cref="IDirectoryConnector.ConnectToServerAsync"/>) is impossible. <see cref="Detach"/> drops the session
/// reference - another window's comparison then no longer keeps a closed window's session object alive - and every later
/// call throws <see cref="ObjectDisposedException"/>.
/// </summary>
public sealed class ReadOnlyDirectoryReader : IDirectoryReader
{
    private readonly SessionInfo _info;
    private readonly ISchemaCatalog _schema;
    private IDirectoryReader? _inner;

    private ReadOnlyDirectoryReader(IDirectoryReader inner)
    {
        _inner = inner;

        // Facts and schema are in-memory metadata of the session; they stay readable after Detach (for what is shown).
        _info = inner.Info;
        _schema = inner.Schema;
    }

    public SessionInfo Info => _info;

    public ISchemaCatalog Schema => _schema;

    /// <summary>The session was dropped (<see cref="Detach"/>): reads throw <see cref="ObjectDisposedException"/>.</summary>
    public bool IsDetached => Volatile.Read(ref _inner) is null;

    /// <summary>A read-only view of <paramref name="reader"/> (an existing <see cref="ReadOnlyDirectoryReader"/> is wrapped again, not returned).</summary>
    public static ReadOnlyDirectoryReader Of(IDirectoryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new ReadOnlyDirectoryReader(reader);
    }

    /// <summary>Drops the session reference; later reads throw <see cref="ObjectDisposedException"/>. Idempotent.</summary>
    public void Detach() => Volatile.Write(ref _inner, null);

    public IAsyncEnumerable<DirectoryObject> SearchAsync(DirectorySearch search, CancellationToken cancellationToken) =>
        Inner.SearchAsync(search, cancellationToken);

    public Task<DirectoryObject?> GetAsync(string distinguishedName, IReadOnlyList<string> attributes, CancellationToken cancellationToken) =>
        Inner.GetAsync(distinguishedName, attributes, cancellationToken);

    public IAsyncEnumerable<string> GetAllValuesAsync(string distinguishedName, string attribute, CancellationToken cancellationToken) =>
        Inner.GetAllValuesAsync(distinguishedName, attribute, cancellationToken);

    /// <summary>The type only (no server, no account).</summary>
    public override string ToString() => IsDetached ? "ReadOnlyDirectoryReader(detached)" : "ReadOnlyDirectoryReader";

    private IDirectoryReader Inner => Volatile.Read(ref _inner) ?? throw new ObjectDisposedException(nameof(ReadOnlyDirectoryReader), "The connection of this reader was closed.");
}
