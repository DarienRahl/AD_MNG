using AdMng.Core.Directory;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Testing.Scenarios;

/// <summary>
/// <see cref="IDirectorySessionProvider"/> over one or more fake sessions: <see cref="Use"/> switches the pinned DC
/// (failover), <see cref="FailWith"/> makes connecting fail until <see cref="Recover"/>.
/// </summary>
public sealed class ScenarioSessionProvider : IDirectorySessionProvider
{
    private readonly Lock _gate = new();
    private IDirectorySession _session;
    private DirectoryErrorKind? _failure;
    private int _reportedFailures;

    public ScenarioSessionProvider(IDirectorySession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    public DirectoryConnectionState State
    {
        get
        {
            lock (_gate)
            {
                return _failure is null ? DirectoryConnectionState.Connected : DirectoryConnectionState.Failed;
            }
        }
    }

    public string? CurrentServer
    {
        get
        {
            lock (_gate)
            {
                return _session.Info.Server;
            }
        }
    }

    /// <summary>Number of <see cref="ReportFailure"/> calls.</summary>
    public int ReportedFailures => Volatile.Read(ref _reportedFailures);

    /// <summary>Pins another session (failover to another DC).</summary>
    public void Use(IDirectorySession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            _session = session;
        }
    }

    /// <summary>Every <see cref="GetSessionAsync"/> throws a <see cref="DirectoryException"/> of this kind.</summary>
    public void FailWith(DirectoryErrorKind kind)
    {
        lock (_gate)
        {
            _failure = kind;
        }
    }

    /// <summary>Ends <see cref="FailWith"/>.</summary>
    public void Recover()
    {
        lock (_gate)
        {
            _failure = null;
        }
    }

    public ValueTask<IDirectorySession> GetSessionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return _failure is { } kind
                ? throw new DirectoryException(kind, "Simulated connection failure.")
                : ValueTask.FromResult(_session);
        }
    }

    public void ReportFailure(IDirectorySession session, Exception error) => Interlocked.Increment(ref _reportedFailures);
}
