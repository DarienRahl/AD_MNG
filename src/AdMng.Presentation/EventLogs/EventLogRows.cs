using System.Globalization;
using AdMng.Core.EventLogs;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.EventLogs;

/// <summary>The time window of a query or an analysis.</summary>
public enum EventLogTimePreset
{
    LastHour,
    Last24Hours,
    Last7Days,

    /// <summary>From – to as the operator typed them.</summary>
    Custom,

    /// <summary>The lockout source's default: 24 hours before the last lockout a DC reports (else before now).</summary>
    BeforeLastLockout,
}

/// <summary>What a source of the reader is.</summary>
public enum EventLogSourceKind
{
    DomainController,
    Computer,
    Files,
}

/// <summary>Helpers of the time presets.</summary>
public static class EventLogTimeWindows
{
    /// <summary>The window of a fixed preset ending at <paramref name="now"/> (null for <see cref="EventLogTimePreset.Custom"/> and the lockout default).</summary>
    public static (DateTimeOffset From, DateTimeOffset To)? Of(EventLogTimePreset preset, DateTimeOffset now) => preset switch
    {
        EventLogTimePreset.LastHour => (now.AddHours(-1), now),
        EventLogTimePreset.Last24Hours => (now.AddHours(-24), now),
        EventLogTimePreset.Last7Days => (now.AddDays(-7), now),
        _ => null,
    };
}

/// <summary>One record of a merged timeline (the reader, the lockout timeline, a drill-down). Shown only while the tab is open.</summary>
public sealed class EventLogRecordRow
{
    public EventLogRecordRow(EventLogRecord record, ILocalizer localizer, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(localizer);
        Record = record;
        TimeText = EventLogText.Moment(localizer, record.TimeCreated, zone);
        HostText = EventHostNames.ShortName(record.Host) is { Length: > 0 } shortName ? shortName : record.Host;
        ChannelText = EventLogText.Channel(localizer, record.Channel);
        LevelText = record.IsAuditFailure
            ? EventLogText.Outcome(localizer, EventAuditOutcome.Failure)
            : record.IsAuditSuccess ? EventLogText.Outcome(localizer, EventAuditOutcome.Success) : EventLogText.Level(localizer, record.Level);
        Summary = EventLogText.Summary(localizer, record);
    }

    public EventLogRecord Record { get; }

    /// <summary>Local time.</summary>
    public string TimeText { get; }

    /// <summary>The first label of the host the record was read from.</summary>
    public string HostText { get; }

    public string ChannelText { get; }

    public int EventId => Record.EventId;

    public string EventIdText => Record.EventId.ToString(CultureInfo.InvariantCulture);

    /// <summary>The level, or the audit outcome for Security audits.</summary>
    public string LevelText { get; }

    public string Provider => Record.Provider ?? string.Empty;

    /// <summary>The key fields (account, source, code with its meaning, logon type).</summary>
    public string Summary { get; }

    public bool IsFailure => Record.IsAuditFailure || Record.Level is EventLogLevels.Error or EventLogLevels.Critical;

    /// <summary>For screen readers: the columns in one line.</summary>
    public string AutomationName => $"{TimeText}, {HostText}, {EventIdText}, {LevelText}, {Summary}";

    /// <summary>Ctrl+F: the text is in a shown column or in a value of the record (case-insensitive).</summary>
    public bool Matches(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var term = text.Trim();
        return Contains(Summary, term) || Contains(Record.Host, term) || Contains(Provider, term) || Contains(EventIdText, term)
            || Contains(ChannelText, term) || Contains(LevelText, term) || Record.Data.Any(d => Contains(d.Value, term) || Contains(d.Key, term));
    }

    /// <summary>No value (the row is shown, never logged).</summary>
    public override string ToString() => "event row";

    private static bool Contains(string? value, string term) => value?.Contains(term, StringComparison.CurrentCultureIgnoreCase) == true;
}

/// <summary>One field of the selected record in the details: its name, value, the meaning of a code and the AD object it names.</summary>
public sealed partial class EventLogFieldRow : ObservableObject
{
    public EventLogFieldRow(string name, string value, string? explanation = null, bool isSystem = false)
    {
        Name = name;
        Value = value;
        Explanation = explanation;
        IsSystem = isSystem;
    }

    public string Name { get; }

    public string Value { get; }

    /// <summary>What a code-like value means ("złe hasło"), null for other fields.</summary>
    public string? Explanation { get; }

    public bool HasExplanation => !string.IsNullOrEmpty(Explanation);

    /// <summary>A field of the <c>System</c> part (time, provider …), not of the event data.</summary>
    public bool IsSystem { get; }

    /// <summary>The AD object the value names (a SID or an account resolved through the window's DC), null when none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasObject))]
    public partial string? ObjectDn { get; set; }

    /// <summary>The name of <see cref="ObjectDn"/> ("Jan Kowalski").</summary>
    [ObservableProperty]
    public partial string? ObjectName { get; set; }

    public bool HasObject => ObjectDn is not null;

    /// <summary>"Name: value (meaning)" – the line of "Kopiuj" and of the exports.</summary>
    public string Line => HasExplanation ? $"{Name}: {Value} ({Explanation})" : $"{Name}: {Value}";

    public override string ToString() => Name;
}

/// <summary>A source of the reader: a domain controller, a computer or files.</summary>
public sealed partial class EventLogSourceViewModel : ObservableObject
{
    public EventLogSourceViewModel(EventLogSourceKind kind, string host, string kindText, IReadOnlyList<string>? files = null, bool isPdcEmulator = false)
    {
        Kind = kind;
        Host = host;
        KindText = kindText;
        Files = files ?? [];
        IsPdcEmulator = isPdcEmulator;
    }

