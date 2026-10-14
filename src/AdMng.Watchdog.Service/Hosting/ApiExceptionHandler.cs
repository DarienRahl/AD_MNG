using AdMng.Watchdog.Core.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>Unhandled exceptions of the API (spec 17.1): logged through <see cref="SafeExceptionLog"/> (types, codes and
/// stack traces – never the message), answered with a ProblemDetails 500 that carries no exception text.</summary>
public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);
        SafeExceptionLog.LogError(logger, exception, "API request");
        if (httpContext.Response.HasStarted)
        {
            return true;
        }

        httpContext.Response.Clear();
        await Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "An unexpected error occurred.")
            .ExecuteAsync(httpContext).ConfigureAwait(false);
        return true;
    }
}
