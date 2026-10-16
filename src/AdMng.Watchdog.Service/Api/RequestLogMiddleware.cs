using AdMng.Watchdog.Service.Auth;

namespace AdMng.Watchdog.Service.Api;

/// <summary>One log line per request (spec 17.1): method, route template (never the path values or the query), status,
/// duration and caller account. Bodies are never logged.</summary>
public sealed partial class RequestLogMiddleware(RequestDelegate next, TimeProvider timeProvider, ILogger<RequestLogMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var started = timeProvider.GetTimestamp();
        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(no route)";
            var account = context.User.Identity?.IsAuthenticated == true ? CallerIdentity.Account(context.User) : "anonymous";
            Log.Request(logger, context.Request.Method, route, context.Response.StatusCode, (long)timeProvider.GetElapsedTime(started).TotalMilliseconds, account);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 2700, Level = LogLevel.Information, Message = "HTTP {Method} {Route} answered {Status} in {ElapsedMs} ms for {Account}.")]
        public static partial void Request(ILogger logger, string method, string route, int status, long elapsedMs, string account);
    }
}
