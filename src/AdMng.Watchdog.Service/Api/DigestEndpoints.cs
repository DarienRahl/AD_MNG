using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Service.Auth;

namespace AdMng.Watchdog.Service.Api;

/// <summary><c>GET /api/v1/digest/preview</c> (Viewer) and <c>POST /api/v1/digest/send</c> (Admin, 202) over
/// <see cref="IDigestService"/> (503 when this build has none).</summary>
public static class DigestEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapGet(WatchdogRoutes.DigestPreview, PreviewAsync);
        api.MapPost(WatchdogRoutes.DigestSend, SendAsync).RequireAuthorization(WatchdogPolicies.Admin).AddEndpointFilter<ClientHeaderFilter>();
    }

    private static async Task<IResult> PreviewAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var digest = context.RequestServices.GetService<IDigestService>();
        return digest is null
            ? ApiResults.Unavailable("The digest is not available.")
            : ApiResults.Json(await digest.PreviewAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> SendAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var digest = context.RequestServices.GetService<IDigestService>();
        if (digest is null)
        {
            return ApiResults.Unavailable("The digest is not available.");
        }

        await digest.SendNowAsync(cancellationToken).ConfigureAwait(false);
        return Results.Accepted();
    }
}
