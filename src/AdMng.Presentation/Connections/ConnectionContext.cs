using AdMng.Presentation.Session;

namespace AdMng.Presentation.Connections;

/// <summary>
/// One connection window (scoped per window): its id, the profile and colour the connect dialog chose, the identity of its
/// current session (title, strip, badge) and its lifetime. It follows its own <see cref="ISessionManager"/> and keeps its
/// entry in <see cref="ConnectionRegistry"/> up to date. When the main window starts closing (<see cref="EndLifetime"/>) it
/// leaves the registry at once, and after that a session change (a bind that completes late) is ignored. Holds no credential.
/// </summary>
public sealed class ConnectionContext : IConnectionHandle, IDisposable
{
    private readonly ISessionManager _sessions;
    private readonly ConnectionRegistry _registry;
    private readonly CancellationTokenSource _lifetimeSource = new();
    private readonly CancellationToken _lifetime;
    private readonly Lock _pinnedGate = new();
    private readonly List<ConnectionReadContext> _pinned = [];
    private ConnectionIdentity? _identity;
    private int _closing;
    private int _disposed;

    public ConnectionContext(ISessionManager sessions, ConnectionRegistry registry)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _lifetime = _lifetimeSource.Token;
        Id = Guid.NewGuid();
        _registry.Register(this);
        _sessions.SessionChanged += OnSessionChanged;
        if (_sessions.Current is not null)
        {
            Refresh();
        }
    }

    public Guid Id { get; }

    /// <summary>The connection profile the connect dialog used (set before connecting), or null.</summary>
    public string? ProfileName { get; set; }

    /// <summary>The colour chosen in the profile or the connect dialog (set before connecting); null = automatic.</summary>
    public ConnectionColor? PreferredColor { get; set; }

    public ConnectionIdentity? Identity => Volatile.Read(ref _identity);

    /// <summary>"contoso.test · CONTOSO\jan.kowalski", or null while not connected.</summary>
    public string? Label => Identity?.Label;

    /// <summary>" – contoso.test" for the titles of child windows; empty while not connected.</summary>
    public string TitleSuffix => Identity is { } identity ? $" – {identity.DomainDnsName}" : string.Empty;

    /// <summary>
    /// Cancelled when the main window starts closing or the scope is disposed. Work that is not an activity (planning,
    /// connect of a pending connect window, reads that outlive their view) links to it.
    /// </summary>
    public CancellationToken Lifetime => _lifetime;

    /// <summary>The window closed (<see cref="Dispose"/>).</summary>
    public bool IsClosed => Volatile.Read(ref _disposed) == 1;

    /// <summary>The main window started closing (<see cref="EndLifetime"/>): other windows no longer see or read this connection.</summary>
    public bool IsClosing => Volatile.Read(ref _closing) == 1;

    public event EventHandler? Changed;

    public event EventHandler? Closed;

    public event EventHandler? SessionChanged;

    /// <summary>
    /// A read context of the current session for another window's read-only feature (the comparison), or null while not
    /// connected or once the window started closing. It ends when this window's session changes or the window starts closing.
    /// </summary>
    public ConnectionReadContext? Pin()
    {
        if (IsClosing || _sessions.Current is not { } session)
        {
            return null;
        }

        var formatter = _sessions.Formatter;
        var policy = _sessions.GetDomainPolicyAsync(CancellationToken.None);
        var context = ConnectionReadContext.Create(Id, Identity, session, formatter, policy);
        lock (_pinnedGate)
        {
            _pinned.RemoveAll(c => c.IsEnded);

            // The session changed or the window started closing while the context was built: it never reads.
            if (IsClosing || !ReferenceEquals(session, _sessions.Current))
            {
                context.End();
                return null;
            }

            _pinned.Add(context);
        }

        return context;
    }

    /// <summary>
    /// The main window starts closing: cancels <see cref="Lifetime"/> and closes the connection for the other windows at
    /// once, not after the close pipeline (activities, then the session, may take seconds): it leaves the registry, the
    /// read contexts they pinned end, <see cref="Pin"/> returns null and <see cref="Closed"/> is raised. Idempotent.
    /// </summary>
    public void EndLifetime()
    {
        try
        {
            _lifetimeSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed with the scope.
        }
        catch (AggregateException)
        {
            // A registration callback threw; the token is cancelled anyway.
        }

        if (Interlocked.Exchange(ref _closing, 1) == 1)
        {
            return;
        }

        EndPinned(keep: null);
        _registry.Unregister(this);
        var closed = Interlocked.Exchange(ref Closed, null);
        closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A token cancelled by <paramref name="token"/> or by the window's <see cref="Lifetime"/>.</summary>
    public CancellationTokenSource LinkLifetime(CancellationToken token) => CancellationTokenSource.CreateLinkedTokenSource(_lifetime, token);

    /// <summary>Ends its lifetime (unregistered, <see cref="Closed"/> raised once, if not yet) and drops its subscribers.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _sessions.SessionChanged -= OnSessionChanged;
        EndLifetime();
        Changed = null;
        SessionChanged = null;
    }

    /// <summary>The id only (no domain, no account).</summary>
    public override string ToString() => $"ConnectionContext({Id:N})";

    internal void SetIdentity(ConnectionIdentity? identity) => Volatile.Write(ref _identity, identity);

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        if (!IsClosing)
        {
            // Other windows' reads of the previous session end first (their reads then fail as "ended", never as a read
            // through another session); then the identity and the listeners follow.
            EndPinned(keep: _sessions.Current);
            Refresh();
            SessionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Ends the pinned read contexts that do not read through <paramref name="keep"/>.</summary>
    private void EndPinned(object? keep)
    {
        ConnectionReadContext[] ending;
        lock (_pinnedGate)
        {
            ending = [.. _pinned.Where(c => !c.IsPinnedTo(keep))];
            _pinned.RemoveAll(c => !c.IsPinnedTo(keep));
        }

        foreach (var context in ending)
        {
            context.End();
        }
    }

    private void Refresh()
    {
        if (_registry.Update(this, _sessions.Current?.Info))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
