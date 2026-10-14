using System.Collections.Concurrent;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Service.Hosting;

namespace AdMng.Watchdog.Service.Notifications;

/// <summary>
/// Channel health after every delivery attempt (spec 15.8): Ok after a success; Warning after three or more transient
/// failures in a row; Error after ChannelBroken / AuthenticationFailed (until the next success). Rate-limit deferrals
/// are not attempts and change nothing. Reported to <see cref="IHealthRegistry.ReportChannel"/> (channel id "email" for
/// SMTP); the pending count is filled by the status endpoint from the outbox. A transition to Error writes Application
/// log event <see cref="EventLogIds.DeliveryFailing"/> (channel id and error code only).
/// </summary>
public sealed partial class ChannelHealthTracker(IHealthRegistry health, TimeProvider timeProvider, ILogger<ChannelHealthTracker> logger)
{
    /// <summary>Transient failures in a row that turn a channel to Warning.</summary>
    public const int WarningAfterTransientFailures = 3;

    private readonly ConcurrentDictionary<string, ChannelHealthDto> _channels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records one attempt and reports the resulting state.</summary>
    public void Record(DeliveryChannelType type, string channelId, DeliveryResult result)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelId);
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome == DeliveryOutcome.Deferred)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var previous = HealthState.Unknown;
        var updated = _channels.AddOrUpdate(
            channelId,
            _ => Next(new ChannelHealthDto { ChannelId = channelId, ChannelType = type, State = HealthState.Unknown }, result, now),
            (_, current) =>
            {
                previous = current.State;
                return Next(current with { ChannelType = type }, result, now);
            });
        health.ReportChannel(updated);
        if (updated.State == HealthState.Error && previous != HealthState.Error)
        {
            Log.ChannelFailing(logger, channelId, updated.LastErrorCode ?? "unknown");
        }
    }

    private static ChannelHealthDto Next(ChannelHealthDto current, DeliveryResult result, DateTimeOffset now)
    {
        if (result.Outcome == DeliveryOutcome.Delivered)
        {
            return current with { State = HealthState.Ok, LastSuccessAt = now, ConsecutiveFailures = 0 };
        }

        var failures = current.ConsecutiveFailures + 1;
        var state = result.Outcome switch
        {
            DeliveryOutcome.ChannelBroken or DeliveryOutcome.AuthenticationFailed => HealthState.Error,
            _ when current.State == HealthState.Error => HealthState.Error,
            _ when failures >= WarningAfterTransientFailures => HealthState.Warning,
            _ => current.State == HealthState.Unknown ? HealthState.Ok : current.State,
        };
        return current with { State = state, LastFailureAt = now, LastErrorCode = result.ErrorCode, ConsecutiveFailures = failures };
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = EventLogIds.DeliveryFailing, Level = LogLevel.Error, Message = "Delivery channel {ChannelId} is failing ({ErrorCode}).")]
        public static partial void ChannelFailing(ILogger logger, string channelId, string errorCode);
    }
}
