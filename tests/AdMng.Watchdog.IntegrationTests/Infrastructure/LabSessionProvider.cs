using AdMng.Core.Directory;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.IntegrationTests.Infrastructure;

/// <summary>
/// <see cref="IDirectorySessionProvider"/> over the real LDAP connector, pinned to the lab DC (CLAUDE.md rule 4) and
/// bound as the engine's account: connects on first use, drops the session after a reported transport failure so the
/// next call reconnects.
/// </summary>
public sealed class LabSessionProvider(EngineAccount account) : IDirectorySessionProvider, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IDirectorySession? _session;

    public DirectoryConnectionState State { get; private set; } = DirectoryConnectionState.Disconnected;

    public string? CurrentServer => _session?.Info.Server;

    public async ValueTask<IDirectorySession> GetSessionAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_session is { } existing)
            {
                return existing;
            }

            State = DirectoryConnectionState.Connecting;
            var (user, password) = account == EngineAccount.Administrator
                ? (WatchdogLab.AdminUser, WatchdogLab.AdminPassword)
                : (WatchdogLab.ReadOnlyUser, WatchdogLab.ReadOnlyPassword);
            _session = await LabFixture.ConnectAsync(WatchdogLab.Plain(user), password, cancellationToken);
            State = DirectoryConnectionState.Connected;
            return _session;
        }
        catch (DirectoryException)
        {
            State = DirectoryConnectionState.Failed;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void ReportFailure(IDirectorySession session, Exception error)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (Interlocked.CompareExchange(ref _session, null, session) == session)
        {
            State = DirectoryConnectionState.Failed;
            _ = session.DisposeAsync().AsTask();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _session, null) is { } session)
        {
            await session.DisposeAsync();
        }

        _gate.Dispose();
    }
}
