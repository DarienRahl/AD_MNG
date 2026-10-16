using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Service.Auth;
using AdMng.Watchdog.Service.Hosting;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Api;

/// <summary><c>GET /api/v1/digest/preview</c> (Viewer) and <c>POST /api/v1/digest/send</c> (Admin, 202) over
/// <see cref="IDigestService"/>. The preview is rendered at the caller's detail level (Admin: Full, Viewer:
/// <c>Authorization.ViewerDetailLevel</c>).</summary>
public static class DigestEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapGet(WatchdogRoutes.DigestPreview, PreviewAsync);
        api.MapPost(WatchdogRoutes.DigestSend, SendAsync).RequireAuthorization(WatchdogPolicies.Admin).AddEndpointFilter<ClientHeaderFilter>();
    }

    private static async Task<IResult> PreviewAsync(HttpContext context, IDigestService digest, IOptionsMonitor<ServiceOptions> options,
        CancellationToken cancellationToken) =>
        ApiResults.Json(await digest.PreviewAsync(CallerIdentity.DetailLevel(context.User, options.CurrentValue), cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> SendAsync(IDigestService digest, CancellationToken cancellationToken)
    {
        await digest.SendNowAsync(cancellationToken).ConfigureAwait(false);
        return Results.Accepted();
    }
}
