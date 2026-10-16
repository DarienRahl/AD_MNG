using AdMng.Core.Directory;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Data.Sqlite;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>Unhandled exceptions of the API (spec 17.1): logged through <see cref="SafeExceptionLog"/> (types, codes and
/// stack traces – never the message), answered with a ProblemDetails that carries no exception text: 503
/// <see cref="ApiProblem.Unavailable"/> when the store or the directory is down (<see cref="SqliteException"/>,
/// <see cref="DirectoryException"/>), 500 otherwise.</summary>
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
        var result = exception switch
        {
            SqliteException => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, type: ApiProblem.Unavailable,
                title: "The watchdog store is not available."),
            DirectoryException => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, type: ApiProblem.Unavailable,
                title: "The directory is not available."),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "An unexpected error occurred."),
        };
        await result.ExecuteAsync(httpContext).ConfigureAwait(false);
        return true;
    }
}
