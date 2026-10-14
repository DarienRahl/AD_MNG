using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Service.Api;

namespace AdMng.Watchdog.Service.Auth;

/// <summary>
/// CSRF guard of state-changing endpoints (spec 17.1): browsers on the intranet send Windows credentials automatically,
/// so every POST/PUT/DELETE must carry <c>X-AdMng-Client: 1</c> (400 otherwise – a cross-site form cannot set it
/// without a CORS preflight, and no CORS policy exists) and, when it has a content type, <c>application/json</c>
/// (form posts → 415).
/// </summary>
public sealed class ClientHeaderFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var request = context.HttpContext.Request;
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method))
        {
            return await next(context).ConfigureAwait(false);
        }

        if (!string.Equals(request.Headers[WatchdogRoutes.ClientHeader].ToString(), "1", StringComparison.Ordinal))
        {
            return ApiResults.Problem(StatusCodes.Status400BadRequest, ApiProblem.ClientHeaderMissing, "The X-AdMng-Client header is required.");
        }

        if (!string.IsNullOrEmpty(request.ContentType) && !request.HasJsonContentType())
        {
            return ApiResults.Problem(StatusCodes.Status415UnsupportedMediaType, ApiProblem.Validation, "Only application/json request bodies are accepted.");
        }

        return await next(context).ConfigureAwait(false);
    }
}
