using AdMng.Presentation.Localization;
using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Presentation.Watchdog;

/// <summary>Delivery health of one channel (e-mail, Teams, webhook).</summary>
public sealed class WatchdogChannelHealthRowViewModel
{
    public WatchdogChannelHealthRowViewModel(ChannelHealthDto channel, ILocalizer localizer, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(localizer);
        ChannelId = channel.ChannelId;
        TypeText = localizer.ChannelTypeName(channel.ChannelType);
        State = channel.State;
        StateText = localizer.HealthStateName(channel.State);
        LastSuccessText = localizer.FormatTimeWithSeconds(channel.LastSuccessAt, zone);
        LastFailureText = localizer.FormatTimeWithSeconds(channel.LastFailureAt, zone);
        ErrorCode = channel.LastErrorCode ?? string.Empty;
        DetailText = localizer.Format(L.Watchdog.StatusChannelDetail, channel.ConsecutiveFailures, channel.Pending);
    }

    public string ChannelId { get; }

    public string TypeText { get; }

    public HealthState State { get; }

    public string StateText { get; }

    public string LastSuccessText { get; }

    public string LastFailureText { get; }

    public string ErrorCode { get; }

    /// <summary>"błędy z rzędu: 4 · w kolejce: 3".</summary>
    public string DetailText { get; }
}
