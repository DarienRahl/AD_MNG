using AdMng.Watchdog.Service.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.Watchdog.Service.Auth;

/// <summary>
/// Authentication and authorization registration (spec 17.3): <c>Negotiate</c> – the Negotiate handler with
/// <see cref="KerberosOnlyEvents"/> on Kestrel (credentials never persisted across requests), the HTTP.sys Windows scheme
/// on HTTP.sys; <c>Development</c> – <see cref="DevelopmentAuthenticationHandler"/>; <c>Test</c> – only the default
/// scheme name, the handler is registered by the tests (the Negotiate handler cannot run under TestServer). Policies
/// from <see cref="WatchdogPolicies"/>, outcomes through <see cref="WatchdogAuthorizationResultHandler"/>.
/// <para>
/// Linux with Negotiate on Kestrel (stage B design 6.1–6.2): the acceptor keys come from <c>KRB5_KTNAME</c> (the unit's
/// environment), <c>EnableLdap</c> stays off – its roles are group CNs, spoofable by anyone who may name a group – and
/// the caller's SIDs come from <see cref="KerberosSidClaimsTransformation"/> (objectSid + tokenGroups over the service's
/// LDAP session, cached in <see cref="RoleCache"/>).
/// </para>
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
        services.TryAddSingleton<ILogonPackageResolver, WindowsLogonPackageResolver>();
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
                if (OperatingSystem.IsLinux())
                {
                    AddLinuxRoleResolution(services);
                }

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
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, WatchdogAuthorizationResultHandler>();
        services.AddAuthorization(WatchdogPolicies.Configure);
    }

    /// <summary>The SID claims of Kerberos callers from the directory (stage B design 6.2) – also used by tests that run
    /// the pipeline with another scheme.</summary>
    public static void AddLinuxRoleResolution(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<RoleCache>();
        services.TryAddSingleton<IPrincipalSidResolver, PrincipalSidResolver>();
        services.AddSingleton<IClaimsTransformation, KerberosSidClaimsTransformation>();
    }
}
