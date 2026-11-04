using System.Globalization;
using AdMng.Core.EventLogs;
using AdMng.Core.EventLogs.Analysis;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.EventLogs;

/// <summary>
/// The texts of the event log features (track E) in the UI language: the codes of <see cref="EventCodes"/> (NTSTATUS,
/// Kerberos results, encryption types with RC4 and DES marked weak, pre-authentication types, logon types, 4625 failure
/// reasons), levels, friendly names of the common channels, access errors with their remedy, the raw XPath reasons, the
/// notes of the analyses with their arguments, and the names of the analysis enums. Dynamic key families; every key is in
/// <see cref="AllKeys"/> (the localization test checks both languages).
/// </summary>
public static class EventLogText
{
    /// <summary>The Security channel.</summary>
    public const string SecurityChannel = "Security";

    /// <summary>Channel name → key suffix of its friendly name (<c>EvtLog.Channel.&lt;suffix&gt;</c>), in the order offered.</summary>
    private static readonly (string Channel, string Key)[] Channels =
    [
        (SecurityChannel, "Security"),
        ("System", "System"),
        ("Application", "Application"),
        ("Directory Service", "DirectoryService"),
        ("DFS Replication", "DfsReplication"),
        ("DNS Server", "DnsServer"),
        ("Microsoft-Windows-GroupPolicy/Operational", "GroupPolicy"),
        ("Microsoft-Windows-NTLM/Operational", "Ntlm"),
        ("Microsoft-Windows-TerminalServices-LocalSessionManager/Operational", "RdsLocalSessions"),
        ("Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational", "RdsConnections"),
        ("Microsoft-Windows-PowerShell/Operational", "PowerShell"),
        ("Windows PowerShell", "WindowsPowerShell"),
        ("Microsoft-Windows-Windows Defender/Operational", "Defender"),
        ("Microsoft-Windows-TaskScheduler/Operational", "TaskScheduler"),
        ("Microsoft-Windows-Sysmon/Operational", "Sysmon"),
        ("ForwardedEvents", "ForwardedEvents"),
    ];

    /// <summary>The common channels offered before the host's own (Security, System, Application, Directory Service …).</summary>
    public static IReadOnlyList<string> CommonChannels { get; } = [.. Channels.Select(c => c.Channel)];

    /// <summary>The levels offered by the reader (Event Viewer's order).</summary>
    public static IReadOnlyList<byte> Levels { get; } =
        [EventLogLevels.Critical, EventLogLevels.Error, EventLogLevels.Warning, EventLogLevels.Information, EventLogLevels.Verbose];

    // ---- Keys -------------------------------------------------------------------------------------------------------------

    public static string LevelKey(byte level) => string.Create(CultureInfo.InvariantCulture, $"EvtLog.Level.{level}");

    public static string AccessKey(EventLogAccessError error) => $"EvtLog.Access.{error}";

    public static string RemedyKey(EventLogAccessError error) => $"EvtLog.Access.{error}.Remedy";

    public static string HostStateKey(EventLogHostState state) => $"EvtLog.HostState.{state}";

    public static string ModeKey(EventLogMode mode) => $"EvtLog.Mode.{mode}";

    public static string AuditStateKey(AuditEvidenceState state) => $"EvtLog.Audit.{state}";

    public static string SourceKindKey(AttemptSourceKind kind) => $"EvtLog.SourceKind.{kind}";

    public static string FailureKindKey(LogonFailureKind kind) => $"EvtLog.FailureKind.{kind}";

    public static string ProxyReasonKey(string reason) => $"EvtLog.ProxyReason.{reason}";

    public static string LockoutNoteKey(string key) => $"EventLogs.Lockout.{key}";

    public static string FailedLogonNoteKey(string key) => $"EventLogs.FailedLogons.{key}";

    public static string FindingKindKey(FailedLogonFindingKind kind) => $"EvtFail.Kind.{kind}";

