using System.Net;
using AdMng.Core.Directory;
using AdMng.Core.Formatting;
using AdMng.Core.Ldap;
using AdMng.Core.Schema;
using AdMng.Presentation.Connection;
using AdMng.Presentation.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdMng.Presentation.Session;

/// <summary>
/// Owns the directory session of one connection window (one manager per window, scoped; several windows - several
/// domains or identities - each have their own).
/// </summary>
public interface ISessionManager
{
    /// <summary>The active session, or null when disconnected.</summary>
    IDirectorySession? Current { get; }

    /// <summary>The open sessions: at most one (one manager per connection window).</summary>
    IReadOnlyList<IDirectorySession> Sessions { get; }

    bool IsConnected { get; }

    /// <summary>Localized formatter for <see cref="Current"/> (schema aware); a schema-less formatter when disconnected.</summary>
    IAttributeFormatter Formatter { get; }

    /// <summary>Raised after <see cref="Current"/> changed (on the thread that connected/disconnected).</summary>
    event EventHandler? SessionChanged;

    /// <summary>
    /// Connects (bind + RootDSE + schema), makes the new session current and then closes the previous one (off the UI
    /// thread). A bind that completes after the call was cancelled or the manager was disposed is closed at once and
    /// never becomes current.
    /// </summary>
    /// <exception cref="DirectoryException">Bind failed.</exception>
    Task<IDirectorySession> ConnectAsync(ConnectionOptions options, NetworkCredential? credential, CancellationToken cancellationToken);

    /// <summary>Closes the current session (no-op when disconnected).</summary>
    Task DisconnectAsync();

    /// <summary>
    /// "Change domain controller" (one DC per session): opens a NEW session on <paramref name="server"/> with the
    /// connection options and in-memory credential of <see cref="Current"/>
    /// (<see cref="IDirectoryConnector.ConnectToServerAsync"/>), verifies it (bind, RootDSE, same domain, another DC),
    /// runs <paramref name="beforeSwitch"/> (e.g. cancel the background operations of the current session), then makes
    /// the new session current in the same place, raises <see cref="SessionChanged"/> and closes the previous session.
    /// On any failure or cancellation before the replacement the new session is closed and the current one stays
    /// current and open. Windows pinned to the previous session see it end.
    /// </summary>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="DomainControllerMismatchException">The server belongs to another domain.</exception>
    /// <exception cref="SameDomainControllerException">The server is the current DC (an alias or address).</exception>
    /// <exception cref="SessionEndedException">The current session was closed or replaced meanwhile.</exception>
    /// <exception cref="DirectoryException">The DC is unreachable or refused the bind.</exception>
    Task<DomainControllerSwitch> SwitchDomainControllerAsync(string server, Func<Task>? beforeSwitch, CancellationToken cancellationToken);

    /// <summary>The domain account policy of the current session, read once per session (never throws; Unknown on failure).</summary>
    Task<DomainPolicy> GetDomainPolicyAsync(CancellationToken cancellationToken);

    /// <summary>The current session or an <see cref="InvalidOperationException"/> when disconnected.</summary>
    IDirectorySession RequireSession();

    /// <summary>
    /// The well-known containers of the current session's domain (<c>wellKnownObjects</c> / <c>otherWellKnownObjects</c>
    /// of the domain head), read once per session and kept in memory; never throws (empty when not readable).
    /// </summary>
    Task<WellKnownObjectMap> GetWellKnownObjectsAsync(CancellationToken cancellationToken) => Task.FromResult(WellKnownObjectMap.Empty);
}

/// <summary>Default <see cref="ISessionManager"/> (scoped: one per connection window).</summary>
public sealed class SessionManager : ISessionManager, IAsyncDisposable, IDisposable
{
    private readonly IDirectoryConnector _connector;
    private readonly IAttributeFormatterFactory _formatterFactory;
    private readonly ILocalizer _localizer;
    private readonly ILogger<SessionManager> _logger;
    private readonly List<SessionEntry> _sessions = [];
    private readonly Lock _gate = new();
    private SessionEntry? _current;
    private IAttributeFormatter? _disconnectedFormatter;
    private bool _disposed;

    public SessionManager(
        IDirectoryConnector connector,
        IAttributeFormatterFactory formatterFactory,
        ILocalizer localizer,
        ILogger<SessionManager>? logger = null)
    {
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _formatterFactory = formatterFactory ?? throw new ArgumentNullException(nameof(formatterFactory));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _logger = logger ?? NullLogger<SessionManager>.Instance;
    }

