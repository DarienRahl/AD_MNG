using AdMng.Core.EventLogs;
using AdMng.Presentation.EventLogs;

namespace AdMng.Presentation.Settings;

/// <summary>
/// A saved query of "Dzienniki zdarzeń" (track E, design note e-event-logs.md 2): the builder as the operator typed it – name,
/// channel, IDs, levels, providers, data conditions, XPath – and the computers it reads. Never a value read from a log (rule 1).
/// </summary>
public sealed class EventLogSavedQuery
{
    public string Name { get; set; } = string.Empty;

    public string Channel { get; set; } = EventLogText.SecurityChannel;

    public EventLogTimePreset TimePreset { get; set; } = EventLogTimePreset.Last24Hours;

    public string EventIds { get; set; } = string.Empty;

    public List<byte> Levels { get; set; } = [];

    public string Providers { get; set; } = string.Empty;

    public EventAuditOutcome? Outcome { get; set; }

    public string Account { get; set; } = string.Empty;

    public string AccountField { get; set; } = "TargetUserName";

    public string Sid { get; set; } = string.Empty;

    public string SidField { get; set; } = "TargetSid";

    public string IpAddress { get; set; } = string.Empty;

    public string Workstation { get; set; } = string.Empty;

    public string WorkstationField { get; set; } = "WorkstationName";

    public bool IsRawMode { get; set; }

    public string RawXPath { get; set; } = string.Empty;

    /// <summary>The computers (FQDN) the query reads; files are never saved.</summary>
    public List<string> Hosts { get; set; } = [];

    /// <summary>The name only.</summary>
    public override string ToString() => Name;
}

/// <summary>What the event log documents remember: the saved queries.</summary>
public sealed class EventLogSettings
{
    /// <summary>Saved queries kept at most.</summary>
    public const int MaxSavedQueries = 100;

    /// <summary>The longest name, text field or XPath kept.</summary>
    public const int MaxTextLength = EventQueryXml.MaxRawXPathLength;

    public List<EventLogSavedQuery> SavedQueries { get; set; } = [];

    /// <summary>Repairs a hand-edited or older settings file.</summary>
    public void Normalize()
    {
        SavedQueries = [.. (SavedQueries ?? [])
            .Where(q => q is not null && !string.IsNullOrWhiteSpace(q.Name))
            .Select(Normalize)
            .DistinctBy(q => q.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSavedQueries)];
    }

    private static EventLogSavedQuery Normalize(EventLogSavedQuery query)
    {
        static string Clip(string? text, int max = 256) => (text ?? string.Empty).Trim() is { } t && t.Length > max ? t[..max] : (text ?? string.Empty).Trim();
        query.Name = Clip(query.Name, 100);
        query.Channel = EventQueryXml.IsAllowedChannel(query.Channel?.Trim()) ? query.Channel!.Trim() : EventLogText.SecurityChannel;
        query.TimePreset = Enum.IsDefined(query.TimePreset) && query.TimePreset != EventLogTimePreset.BeforeLastLockout ? query.TimePreset : EventLogTimePreset.Last24Hours;
        query.EventIds = Clip(query.EventIds, 1024);
        query.Levels = [.. (query.Levels ?? []).Where(l => l <= EventLogLevels.Verbose).Distinct()];
        query.Providers = Clip(query.Providers, 1024);
        query.Outcome = query.Outcome is { } outcome && Enum.IsDefined(outcome) ? outcome : null;
        query.Account = Clip(query.Account);
        query.AccountField = Clip(query.AccountField, 64);
        query.Sid = Clip(query.Sid);
        query.SidField = Clip(query.SidField, 64);
        query.IpAddress = Clip(query.IpAddress, 64);
        query.Workstation = Clip(query.Workstation);
        query.WorkstationField = Clip(query.WorkstationField, 64);
        query.RawXPath = Clip(query.RawXPath, MaxTextLength);
        query.Hosts = [.. (query.Hosts ?? []).Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => Clip(h)).Distinct(StringComparer.OrdinalIgnoreCase).Take(64)];
        return query;
    }
}
