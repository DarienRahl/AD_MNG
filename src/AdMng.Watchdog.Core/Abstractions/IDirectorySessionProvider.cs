using AdMng.Core.Directory;

namespace AdMng.Watchdog.Core.Abstractions;

public enum DirectoryConnectionState { Disconnected, Connecting, Connected, Failed }

/// <summary>The live LDAP session of the service, pinned to the watched DC.</summary>
public interface IDirectorySessionProvider
{
    DirectoryConnectionState State { get; }
    string? CurrentServer { get; }
    /// <summary>The live session pinned to the watched DC (connects / reconnects / fails over). Callers never dispose it.
    /// PDC selection is the provider's job: with Directory.PreferPdc it reads fSMORoleOwner of the domain head on
    /// connect and (re)connects to the PDC emulator; Core only reports IsPdc.</summary>
    ValueTask<IDirectorySession> GetSessionAsync(CancellationToken cancellationToken);
    /// <summary>Reports a transport-level failure (ServerUnavailable, Timeout…) so the next call reconnects.</summary>
    void ReportFailure(IDirectorySession session, Exception error);
}
