using System.Net;
using System.Net.NetworkInformation;

namespace AdMng.Watchdog.Service.Auth;

/// <summary>The machine's own IP addresses (refreshed every minute) – a call to <c>/healthz</c> from one of them counts
/// as local (spec 17.2; the install script's <c>--health</c> call uses the configured FQDN).</summary>
public sealed class MachineAddresses(TimeProvider timeProvider)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
    private readonly Lock _gate = new();
    private HashSet<IPAddress> _addresses = [];
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    public bool IsOwnAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            if (now - _loadedAt > RefreshInterval)
            {
                _addresses = Load();
                _loadedAt = now;
            }

            return _addresses.Contains(normalized);
        }
    }

    private static HashSet<IPAddress> Load()
    {
        try
        {
            return [.. NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(u => u.Address)];
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }
}
