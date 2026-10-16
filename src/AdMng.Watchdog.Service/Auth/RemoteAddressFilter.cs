using System.Net;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Service.Hosting;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Auth;

/// <summary>
/// Network allow-lists on <c>Connection.RemoteIpAddress</c> only – forwarded headers are never honoured (spec 17.1,
/// 17.3): <c>/healthz</c> answers loopback, the machine's own addresses and <c>Hosting.MonitoringSubnets</c>; every other
/// path, when <c>Hosting.AllowedRemoteSubnets</c> is set, answers only those subnets and loopback. Others get 403. A
/// request without a remote IP (in-process transport) counts as local.
/// </summary>
public sealed class RemoteAddressFilter(RequestDelegate next, IOptionsMonitor<ServiceOptions> options, MachineAddresses machine)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null || IPAddress.IsLoopback(Normalize(remote)))
        {
            return next(context);
        }

        var address = Normalize(remote);
        var hosting = options.CurrentValue.Hosting;
        bool allowed;
        if (context.Request.Path.Equals(WatchdogRoutes.Health, StringComparison.OrdinalIgnoreCase))
        {
            allowed = machine.IsOwnAddress(address) || InSubnets(address, hosting.MonitoringSubnets);
        }
        else
        {
            allowed = hosting.AllowedRemoteSubnets.Length == 0 || InSubnets(address, hosting.AllowedRemoteSubnets);
        }

        if (allowed)
        {
            return next(context);
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    /// <summary>True when <paramref name="address"/> is in one of the CIDR <paramref name="subnets"/> (invalid entries
    /// are ignored; a plain address counts as /32 or /128).</summary>
    public static bool InSubnets(IPAddress address, IEnumerable<string> subnets)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(subnets);
        var normalized = Normalize(address);
        foreach (var subnet in subnets)
        {
            if (string.IsNullOrWhiteSpace(subnet))
            {
                continue;
            }

            var text = subnet.Trim();
            if (IPNetwork.TryParse(text, out var network) && network.Contains(normalized))
            {
                return true;
            }

            if (IPAddress.TryParse(text, out var single) && Normalize(single).Equals(normalized))
            {
                return true;
            }
        }

        return false;
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
