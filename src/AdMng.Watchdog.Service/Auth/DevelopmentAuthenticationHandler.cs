using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AdMng.Watchdog.Service.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Auth;

/// <summary>
/// The Development scheme (spec 17.3): a fixed identity (<c>Development.Account</c> with the group SIDs
/// <c>Development.Sids</c>) for loopback callers – only in the Development environment and never as a Windows service
/// (also enforced at start-up by <see cref="DevelopmentAuthGuard"/>). Every other request stays anonymous (401).
/// </summary>
public sealed class DevelopmentAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Development";

    private readonly IOptionsMonitor<ServiceOptions> _service;
    private readonly IHostEnvironment _environment;

    public DevelopmentAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptionsMonitor<ServiceOptions> service,
        IHostEnvironment environment)
        : base(options, logger, encoder)
    {
        _service = service;
        _environment = environment;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var remote = Context.Connection.RemoteIpAddress;
        if (!_environment.IsDevelopment() || (remote is not null && !IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote)))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var development = _service.CurrentValue.Development;
        var identity = new ClaimsIdentity(SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.Name, development.Account));
        foreach (var sid in development.Sids.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            identity.AddClaim(new Claim(ClaimTypes.GroupSid, sid.Trim()));
        }

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
