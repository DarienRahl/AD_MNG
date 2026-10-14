using AdMng.Core.Directory;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Tests.Notifications.Fakes;

/// <summary>An <see cref="IDirectorySessionProvider"/> handing out one session (or failing) and recording failure reports.</summary>
internal sealed class FakeSessionProvider(IDirectorySession? session) : IDirectorySessionProvider
{
    public DirectoryConnectionState State => session is null ? DirectoryConnectionState.Failed : DirectoryConnectionState.Connected;

    public string? CurrentServer => session?.Info.Server;

    public int FailureReports { get; private set; }

    public ValueTask<IDirectorySession> GetSessionAsync(CancellationToken cancellationToken) =>
        session is null
            ? ValueTask.FromException<IDirectorySession>(new DirectoryException(DirectoryErrorKind.ServerUnavailable, "Serwer niedostępny: dc01.contoso.test"))
            : ValueTask.FromResult(session);

    public void ReportFailure(IDirectorySession session, Exception error) => FailureReports++;
}