    public event EventHandler? SessionChanged;

    public IDirectorySession? Current => _current?.Session;

    public IReadOnlyList<IDirectorySession> Sessions
    {
        get
        {
            lock (_gate)
            {
                return [.. _sessions.Select(s => s.Session)];
            }
        }
    }

    public bool IsConnected => _current is not null;

    public IAttributeFormatter Formatter => _current?.Formatter
        ?? (_disconnectedFormatter ??= new LocalizedAttributeFormatter(_formatterFactory.Create(SchemaCatalog.Empty), _localizer));

    public async Task<IDirectorySession> ConnectAsync(ConnectionOptions options, NetworkCredential? credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);

        // Locating the DC and binding may block (DNS, DsGetDcName, Kerberos) - never on the UI thread.
        var session = await Task.Run(() => _connector.ConnectAsync(options, credential, cancellationToken), cancellationToken)
            .ConfigureAwait(true);
        bool abandoned;
        SessionEntry? previous = null;
        lock (_gate)
        {
            // The bind may complete after its window closed (manager disposed) or after the connect was cancelled: such a
            // session never becomes current - it would be a connection with a credential and without a window.
            abandoned = _disposed || cancellationToken.IsCancellationRequested;
            if (!abandoned)
            {
                var entry = new SessionEntry(session, new LocalizedAttributeFormatter(_formatterFactory.Create(session.Schema), _localizer));
                previous = _current;
                _sessions.Clear();
                _sessions.Add(entry);
                _current = entry;
            }
        }

        if (abandoned)
        {
            await CloseLateAsync(session).ConfigureAwait(true);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
            throw new OperationCanceledException(cancellationToken);
        }

        SessionChanged?.Invoke(this, EventArgs.Empty);

        // One session per window: the previous one is closed (windows pinned to it see it end). A test double that hands
        // out one shared session object is not closed while it is the new session.
        if (previous is not null && !ReferenceEquals(previous.Session, session))
        {
            await CloseAsync(previous.Session).ConfigureAwait(true);
        }

