using AdMng.Core.Directory;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Tests.Rules.Fakes;

/// <summary>Hands out one directory session (or fails with a DirectoryException while <see cref="Failure"/> is set).</summary>
internal sealed class FakeSessionProvider(IDirectorySession session) : IDirectorySessionProvider
{
    public IDirectorySession Session { get; set; } = session;

    public DirectoryException? Failure { get; set; }

    public int Failures { get; private set; }

    public DirectoryConnectionState State => Failure is null ? DirectoryConnectionState.Connected : DirectoryConnectionState.Failed;

    public string? CurrentServer => Session.Info.Server;

    public ValueTask<IDirectorySession> GetSessionAsync(CancellationToken cancellationToken) =>
        Failure is { } failure ? ValueTask.FromException<IDirectorySession>(failure) : ValueTask.FromResult(Session);

    public void ReportFailure(IDirectorySession session, Exception error) => Failures++;
}
