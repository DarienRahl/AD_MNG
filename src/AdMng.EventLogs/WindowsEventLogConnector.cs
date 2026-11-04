using AdMng.Core.Computers.Remote;
using AdMng.Core.Directory;
using AdMng.Core.EventLogs;
using AdMng.Directory;

namespace AdMng.EventLogs;

/// <summary>
/// <see cref="IEventLogConnector"/> over the Windows event log API (design note e-event-logs.md 1, 3.2): computers over
/// MS-EVEN6 (<c>EventLogSession</c>, Kerberos only, the window's Windows account or a read-only copy of its in-memory
/// password, by FQDN – an IP literal or a short name is refused before any connection) and <c>.evtx</c> files the
/// operator chose. Windows only: elsewhere <see cref="IsSupported"/> is false and every open fails with
/// <see cref="EventLogAccessError.NotSupported"/>. Nothing is logged or written (CLAUDE.md rules 1 and 2); failures carry
/// the error kind and the Windows code only.
/// </summary>
/// <param name="resolver">
/// Resolves the host before the RPC connection, so a name that does not resolve is reported as
/// <see cref="EventLogAccessError.Unreachable"/> instead of the ambiguous RPC "server unavailable"; null skips the check.
/// </param>
public sealed class WindowsEventLogConnector(IHostAddressResolver? resolver = null) : IEventLogConnector
{
    /// <summary>Win32 WSAHOST_NOT_FOUND (11001): reported when the host name does not resolve.</summary>
    public const int HostNotFoundError = 11001;

    public bool IsSupported => OperatingSystem.IsWindows();

    /// <exception cref="EventLogAccessException">
    /// <see cref="EventLogAccessError.NotSupported"/>: not Windows, not an FQDN (code 123), or a window without a Windows
    /// logon (simple bind); <see cref="EventLogAccessError.Unreachable"/>, <see cref="EventLogAccessError.RpcUnavailable"/>,
    /// <see cref="EventLogAccessError.AccessDenied"/> or <see cref="EventLogAccessError.Failed"/> from the first connection.
    /// </exception>
    public async Task<IEventLogReader> OpenComputerAsync(string host, IDirectorySession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(session);
        var name = EventLogHosts.Require(host);
        if (!OperatingSystem.IsWindows())
        {
            throw new EventLogAccessException(EventLogAccessError.NotSupported);
        }

        SessionLogon logon;
        try
        {
            logon = SessionLogons.Of(session);
        }
        catch (RemoteActionException ex)
        {
            throw EventLogErrors.Translate(ex, file: false);
        }

        try
        {
            await EnsureResolvesAsync(name, cancellationToken).ConfigureAwait(false);
            var reader = new WindowsHostEventLogReader(name, logon);
            try
            {
                await reader.ProbeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await reader.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            return reader;
        }
        catch
        {
            logon.Dispose();
            throw;
        }
    }

    /// <exception cref="ArgumentException">No path, or a path that is not fully qualified.</exception>
    /// <exception cref="EventLogAccessException"><see cref="EventLogAccessError.NotSupported"/> when not on Windows.</exception>
    public IEventLogReader OpenFiles(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            throw new ArgumentException("At least one file is required.", nameof(paths));
        }

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            {
                throw new ArgumentException("Every file path must be fully qualified.", nameof(paths));
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new EventLogAccessException(EventLogAccessError.NotSupported);
        }

        return new WindowsFileEventLogReader([.. paths.Select(Path.GetFullPath)]);
    }

    private async Task EnsureResolvesAsync(string host, CancellationToken cancellationToken)
    {
        if (resolver is null)
        {
            return;
        }

        IReadOnlyList<System.Net.IPAddress> addresses;
        try
        {
            addresses = await resolver.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteActionException ex)
        {
            throw new EventLogAccessException(EventLogAccessError.Unreachable, ex) { NativeErrorCode = ex.ErrorCode };
        }

        if (addresses.Count == 0)
        {
            throw new EventLogAccessException(EventLogAccessError.Unreachable) { NativeErrorCode = HostNotFoundError };
        }
    }
}
