using System.Net;
using System.Net.Sockets;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary><see cref="IReverseDnsResolver"/> over <see cref="Dns.GetHostEntryAsync(string, CancellationToken)"/> (reverse lookup of an address) with
/// a <see cref="Timeout"/> of 2 seconds (section 11.4 #6); failures and timeouts yield null.</summary>
public sealed class SystemReverseDnsResolver(TimeProvider timeProvider) : IReverseDnsResolver
{
    /// <summary>Per-lookup timeout.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <inheritdoc/>
    public async Task<string?> ResolveAsync(string ipAddress, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(ipAddress, out var address))
        {
            return null;
        }

        using var timeout = new CancellationTokenSource(Timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var entry = await Dns.GetHostEntryAsync(address.ToString(), linked.Token).ConfigureAwait(false);
            var name = entry.HostName?.TrimEnd('.');
            return string.IsNullOrEmpty(name) || IPAddress.TryParse(name, out _) ? null : name;
        }
        catch (SocketException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
