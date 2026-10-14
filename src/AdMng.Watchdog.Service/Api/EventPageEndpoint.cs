using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Service.Auth;
using AdMng.Watchdog.Service.Hosting;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Api;

/// <summary>
/// <c>GET /events/{eventId}</c> (Viewer): the read-only HTML page of an event – the link target of notifications (spec
/// 14.6, 17.2), rendered by <see cref="IEventHtmlRenderer"/> at the caller's detail level, access logged. Served with a
/// strict Content-Security-Policy (no scripts, no external resources), <c>nosniff</c>, no framing, no referrer, no
/// caching.
/// </summary>
public static class EventPageEndpoint
{
    public const string ContentSecurityPolicy =
        "default-src 'none'; style-src 'unsafe-inline'; img-src data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    public static void Map(IEndpointRouteBuilder root)
    {
        ArgumentNullException.ThrowIfNull(root);
        root.MapGet(WatchdogRoutes.EventPage, GetAsync);
    }

    private static async Task<IResult> GetAsync(
        Guid eventId,
        HttpContext context,
        IEventStore events,
        IOutboxStore outbox,
        EventAccessLog access,
        IOptionsMonitor<ServiceOptions> options,
        CancellationToken cancellationToken)
    {
        SetSecurityHeaders(context.Response);
        access.Viewed(eventId, CallerIdentity.Account(context.User));
        var renderer = context.RequestServices.GetService<IEventHtmlRenderer>();
        if (renderer is null)
        {
            return Results.Text("The event page is not available.", "text/plain; charset=utf-8", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var watchEvent = await events.GetByEventIdAsync(eventId, cancellationToken).ConfigureAwait(false);
        var details = await EventEndpoints.LoadDetailsAsync(events, outbox, watchEvent, cancellationToken).ConfigureAwait(false);
        if (details is null)
        {
            return Results.Text("The event does not exist (it may have been purged).", "text/plain; charset=utf-8", statusCode: StatusCodes.Status404NotFound);
        }

        var html = renderer.Render(ViewerEventFilter.Apply(details, CallerIdentity.DetailLevel(context.User, options.CurrentValue)));
        return Results.Content(html, "text/html; charset=utf-8");
    }

    private static void SetSecurityHeaders(HttpResponse response)
    {
        response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.XFrameOptions = "DENY";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers.CacheControl = "no-store";
    }
}
