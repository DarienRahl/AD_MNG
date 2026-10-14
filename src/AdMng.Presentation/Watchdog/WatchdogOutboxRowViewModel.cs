using System.Globalization;
using AdMng.Presentation.Localization;
using AdMng.Watchdog.Contracts.Api;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.Watchdog;

/// <summary>One row of the delivery log (outbox message): channel, kind, status, attempts, times, error, subject.</summary>
public sealed partial class WatchdogOutboxRowViewModel : ObservableObject
{
    private readonly ILocalizer _localizer;
    private readonly TimeZoneInfo _zone;

    public WatchdogOutboxRowViewModel(OutboxItemDto item, ILocalizer localizer, TimeZoneInfo zone)
    {
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _zone = zone ?? throw new ArgumentNullException(nameof(zone));
        Item = item ?? throw new ArgumentNullException(nameof(item));
        Refresh();
    }

    [ObservableProperty]
    public partial OutboxItemDto Item { get; private set; }

    public long Id => Item.Id;

    public OutboxStatus Status => Item.Status;

    /// <summary>Dead deliveries can be queued again.</summary>
    public bool IsDead => Item.Status == OutboxStatus.Dead;

    [ObservableProperty]
    public partial string ChannelText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string KindText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string AttemptsText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string CreatedText { get; private set; } = string.Empty;

    /// <summary>Sent at, else the next attempt.</summary>
    [ObservableProperty]
    public partial string TimeText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ErrorText { get; private set; } = string.Empty;

    public string Subject => Item.Subject ?? string.Empty;

    /// <summary>Takes the row's new state (after a retry).</summary>
    public void Update(OutboxItemDto item)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        Refresh();
    }

    public void Refresh()
    {
        var item = Item;
        ChannelText = item.ChannelName ?? (item.ChannelType == DeliveryChannelType.Email ? _localizer.ChannelTypeName(DeliveryChannelType.Email) : item.ChannelId);
        KindText = _localizer.DeliveryKindName(item.Kind);
        StatusText = _localizer.OutboxStatusName(item.Status);
        AttemptsText = item.Attempts.ToString(CultureInfo.InvariantCulture);
        CreatedText = _localizer.FormatTimeWithSeconds(item.CreatedAt, _zone);
        TimeText = item.SentAt is { } sent
            ? _localizer.FormatTimeWithSeconds(sent, _zone)
            : item.NextAttemptAt is { } next ? _localizer.Format(L.Watchdog.OutboxNextAttempt, _localizer.FormatTimeWithSeconds(next, _zone)) : string.Empty;
        ErrorText = string.Join(" – ", new[] { item.LastErrorCode, item.LastError }.Where(s => !string.IsNullOrEmpty(s)));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(IsDead));
        OnPropertyChanged(nameof(Subject));
    }
}
