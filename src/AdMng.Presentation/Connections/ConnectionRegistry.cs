using AdMng.Core.Directory;
using AdMng.Presentation.Services;

namespace AdMng.Presentation.Connections;

/// <summary>
/// The connection contexts of all open connection windows (app-wide singleton): the connected ones for other windows
/// (<see cref="Connections"/>) and the colour and ordinal assignment of 4.3 of the design note. Thread-safe;
/// <see cref="ConnectionsChanged"/> is raised on the UI thread. Holds no credential and stores nothing.
/// </summary>
public sealed class ConnectionRegistry
{
    private readonly Lock _gate = new();
    private readonly List<ConnectionContext> _contexts = [];
    private readonly IUiDispatcher _dispatcher;

    public ConnectionRegistry(IUiDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>A connection window opened, closed, connected, disconnected or changed its identity (UI thread).</summary>
    public event EventHandler? ConnectionsChanged;

    /// <summary>The connected windows, in opening order.</summary>
    public IReadOnlyList<IConnectionHandle> Connections
    {
        get
        {
            lock (_gate)
            {
                return [.. _contexts.Where(c => c.Identity is not null)];
            }
        }
    }

    /// <summary>Every open connection context (also those not connected), in opening order.</summary>
    public IReadOnlyList<IConnectionHandle> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _contexts];
            }
        }
    }

    /// <summary>The open connection with <paramref name="id"/>, or null.</summary>
    public IConnectionHandle? Find(Guid id)
    {
        lock (_gate)
        {
            return _contexts.FirstOrDefault(c => c.Id == id);
        }
    }

    internal void Register(ConnectionContext context)
    {
        lock (_gate)
        {
            _contexts.Add(context);
        }

        RaiseChanged();
    }

    internal void Unregister(ConnectionContext context)
    {
        bool removed;
        lock (_gate)
        {
            removed = _contexts.Remove(context);
        }

        if (removed)
        {
            RaiseChanged();
        }
    }

    /// <summary>
    /// Sets the identity of <paramref name="context"/> for the session <paramref name="info"/> (null = disconnected) and
    /// returns whether it changed. The colour and the ordinal are assigned under the lock, so two windows that connect at
    /// the same time cannot take the same automatic colour. A context that is no longer registered (its window closed) is
    /// left alone: a bind that completes late never re-registers a closed window.
    /// </summary>
    internal bool Update(ConnectionContext context, SessionInfo? info)
    {
        lock (_gate)
        {
            if (!_contexts.Contains(context))
            {
                return false;
            }

            var previous = context.Identity;
            ConnectionIdentity? identity = null;
            if (info is not null)
            {
                if (previous is not null && previous.IsSameIdentity(info.DomainDnsName, info.BoundIdentity)
                    && string.Equals(previous.ProfileName, context.ProfileName, StringComparison.Ordinal)
                    && IsSameColorChoice(previous, context.PreferredColor))
                {
                    // "Zmień kontroler domeny" (same domain and account): the colour and the ordinal stay, the DC follows.
                    identity = previous with { Server = info.Server, Security = info.Security };
                }
                else
                {
                    var (color, source) = AssignColor(context, info, context.PreferredColor);
                    identity = ConnectionIdentity.From(info, context.ProfileName, color, source, AssignOrdinal(context, info));
                }
            }

            if (Equals(previous, identity))
            {
                return false;
            }

            context.SetIdentity(identity);
        }

        RaiseChanged();
        return true;
    }

    /// <summary>
    /// The profile's colour when one was chosen (never changed); otherwise the automatic colour of the domain, moved to the
    /// next free palette entry when an open window of ANOTHER identity (domain + account) already shows it. Open windows
    /// keep their colour; a closed window frees its colour.
    /// </summary>
    private (ConnectionColor Color, ConnectionColorSource Source) AssignColor(ConnectionContext context, SessionInfo info, ConnectionColor? preferred)
    {
        if (preferred is { } chosen)
        {
            return (chosen, ConnectionColorSource.Profile);
        }

        var automatic = ConnectionColors.Automatic(info.DomainDnsName);
        var taken = _contexts
            .Where(c => !ReferenceEquals(c, context) && c.Identity is { } other && !other.IsSameIdentity(info.DomainDnsName, info.BoundIdentity))
            .Select(c => c.Identity!.Color)
            .ToHashSet();
        var color = automatic;
        for (var i = 0; i < ConnectionColors.Palette.Count; i++)
        {
            if (!taken.Contains(color))
            {
                return (color, ConnectionColorSource.Automatic);
            }

            color = ConnectionColors.Next(color);
        }

        // More identities open than colours: the domain text still tells them apart.
        return (automatic, ConnectionColorSource.Automatic);
    }

    /// <summary>The colour was chosen the same way (automatic, or the same profile colour) as for <paramref name="previous"/>.</summary>
    private static bool IsSameColorChoice(ConnectionIdentity previous, ConnectionColor? preferred) => preferred is { } chosen
        ? previous.ColorSource == ConnectionColorSource.Profile && previous.Color == chosen
        : previous.ColorSource == ConnectionColorSource.Automatic;

    /// <summary>The smallest ordinal not used by another open window of the identical domain and account.</summary>
    private int AssignOrdinal(ConnectionContext context, SessionInfo info)
    {
        var used = _contexts
            .Where(c => !ReferenceEquals(c, context) && c.Identity is { } other && other.IsSameIdentity(info.DomainDnsName, info.BoundIdentity))
            .Select(c => c.Identity!.Ordinal)
            .ToHashSet();
        var ordinal = 1;
        while (used.Contains(ordinal))
        {
            ordinal++;
        }

        return ordinal;
    }

    private void RaiseChanged() => _ = _dispatcher.RunAsync(() => ConnectionsChanged?.Invoke(this, EventArgs.Empty));
}
