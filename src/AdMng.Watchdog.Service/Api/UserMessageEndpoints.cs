using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Service.Auth;
using AdMng.Watchdog.Service.UserMessages;

namespace AdMng.Watchdog.Service.Api;

/// <summary>Messages to users submitted by AD Manager (README U1b) – allowed for Watchdog Admins AND Viewers (a user
/// decision: helpdesk operators are Viewers; <c>DeniedSids</c> still wins): <c>GET /api/v1/user-messages/status</c>
/// (<see cref="UserMessagingStatusDto"/>) and <c>POST /api/v1/user-messages</c> (<see cref="UserMessageService"/>).</summary>
public static class UserMessageEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapGet(WatchdogRoutes.UserMessagesStatus, StatusAsync).RequireAuthorization(WatchdogPolicies.Viewer);
        api.MapPost(WatchdogRoutes.UserMessages, SendAsync).RequireAuthorization(WatchdogPolicies.Viewer).AddEndpointFilter<ClientHeaderFilter>();
    }

    private static async Task<IResult> StatusAsync(UserMessageService messages, CancellationToken cancellationToken) =>
        ApiResults.Json(await messages.GetStatusAsync(cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> SendAsync(HttpContext context, UserMessageService messages, CancellationToken cancellationToken)
    {
        var (request, error) = await ApiResults.ReadJsonAsync<UserMessageRequest>(context.Request, allowEmpty: false, cancellationToken).ConfigureAwait(false);
        return error ?? await messages.SubmitAsync(request!, context.User, cancellationToken).ConfigureAwait(false);
    }
}
