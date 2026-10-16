using System.Net;
using AdMng.Core.Directory;
using AdMng.Core.Formatting;
using AdMng.Core.Schema;
using AdMng.Presentation.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdMng.Presentation.Session;

/// <summary>Owns the directory sessions of the application.</summary>
public interface ISessionManager
{
    /// <summary>The active session, or null when disconnected.</summary>
    IDirectorySession? Current { get; }

    /// <summary>All open sessions (one today; multiple concurrent sessions are a later phase).</summary>
    IReadOnlyList<IDirectorySession> Sessions { get; }

    bool IsConnected { get; }

    /// <summary>Localized formatter for <see cref="Current"/> (schema aware); a schema-less formatter when disconnected.</summary>
    IAttributeFormatter Formatter { get; }

    /// <summary>Raised after <see cref="Current"/> changed (on the thread that connected/disconnected).</summary>
    event EventHandler? SessionChanged;

    /// <summary>Connects (bind + RootDSE + schema) and makes the new session current.</summary>
    /// <exception cref="DirectoryException">Bind failed.</exception>
    Task<IDirectorySession> ConnectAsync(ConnectionOptions options, NetworkCredential? credential, CancellationToken cancellationToken);

    /// <summary>Closes the current session (no-op when disconnected).</summary>
    Task DisconnectAsync();

    /// <summary>The domain account policy of the current session, read once per session (never throws; Unknown on failure).</summary>
    Task<DomainPolicy> GetDomainPolicyAsync(CancellationToken cancellationToken);

    /// <summary>The current session or an <see cref="InvalidOperationException"/> when disconnected.</summary>
    IDirectorySession RequireSession();
}

/// <summary>Default <see cref="ISessionManager"/>.</summary>
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
        // Locating the DC and binding may block (DNS, DsGetDcName, Kerberos) - never on the UI thread.
        var session = await Task.Run(() => _connector.ConnectAsync(options, credential, cancellationToken), cancellationToken)
            .ConfigureAwait(true);
        var entry = new SessionEntry(session, new LocalizedAttributeFormatter(_formatterFactory.Create(session.Schema), _localizer));
        lock (_gate)
        {
            _sessions.Add(entry);
            _current = entry;
        }

        SessionChanged?.Invoke(this, EventArgs.Empty);
        return session;
    }

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
            _current = _sessions.Count > 0 ? _sessions[^1] : null;
        }

        SessionChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await Task.Run(() => closing.Session.DisposeAsync().AsTask()).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is DirectoryException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogWarning("Closing the directory session failed ({ErrorType}).", ex.GetType().Name);
        }
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

    public async ValueTask DisposeAsync()
    {
        SessionEntry[] all;
        lock (_gate)
        {
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

    private sealed class SessionEntry(IDirectorySession session, IAttributeFormatter formatter)
    {
        public IDirectorySession Session { get; } = session;

        public IAttributeFormatter Formatter { get; } = formatter;

        public Task<DomainPolicy>? Policy { get; set; }
    }
}
