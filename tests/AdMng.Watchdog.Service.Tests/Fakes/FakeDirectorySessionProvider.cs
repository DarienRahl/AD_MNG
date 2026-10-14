using AdMng.Core.Directory;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Service.Tests.Fakes;

/// <summary>Serves one in-memory session (or none: <see cref="DirectoryException"/> ServerUnavailable).</summary>
public sealed class FakeDirectorySessionProvider(IDirectorySession? session) : IDirectorySessionProvider
{
    public DirectoryConnectionState State => session is null ? DirectoryConnectionState.Failed : DirectoryConnectionState.Connected;

    public string? CurrentServer => session?.Info.Server;

    public int Failures { get; private set; }

    public ValueTask<IDirectorySession> GetSessionAsync(CancellationToken cancellationToken) =>
        session is null
            ? ValueTask.FromException<IDirectorySession>(new DirectoryException(DirectoryErrorKind.ServerUnavailable, "No test directory."))
            : ValueTask.FromResult(session);

    public void ReportFailure(IDirectorySession session, Exception error) => Failures++;
}
