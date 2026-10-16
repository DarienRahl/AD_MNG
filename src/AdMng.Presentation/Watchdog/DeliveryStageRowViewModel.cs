using System.Globalization;
using AdMng.Presentation.Localization;
using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Presentation.Watchdog;

/// <summary>One stage of a test send (✓/✗, detail, status codes, duration).</summary>
public sealed class DeliveryStageRowViewModel
{
    public DeliveryStageRowViewModel(DeliveryStageResult stage, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(localizer);
        Success = stage.Success;
        StageText = localizer.StageName(stage.Stage);
        Detail = stage.Detail ?? string.Empty;
        StatusText = string.Join(
            " ",
            new[] { stage.StatusCode?.ToString(CultureInfo.InvariantCulture), stage.EnhancedStatus }.Where(s => !string.IsNullOrEmpty(s)));
        DurationText = WatchdogDisplay.FormatDuration(stage.Duration, localizer.Culture);
    }

    public bool Success { get; }

    /// <summary>"✓" / "✗".</summary>
    public string Mark => Success ? "✓" : "✗";

    public string StageText { get; }

    public string Detail { get; }

    /// <summary>"535 5.7.139".</summary>
    public string StatusText { get; }

    public string DurationText { get; }
}
