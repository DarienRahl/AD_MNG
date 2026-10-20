using System.Security.Claims;
using AdMng.Watchdog.Service.Auth;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Tests.Fakes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Http;

namespace AdMng.Watchdog.Service.Tests.Auth;

/// <summary>NTLM is rejected by the Negotiate events (Kestrel) and by the middleware (both servers) (spec 17.3).</summary>
public sealed class KerberosOnlyTests
{
    private static readonly AuthenticationScheme Negotiate = new(NegotiateDefaults.AuthenticationScheme, null, typeof(NegotiateHandler));

    [Theory]
    [InlineData("Kerberos", true)]
    [InlineData("NTLM", false)]
    [InlineData("Negotiate", false)]
    public async Task Middleware_LetsOnlyKerberosThrough(string authenticationType, bool passes)
    {
        var options = new StaticOptionsMonitor<ServiceOptions>(new ServiceOptions());
        var called = false;
        var middleware = new KerberosOnlyMiddleware(_ => { called = true; return Task.CompletedTask; }, options, NoPackage, new TestLog().For<KerberosOnlyMiddleware>());
        var context = new DefaultHttpContext { User = Principal(authenticationType) };

        await middleware.InvokeAsync(context);

        Assert.Equal(passes, called);
        Assert.Equal(passes ? StatusCodes.Status200OK : StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    /// <summary>HTTP.sys challenges with Negotiate and reports every such logon as "Negotiate": the package that really
    /// authenticated the caller (from its Windows logon) decides; an unknown package is refused.</summary>
    [Theory]
    [InlineData("Kerberos", true)]
    [InlineData("NTLM", false)]
    [InlineData(null, false)]
    public async Task Middleware_ResolvesTheRealPackage_OfNegotiateLogons(string? package, bool passes)
    {
        var log = new TestLog();
        var called = false;
        var middleware = new KerberosOnlyMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            new StaticOptionsMonitor<ServiceOptions>(new ServiceOptions { Hosting = new HostingOptions { Server = WatchdogServer.HttpSys } }),
            new FixedPackage(package),
            log.For<KerberosOnlyMiddleware>());
        var context = new DefaultHttpContext { User = Principal("Negotiate") };

        await middleware.InvokeAsync(context);

        Assert.Equal(passes, called);
        Assert.Equal(passes ? StatusCodes.Status200OK : StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal(passes ? 0 : 1, log.Entries.Count(e => e.EventId.Id == 2500));
    }

    [Fact]
    public async Task Middleware_IgnoresAnonymousRequests_AndOtherModes()
    {
        var calls = 0;
        var negotiate = new KerberosOnlyMiddleware(_ => { calls++; return Task.CompletedTask; }, new StaticOptionsMonitor<ServiceOptions>(new ServiceOptions()), NoPackage, new TestLog().For<KerberosOnlyMiddleware>());
        var development = new KerberosOnlyMiddleware(_ => { calls++; return Task.CompletedTask; },
            new StaticOptionsMonitor<ServiceOptions>(new ServiceOptions { Authentication = WatchdogAuthenticationMode.Development }), NoPackage, new TestLog().For<KerberosOnlyMiddleware>());

        await negotiate.InvokeAsync(new DefaultHttpContext());
        await development.InvokeAsync(new DefaultHttpContext { User = Principal("Development") });

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Middleware_AllowsNtlm_WhenKerberosIsNotRequired()
    {
        var called = false;
        var middleware = new KerberosOnlyMiddleware(_ => { called = true; return Task.CompletedTask; },
            new StaticOptionsMonitor<ServiceOptions>(new ServiceOptions { Hosting = new HostingOptions { RequireKerberos = false } }), NoPackage, new TestLog().For<KerberosOnlyMiddleware>());

        await middleware.InvokeAsync(new DefaultHttpContext { User = Principal("NTLM") });

        Assert.True(called);
    }

    [Fact]
    public async Task Events_FailNtlmIdentities()
    {
        var log = new TestLog();
        var events = new KerberosOnlyEvents(new StaticOptionsMonitor<ServiceOptions>(new ServiceOptions()), log.For<KerberosOnlyEvents>());
        var ntlm = new AuthenticatedContext(new DefaultHttpContext(), Negotiate, new NegotiateOptions()) { Principal = Principal("NTLM") };
        var kerberos = new AuthenticatedContext(new DefaultHttpContext(), Negotiate, new NegotiateOptions()) { Principal = Principal("Kerberos") };

        await events.Authenticated(ntlm);
        await events.Authenticated(kerberos);

        Assert.True(ntlm.Result?.Failure is not null);
        Assert.Null(kerberos.Result);
        Assert.Contains(log.Entries, e => e.EventId.Id == 2500 && e.Message.Contains("NTLM", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Events_TurnAFailedHandshakeInto401()
    {
        var log = new TestLog();
        var events = new KerberosOnlyEvents(new StaticOptionsMonitor<ServiceOptions>(new ServiceOptions()), log.For<KerberosOnlyEvents>());
        var http = new DefaultHttpContext();
        var failed = new AuthenticationFailedContext(http, Negotiate, new NegotiateOptions())
        {
            Exception = new InvalidOperationException("GSS: brak SPN HTTP/admng-wd.contoso.com"),
        };

        await events.AuthenticationFailed(failed);

        Assert.Equal(StatusCodes.Status401Unauthorized, http.Response.StatusCode);
        Assert.True(failed.Result?.Handled);
        Assert.DoesNotContain("admng-wd", log.Text, StringComparison.Ordinal);
    }

    private static readonly ILogonPackageResolver NoPackage = new FixedPackage(null);

    private static ClaimsPrincipal Principal(string authenticationType) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, @"CONTOSO\jan.kowalski")], authenticationType));

    private sealed class FixedPackage(string? package) : ILogonPackageResolver
    {
        public string? Resolve(ClaimsIdentity identity) => package;
    }
}
