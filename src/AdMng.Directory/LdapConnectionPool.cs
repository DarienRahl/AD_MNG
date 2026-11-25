using System.DirectoryServices.Protocols;
using AdMng.Core.Directory;
using Microsoft.Extensions.Logging;
using DirectoryException = AdMng.Core.Directory.DirectoryException;

namespace AdMng.Directory;

/// <summary>What a lease is used for.</summary>
internal enum LeaseKind
{
    /// <summary>One request (or a short sequence of independent requests).</summary>
    Request,

    /// <summary>
    /// A paged search: the server keeps a result set bound to the connection until the last page, so the lease
    /// pins the connection and occupies one of its <see cref="LdapConnectionPool.PagedSearchesPerConnection"/> slots.
    /// </summary>
    PagedSearch,
}

/// <summary>
/// A small pool of bound connections to the session's DC.
/// </summary>
/// <remarks>
/// <para>
/// Requests on one connection are serialized by a gate whose width depends on the platform: WinLDAP multiplexes
/// concurrent requests correctly, but S.DS.P over OpenLDAP derives the result of a search from the type of the
/// first message it gets back - when another thread has already read the search's messages, the result code and
/// the response controls (paging cookie, VLV position) are silently lost. One in-flight request per connection
/// avoids that on Linux/macOS.
/// </para>
/// <para>
/// Paged searches keep a result set on the server between pages (AD: MaxResultSetsPerConn = 10, Samba evicts the
/// oldest beyond 10), so at most <see cref="PagedSearchesPerConnection"/> of them share a connection.
/// </para>
/// <para>
/// New connections are opened lazily in the background when every connection is busy, so a request never waits for
/// a bind while a connection exists. Broken connections are dropped; idle ones are recycled before the DC's idle
/// timeout (MaxConnIdleTime, 15 minutes by default) or a firewall can silently close them.
/// </para>
/// <para>
/// A connection is never disposed while a request runs on it (audit finding DLR-4): S.DS.P frees an attribute name twice
/// when its connection is disposed while it builds the entries of a response (<c>LdapConnection.ConstructEntry</c>,
/// dotnet/runtime#35189) - glibc aborts the process (SIGABRT, exit code 134) or the heap is corrupted. Every request counts
/// as in flight from the moment it is admitted until S.DS.P is done with it (<see cref="PendingResponse{TResponse}.Finished"/>
/// - also when its caller stopped waiting long before). A connection that leaves the pool (broken, idle, or the pool closes)
/// takes no new request at once and is disposed when its last request in flight has ended.
/// </para>
/// </remarks>
internal sealed class LdapConnectionPool : IAsyncDisposable
{
    /// <summary>Connections unused for this long are reopened on next use.</summary>
    public static readonly TimeSpan IdleRecycleAfter = TimeSpan.FromMinutes(5);

    /// <summary>Concurrent paged searches per connection (half of AD's default MaxResultSetsPerConn).</summary>
    public const int PagedSearchesPerConnection = 5;

    private readonly LdapConnectionFactory _factory;
    private readonly int _maxConnections;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private readonly List<PooledConnection> _connections = [];

    /// <summary>Broken connections that left the pool and wait for their requests or leases to end before they are disposed.</summary>
    private readonly List<PooledConnection> _retiring = [];
    private readonly CancellationTokenSource _disposeCts = new();

    /// <summary>Cancelled when the pool closes: the growth and every request in flight (captured, so it outlives the source).</summary>
    private readonly CancellationToken _closing;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _growth;
    private DirectoryException? _lastGrowthError;
    private int _nextConnectionId;
    private bool _disposed;