    public static string FindingExplanationKey(FailedLogonFindingKind kind) => $"EvtFail.Kind.{kind}.Explanation";

    public static string SeverityKey(FailedLogonSeverity severity) => $"EvtFail.Severity.{severity}";

    public static string TimelineKindKey(LockoutTimelineKind kind) => $"EvtLock.Timeline.{kind}";

    public static string TimePresetKey(EventLogTimePreset preset) => $"EvtLog.TimePreset.{preset}";

    public static string OutcomeKey(EventAuditOutcome? outcome) => outcome is { } o ? $"EvtLog.Outcome.{o}" : "EvtLog.Outcome.Any";

    private static string ChannelKey(string suffix) => $"EvtLog.Channel.{suffix}";

    /// <summary>The note keys of the lockout analysis (every constant of <see cref="LockoutNoteKeys"/>).</summary>
    public static IReadOnlyList<string> LockoutNotes { get; } = Constants(typeof(LockoutNoteKeys));

    /// <summary>The note keys of the failed-logon analysis.</summary>
    public static IReadOnlyList<string> FailedLogonNotes { get; } = Constants(typeof(FailedLogonNoteKeys));

    /// <summary>Every dynamic key (both resource files must define each one).</summary>
    public static IEnumerable<string> AllKeys()
    {
        foreach (var code in EventCodes.All)
        {
            yield return code.Key;
        }

        foreach (var level in new byte[] { 0, 1, 2, 3, 4, 5 })
        {
            yield return LevelKey(level);
        }

        foreach (var (_, key) in Channels)
        {
            yield return ChannelKey(key);
        }

        foreach (var error in Enum.GetValues<EventLogAccessError>())
        {
            yield return AccessKey(error);
            yield return RemedyKey(error);
        }

        foreach (var reason in EventQueryXml.RawXPathReasons.All)
        {
            yield return reason;
        }

        foreach (var note in LockoutNotes)
        {
            yield return LockoutNoteKey(note);
        }

        foreach (var note in FailedLogonNotes)
        {
            yield return FailedLogonNoteKey(note);
        }

        foreach (var reason in Constants(typeof(ProxyReasonKeys)))
        {
            yield return ProxyReasonKey(reason);
        }

        foreach (var key in Enum.GetValues<EventLogHostState>().Select(HostStateKey)
            .Concat(Enum.GetValues<EventLogMode>().Select(ModeKey))
            .Concat(Enum.GetValues<AuditEvidenceState>().Select(AuditStateKey))
            .Concat(Enum.GetValues<AttemptSourceKind>().Select(SourceKindKey))
            .Concat(Enum.GetValues<LogonFailureKind>().Select(FailureKindKey))
            .Concat(Enum.GetValues<FailedLogonFindingKind>().SelectMany(k => new[] { FindingKindKey(k), FindingExplanationKey(k) }))
            .Concat(Enum.GetValues<FailedLogonSeverity>().Select(SeverityKey))
            .Concat(Enum.GetValues<LockoutTimelineKind>().Select(TimelineKindKey))
            .Concat(Enum.GetValues<EventLogTimePreset>().Select(TimePresetKey))
            .Concat([OutcomeKey(null), OutcomeKey(EventAuditOutcome.Success), OutcomeKey(EventAuditOutcome.Failure)]))
        {
            yield return key;
        }
    }

    // ---- Texts ------------------------------------------------------------------------------------------------------------

