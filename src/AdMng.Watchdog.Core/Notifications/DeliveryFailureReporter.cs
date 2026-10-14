using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>
/// Stores a <c>HealthDeliveryFailing</c> event when a channel enters Error (section 15.1): object = the watchdog
/// instance, changes <c>code</c> = "delivery.failing", <c>channel</c> = channel id, <c>channelType</c> and
/// <c>errorCode</c> (classified code, never message content). The rules route it through every other working channel
/// (<see cref="NotificationTargetResolver"/> skips the failing one; e-mail falls back to the digest recipients).
/// </summary>
public sealed class DeliveryFailureReporter
{
    /// <summary>Value of the <c>code</c> change.</summary>
    public const string Code = "delivery.failing";

    private readonly IEventStore _events;
    private readonly IWatchdogSignals _signals;

    public DeliveryFailureReporter(IEventStore events, IWatchdogSignals signals)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(signals);
        _events = events;
        _signals = signals;
    }

    /// <summary>Stores the event and raises <see cref="WatchdogSignal.EventsStored"/>.</summary>
    public async Task<long> ReportAsync(string channelId, DeliveryChannelType channelType, string? errorCode, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelId);
        var id = Guid.CreateVersion7(now);
        var watchEvent = new WatchEvent
        {
            EventId = id,
            ChangeSetId = id,
            Kind = WatchEventKind.HealthDeliveryFailing,
            Severity = EventKindCatalog.DefaultSeverity(WatchEventKind.HealthDeliveryFailing, WatchObjectKind.Watchdog),
            Object = new ObjectRef { Dn = string.Empty, Name = string.Empty, Kind = WatchObjectKind.Watchdog },
            Changes =
            [
                Text("code", Code),
                Text("channel", channelId),
                Text("channelType", channelType.ToString()),
                Text("errorCode", string.IsNullOrEmpty(errorCode) ? "unknown" : errorCode),
            ],
            DetectedAt = now,
            Source = DetectionSource.Health,
            Attribution = AdMng.Watchdog.Contracts.Events.Attribution.NotApplicable,
        };

        var stored = await _events.AppendAsync(watchEvent, cancellationToken).ConfigureAwait(false);
        _signals.Raise(WatchdogSignal.EventsStored);
        return stored;
    }

    private static AttributeChange Text(string attribute, string value) =>
        new() { Attribute = attribute, ValueKind = AttributeValueKind.Text, NewValues = [value], NewCount = 1 };
}
