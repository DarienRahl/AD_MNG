using System.Runtime.CompilerServices;
using AdMng.Core.Directory;
using AdMng.Core.Formatting;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.Presentation.Session;

namespace AdMng.Presentation.Connections;

/// <summary>
/// What one run of a read-only feature of another window (the comparison of objects) may use of a connection: the reader,
/// the formatter, the identity snapshot and the domain policy of ONE session, captured together (design note
/// docs/dev/multi-window.md 8.2). The reader forwards only reads, sends at most <see cref="MaxConcurrentRequests"/>
/// requests at a time (the session's small connection pool serves its own window first) and is dropped when the session
/// ends or the window closes (<see cref="End"/>): later reads throw <see cref="ObjectDisposedException"/>. Holds no
/// credential; disposing it releases the session reference.
/// </summary>
public sealed class ConnectionReadContext : IDisposable
{
    /// <summary>Concurrent requests one read context sends to its session.</summary>
    public const int MaxConcurrentRequests = 2;

    private readonly ReadOnlyDirectoryReader _inner;
    private readonly LimitedReader _reader;
    private object? _session;

    private ConnectionReadContext(Guid connectionId, ConnectionIdentity? identity, IDirectoryReader session, IAttributeFormatter formatter, Task<DomainPolicy> policy)
    {
        ConnectionId = connectionId;
        Identity = identity;
        _session = session;
        _inner = ReadOnlyDirectoryReader.Of(session);
        _reader = new LimitedReader(_inner, MaxConcurrentRequests);
        Formatter = formatter;
        Policy = policy;
    }

    /// <summary>The connection window the session belongs to (<see cref="Guid.Empty"/> in hosts without connection windows).</summary>
    public Guid ConnectionId { get; }

    /// <summary>The identity snapshot of the moment the context was pinned (null in hosts without connection windows).</summary>
    public ConnectionIdentity? Identity { get; }

    /// <summary>Facts of the pinned session (domain, naming context, DC, account) - also after <see cref="End"/>.</summary>
    public SessionInfo Info => _inner.Info;

    /// <summary>Schema of the pinned session (in-memory metadata) - also after <see cref="End"/>.</summary>
    public ISchemaCatalog Schema => _inner.Schema;

    /// <summary>The read-only, request-limited reader of the pinned session.</summary>
    public IDirectoryReader Reader => _reader;

    /// <summary>The localized formatter built for the pinned session's schema.</summary>
    public IAttributeFormatter Formatter { get; }

    /// <summary>The domain policy of the pinned session (read once per session; never fails).</summary>
    public Task<DomainPolicy> Policy { get; }

    /// <summary>"contoso.test · CONTOSO\jan.kowalski" (from the identity, else from the session facts).</summary>
    public string Label => Identity?.Label
        ?? (Info.BoundIdentity.Length > 0 ? $"{Info.DomainDnsName} · {Info.BoundIdentity}" : Info.DomainDnsName);

    /// <summary>The identity colour, or null without an identity.</summary>
    public ConnectionColor? Color => Identity?.Color;

    /// <summary>The session ended or the window closed: reads throw <see cref="ObjectDisposedException"/>.</summary>
    public bool IsEnded => _inner.IsDetached;

    /// <summary>
    /// A context over <paramref name="session"/> with the formatter and policy of the same session (for the window's own
    /// side, or a host without connection windows).
    /// </summary>
    public static ConnectionReadContext Create(
        Guid connectionId,
        ConnectionIdentity? identity,
        IDirectoryReader session,
        IAttributeFormatter formatter,
        Task<DomainPolicy> policy)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(formatter);
        ArgumentNullException.ThrowIfNull(policy);
        return new ConnectionReadContext(connectionId, identity, session, formatter, policy);
    }

    /// <summary>Drops the session: later reads throw <see cref="ObjectDisposedException"/>. Idempotent.</summary>
    public void End()
    {
        Volatile.Write(ref _session, null);
        _inner.Detach();
    }

    public void Dispose() => End();

    /// <summary>The connection id only (no domain, no account).</summary>
    public override string ToString() => $"ConnectionReadContext({ConnectionId:N})";

    /// <summary>True while the context reads through <paramref name="session"/>.</summary>
    internal bool IsPinnedTo(object? session) => session is not null && ReferenceEquals(Volatile.Read(ref _session), session);

    /// <summary>True while both contexts read through the same session (false once either has ended).</summary>
    internal bool ReadsSameSessionAs(ConnectionReadContext? other) => other is not null && other.IsPinnedTo(Volatile.Read(ref _session));

    /// <summary>At most N requests at a time; a search holds its slot until it is enumerated or abandoned.</summary>
    private sealed class LimitedReader(IDirectoryReader inner, int limit) : IDirectoryReader
    {
        private readonly SemaphoreSlim _slots = new(limit, limit);

        public SessionInfo Info => inner.Info;

        public ISchemaCatalog Schema => inner.Schema;

        public async IAsyncEnumerable<DirectoryObject> SearchAsync(DirectorySearch search, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await foreach (var obj in inner.SearchAsync(search, cancellationToken).ConfigureAwait(false))
                {
                    yield return obj;
                }
            }
            finally
            {
                _slots.Release();
            }
        }

        public async Task<DirectoryObject?> GetAsync(string distinguishedName, IReadOnlyList<string> attributes, CancellationToken cancellationToken)
        {
            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await inner.GetAsync(distinguishedName, attributes, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _slots.Release();
            }
        }

        public async IAsyncEnumerable<string> GetAllValuesAsync(string distinguishedName, string attribute, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await foreach (var value in inner.GetAllValuesAsync(distinguishedName, attribute, cancellationToken).ConfigureAwait(false))
                {
                    yield return value;
                }
            }
            finally
            {
                _slots.Release();
            }
        }

        public override string ToString() => "LimitedReader";
    }
}
