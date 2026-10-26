using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Service.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace AdMng.Watchdog.Service.Auth;

/// <summary>
/// The authorization outcome as HTTP (spec 17.3, stage B design 6.2 step 5): a failure carrying
/// <see cref="RoleLookupUnavailableReason"/> answers 503 with the problem type <see cref="ApiProblem.RoleLookupUnavailable"/>
/// and <c>Retry-After: 30</c> instead of 403; everything else is the default behaviour (challenge 401, forbid 403).
/// Anonymous endpoints (<c>/healthz</c>) never get here.
/// </summary>
public sealed class WatchdogAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    /// <summary>Seconds a client should wait before retrying after a 503 of the role lookup.</summary>
    public const int RetryAfterSeconds = 30;

    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);
        if (authorizeResult.Forbidden && authorizeResult.AuthorizationFailure?.FailureReasons.Any(r => r is RoleLookupUnavailableReason) == true)
        {
            context.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await ApiResults.Problem(StatusCodes.Status503ServiceUnavailable, ApiProblem.RoleLookupUnavailable,
                "The caller's groups cannot be looked up in the directory right now; retry later.").ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult).ConfigureAwait(false);
    }
}
