using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Service.Auth;

namespace AdMng.Watchdog.Service.Api;

/// <summary><c>POST /api/v1/rules/preview</c> (Admin): what an unsaved rule would have matched and sent over the last
/// days (<see cref="IRulePreviewService"/>).</summary>
public static class RuleEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapPost(WatchdogRoutes.RulePreview, PreviewAsync).RequireAuthorization(WatchdogPolicies.Admin).AddEndpointFilter<ClientHeaderFilter>();
    }

    private static async Task<IResult> PreviewAsync(HttpContext context, IRulePreviewService preview, CancellationToken cancellationToken)
    {
        var (request, error) = await ApiResults.ReadJsonAsync<RulePreviewRequest>(context.Request, allowEmpty: false, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        try
        {
            return ApiResults.Json(await preview.PreviewAsync(request!, cancellationToken).ConfigureAwait(false));
        }
        catch (ArgumentException)
        {
            return ApiResults.Invalid("rule", "invalidRule", "The rule cannot be previewed.");
        }
    }
}