    public LdapConnectionPool(LdapConnectionFactory factory, int maxConnections, ILogger logger, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConnections, 1);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _factory = factory;
        _maxConnections = maxConnections;
        _logger = logger;
        _timeProvider = timeProvider;
        _closing = _disposeCts.Token;
    }

    /// <summary>Requests in flight per connection (see remarks).</summary>
    public static int RequestsPerConnection { get; } = OperatingSystem.IsWindows() ? 8 : 1;

    /// <summary>
    /// How long <see cref="DisposeAsync"/> waits for the requests in flight it cancelled - below the 5 s after which a
    /// connection window abandons the close of its session (docs/dev/multi-window.md 3.4).
    /// </summary>
    public static readonly TimeSpan DefaultCloseWait = TimeSpan.FromSeconds(2);

    /// <summary>Sends the requests of the leases (replaced by the pool's unit tests).</summary>
    internal ILdapRequestSender Sender { get; init; } = LdapRequestSender.Instance;

    /// <summary>The bound of <see cref="DisposeAsync"/>'s wait for requests in flight.</summary>
    internal TimeSpan CloseWait { get; init; } = DefaultCloseWait;

    public LdapConnectionFactory Factory => _factory;

    /// <summary>Upper bound of pooled connections.</summary>
    public int MaxConnections => _maxConnections;

    /// <summary>Random identity of this pool (tells its connection ids apart from another session's).</summary>
    public int Id { get; } = Random.Shared.Next();

    /// <summary>Number of open healthy connections (diagnostics and tests).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _connections.Count;
            }
        }
    }

    /// <summary>True while an additional connection is being opened (diagnostics and tests).</summary>
    public bool IsGrowing
    {
        get
        {
            lock (_gate)
            {
                return _growth is not null;
            }
        }
    }

    /// <summary>
    /// True when nothing runs on the pool: no lease, no request (also no background abandon) and no growth
    /// (diagnostics and tests).
    /// </summary>
    public bool IsIdle
    {
        get
        {
            lock (_gate)
            {
                return _growth is null && _connections.TrueForAll(c => c.Load == 0 && c.PagedSearches == 0 && c.InFlight == 0);
            }
        }
    }

    /// <summary>Adds an already bound connection (the one used to establish the session).</summary>
    public void Add(LdapConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _connections.Add(new PooledConnection(++_nextConnectionId, connection, _timeProvider.GetUtcNow()));
        }
    }

    /// <summary>
    /// Returns a lease on the least loaded suitable connection. Waits only when there is no connection yet or every
    /// connection's paged-search slots are taken (and the pool cannot grow).
    /// </summary>
    /// <exception cref="DirectoryException">No connection exists and a new one could not be opened.</exception>
    public ValueTask<ConnectionLease> AcquireAsync(LeaseKind kind, CancellationToken cancellationToken) =>
        AcquireAsync(kind, preferredConnectionId: null, cancellationToken);

    /// <summary>
    /// Like <see cref="AcquireAsync(LeaseKind, CancellationToken)"/>, but returns the connection with
    /// <paramref name="preferredConnectionId"/> while it is still healthy - for server state that is bound to a
    /// connection (VLV contexts).
    /// </summary>
    public async ValueTask<ConnectionLease> AcquireAsync(LeaseKind kind, int? preferredConnectionId, CancellationToken cancellationToken)
    {
        while (true)
        {
            ConnectionLease? lease = null;
            Task changed;
            List<PooledConnection>? expired;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                expired = RemoveIdle();
                var best = (preferredConnectionId is { } id ? _connections.Find(c => c.Id == id && CanTake(c, kind)) : null)
                    ?? Select(kind);
                if (best is not null)
                {
                    var busy = best.Load > 0 || (kind == LeaseKind.PagedSearch && best.PagedSearches > 0);
                    if (busy && CanGrow())
                    {
                        // Everything is busy: add capacity in the background, share the least loaded one meanwhile.
                        StartGrowth();
                    }

                    if (kind == LeaseKind.PagedSearch)
                    {
                        best.PagedSearches++;
                    }
                    else
                    {
                        best.Load++;
                    }

                    best.LastUsed = _timeProvider.GetUtcNow();
                    lease = new ConnectionLease(this, best, kind);
                }
                else if (CanGrow())
                {
                    StartGrowth();
                }

                changed = _changed.Task;
            }

            DisposeExpired(expired);
            if (lease is not null)
            {
                return lease;
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_connections.Count == 0 && _growth is null && _lastGrowthError is { } error)
                {
                    throw error;
                }
            }
        }
    }

    /// <summary>
    /// Closes the pool: no connection is handed out any more and requests waiting for a connection or for a request slot
    /// give up (unsent). Requests in flight are cancelled - their callers get an <see cref="ObjectDisposedException"/> at
    /// once, WinLDAP abandons them, and on Linux/macOS a blocking request that waits for the server is woken by a shutdown of
    /// its socket - and each connection is disposed as soon as no request runs on it any more. The wait for that is bounded
    /// by <see cref="CloseWait"/>; a connection whose request still runs then is disposed when that request ends, never under
    /// it (see remarks of the class). On Linux/macOS a blocking request is not bounded by its own timeout: it ends when the
    /// socket delivers data or fails, which the shutdown of the socket makes happen.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        List<PooledConnection> connections;
        List<PooledConnection> unused = [];
        Task? growth;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            connections = [.. _connections, .. _retiring.Where(c => !c.IsDisposed)];
            _connections.Clear();
            _retiring.Clear();
            foreach (var pooled in connections)
            {
                pooled.IsClosed = true;
                if (RetireIfUnused(pooled))
                {
                    unused.Add(pooled);
                }
            }

            growth = _growth;
            SignalChanged();
        }

        // Releases the callers of the requests in flight (the token is linked into every request) and stops the growth.
        await _disposeCts.CancelAsync().ConfigureAwait(false);

        // Queued requests give up; connections without a request in flight close now.
        foreach (var pooled in connections)
        {
            pooled.SignalDropped();
        }

        foreach (var pooled in unused)
        {
            DisposeConnection(pooled);
        }

        var running = connections.FindAll(c => !unused.Contains(c));
        if (running.Count > 0)
        {
            foreach (var pooled in running)
            {
                Sender.Interrupt(pooled.Connection);
            }

            var released = Task.WhenAll(running.Select(c => c.Released));
            await released.WaitAsync(CloseWait).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (!released.IsCompleted)
            {
                Log.PoolClosedWithRequestsRunning(_logger, _factory.Host, running.Count(c => !c.Released.IsCompleted));
            }
        }

        if (growth is not null)
        {
            await growth.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        _disposeCts.Dispose();
    }

    private PooledConnection? Select(LeaseKind kind)
    {
        PooledConnection? best = null;
        foreach (var candidate in _connections)
        {
            if (!CanTake(candidate, kind))
            {
                continue;
            }

            if (best is null || Weight(candidate, kind) < Weight(best, kind))
            {
                best = candidate;
            }
        }

        return best;

        // Paged searches spread over connections by their open result sets as well.
        static int Weight(PooledConnection connection, LeaseKind kind) =>
            kind == LeaseKind.PagedSearch ? connection.Load + connection.PagedSearches : connection.Load;
    }

    private static bool CanTake(PooledConnection connection, LeaseKind kind) =>
        kind != LeaseKind.PagedSearch || connection.PagedSearches < PagedSearchesPerConnection;

    private void Release(PooledConnection pooled, LeaseKind kind)
    {
        bool dispose;
        lock (_gate)
        {
            if (kind == LeaseKind.PagedSearch)
            {
                pooled.PagedSearches--;
            }
            else
            {
                pooled.Load--;
            }

            pooled.LastUsed = _timeProvider.GetUtcNow();
            dispose = RetireIfUnused(pooled);
            SignalChanged();
        }

        if (dispose)
        {
            DisposeConnection(pooled);
        }
    }

    private void MarkBroken(PooledConnection pooled, string reason)
    {
        bool dispose;
        lock (_gate)
        {
            // A closed pool has dropped all of its connections already.
            if (pooled.IsBroken || pooled.IsClosed)
            {
                return;
            }

            pooled.IsBroken = true;
            _connections.Remove(pooled);
            dispose = RetireIfUnused(pooled);
            if (!dispose)
            {
                // Still used: closing the pool must still reach it (to wake its request and dispose it afterwards).
                _retiring.RemoveAll(c => c.IsDisposed);
                _retiring.Add(pooled);
            }

            SignalChanged();
        }

        Log.PoolConnectionDropped(_logger, _factory.Host, reason);

        // Requests queued behind a request that may never end move to another connection (they were not sent).
        pooled.SignalDropped();
        if (dispose)
        {
            DisposeConnection(pooled);
        }
    }

    private void ChangeLoad(PooledConnection pooled, int delta)
    {
        bool dispose;
        lock (_gate)
        {
            pooled.Load += delta;
            pooled.LastUsed = _timeProvider.GetUtcNow();
            dispose = RetireIfUnused(pooled);
        }

        // A broken connection whose last request (e.g. one the caller stopped waiting for) just ended.
        if (dispose)
        {
            DisposeConnection(pooled);
        }
    }

    /// <summary>Admits a request that is about to be sent; false when the connection must not take one any more.</summary>
    private bool TryStartRequest(PooledConnection pooled)
    {
        lock (_gate)
        {
            if (pooled.IsClosed || pooled.IsDisposed)
            {
                return false;
            }

            pooled.InFlight++;
            return true;
        }
    }

    /// <summary>S.DS.P is done with a request (<see cref="PendingResponse{TResponse}.Finished"/>).</summary>
    private void EndRequest(PooledConnection pooled)
    {
        bool dispose;
        lock (_gate)
        {
            pooled.InFlight--;
            dispose = RetireIfUnused(pooled);
        }

        // A connection that left the pool while this request ran.
        if (dispose)
        {
            DisposeConnection(pooled);
        }
    }

    /// <summary>
    /// Marks a connection that left the pool disposed once nothing may use it any more (the caller holds the lock): never
    /// while a request runs on it; a broken one also not while a lease holds it (the holder learns of the failure from its
    /// own request), one of a closed pool regardless of leases (they cannot send any more). Returns true when the caller
    /// must dispose it (outside the lock, <see cref="DisposeConnection"/>).
    /// </summary>
    private static bool RetireIfUnused(PooledConnection pooled)
    {
        if (pooled.IsDisposed || pooled.InFlight != 0)
        {
            return false;
        }

        if (!pooled.IsClosed && !(pooled.IsBroken && pooled.Load == 0 && pooled.PagedSearches == 0))
        {
            return false;
        }

        pooled.IsDisposed = true;
        return true;
    }

    /// <summary>Disposes a connection that <see cref="RetireIfUnused"/> or <see cref="RemoveIdle"/> retired (outside the lock).</summary>
    private static void DisposeConnection(PooledConnection pooled)
    {
        try
        {
            pooled.Connection.Dispose();
        }
        finally
        {
            pooled.MarkReleased();
        }
    }

    private List<PooledConnection>? RemoveIdle()
    {
        var now = _timeProvider.GetUtcNow();
        List<PooledConnection>? expired = null;
        for (var i = _connections.Count - 1; i >= 0; i--)
        {
            var pooled = _connections[i];
            // Not while S.DS.P still runs a request on it (WinLDAP: one its caller abandoned).
            if (pooled.Load == 0 && pooled.PagedSearches == 0 && pooled.InFlight == 0 && now - pooled.LastUsed >= IdleRecycleAfter)
            {
                _connections.RemoveAt(i);
                pooled.IsDisposed = true;
                (expired ??= []).Add(pooled);
            }
        }

        return expired;
    }

    private void DisposeExpired(List<PooledConnection>? expired)
    {
        if (expired is null)
        {
            return;
        }

        foreach (var pooled in expired)
        {
            Log.PoolConnectionRecycled(_logger, _factory.Host);
            DisposeConnection(pooled);
        }
    }

    private bool CanGrow() => _growth is null && _connections.Count < _maxConnections;

    /// <summary>Wakes everybody waiting for a connection or a paged-search slot (the caller holds the lock).</summary>
    private void SignalChanged()
    {
        var previous = _changed;
        _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }

    /// <summary>Starts opening one more connection (the caller holds the lock). The task never faults.</summary>
    private void StartGrowth() => _growth = GrowAsync();

    private async Task GrowAsync()
    {
        // Leave the caller's lock before doing any work; the caller publishes _growth first.
        await Task.Yield();
        LdapConnection? connection = null;
        DirectoryException? error = null;
        try
        {
            connection = await _factory.OpenAsync(probeReachability: false, _disposeCts.Token).ConfigureAwait(false);
        }
        catch (DirectoryException ex)
        {
            Log.PoolGrowthFailed(_logger, _factory.Host, ex.Kind);
            error = ex;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            error = new DirectoryException(DirectoryErrorKind.Cancelled, "The session was closed.", ex);
        }
        catch (Exception ex)
        {
            // Never fault: _growth must be reset below, or the pool could never grow again and requests
            // waiting for a first connection would wait forever.
            Log.PoolGrowthFailed(_logger, _factory.Host, DirectoryErrorKind.Unknown);
            error = new DirectoryException(DirectoryErrorKind.Unknown, "Could not open an LDAP connection.", ex);
        }

        int count;
        lock (_gate)
        {
            _growth = null;
            _lastGrowthError = error;
            if (connection is not null && !_disposed)
            {
                _connections.Add(new PooledConnection(++_nextConnectionId, connection, _timeProvider.GetUtcNow()));
                connection = null;
            }

            count = _connections.Count;
            SignalChanged();
        }

        if (connection is not null)
        {
            // The pool was disposed while binding.
            connection.Dispose();
        }
        else if (error is null)
        {
            Log.PoolConnectionOpened(_logger, _factory.Host, count, _maxConnections);
        }
    }

    /// <summary>Book-keeping of one pooled connection; counters are guarded by the pool lock.</summary>
    internal sealed class PooledConnection(int id, LdapConnection connection, DateTimeOffset created)
    {
        private readonly CancellationTokenSource _dropped = new();
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Identifies the connection within the pool (never reused).</summary>
        public int Id { get; } = id;

        /// <summary>Cancelled when the connection left the pool (broken or pool disposed).</summary>
        public CancellationToken Dropped => _dropped.Token;

        public LdapConnection Connection { get; } = connection;

        /// <summary>Serializes requests (see <see cref="RequestsPerConnection"/>).</summary>
        public SemaphoreSlim RequestGate { get; } = new(RequestsPerConnection, RequestsPerConnection);

        /// <summary>Request leases plus requests in flight (also abandoned ones still running) - the selection metric.</summary>
        public int Load { get; set; }

        /// <summary>Open paged result sets.</summary>
        public int PagedSearches { get; set; }

        /// <summary>
        /// Requests S.DS.P may still run on the connection: admitted and not yet <see cref="PendingResponse{TResponse}.Finished"/>
        /// (also ones whose callers stopped waiting). The connection is never disposed while this is above zero.
        /// </summary>
        public int InFlight { get; set; }

        public DateTimeOffset LastUsed { get; set; } = created;

        /// <summary>Left the pool after a failure; disposed once no lease and no request uses it.</summary>
        public bool IsBroken { get; set; }

        /// <summary>The pool closed; takes no request any more and is disposed once no request runs on it.</summary>
        public bool IsClosed { get; set; }

        /// <summary>Retired: disposed (or about to be, outside the lock).</summary>
        public bool IsDisposed { get; set; }

        /// <summary>Completes once the connection was disposed.</summary>
        public Task Released => _released.Task;

        public void MarkReleased() => _released.TrySetResult();

        /// <summary>Wakes requests waiting for <see cref="RequestGate"/> (call outside the pool lock).</summary>
        public void SignalDropped()
        {
            try
            {
                _dropped.Cancel();
            }
            catch (AggregateException)
            {
                // Only our own waiters are registered; they do not throw.
            }
        }
    }

    /// <summary>Use of one pooled connection; dispose to return it.</summary>
    internal sealed class ConnectionLease : IDisposable
    {
        private readonly LdapConnectionPool _pool;
        private readonly PooledConnection _pooled;
        private readonly LeaseKind _kind;
        private int _released;

        public ConnectionLease(LdapConnectionPool pool, PooledConnection pooled, LeaseKind kind)
        {
            _pool = pool;
            _pooled = pooled;
            _kind = kind;
        }

        public LdapConnection Connection => _pooled.Connection;

        /// <summary>Pool-unique id of the leased connection.</summary>
        public int ConnectionId => _pooled.Id;

        /// <summary>
        /// Sends one request on the leased connection once the connection's request gate admits it. The gate is
        /// held until the request really finished - also when the caller stopped waiting (cancellation) - and is free
        /// again when a request that finished returns to its caller, so the caller's next request finds the
        /// connection idle (no needless growth, idle recycling sees it).
        /// </summary>
        /// <exception cref="ObjectDisposedException">The pool closed before or while the request ran (the caller is released at
        /// once; the request itself may end later - its connection is disposed only then).</exception>
        public async Task<TResponse> SendAsync<TResponse>(DirectoryRequest request, TimeSpan timeout, CancellationToken cancellationToken)
            where TResponse : DirectoryResponse
        {
            var scope = await EnterAsync(cancellationToken).ConfigureAwait(false);
            PendingResponse<TResponse> pending;

            // Closing the pool cancels the request too. Kept until S.DS.P is done with the request (RequestScope).
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _pool._closing);
            try
            {
                pending = _pool.Sender.Send<TResponse>(Connection, request, timeout, cancellation.Token);
            }
            catch
            {
                cancellation.Dispose();
                scope.Dispose();
                throw;
            }

            scope.Sent(pending.Finished, cancellation);
            try
            {
                return await pending.ReceiveAsync(scope.Complete).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && _pool._closing.IsCancellationRequested)
            {
                throw new ObjectDisposedException("The directory session was closed while the request ran.", ex);
            }
        }

        /// <summary>The connection failed at transport level; drop it from the pool.</summary>
        public void MarkBroken(string reason) => _pool.MarkBroken(_pooled, reason);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _pool.Release(_pooled, _kind);
            }
        }

        /// <summary>Waits until this lease may send a request on its connection.</summary>
        /// <remarks>
        /// The request counts as load of the connection until it really finished: on Linux/macOS a cancelled
        /// search keeps the connection's only request slot until the server answered, so new requests should
        /// prefer other connections meanwhile.
        /// </remarks>
        private async ValueTask<RequestScope> EnterAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _released) == 1, this);
            _pool.ChangeLoad(_pooled, +1);
            try
            {
                if (!_pooled.RequestGate.Wait(0, CancellationToken.None))
                {
                    await WaitForGateAsync(cancellationToken).ConfigureAwait(false);
                }

                if (_kind == LeaseKind.Request && _pooled.Dropped.IsCancellationRequested)
                {
                    // The connection left the pool after this lease was taken: the request is retried on another
                    // one. (A paged search cannot move - its result set lives on this connection - so it tries.)
                    _pooled.RequestGate.Release();
                    throw ConnectionDropped();
                }

                if (!_pool.TryStartRequest(_pooled))
                {
                    // The pool closed: the connection is disposed (or will be once its requests ended), nothing is sent.
                    _pooled.RequestGate.Release();
                    throw new ObjectDisposedException(nameof(LdapConnectionPool), "The directory session was closed.");
                }
            }
            catch
            {
                _pool.ChangeLoad(_pooled, -1);
                throw;
            }

            return new RequestScope(_pool, _pooled);
        }

        private static RequestNotSentException ConnectionDropped() =>
            new(new LdapException(LdapErrors.ServerDown, "The connection was dropped before the request was sent."));

        /// <summary>
        /// Waits for the request gate until the connection is dropped: the request ahead may never end (a silent
        /// server on Linux), so a dropped connection releases its waiters as "not sent" - callers retry elsewhere.
        /// </summary>
        private async Task WaitForGateAsync(CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _pooled.Dropped);
            try
            {
                await _pooled.RequestGate.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && _pooled.Dropped.IsCancellationRequested)
            {
                throw ConnectionDropped();
            }
        }
    }

    /// <summary>
    /// One request on a connection: releases the request slot and its share of the load when the request finished for its
    /// caller, and ends the request in flight when S.DS.P is done with it (mostly the same moment).
    /// </summary>
    private sealed class RequestScope(LdapConnectionPool pool, PooledConnection pooled) : IDisposable
    {
        private int _slotReleased;
        private int _ended;
        private Task? _finished;
        private IDisposable? _cancellation;

        /// <summary>The request was sent: it is in flight until <paramref name="finished"/> completes.</summary>
        public void Sent(Task finished, IDisposable cancellation)
        {
            _finished = finished;
            _cancellation = cancellation;
        }

        /// <summary>
        /// The request finished (also one the caller stopped waiting for). A transport failure or a client timeout
        /// drops the connection BEFORE the gate admits the next queued request, which then moves elsewhere.
        /// </summary>
        public void Complete(Task request)
        {
            if (request.Exception?.InnerException is { } error)
            {
                if (LdapErrors.IsConnectionBroken(error))
                {
                    pool.MarkBroken(pooled, "server down");
                }
                else if (LdapErrors.IsClientTimeout(error))
                {
                    pool.MarkBroken(pooled, "no answer within the request timeout");
                }
            }

            ReleaseSlot();

            // Already completed unless the caller's request ended before S.DS.P's work on it (WinLDAP: an abort).
            var finished = _finished ?? request;
            if (finished.IsCompleted)
            {
                End();
            }
            else
            {
                _ = finished.ContinueWith(
                    static (_, state) => ((RequestScope)state!).End(),
                    this,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        /// <summary>The request was not sent after all.</summary>
        public void Dispose()
        {
            ReleaseSlot();
            End();
        }

        private void ReleaseSlot()
        {
            if (Interlocked.Exchange(ref _slotReleased, 1) == 1)
            {
                return;
            }

            pooled.RequestGate.Release();
            pool.ChangeLoad(pooled, -1);
        }

        private void End()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 1)
            {
                return;
            }

            _cancellation?.Dispose();
            pool.EndRequest(pooled);
        }
    }
}
