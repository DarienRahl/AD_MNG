using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace AdMng.Watchdog.IntegrationTests.EndToEnd;

/// <summary>
/// Test-only endpoint <see cref="Path"/> in front of the service pipeline (spec 22.4 Kerberos test): authenticates the
/// request with the service's Negotiate scheme (challenging when there is no token yet) and answers with the identity's
/// <c>AuthenticationType</c> – on Linux the handler emits no SID claims, so the API itself can only answer 403.
/// </summary>
public sealed class IdentityProbeStartupFilter : IStartupFilter
{
    public const string Path = "/_test/identity";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Map(Path, probe =>
        {
            // The Negotiate handler runs only behind the authentication middleware (default scheme: Negotiate).
            probe.UseAuthentication();
            probe.Run(async context =>
            {
                if (context.User.Identity is not { IsAuthenticated: true } identity)
                {
                    await context.ChallengeAsync(NegotiateDefaults.AuthenticationScheme);
                    return;
                }

                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync(identity.AuthenticationType ?? string.Empty, context.RequestAborted);
            });
        });
        next(app);
    };
}
