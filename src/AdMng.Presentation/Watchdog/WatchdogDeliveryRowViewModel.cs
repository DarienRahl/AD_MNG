using AdMng.Presentation.Localization;
using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Presentation.Watchdog;

/// <summary>One delivery (outbox message) that contained the event shown in the details.</summary>
public sealed class WatchdogDeliveryRowViewModel
{
    public WatchdogDeliveryRowViewModel(EventDeliveryDto delivery, ILocalizer localizer, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(localizer);
        Status = delivery.Status;
        ChannelText = delivery.ChannelName ?? (delivery.ChannelType == DeliveryChannelType.Email ? localizer.ChannelTypeName(DeliveryChannelType.Email) : delivery.ChannelId);
        TypeText = localizer.ChannelTypeName(delivery.ChannelType);
        KindText = localizer.DeliveryKindName(delivery.Kind);
        StatusText = localizer.OutboxStatusName(delivery.Status);
        CreatedText = localizer.FormatTimeWithSeconds(delivery.CreatedAt, zone);
        SentText = localizer.FormatTimeWithSeconds(delivery.SentAt, zone);
        ErrorCode = delivery.LastErrorCode ?? string.Empty;
    }

    public OutboxStatus Status { get; }

    public string ChannelText { get; }

    public string TypeText { get; }

    public string KindText { get; }

    public string StatusText { get; }

    public string CreatedText { get; }

    public string SentText { get; }

    public string ErrorCode { get; }
}
