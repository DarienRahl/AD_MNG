using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Tests.Infrastructure;

/// <summary>
/// The <c>Test</c> scheme (spec 22.2): <c>X-Test-Account</c> (DOMAIN\name) authenticates, <c>X-Test-Sids</c>
/// (comma-separated group SIDs), <c>X-Test-PrimaryGroupSid</c>, <c>X-Test-UserSid</c> and <c>X-Test-AuthType</c>
/// (default "Kerberos") shape the identity. No header = anonymous, or <see cref="TestAuthenticationOptions.DefaultAccount"/>.
/// </summary>
public sealed class TestAuthenticationHandler(IOptionsMonitor<TestAuthenticationOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<TestAuthenticationOptions>(options, logger, encoder)
{
    public const string AccountHeader = "X-Test-Account";
    public const string SidsHeader = "X-Test-Sids";
    public const string PrimaryGroupHeader = "X-Test-PrimaryGroupSid";
    public const string UserSidHeader = "X-Test-UserSid";
    public const string AuthTypeHeader = "X-Test-AuthType";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var account = Request.Headers[AccountHeader].ToString();
        var sids = Request.Headers[SidsHeader].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (string.IsNullOrEmpty(account))
        {
            if (Options.DefaultAccount is null)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            account = Uri.EscapeDataString(Options.DefaultAccount);
            sids = [.. Options.DefaultSids];
        }

        var authType = Request.Headers[AuthTypeHeader].ToString() is { Length: > 0 } type ? type : "Kerberos";
        var identity = new ClaimsIdentity(authType, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.Name, Uri.UnescapeDataString(account)));
        foreach (var sid in sids)
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
