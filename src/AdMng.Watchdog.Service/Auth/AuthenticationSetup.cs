using AdMng.Watchdog.Service.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;

namespace AdMng.Watchdog.Service.Auth;

/// <summary>
/// Authentication and authorization registration (spec 17.3): <c>Negotiate</c> – the Negotiate handler with
/// <see cref="KerberosOnlyEvents"/> on Kestrel (credentials never persisted across requests), the HTTP.sys Windows scheme
/// on HTTP.sys; <c>Development</c> – <see cref="DevelopmentAuthenticationHandler"/>; <c>Test</c> – only the default
/// scheme name, the handler is registered by the tests (the Negotiate handler cannot run under TestServer). Policies
/// from <see cref="WatchdogPolicies"/>.
/// </summary>
public static class AuthenticationSetup
{
    /// <summary>Scheme name the service tests register their header-based handler under.</summary>
    public const string TestScheme = "Test";

    /// <summary>The authentication scheme name of the HTTP.sys server (<c>HttpSysDefaults.AuthenticationScheme</c>).</summary>
    public const string HttpSysScheme = "Windows";

    public static void Configure(IServiceCollection services, ServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        switch (options.Authentication)
        {
            case WatchdogAuthenticationMode.Negotiate when options.Hosting.Server == WatchdogServer.HttpSys:
                services.AddAuthentication(HttpSysScheme);
                break;
            case WatchdogAuthenticationMode.Negotiate:
                services.AddTransient<KerberosOnlyEvents>();
                services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
                    .AddNegotiate(negotiate =>
                    {
                        negotiate.PersistKerberosCredentials = false;
                        negotiate.PersistNtlmCredentials = false;
                        negotiate.EventsType = typeof(KerberosOnlyEvents);
                    });
                break;
            case WatchdogAuthenticationMode.Development:
                services.AddAuthentication(DevelopmentAuthenticationHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(DevelopmentAuthenticationHandler.SchemeName, null);
                break;
            default:
                services.AddAuthentication(TestScheme);
                break;
        }

        services.AddSingleton<IAuthorizationHandler, SidRoleHandler>();
        services.AddAuthorization(WatchdogPolicies.Configure);
    }
}
