using System.Globalization;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Watchdog.Service.Api;

/// <summary>Every response carries <c>X-AdMng-Watchdog-Api: 1</c>; a request announcing another API version gets 400
/// (spec 17.1).</summary>
public sealed class ApiVersionMiddleware(RequestDelegate next)
{
    private static readonly string Version = WatchdogRoutes.ApiVersion.ToString(CultureInfo.InvariantCulture);

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.OnStarting(static state =>
        {
            ((HttpResponse)state).Headers[WatchdogRoutes.ApiVersionHeader] = Version;
            return Task.CompletedTask;
        }, context.Response);

        var requested = context.Request.Headers[WatchdogRoutes.ApiVersionHeader].ToString();
        if (requested.Length > 0 && !string.Equals(requested.Trim(), Version, StringComparison.Ordinal))
        {
            await ApiResults.Problem(StatusCodes.Status400BadRequest, ApiProblem.Validation, "Unsupported API version.").ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }
}
