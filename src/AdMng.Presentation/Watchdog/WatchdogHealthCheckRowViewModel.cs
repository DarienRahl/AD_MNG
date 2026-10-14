using AdMng.Presentation.Localization;
using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Presentation.Watchdog;

/// <summary>One health check of the service: localized title (by code prefix), state, remedy, detail and time.</summary>
public sealed class WatchdogHealthCheckRowViewModel
{
    public WatchdogHealthCheckRowViewModel(HealthCheckDto check, ILocalizer localizer, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(localizer);
        Code = check.Code;
        State = check.State;
        Title = localizer.HealthText(check.Code);
        StateText = localizer.HealthStateName(check.State);
        Remedy = check.State is HealthState.Ok or HealthState.Disabled ? string.Empty : localizer.HealthRemedy(check.Code) ?? string.Empty;
        Detail = string.Join(" · ", new[] { check.Detail, check.Args.Count == 0 ? null : string.Join(", ", check.Args) }.Where(s => !string.IsNullOrEmpty(s)));
        CheckedText = localizer.FormatTimeWithSeconds(check.CheckedAt == default ? null : check.CheckedAt, zone);
    }

    public string Code { get; }

    public HealthState State { get; }

    public string Title { get; }

    public string StateText { get; }

    /// <summary>One-line remedy for Warning/Error states.</summary>
    public string Remedy { get; }

    /// <summary>English diagnostic text of the service (no directory values) and its arguments.</summary>
    public string Detail { get; }

    public string CheckedText { get; }
}
