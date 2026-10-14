using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
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
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var (query, issues) = EventQueryBinder.Bind(context.Request.Query);
        if (query is null)
        {
            return ApiResults.Invalid(issues);
        }

        if (query.After is { } after && query.WaitSeconds > 0)
        {
            await events.WaitForNewerAsync(after, TimeSpan.FromSeconds(query.WaitSeconds), cancellationToken).ConfigureAwait(false);
        }

        var page = await events.QueryAsync(query, cancellationToken).ConfigureAwait(false);
        return ApiResults.Json(ViewerEventFilter.Apply(page, CallerIdentity.DetailLevel(context.User, options.CurrentValue)));
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
