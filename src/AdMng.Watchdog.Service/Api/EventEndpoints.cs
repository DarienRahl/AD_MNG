using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Service.Auth;
using AdMng.Watchdog.Service.Hosting;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Api;

/// <summary>
/// The event feed (spec 17.2): paging (<c>after</c> ascending for live polling with an optional long-poll <c>wait</c>,
/// otherwise descending with <c>before</c>), single events with their change set and deliveries (access logged), and
/// acknowledgement (single and bulk, idempotent, caller account stored). Non-Admins get the configured viewer detail
/// level (<see cref="ViewerEventFilter"/>).
/// </summary>
public static class EventEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapGet(WatchdogRoutes.Events, QueryAsync);
        api.MapGet(WatchdogRoutes.EventById, GetAsync);
        api.MapPost(WatchdogRoutes.EventAcknowledge, AcknowledgeAsync).AddEndpointFilter<ClientHeaderFilter>();
        api.MapPost(WatchdogRoutes.EventsAcknowledge, AcknowledgeManyAsync).AddEndpointFilter<ClientHeaderFilter>();
    }

    private static async Task<IResult> QueryAsync(
        HttpContext context,
        IEventStore events,
        IOptionsMonitor<ServiceOptions> options,
        IHostApplicationLifetime lifetime,
        CancellationToken cancellationToken)
    {
        var (query, issues) = EventQueryBinder.Bind(context.Request.Query);
        if (query is null)
        {
            return ApiResults.Invalid(issues);
        }

        // Minimal viewers do not see workstation and IP: their text search must not match them either.
        var level = CallerIdentity.DetailLevel(context.User, options.CurrentValue);
        var hideSources = level == DetailLevel.Minimal && !string.IsNullOrWhiteSpace(query.Text);
        if (hideSources && events is not IViewerEventSearch)
        {
            return ApiResults.Invalid("text", "textSearchUnavailable", "The text search is not available at the configured detail level.");
        }

        if (query.After is { } after && query.WaitSeconds > 0)
        {
            await WaitForNewerAsync(events, after, TimeSpan.FromSeconds(query.WaitSeconds), lifetime.ApplicationStopping, cancellationToken).ConfigureAwait(false);
        }

        var page = hideSources
            ? await ((IViewerEventSearch)events).QueryWithoutSourcesAsync(query, cancellationToken).ConfigureAwait(false)
            : await events.QueryAsync(query, cancellationToken).ConfigureAwait(false);
        return ApiResults.Json(ViewerEventFilter.Apply(page, level));
    }

    private static async Task<IResult> GetAsync(
        long id,
        HttpContext context,
        IEventStore events,
        IOutboxStore outbox,
        EventAccessLog access,
        IOptionsMonitor<ServiceOptions> options,
        CancellationToken cancellationToken)
    {
        access.Viewed(id, CallerIdentity.Account(context.User));
        var details = await LoadDetailsAsync(events, outbox, await events.GetAsync(id, cancellationToken).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
        return details is null
            ? ApiResults.NotFound("The event does not exist (it may have been purged).")
            : ApiResults.Json(ViewerEventFilter.Apply(details, CallerIdentity.DetailLevel(context.User, options.CurrentValue)));
    }

    /// <summary>The long-poll wait, also ended by the service stopping: Kestrel drains in-flight requests on a graceful
    /// stop without cancelling them, so an open GUI tab would otherwise hold every stop for up to 30 s. A stop answers
    /// with the current page.</summary>
    private static async Task WaitForNewerAsync(IEventStore events, long after, TimeSpan wait, CancellationToken stopping, CancellationToken requestAborted)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, stopping);
        try
        {
            await events.WaitForNewerAsync(after, wait, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested && !requestAborted.IsCancellationRequested)
        {
            // Stopping: fall through to the page.
        }
    }

    /// <summary>The event with its change set (other events of the same <c>ChangeSetId</c>) and its deliveries.</summary>
    internal static async Task<EventDetails?> LoadDetailsAsync(
        IEventStore events,
        IOutboxStore outbox,
        Contracts.Events.WatchEvent? watchEvent,
        CancellationToken cancellationToken)
    {
        if (watchEvent is null)
        {
            return null;
        }

        var related = watchEvent.ChangeSetId == Guid.Empty
            ? []
            : (await events.GetChangeSetAsync(watchEvent.ChangeSetId, cancellationToken).ConfigureAwait(false)).Where(e => e.Id != watchEvent.Id).ToList();
        var deliveries = await outbox.GetDeliveriesForEventAsync(watchEvent.Id, cancellationToken).ConfigureAwait(false);
        return new EventDetails { Event = watchEvent, RelatedEvents = related, Deliveries = deliveries };
    }

    private static async Task<IResult> AcknowledgeAsync(
        long id,
        HttpContext context,
        IEventStore events,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var (request, error) = await ApiResults.ReadJsonAsync<AcknowledgeRequest>(context.Request, allowEmpty: true, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var comment = Comment(request?.Comment, out var invalid);
        if (invalid is not null)
        {
            return invalid;
        }

        var found = await events.AcknowledgeAsync(id, CallerIdentity.Account(context.User), comment, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return found ? Results.NoContent() : ApiResults.NotFound("The event does not exist (it may have been purged).");
    }

    private static async Task<IResult> AcknowledgeManyAsync(
        HttpContext context,
        IEventStore events,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var (request, error) = await ApiResults.ReadJsonAsync<BulkAcknowledgeRequest>(context.Request, allowEmpty: false, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        if (request!.Ids.Count is < 1 or > ApiLimits.MaxPageSize)
        {
            return ApiResults.Invalid("ids", "range", "Between 1 and 500 event ids are required.");
        }

        var comment = Comment(request.Comment, out var invalid);
        if (invalid is not null)
        {
            return invalid;
        }

        await events.AcknowledgeManyAsync(request.Ids, CallerIdentity.Account(context.User), comment, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static string? Comment(string? comment, out IResult? invalid)
    {
        invalid = null;
        if (string.IsNullOrWhiteSpace(comment))
        {
            return null;
        }

        var trimmed = comment.Trim();
        if (trimmed.Length > ApiLimits.MaxCommentLength)
        {
            invalid = ApiResults.Invalid("comment", "tooLong", "The comment is longer than 500 characters.");
            return null;
        }

        return trimmed;
    }
}
