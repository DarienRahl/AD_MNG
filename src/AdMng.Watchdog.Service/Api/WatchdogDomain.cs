using AdMng.Core.Directory;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Service.Hosting;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Api;

/// <summary>The DNS name of the watched domain for API responses and configuration events: the bootstrap
/// <c>Directory.Domain</c>, else the connected session's domain, else the computer's domain. Never connects.</summary>
public sealed class WatchdogDomain(IOptionsMonitor<ServiceOptions> options, IDirectorySessionProvider directory, IDirectoryConnector connector)
{
    public async ValueTask<string?> GetAsync(CancellationToken cancellationToken)
    {
        if (options.CurrentValue.Directory.Domain is { Length: > 0 } configured)
        {
            return configured.Trim();
        }

        if (directory.State == DirectoryConnectionState.Connected)
        {
            try
            {
                return (await directory.GetSessionAsync(cancellationToken).ConfigureAwait(false)).Info.DomainDnsName;
            }
            catch (DirectoryException)
            {
                // Dropped meanwhile – fall back to the computer's domain.
            }
        }

        return connector.GetComputerDomain();
    }
}
