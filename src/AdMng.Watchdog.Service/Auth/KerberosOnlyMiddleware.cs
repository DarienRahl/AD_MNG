using AdMng.Watchdog.Service.Hosting;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Auth;

/// <summary>
/// The enforcement point of "Kerberos only" for both servers (spec 17.3) – on HTTP.sys the Negotiate events never run:
/// an authenticated identity whose <c>AuthenticationType</c> is not Kerberos gets 401. Development and Test
/// authentication are exempt (they never run with the Negotiate mode).
/// </summary>
public sealed class KerberosOnlyMiddleware(RequestDelegate next, IOptionsMonitor<ServiceOptions> options)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = options.CurrentValue;
        var identity = context.User.Identity;
        if (settings.Authentication == WatchdogAuthenticationMode.Negotiate
            && settings.Hosting.RequireKerberos
            && identity?.IsAuthenticated == true
            && !string.Equals(identity.AuthenticationType, KerberosOnlyEvents.Kerberos, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        return next(context);
    }
}
