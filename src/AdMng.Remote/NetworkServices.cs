using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AdMng.Core.Computers.Remote;

namespace AdMng.Remote;

/// <summary>
/// The workstation's DNS resolver (K1 design note 4.4): 2 s per name, IPv4 first; a name that does not resolve gives no
/// addresses. Results are only returned, never cached or logged.
/// </summary>
public sealed class DnsHostAddressResolver : IHostAddressResolver
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (!HostNames.IsValid(host))
        {
            return [];
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(HostNames.Normalize(host), timeout.Token).ConfigureAwait(false);
            return [.. addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)];
        }
        catch (SocketException)
        {
            return [];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RemoteActionException(RemoteErrorKind.Timeout);
        }
    }
}

/// <summary>ICMP echo through <see cref="Ping"/> (no credential); a name that does not resolve answers nothing.</summary>
public sealed class IcmpPingService : IPingService
{
    public async Task<PingResult> PingAsync(string host, int count, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!HostNames.IsValid(host))
        {
            throw new RemoteActionException(RemoteErrorKind.NotSupported);
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var name = HostNames.Normalize(host);
        var roundTrips = new List<TimeSpan?>(count);
        IPAddress? address = null;
        using var ping = new Ping();
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var reply = await ping.SendPingAsync(name, timeout, buffer: null, options: null, cancellationToken).ConfigureAwait(false);
                if (reply.Status == IPStatus.Success)
                {
                    address ??= reply.Address;
                    roundTrips.Add(TimeSpan.FromMilliseconds(reply.RoundtripTime));
                }
                else
                {
                    roundTrips.Add(null);
                }
            }
            catch (PingException)
            {
                roundTrips.Add(null);
            }
        }

        return new PingResult(name, address, roundTrips);
    }
}