    public EventLogSourceKind Kind { get; }

    /// <summary>The FQDN, or the display name of the files.</summary>
    public string Host { get; }

    /// <summary>"kontroler domeny, emulator PDC", "komputer", "pliki .evtx".</summary>
    public string KindText { get; }

    public IReadOnlyList<string> Files { get; }

    public bool IsPdcEmulator { get; }

    public bool IsFiles => Kind == EventLogSourceKind.Files;

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = true;

    public string AutomationName => $"{Host}, {KindText}";

    public override string ToString() => Kind.ToString();
}

/// <summary>The state, coverage and access of one source during and after a run (the "Zasięg i dostęp" panel).</summary>
public sealed partial class EventLogHostRow : ObservableObject
{
    private readonly ILocalizer _localizer;
    private readonly TimeZoneInfo _zone;

    public EventLogHostRow(string host, string kindText, ILocalizer localizer, TimeZoneInfo zone)
    {
        Host = host;
        KindText = kindText;
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _zone = zone ?? throw new ArgumentNullException(nameof(zone));
        StateText = EventLogText.HostState(localizer, EventLogHostState.Waiting);
    }

    public string Host { get; }

    /// <summary>"kontroler domeny (emulator PDC)", "komputer", "pliki .evtx".</summary>
    public string KindText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(HasFailed))]
    public partial EventLogHostState State { get; private set; } = EventLogHostState.Waiting;

    [ObservableProperty]
    public partial string StateText { get; private set; }

    [ObservableProperty]
    public partial int Records { get; private set; }

    [ObservableProperty]
    public partial EventLogCoverage? Coverage { get; private set; }

    /// <summary>"najstarsze 3.11.2026 01:12 · 12,5 MB z 128 MB · nadpisywanie".</summary>
    [ObservableProperty]
    public partial string CoverageText { get; private set; } = string.Empty;

    /// <summary>"DC01 sięga tylko 6 godz. wstecz (od …)" when the window starts before the oldest record.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    public partial string? Warning { get; private set; }

    /// <summary>The access error with its remedy.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public EventLogAccessError? Error { get; private set; }

    public bool HasWarning => Warning is not null;

    public bool HasError => ErrorText is not null;

    public bool HasFailed => State == EventLogHostState.Failed;

    public bool IsBusy => State is EventLogHostState.Opening or EventLogHostState.Reading;

    public string RecordsText => Records.ToString("N0", _localizer.Culture);

    /// <summary>Applies a progress report; <paramref name="from"/> is the window start the coverage is checked against.</summary>
    public void Update(EventLogHostProgress progress, DateTimeOffset? from, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(progress);
        State = progress.State;
        StateText = EventLogText.HostState(_localizer, progress.State);
        Records = progress.Records;
        OnPropertyChanged(nameof(RecordsText));
        Error = progress.Error;
        ErrorText = progress.Error is { } error ? EventLogText.AccessError(_localizer, error, progress.NativeErrorCode) : null;
        if (progress.Coverage is { } coverage)
        {
            Coverage = coverage;
            CoverageText = Describe(coverage);
            Warning = WarningOf(coverage, from, now);
        }
    }

    /// <summary>The coverage in one line.</summary>
    public string Describe(EventLogCoverage coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        if (!coverage.IsEnabled)
        {
            return _localizer[L.EventLogs.CoverageDisabled];
        }

        var parts = new List<string>();
        parts.Add(coverage.Oldest is { } oldest
            ? _localizer.Format(L.EventLogs.CoverageOldest, EventLogText.Moment(_localizer, oldest, _zone))
            : _localizer[L.EventLogs.CoverageEmpty]);
        if (coverage.RecordCount is { } count)
        {
            parts.Add(_localizer.Format(L.EventLogs.CoverageRecords, count.ToString("N0", _localizer.Culture)));
        }

        if (coverage.FileSizeBytes is not null || coverage.MaximumSizeBytes is not null)
        {
            parts.Add(coverage.MaximumSizeBytes is null
                ? EventLogText.Megabytes(_localizer, coverage.FileSizeBytes)
                : _localizer.Format(L.EventLogs.CoverageSize, EventLogText.Megabytes(_localizer, coverage.FileSizeBytes ?? 0), EventLogText.Megabytes(_localizer, coverage.MaximumSizeBytes)));
        }

        parts.Add(EventLogText.Mode(_localizer, coverage.Mode));
        if (coverage.IsFull)
        {
            parts.Add(_localizer[L.EventLogs.CoverageFull]);
        }

        return string.Join(" · ", parts);
    }

    /// <summary>"DC01 sięga tylko 6 godz. wstecz (od …)" when the log starts after <paramref name="from"/>.</summary>
    public string? WarningOf(EventLogCoverage coverage, DateTimeOffset? from, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        if (from is not { } start || !coverage.IsEnabled || coverage.Oldest is not { } oldest || oldest <= start)
        {
            return null;
        }

        return _localizer.Format(L.EventLogs.CoverageShort, EventHostNames.ShortName(Host) is { Length: > 0 } shortName ? shortName : Host,
            EventLogText.Duration(_localizer, now - oldest), EventLogText.Moment(_localizer, oldest, _zone));
    }

    public string AutomationName => $"{Host}, {StateText}, {CoverageText}";

    public override string ToString() => State.ToString();
}
