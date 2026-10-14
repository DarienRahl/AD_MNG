using System.Globalization;
using AdMng.Presentation.Localization;
using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Presentation.Watchdog;

/// <summary>One saved configuration change (who, when, which version, a value-free summary).</summary>
public sealed class WatchdogConfigAuditRowViewModel(ConfigAuditEntry entry, ILocalizer localizer, TimeZoneInfo zone)
{
    public long Id { get; } = entry?.Id ?? throw new ArgumentNullException(nameof(entry));

    public string TimeText { get; } = (localizer ?? throw new ArgumentNullException(nameof(localizer))).FormatTimeWithSeconds(entry.At, zone);

    public string Account { get; } = entry.ByAccount;

    public string VersionText { get; } = entry.Version.ToString(CultureInfo.InvariantCulture);

    public string Summary { get; } = entry.Summary;
}
