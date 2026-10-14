using AdMng.Core.Directory;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Service.Directory;

/// <summary>The session provider when <c>Directory.Disabled</c> (smoke test, API-only development runs): never connects.</summary>
public sealed class DisabledDirectorySessionProvider : IDirectorySessionProvider
{
    public DirectoryConnectionState State => DirectoryConnectionState.Disconnected;

    public string? CurrentServer => null;

    public ValueTask<IDirectorySession> GetSessionAsync(CancellationToken cancellationToken) =>
        ValueTask.FromException<IDirectorySession>(new DirectoryException(DirectoryErrorKind.ServerUnavailable, "Directory monitoring is disabled (Directory.Disabled)."));

    public void ReportFailure(IDirectorySession session, Exception error)
    {
    }
}
