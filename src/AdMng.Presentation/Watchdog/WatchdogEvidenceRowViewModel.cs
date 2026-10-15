using System.Globalization;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Presentation.Watchdog;

/// <summary>One Security-log record an attribution is based on (DC, log, event id, record id, time).</summary>
public sealed class WatchdogEvidenceRowViewModel(EvidenceRef evidence, string timeText)
{
    public string Dc { get; } = evidence?.Dc ?? throw new ArgumentNullException(nameof(evidence));

    public string Log { get; } = evidence.Log;

    public int EventCode { get; } = evidence.EventCode;

    public string RecordText { get; } = evidence.RecordId.ToString(CultureInfo.InvariantCulture);

    public string TimeText { get; } = timeText ?? string.Empty;
}
