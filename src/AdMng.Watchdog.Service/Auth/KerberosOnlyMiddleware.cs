using System.Security.Claims;
using AdMng.Watchdog.Service.Hosting;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Auth;

/// <summary>
/// The enforcement point of "Kerberos only" for both servers (spec 17.3) – on HTTP.sys the Negotiate events never run:
/// an authenticated identity that was not authenticated by Kerberos gets 401. Kestrel's Negotiate handler reports
/// <c>Kerberos</c>/<c>NTLM</c> directly; HTTP.sys reports every SPNEGO logon as <c>Negotiate</c>, so the real package is
/// read from the caller's logon token (<see cref="ILogonPackageResolver"/>) and an undeterminable one is refused.
/// Development and Test authentication are exempt (they never run with the Negotiate mode).
/// </summary>
public sealed partial class KerberosOnlyMiddleware(
    RequestDelegate next,
    IOptionsMonitor<ServiceOptions> options,
    ILogonPackageResolver packages,
    ILogger<KerberosOnlyMiddleware> logger)
{
    private const string Negotiate = "Negotiate";

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = options.CurrentValue;
        if (settings.Authentication != WatchdogAuthenticationMode.Negotiate
            || !settings.Hosting.RequireKerberos
            || context.User.Identity is not ClaimsIdentity { IsAuthenticated: true } identity
            || string.Equals(identity.AuthenticationType, KerberosOnlyEvents.Kerberos, StringComparison.OrdinalIgnoreCase))
        {
            return next(context);
        }

        var package = string.Equals(identity.AuthenticationType, Negotiate, StringComparison.OrdinalIgnoreCase)
            ? packages.Resolve(identity)
            : identity.AuthenticationType;
        if (string.Equals(package, KerberosOnlyEvents.Kerberos, StringComparison.OrdinalIgnoreCase))
        {
            return next(context);
        }

        Log.NotKerberos(logger, package ?? "unknown (" + (identity.AuthenticationType ?? "none") + ")");
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 2500, Level = LogLevel.Warning,
            Message = "Rejected a {AuthenticationType} logon: only Kerberos is accepted (check the SPN HTTP/<host> and that clients use the host's A record).")]
        public static partial void NotKerberos(ILogger logger, string authenticationType);
    }
}
