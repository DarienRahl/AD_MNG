using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Tests.Infrastructure;

/// <summary>
/// The <c>Test</c> scheme (spec 22.2): <c>X-Test-Account</c> (DOMAIN\name) authenticates, <c>X-Test-Sids</c>
/// (comma-separated group SIDs), <c>X-Test-PrimaryGroupSid</c>, <c>X-Test-UserSid</c> and <c>X-Test-AuthType</c>
/// (default "Kerberos") shape the identity. No header = anonymous.
/// </summary>
public sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string AccountHeader = "X-Test-Account";
    public const string SidsHeader = "X-Test-Sids";
    public const string PrimaryGroupHeader = "X-Test-PrimaryGroupSid";
    public const string UserSidHeader = "X-Test-UserSid";
    public const string AuthTypeHeader = "X-Test-AuthType";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var account = Request.Headers[AccountHeader].ToString();
        if (string.IsNullOrEmpty(account))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var authType = Request.Headers[AuthTypeHeader].ToString() is { Length: > 0 } type ? type : "Kerberos";
        var identity = new ClaimsIdentity(authType, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.Name, Uri.UnescapeDataString(account)));
        foreach (var sid in Request.Headers[SidsHeader].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            identity.AddClaim(new Claim(ClaimTypes.GroupSid, sid));
        }

        if (Request.Headers[PrimaryGroupHeader].ToString() is { Length: > 0 } primary)
        {
            identity.AddClaim(new Claim(ClaimTypes.PrimaryGroupSid, primary));
        }

        if (Request.Headers[UserSidHeader].ToString() is { Length: > 0 } user)
        {
            identity.AddClaim(new Claim(ClaimTypes.PrimarySid, user));
        }

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
