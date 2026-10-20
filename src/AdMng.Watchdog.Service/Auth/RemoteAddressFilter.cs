using System.Net;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Service.Hosting;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Auth;

/// <summary>
/// Network allow-lists on <c>Connection.RemoteIpAddress</c> only – forwarded headers are never honoured (spec 17.1,
/// 17.3): <c>/healthz</c> answers loopback, the machine's own addresses and <c>Hosting.MonitoringSubnets</c>; every other
/// path, when <c>Hosting.AllowedRemoteSubnets</c> is set, answers only those subnets and loopback. Others get 403. A
/// request without a remote IP (in-process transport, <c>--smoke-test</c>) counts as local.
/// <para>
/// Linux with Negotiate (stage B design 4.3 item 1, 6.1): loopback and the machine's own addresses are <b>not</b> exempt
/// on other paths than <c>/healthz</c> – they pass only when they fall into <c>AllowedRemoteSubnets</c> (also when the
/// list is empty), so a local process holding the shared keytab cannot use a forged ticket against the API from the
/// server itself. The check runs before authentication: such a request never reaches the Negotiate handler.
/// </para>
/// </summary>
public sealed class RemoteAddressFilter(RequestDelegate next, IOptionsMonitor<ServiceOptions> options, MachineAddresses machine)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null)
        {
            return next(context);
        }

        var address = Normalize(remote);
        var settings = options.CurrentValue;
        var hosting = settings.Hosting;
        bool allowed;
        if (IsHealthRequest(context))
        {
            allowed = IPAddress.IsLoopback(address) || machine.IsOwnAddress(address) || InSubnets(address, hosting.MonitoringSubnets);
        }
        else if (!ExemptsLocalCallers(settings, OperatingSystem.IsLinux()) && (IPAddress.IsLoopback(address) || machine.IsOwnAddress(address)))
        {
            allowed = InSubnets(address, hosting.AllowedRemoteSubnets);
        }
        else
        {
            allowed = IPAddress.IsLoopback(address) || hosting.AllowedRemoteSubnets.Length == 0 || InSubnets(address, hosting.AllowedRemoteSubnets);
        }

        if (allowed)
        {
            return next(context);
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    /// <summary>False on Linux with Negotiate authentication: loopback and the machine's own addresses must then be in
    /// <c>AllowedRemoteSubnets</c> like any other caller (stage B design 6.1). Development and Test authentication keep the
    /// exemption (loopback-only lab and test modes), and so does Windows.</summary>
    public static bool ExemptsLocalCallers(ServiceOptions options, bool linux)
    {
        ArgumentNullException.ThrowIfNull(options);
        return !(linux && options.Authentication == WatchdogAuthenticationMode.Negotiate);
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

    /// <summary>The health rule follows the endpoint routing matched (it also maps "/healthz/" and any letter case), with
    /// the path – without trailing slashes, case-insensitive – as the fallback before routing.</summary>
    private static bool IsHealthRequest(HttpContext context)
    {
        if (context.GetEndpoint() is RouteEndpoint endpoint
            && string.Equals(endpoint.RoutePattern.RawText, WatchdogRoutes.Health, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var path = context.Request.Path.Value?.TrimEnd('/');
        return string.Equals(path, WatchdogRoutes.Health, StringComparison.OrdinalIgnoreCase);
    }
}
