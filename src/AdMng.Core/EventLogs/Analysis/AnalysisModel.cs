using System.Globalization;

namespace AdMng.Core.EventLogs.Analysis;

/// <summary>How the source of a logon attempt is identified (design note e-event-logs.md 4.1, 4.2).</summary>
public enum AttemptSourceKind
{
    /// <summary>The record names no source (4776 without a workstation, 4771 without an address).</summary>
    Unknown,

    /// <summary>The 4740 "Caller Computer Name" (<c>TargetDomainName</c>; supplied by the client, may be a front-end server).</summary>
    CallerComputer,

    /// <summary>The workstation name the client supplied (4776 <c>Workstation</c>, 4625/4624 <c>WorkstationName</c>).</summary>
    Workstation,

    /// <summary>The client address the DC saw (4771/4768/4625/4624 <c>IpAddress</c>, the <c>::ffff:</c> prefix stripped).</summary>
    IpAddress,

    /// <summary>The logging computer itself (a loopback address, or no address and no workstation in a 4625/4624).</summary>
    Local,
}

/// <summary>
/// Where logon attempts came from. <see cref="Value"/> is normalized: computer names in upper case without leading
/// backslashes and a trailing <c>$</c>; addresses in lower case without the IPv4-mapped prefix; for
/// <see cref="AttemptSourceKind.Local"/> the logging host (lower case, no trailing dot); empty for
/// <see cref="AttemptSourceKind.Unknown"/>. Held in memory only (CLAUDE.md rule 1); <see cref="ToString"/> carries no value.
/// </summary>
public readonly record struct AttemptSource(AttemptSourceKind Kind, string Value)
{
    /// <summary>The value comes from the client (a workstation or caller computer name), not from the DC's own view.</summary>
    public bool IsClientSupplied => Kind is AttemptSourceKind.Workstation or AttemptSourceKind.CallerComputer;

    /// <summary>The kind only.</summary>
    public override string ToString() => Kind + " source";
}

/// <summary>
/// What a logon failure code means for the analyses (NTSTATUS of 4625/4776, Kerberos result codes of 4768/4771). Keys
/// only; the texts are in the Presentation resources.
/// </summary>
public enum LogonFailureKind
{
    /// <summary>Not a failure (status 0).</summary>
    None,

    /// <summary>Wrong password (<c>0xC000006A</c>, Kerberos <c>0x18</c>) – counts toward the lockout threshold.</summary>
    BadPassword,

    /// <summary>No such account (<c>0xC0000064</c>, Kerberos <c>0x6</c>).</summary>
    UnknownAccount,

    /// <summary>The account is locked out (<c>0xC0000234</c>).</summary>
    AccountLocked,

    /// <summary>Kerberos <c>0x12</c>: the client's credentials were revoked – disabled, expired or locked out.</summary>
    AccountRevoked,

    /// <summary><c>0xC0000072</c>.</summary>
    AccountDisabled,

    /// <summary><c>0xC0000193</c>.</summary>
    AccountExpired,

    /// <summary><c>0xC0000071</c>, Kerberos <c>0x17</c>.</summary>
    PasswordExpired,

    /// <summary><c>0xC0000224</c>.</summary>
    PasswordMustChange,

    /// <summary>Outside the logon hours (<c>0xC000006F</c>).</summary>
    LogonHours,

    /// <summary>Not allowed from this workstation (<c>0xC0000070</c>).</summary>
    WorkstationRestriction,

    /// <summary>The logon type is not granted (<c>0xC000015B</c>).</summary>
    LogonTypeNotGranted,

    /// <summary>The clocks differ too much (<c>0xC0000133</c>, Kerberos <c>0x25</c>).</summary>
    ClockSkew,

    /// <summary>Selective authentication of a trust (<c>0xC0000413</c>).</summary>
    AuthenticationFirewall,

    /// <summary>Kerberos <c>0xC</c>: a KDC policy (logon hours, workstation, Protected Users, silo).</summary>
    Policy,

    /// <summary>Any other code.</summary>
    Other,
}

/// <summary>How many records of one event ID carried one failure code.</summary>
/// <param name="EventId">The event ID.</param>
/// <param name="Code">The code as <see cref="EventCode.Display"/> writes it (<c>0xC000006A</c>, <c>0x18</c>; 4625: the sub-status behind the generic codes), null when the record had none.</param>
/// <param name="Kind">What the code means.</param>
/// <param name="Count">The number of records.</param>
public sealed record EventCodeCount(int EventId, string? Code, LogonFailureKind Kind, int Count)
{
    /// <summary>The event ID and kind only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{EventId} {Kind}: {Count}");
}

/// <summary>A record an analysis result points at, for drill-down: <see cref="EventLogRecord.Host"/> (as read) and <see cref="EventLogRecord.RecordId"/>.</summary>
public readonly record struct EventRecordRef(string Host, long RecordId)
{
    /// <summary>The record ID only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"record #{RecordId}");
}

/// <summary>How much a note matters.</summary>
public enum AnalysisNoteSeverity
{
    Info,
    Warning,
}

/// <summary>
/// A note of an analysis report: a stable key the UI localizes and its arguments (documented per key: times as ISO 8601
/// round-trip strings, counts and enum names invariant). The arguments may hold directory values (DC and computer names,
/// addresses) – in memory only; <see cref="ToString"/> carries the key only.
/// </summary>
public sealed record AnalysisNote(string Key, IReadOnlyList<string> Arguments, AnalysisNoteSeverity Severity = AnalysisNoteSeverity.Info)
{
    /// <summary>The key only.</summary>
    public override string ToString() => Key;
}
