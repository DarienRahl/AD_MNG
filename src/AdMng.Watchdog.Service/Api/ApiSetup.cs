using AdMng.Watchdog.Service.Auth;

namespace AdMng.Watchdog.Service.Api;

/// <summary>
/// The HTTP pipeline and every endpoint group (spec 17): exception handler (problem details without exception text),
/// API version header, request log, routing, remote-address filter (on the matched endpoint), authentication, the Kerberos-only check,
/// authorization (fallback policy Viewer), per-caller concurrency limit, then the endpoints. No CORS, no forwarded
/// headers, no static files.
/// </summary>
public static class ApiSetup
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseExceptionHandler();
        app.UseMiddleware<ApiVersionMiddleware>();
        app.UseMiddleware<RequestLogMiddleware>();
        app.UseRouting();
        app.UseMiddleware<RemoteAddressFilter>();   // after routing: decides on the matched endpoint (/healthz/ too)
        app.UseAuthentication();
        app.UseMiddleware<KerberosOnlyMiddleware>();
        app.UseAuthorization();
        app.UseRateLimiter();

        StatusEndpoints.Map(app, app);
        EventEndpoints.Map(app);
        ConfigEndpoints.Map(app);
        TestEndpoints.Map(app);
        RuleEndpoints.Map(app);
        DigestEndpoints.Map(app);
        OutboxEndpoints.Map(app);
        UserMessageEndpoints.Map(app);
        EventPageEndpoint.Map(app);
    }
}