        return session;
    }

    /// <summary>
    /// Closes the current session. Afterwards the manager is always disconnected (<see cref="Current"/> is null) - it
    /// never falls back to an older session, which could belong to another identity or another domain.
    /// </summary>
    public async Task DisconnectAsync()
    {
        SessionEntry? closing;
        lock (_gate)
        {
            closing = _current;
            if (closing is null)
            {
                return;
            }

            _sessions.Remove(closing);
            _current = null;
        }

        SessionChanged?.Invoke(this, EventArgs.Empty);
        await CloseAsync(closing.Session).ConfigureAwait(true);
    }

    public async Task<DomainControllerSwitch> SwitchDomainControllerAsync(string server, Func<Task>? beforeSwitch, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        var from = _current ?? throw new InvalidOperationException("Not connected to a directory.");
        var previous = from.Session;

        // Locating, binding and reading the schema may block (DNS, Kerberos) - never on the UI thread.
        var session = await Task.Run(() => _connector.ConnectToServerAsync(previous, server.Trim(), cancellationToken), cancellationToken)
            .ConfigureAwait(true);
        try
        {
            if (!DistinguishedName.AreEqual(session.Info.DefaultNamingContext, previous.Info.DefaultNamingContext))
            {
                throw new DomainControllerMismatchException(session.Info.Server, session.Info.DomainDnsName, previous.Info.DomainDnsName);
            }

            if (string.Equals(session.Info.Server, previous.Info.Server, StringComparison.OrdinalIgnoreCase))
            {
                throw new SameDomainControllerException(previous.Info.Server);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (beforeSwitch is not null)
            {
                await beforeSwitch().ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();
            }

            var entry = new SessionEntry(session, new LocalizedAttributeFormatter(_formatterFactory.Create(session.Schema), _localizer));
            lock (_gate)
            {
                // The window closed during the switch (manager disposed): the new session is closed below, never current.
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                var index = _sessions.IndexOf(from);
                if (!ReferenceEquals(_current, from) || index < 0)
                {
                    throw new SessionEndedException("The session was closed while the domain controller was being changed.");
                }

                _sessions[index] = entry;
                _current = entry;
            }
        }
        catch
        {
            await CloseAsync(session).ConfigureAwait(true);
            throw;
        }

        SessionChanged?.Invoke(this, EventArgs.Empty);
        await CloseAsync(previous).ConfigureAwait(true);
        return new DomainControllerSwitch(previous.Info.Server, session);
    }

    public Task<DomainPolicy> GetDomainPolicyAsync(CancellationToken cancellationToken)
    {
        var entry = _current;
        if (entry is null)
        {
            return Task.FromResult(DomainPolicy.Unknown);
        }

        lock (_gate)
        {
            // One read per session; a failed read is remembered too (the policy is optional information).
            entry.Policy ??= Task.Run(() => ReadPolicyAsync(entry.Session));
            return entry.Policy.WaitAsync(cancellationToken);
        }
    }

    public IDirectorySession RequireSession() =>
        Current ?? throw new InvalidOperationException("Not connected to a directory.");

    public Task<WellKnownObjectMap> GetWellKnownObjectsAsync(CancellationToken cancellationToken)
    {
        var entry = _current;
        if (entry is null)
        {
            return Task.FromResult(WellKnownObjectMap.Empty);
        }

        lock (_gate)
        {
            // One read per session; a failed read is remembered too (without the map nothing is marked or redirected).
            entry.WellKnown ??= Task.Run(() => ReadWellKnownObjectsAsync(entry.Session));
            return entry.WellKnown.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Closes every session. Idempotent; afterwards a connect or a domain controller switch that is still binding closes
    /// its new session instead of making it current, and new calls throw <see cref="ObjectDisposedException"/>.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        SessionEntry[] all;
        lock (_gate)
        {
            _disposed = true;
            all = [.. _sessions];
            _sessions.Clear();
            _current = null;
        }

        foreach (var entry in all)
        {
            await entry.Session.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Synchronous disposal for hosts that dispose the container synchronously (closes all sessions).</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// Closes a session whose bind completed too late (cancelled, or the manager was disposed). A test double that hands
    /// out one shared session object is not closed while that object is still the current session.
    /// </summary>
    private Task CloseLateAsync(IDirectorySession session)
    {
        _logger.LogInformation("A directory session that completed after its connect was abandoned is closed.");
        return ReferenceEquals(_current?.Session, session) ? Task.CompletedTask : CloseAsync(session);
    }

    /// <summary>Closes a session off the UI thread; failures are logged (type only), never thrown.</summary>
    private async Task CloseAsync(IDirectorySession session)
    {
        try
        {
            await Task.Run(() => session.DisposeAsync().AsTask()).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is DirectoryException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogWarning("Closing the directory session failed ({ErrorType}).", ex.GetType().Name);
        }
    }

    private async Task<DomainPolicy> ReadPolicyAsync(IDirectorySession session)
    {
        try
        {
            var domain = await session.GetAsync(session.Info.DefaultNamingContext, DomainPolicy.Attributes, CancellationToken.None)
                .ConfigureAwait(false);
            return domain is null ? DomainPolicy.Unknown : DomainPolicy.FromDomainObject(domain);
        }
        catch (Exception ex)
        {
            // Best effort: without the policy, lockout states are shown as "possibly locked".
            _logger.LogInformation("Domain policy not readable ({ErrorType}); lockout state will be approximated.", ex.GetType().Name);
            return DomainPolicy.Unknown;
        }
    }

    private async Task<WellKnownObjectMap> ReadWellKnownObjectsAsync(IDirectorySession session)
    {
        try
        {
            var domain = await session.GetAsync(session.Info.DefaultNamingContext, WellKnownObjects.Attributes, CancellationToken.None)
                .ConfigureAwait(false);
            return domain is null ? WellKnownObjectMap.Empty : WellKnownObjectMap.FromDomainObject(domain);
        }
        catch (Exception ex)
        {
            // Best effort: without the map the tree shows no badges and new objects go to the current container.
            _logger.LogInformation("Well-known containers not readable ({ErrorType}).", ex.GetType().Name);
            return WellKnownObjectMap.Empty;
        }
    }

    private sealed class SessionEntry(IDirectorySession session, IAttributeFormatter formatter)
    {
        public IDirectorySession Session { get; } = session;

        public IAttributeFormatter Formatter { get; } = formatter;

        public Task<DomainPolicy>? Policy { get; set; }

        public Task<WellKnownObjectMap>? WellKnown { get; set; }
    }
}