    /// <summary>"Informacje", "Ostrzeżenie" … (level 0, as Security audits use, is shown as Information like Event Viewer does).</summary>
    public static string Level(ILocalizer localizer, byte? level)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return level is { } l && l <= EventLogLevels.Verbose ? localizer[LevelKey(l)] : string.Empty;
    }

    /// <summary>The friendly name of a common channel ("Zabezpieczenia"), else the channel name itself.</summary>
    public static string Channel(ILocalizer localizer, string? channel)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        if (string.IsNullOrEmpty(channel))
        {
            return string.Empty;
        }

        foreach (var (name, key) in Channels)
        {
            if (string.Equals(name, channel, StringComparison.OrdinalIgnoreCase))
            {
                return localizer[ChannelKey(key)];
            }
        }

        return channel;
    }

    /// <summary>"Zabezpieczenia (Security)" for a common channel, else the name: what the channel list shows.</summary>
    public static string ChannelOption(ILocalizer localizer, string channel)
    {
        var friendly = Channel(localizer, channel);
        return string.Equals(friendly, channel, StringComparison.Ordinal) ? channel : $"{friendly} ({channel})";
    }

    /// <summary>What a code means ("złe hasło"); for a code outside the catalog "nieznany kod".</summary>
    public static string Code(ILocalizer localizer, EventCode code)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(code);
        return code.IsKnown && localizer.Contains(code.Key) ? localizer[code.Key] : localizer[L.EventLogs.UnknownCode];
    }

    /// <summary>"0xC000006A – złe hasło" (the code as the event writes it and its meaning).</summary>
    public static string CodeWithMeaning(ILocalizer localizer, EventCode code) =>
        localizer.Format(L.EventLogs.CodeWithMeaning, code.Display, Code(localizer, code));

    /// <summary>
    /// The meaning of a code as an analysis reports it (<see cref="EventCodeCount.Code"/>: <c>0x</c> + hex; NTSTATUS for 4625
    /// and 4776, a Kerberos result for 4768, 4769 and 4771).
    /// </summary>
    public static string AnalysisCode(ILocalizer localizer, int eventId, string? code)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        if (code is null)
        {
            return localizer[L.EventLogs.NoCode];
        }

        var kind = eventId is 4768 or 4769 or 4771 ? EventCodeKind.KerberosResult : EventCodeKind.NtStatus;
        return EventCodes.Describe(kind, code) is { } described ? CodeWithMeaning(localizer, described) : code;
    }

    /// <summary>"4771 · 0x18 – złe hasło: 3" – one line of a code count.</summary>
    public static string CodeCount(ILocalizer localizer, EventCodeCount count)
    {
        ArgumentNullException.ThrowIfNull(count);
        return localizer.Format(L.EventLogs.CodeCountLine, count.EventId.ToString(CultureInfo.InvariantCulture), AnalysisCode(localizer, count.EventId, count.Code),
            count.Count.ToString("N0", localizer.Culture));
    }

    /// <summary>
    /// The explanation of a field whose value is a code (<c>Status</c>, <c>SubStatus</c>, <c>FailureReason</c>,
    /// <c>LogonType</c>, <c>TicketEncryptionType</c>, <c>PreAuthType</c> …), null for other fields.
    /// </summary>
    public static string? Explain(ILocalizer localizer, EventLogRecord record, string name, string? value)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        EventCodeKind? kind = name switch
        {
            "Status" when record.EventId is 4768 or 4769 or 4771 or 4772 or 4773 => EventCodeKind.KerberosResult,
            "Status" or "SubStatus" or "FailureCode" => EventCodeKind.NtStatus,
            "FailureReason" => EventCodeKind.FailureReason,
            "LogonType" => EventCodeKind.LogonType,
            "TicketEncryptionType" or "SessionKeyEncryptionType" or "PreAuthEncryptionType" => EventCodeKind.KerberosEncryptionType,
            "PreAuthType" => EventCodeKind.KerberosPreAuthType,
            _ => null,
        };
        if (kind is not { } k || EventCodes.Describe(k, value) is not { } code)
        {
            return null;
        }

        return Code(localizer, code);
    }

    /// <summary>The access error of a source with its remedy ("Brak dostępu … Dodaj konto do grupy Czytelnicy dziennika zdarzeń …").</summary>
    public static string AccessError(ILocalizer localizer, EventLogAccessError error, int? nativeErrorCode = null)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        var text = localizer[AccessKey(error)] + " " + localizer[RemedyKey(error)];
        return nativeErrorCode is { } code ? localizer.Format(L.EventLogs.WithErrorCode, text, code.ToString(CultureInfo.InvariantCulture)) : text;
    }

    /// <summary>The short name of an access error (without the remedy).</summary>
    public static string AccessErrorShort(ILocalizer localizer, EventLogAccessError error)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[AccessKey(error)];
    }

    public static string HostState(ILocalizer localizer, EventLogHostState state) => localizer[HostStateKey(state)];

    public static string Mode(ILocalizer localizer, EventLogMode mode) => localizer[ModeKey(mode)];

    public static string AuditState(ILocalizer localizer, AuditEvidenceState state) => localizer[AuditStateKey(state)];

    public static string SourceKind(ILocalizer localizer, AttemptSourceKind kind) => localizer[SourceKindKey(kind)];

    public static string FailureKind(ILocalizer localizer, LogonFailureKind kind) => localizer[FailureKindKey(kind)];

    public static string FindingKind(ILocalizer localizer, FailedLogonFindingKind kind) => localizer[FindingKindKey(kind)];

    public static string FindingExplanation(ILocalizer localizer, FailedLogonFindingKind kind) => localizer[FindingExplanationKey(kind)];

    public static string Severity(ILocalizer localizer, FailedLogonSeverity severity) => localizer[SeverityKey(severity)];

    public static string TimelineKind(ILocalizer localizer, LockoutTimelineKind kind) => localizer[TimelineKindKey(kind)];

    public static string TimePreset(ILocalizer localizer, EventLogTimePreset preset) => localizer[TimePresetKey(preset)];

    public static string Outcome(ILocalizer localizer, EventAuditOutcome? outcome) => localizer[OutcomeKey(outcome)];

    /// <summary>The reason of the raw XPath check (its key is a resource key).</summary>
    public static string RawXPathReason(ILocalizer localizer, string reasonKey)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer.Contains(reasonKey) ? localizer[reasonKey] : localizer[EventQueryXml.RawXPathReasons.Syntax];
    }

    /// <summary>"Komputer WAW-PC001" / "Adres 10.10.1.21" – a source with its kind.</summary>
    public static string Source(ILocalizer localizer, AttemptSource source)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return source.Kind == AttemptSourceKind.Unknown
            ? SourceKind(localizer, AttemptSourceKind.Unknown)
            : localizer.Format(L.EventLogs.SourceWithKind, SourceKind(localizer, source.Kind), source.Value);
    }

    /// <summary>A note of the lockout analysis with its arguments in the UI language (times in local time).</summary>
    public static string LockoutNote(ILocalizer localizer, AnalysisNote note, TimeZoneInfo zone) =>
        Note(localizer, LockoutNoteKey(note.Key), note, zone);

    /// <summary>A note of the failed-logon analysis.</summary>
    public static string FailedLogonNote(ILocalizer localizer, AnalysisNote note, TimeZoneInfo zone) =>
        Note(localizer, FailedLogonNoteKey(note.Key), note, zone);

    /// <summary>A moment in local time ("3.11.2026 08:34:01"), empty for null.</summary>
    public static string Moment(ILocalizer localizer, DateTimeOffset? time, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(zone);
        return time is { } t ? TimeZoneInfo.ConvertTime(t, zone).ToString("G", localizer.Culture) : string.Empty;
    }

    /// <summary>"45 min", "6 godz.", "3 dni" – how far back a log reaches.</summary>
    public static string Duration(ILocalizer localizer, TimeSpan span)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalHours < 1
            ? localizer.Format(L.EventLogs.DurationMinutes, Math.Max(1, (int)Math.Round(span.TotalMinutes)))
            : span.TotalDays < 2
                ? localizer.Format(L.EventLogs.DurationHours, (int)Math.Round(span.TotalHours))
                : localizer.Format(L.EventLogs.DurationDays, (int)Math.Round(span.TotalDays));
    }

    /// <summary>"12,5 MB" – a size in megabytes, empty for null.</summary>
    public static string Megabytes(ILocalizer localizer, long? bytes) =>
        bytes is { } b ? localizer.Format(L.EventLogs.Megabytes, (b / 1048576.0).ToString("0.#", localizer.Culture)) : string.Empty;

    /// <summary>
    /// A short summary of a record from its key fields: the account, the source (address, workstation, caller), the result
    /// code with its meaning and the logon type – or the first values when it has none of them. <paramref name="accountText"/>
    /// replaces a logged target account (a masked unknown name).
    /// </summary>
    public static string Summary(ILocalizer localizer, EventLogRecord record, string? accountText = null)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(record);
        var parts = new List<string>();
        if (Account(record.Get("TargetUserName"), record.Get("TargetDomainName"), record.EventId) is { } target)
        {
            parts.Add(accountText ?? target);
        }
        else if (Account(record.Get("SubjectUserName"), record.Get("SubjectDomainName"), record.EventId) is { } subject)
        {
            parts.Add(subject);
        }

        if (EventCodes.NormalizeIpAddress(record.Get("IpAddress")) is { } ip && ip != "-")
        {
            parts.Add(ip);
        }

        var workstation = EventCodes.NormalizeWorkstation(record.Get("WorkstationName") ?? record.Get("Workstation"));
        if (record.EventId == 4740)
        {
            workstation = EventCodes.NormalizeWorkstation(record.Get("TargetDomainName"));
        }

        if (workstation is not null)
        {
            parts.Add(workstation);
        }

        if (EventCodes.ResultOf(record) is { } result && result.Value != 0)
        {
            parts.Add(CodeWithMeaning(localizer, result));
        }

        if (EventCodes.Describe(EventCodeKind.LogonType, record.Get("LogonType")) is { IsKnown: true } logonType)
        {
            parts.Add(localizer.Format(L.EventLogs.LogonTypeShort, logonType.Display, Code(localizer, logonType)));
        }

        if (parts.Count == 0)
        {
            parts.AddRange(record.Data.Take(3).Select(d => d.Key + ": " + Shorten(d.Value)));
        }

        return string.Join(" · ", parts);
    }

    private static string? Account(string? name, string? domain, int eventId)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "-")
        {
            return null;
        }

        // 4740 writes the caller computer in TargetDomainName; 4768/4771 the realm – not worth repeating.
        return eventId is 4740 or 4768 or 4769 or 4771 or 4776 || string.IsNullOrWhiteSpace(domain) || domain == "-" ? name : domain + "\\" + name;
    }

    private static string Shorten(string value) => value.Length > 60 ? value[..57] + "…" : value;

    private static string Note(ILocalizer localizer, string key, AnalysisNote note, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(note);
        var arguments = note.Arguments.Select(a => (object?)Argument(localizer, a, zone)).ToArray();
        return localizer.Contains(key) ? localizer.Format(key, arguments) : note.Key;
    }

    /// <summary>An argument of a note: an enum name or key becomes its text, an ISO time the local time, anything else stays.</summary>
    private static string Argument(ILocalizer localizer, string argument, TimeZoneInfo zone)
    {
        if (Enum.TryParse<AttemptSourceKind>(argument, out var kind) && Enum.IsDefined(kind) && !int.TryParse(argument, out _))
        {
            return SourceKind(localizer, kind);
        }

        if (Enum.TryParse<EventLogAccessError>(argument, out var error) && Enum.IsDefined(error) && !int.TryParse(argument, out _))
        {
            return AccessError(localizer, error);
        }

        if (argument is ProxyReasonKeys.NamePattern or ProxyReasonKeys.ManyAccounts)
        {
            return localizer[ProxyReasonKey(argument)];
        }

        if (argument.Length >= 19 && argument[4] == '-' && argument[10] == 'T'
            && DateTimeOffset.TryParse(argument, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time))
        {
            return Moment(localizer, time, zone);
        }

        return localizer.Contains(argument) ? localizer[argument] : argument;
    }

    private static List<string> Constants(Type type) =>
        [.. type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)];
}
