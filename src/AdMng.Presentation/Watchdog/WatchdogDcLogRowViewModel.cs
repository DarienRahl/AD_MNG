using System.Globalization;
using AdMng.Presentation.Localization;
using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Presentation.Watchdog;

/// <summary>Security-log reading of one DC (attribution source): state, last read, coverage, source lookups, error.</summary>
public sealed class WatchdogDcLogRowViewModel
{
    public WatchdogDcLogRowViewModel(DcLogStatusDto dc, ILocalizer localizer, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(dc);
        ArgumentNullException.ThrowIfNull(localizer);
        Host = dc.Host;
        State = dc.State;
        StateText = localizer.HealthStateName(dc.State);
        LastReadText = localizer.FormatTimeWithSeconds(dc.LastReadAt, zone);
        CoverageText = dc.Coverage24h is { } coverage ? coverage.ToString("P0", localizer.Culture) : string.Empty;
        LookupsText = localizer.Format(
            L.Watchdog.StatusLookups,
            dc.SourceLookups24h,
            dc.SourceLookupAverage is { } average ? WatchdogDisplay.FormatDuration(average, localizer.Culture) : "—",
            dc.SourceLookupTimeouts24h);
        ErrorText = dc.ErrorCode is { } code ? localizer.Format(L.Watchdog.StatusWin32Error, code) : string.Empty;
        RecordText = dc.LastRecordId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }

    public string Host { get; }

    public HealthState State { get; }

    public string StateText { get; }

    public string LastReadText { get; }

    /// <summary>Share of attributable changes attributed in the last 24 h ("96 %").</summary>
    public string CoverageText { get; }

    public string LookupsText { get; }

    public string ErrorText { get; }

    public string RecordText { get; }
}
