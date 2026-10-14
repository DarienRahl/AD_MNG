using System.Globalization;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Service.Auth;
using AdMng.Watchdog.Service.Hosting;

namespace AdMng.Watchdog.Service.Api;

/// <summary>The delivery log (spec 17.2, Admin): <c>GET /api/v1/outbox</c> (<see cref="OutboxQuery"/> keys
/// <c>status</c>, <c>channelId</c>, <c>before</c>, <c>limit</c>) and <c>POST /api/v1/outbox/{id}/retry</c> (204, 404,
/// 409 when the message is not Dead).</summary>
public static class OutboxEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapGet(WatchdogRoutes.Outbox, QueryAsync).RequireAuthorization(WatchdogPolicies.Admin);
        api.MapPost(WatchdogRoutes.OutboxRetry, RetryAsync).RequireAuthorization(WatchdogPolicies.Admin).AddEndpointFilter<ClientHeaderFilter>();
    }

    private static async Task<IResult> QueryAsync(HttpContext context, IOutboxStore outbox, CancellationToken cancellationToken)
    {
        var query = context.Request.Query;
        var result = new OutboxQuery();
        if (query["status"].ToString() is { Length: > 0 } status)
        {
            if (!Enum.TryParse<OutboxStatus>(status, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed) || char.IsDigit(status[0]))
            {
                return ApiResults.Invalid("status", "invalidValue", "status has an unknown value.");
            }

            result = result with { Status = parsed };
        }

        if (query["channelId"].ToString() is { Length: > 0 } channelId)
        {
            result = result with { ChannelId = channelId.Trim() };
        }

        if (query["before"].ToString() is { Length: > 0 } before)
        {
            if (!long.TryParse(before, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                return ApiResults.Invalid("before", "invalidNumber", "before must be an integer.");
            }

            result = result with { Before = id };
        }

        if (query["limit"].ToString() is { Length: > 0 } limitText)
        {
            if (!int.TryParse(limitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) || limit is < 1 or > ApiLimits.MaxPageSize)
            {
                return ApiResults.Invalid("limit", "range", "limit must be between 1 and 500.");
            }

            result = result with { Limit = limit };
        }

        return ApiResults.Json(await outbox.QueryAsync(result, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> RetryAsync(long id, IOutboxStore outbox, IWatchdogSignals signals, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (await outbox.RequeueAsync(id, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false))
        {
            signals.Raise(WatchdogSignal.OutboxEnqueued);
            return Results.NoContent();
        }

        var page = await outbox.QueryAsync(new OutboxQuery { Before = id + 1, Limit = 1 }, cancellationToken).ConfigureAwait(false);
        return page.Items.Count == 1 && page.Items[0].Id == id
            ? ApiResults.Problem(StatusCodes.Status409Conflict, ApiProblem.Validation, "Only a message in the Dead state can be retried.")
            : ApiResults.NotFound("The outbox message does not exist.");
    }
}
