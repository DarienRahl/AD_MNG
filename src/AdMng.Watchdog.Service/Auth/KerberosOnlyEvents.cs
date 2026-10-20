using AdMng.Watchdog.Service.Hosting;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Auth;

/// <summary>
/// Negotiate events of the Kestrel server (spec 17.3): with <c>Hosting.RequireKerberos</c> an identity authenticated by
/// anything but Kerberos (NTLM) fails; a failed handshake answers 401 with a log hint about the SPN instead of a 500. On
/// Linux every GSS failure of the MIT acceptor (a ticket encrypted with a key the keytab does not hold, an SPN or CNAME
/// mismatch, a malformed token) arrives here as an exception or a credential/client error – without this handler it
/// would be an HTTP 500 (stage B design 6.1, lab). The exception text is never logged (it may name hosts and principals).
/// </summary>
public sealed partial class KerberosOnlyEvents : NegotiateEvents
{
    public const string Kerberos = "Kerberos";

    private readonly IOptionsMonitor<ServiceOptions> _options;
    private readonly ILogger<KerberosOnlyEvents> _logger;

    public KerberosOnlyEvents(IOptionsMonitor<ServiceOptions> options, ILogger<KerberosOnlyEvents> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
        OnAuthenticated = AuthenticatedAsync;
        OnAuthenticationFailed = AuthenticationFailedAsync;
    }

    private Task AuthenticatedAsync(AuthenticatedContext context)
    {
        var type = context.Principal?.Identity?.AuthenticationType;
        if (_options.CurrentValue.Hosting.RequireKerberos && !string.Equals(type, Kerberos, StringComparison.OrdinalIgnoreCase))
        {
            Log.NotKerberos(_logger, type ?? "none");
            context.Fail("NTLM is not accepted");
        }

        return Task.CompletedTask;
    }

    private Task AuthenticationFailedAsync(AuthenticationFailedContext context)
    {
        Log.HandshakeFailed(_logger);
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.HandleResponse();
        return Task.CompletedTask;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 2500, Level = LogLevel.Warning,
            Message = "Rejected a {AuthenticationType} logon: only Kerberos is accepted (check the SPN HTTP/<host> on the service account and that clients use the host's A record).")]
        public static partial void NotKerberos(ILogger logger, string authenticationType);

        [LoggerMessage(EventId = 2501, Level = LogLevel.Warning,
            Message = "Negotiate authentication failed: check the SPN HTTP/<host> on the service account, that clients use the host's A record (not a CNAME) and that the keytab's kvno matches msDS-KeyVersionNumber in AD.")]
        public static partial void HandshakeFailed(ILogger logger);
    }
}
