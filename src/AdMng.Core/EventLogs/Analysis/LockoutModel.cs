namespace AdMng.Core.EventLogs.Analysis;

/// <summary>
/// The account whose lockout is investigated (design note e-event-logs.md 4.1), as read from the window's DC. Directory
/// values in memory only; <see cref="ToString"/> carries none.
/// </summary>
/// <param name="SamAccountName">The sAMAccountName as stored in AD (the event log compares data case-sensitively).</param>
/// <param name="Sid">The objectSid (<c>S-1-5-21-…</c>), null when not known.</param>
/// <param name="UserPrincipalName">The userPrincipalName, null when not set.</param>
/// <param name="DistinguishedName">The DN (for the UI's links), null when not known.</param>
public sealed record LockoutAccount(string SamAccountName, string? Sid = null, string? UserPrincipalName = null, string? DistinguishedName = null)
{
    public override string ToString() => "lockout account";
}

/// <summary>The lockout state of the account on one DC (read through <c>DomainControllerReader</c>, one short-lived session per DC).</summary>
/// <param name="Server">The DC's DNS host name.</param>
/// <param name="IsPdcEmulator">The DC holds the PDC emulator role (bad passwords elsewhere are retried there).</param>
/// <param name="BadPasswordCount"><c>badPwdCount</c> (not replicated – per DC).</param>
/// <param name="BadPasswordTime"><c>badPasswordTime</c>, null when 0.</param>
/// <param name="LockoutTime"><c>lockoutTime</c>, null when 0 (not locked).</param>
/// <param name="LastLogon"><c>lastLogon</c> (not replicated), null when 0.</param>
/// <param name="ErrorKey">Why the state could not be read (a key the UI localizes), null when it was read.</param>
public sealed record AccountLockoutState(
    string Server,
    bool IsPdcEmulator = false,
    int? BadPasswordCount = null,
    DateTimeOffset? BadPasswordTime = null,
    DateTimeOffset? LockoutTime = null,
    DateTimeOffset? LastLogon = null,
    string? ErrorKey = null)
{
    /// <summary>The DC's addresses when known (an attempt "from" a DC address was relayed by that DC).</summary>
    public IReadOnlyList<string> Addresses { get; init; } = [];

    public override string ToString() => IsPdcEmulator ? "lockout state (PDC emulator)" : "lockout state";
}

/// <summary>The lockout policy that applies to the account (the domain policy or the user's resultant PSO).</summary>
/// <param name="Threshold">Bad passwords that lock the account; 0 = never locked.</param>
/// <param name="ObservationWindow">"Reset account lockout counter after", null when not known.</param>
/// <param name="Duration">How long a lockout lasts; null or not positive = until an administrator unlocks the account.</param>
public sealed record LockoutPolicy(int Threshold, TimeSpan? ObservationWindow = null, TimeSpan? Duration = null)
{
    public override string ToString() => "lockout policy";
}

/// <summary>Tuning of <see cref="LockoutInvestigation.Analyze"/>.</summary>
public sealed record LockoutInvestigationOptions
{
    /// <summary>
    /// Name patterns of front-end servers that authenticate on behalf of users (Exchange/OWA/ActiveSync, ADFS and Web
    /// Application Proxy, NPS/RADIUS, RD Gateway, IIS) – a matching caller or workstation is marked as a likely proxy.
    /// </summary>
    public static IReadOnlyList<string> DefaultProxyNamePatterns { get; } =
        ["*EXCH*", "*MAIL*", "*OWA*", "*ADFS*", "*WAP*", "*NPS*", "*RADIUS*", "*RDG*", "*GATEWAY*", "*IIS*", "*WEB*", "*PROXY*"];

    /// <summary>The defaults (declared after <see cref="DefaultProxyNamePatterns"/>: static initializers run in order).</summary>
    public static LockoutInvestigationOptions Default { get; } = new();

    /// <summary>Patterns with <c>*</c> and <c>?</c>, matched case-insensitively against computer names (full and first label).</summary>
    public IReadOnlyList<string> ProxyNamePatterns { get; init; } = DefaultProxyNamePatterns;

    /// <summary>Other accounts failing from one source (in the records given) that mark it as a likely proxy.</summary>
    public int ProxyAccountThreshold { get; init; } = 3;

    /// <summary>The observation window used when the policy does not say.</summary>
    public TimeSpan DefaultObservationWindow { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>How long after a lockout a bad password still counts as part of it (the lockout replicates to other DCs).</summary>
    public TimeSpan LockoutTolerance { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>Everything <see cref="LockoutInvestigation.Analyze"/> works on – read by the Presentation, in memory only.</summary>
public sealed record LockoutInvestigationInput
{
    public required LockoutAccount Account { get; init; }

    /// <summary>The lockout state of the account on every DC.</summary>
    public IReadOnlyList<AccountLockoutState> DomainControllers { get; init; } = [];

    /// <summary>The DC that wrote <c>lockoutTime</c> (<c>msDS-ReplAttributeMetaData</c>), null when not known.</summary>
    public string? OriginatingDc { get; init; }

    public required LockoutPolicy Policy { get; init; }

    /// <summary>The window start (UTC).</summary>
    public required DateTimeOffset From { get; init; }

    /// <summary>The window end (UTC); also the moment "locked now" is judged at.</summary>
    public required DateTimeOffset To { get; init; }

    /// <summary>
    /// The records read with <see cref="LockoutInvestigation.Queries"/> from every DC. Records of other accounts may be
    /// included (from a broader read): they only count as other accounts failing from a source (likely proxy).
    /// </summary>
    public IReadOnlyList<EventLogRecord> Records { get; init; } = [];

    /// <summary>The coverage of the DCs' Security logs.</summary>
    public IReadOnlyList<EventLogCoverage> Coverage { get; init; } = [];

    /// <summary>Hosts whose log could not be read, by host.</summary>
    public IReadOnlyDictionary<string, EventLogAccessError> ReadFailures { get; init; } = new Dictionary<string, EventLogAccessError>();

    public LockoutInvestigationOptions Options { get; init; } = LockoutInvestigationOptions.Default;

    public override string ToString() => "lockout investigation input";
}

/// <summary>One lockout (4740) of the account.</summary>
/// <param name="Time">When the DC logged it.</param>
/// <param name="DomainController">The DC that logged it (normalized host).</param>
/// <param name="CallerComputer">The caller computer (normalized), null when the record names none.</param>
/// <param name="Record">The record.</param>
public sealed record LockoutEventEntry(DateTimeOffset Time, string DomainController, string? CallerComputer, EventRecordRef Record)
{
    /// <summary>Bad passwords read within the observation window before it.</summary>
    public int BadPasswordsBefore { get; init; }

    /// <summary>The caller computer is a domain controller (the real client is in that DC's log).</summary>
    public bool CallerIsDomainController { get; init; }

    public override string ToString() => "lockout";
}

/// <summary>Why a source is marked as a likely proxy.</summary>
public static class ProxyReasonKeys
{
    /// <summary>Its name (or a workstation name seen with its address) matches a proxy name pattern.</summary>
    public const string NamePattern = nameof(NamePattern);

    /// <summary>Failures for many other accounts come from it.</summary>
    public const string ManyAccounts = nameof(ManyAccounts);
}

/// <summary>
/// A source of the account's failed attempts or lockouts, ranked: sources that are not DC addresses first, then bad
/// passwords within the observation window before a lockout, bad passwords, failures, lockouts named, the last attempt.
/// </summary>
public sealed record LockoutSource
{
    /// <summary>
    /// The source. A computer named both as a 4740 caller and as a workstation is one source of kind
    /// <see cref="AttemptSourceKind.Workstation"/> (with <see cref="LockoutCount"/>).
    /// </summary>
    public required AttemptSource Source { get; init; }

    /// <summary>Failed attempts for the account from it.</summary>
    public int FailureCount { get; init; }

    /// <summary>Of them, wrong passwords (what increments <c>badPwdCount</c>).</summary>
    public int BadPasswordCount { get; init; }

    /// <summary>Wrong passwords within the observation window before a lockout (what locked the account).</summary>
    public int ContributingCount { get; init; }

    /// <summary>Lockouts (4740) naming it as the caller computer.</summary>
    public int LockoutCount { get; init; }

    /// <summary>Failures answered "account locked" (<c>0xC0000234</c>, Kerberos <c>0x12</c>) – it keeps trying.</summary>
    public int WhileLockedCount { get; init; }

    /// <summary>Clock skew failures (Kerberos <c>0x25</c>, <c>0xC0000133</c>).</summary>
    public int ClockSkewCount { get; init; }

    public DateTimeOffset First { get; init; }

    public DateTimeOffset Last { get; init; }

    /// <summary>The DCs that logged it (normalized hosts, sorted).</summary>
    public IReadOnlyList<string> DomainControllers { get; init; } = [];

    /// <summary>Failures per event ID and code.</summary>
    public IReadOnlyList<EventCodeCount> Codes { get; init; } = [];

    /// <summary>Workstation names seen together with an address (4625), sorted – for the pivot to the computer.</summary>
    public IReadOnlyList<string> RelatedNames { get; init; } = [];

    /// <summary>The source is (or names) a domain controller.</summary>
    public bool IsDomainController { get; init; }

    /// <summary>Other accounts failing from it in the records given.</summary>
    public int OtherAccountCount { get; init; }

    /// <summary>It looks like a front-end server authenticating for users (the real client is behind it).</summary>
    public bool LikelyProxy { get; init; }

    /// <summary>One of <see cref="ProxyReasonKeys"/> when <see cref="LikelyProxy"/>.</summary>
    public string? ProxyReasonKey { get; init; }

    public override string ToString() => Source + " (lockout)";
}

/// <summary>What a timeline entry is.</summary>
public enum LockoutTimelineKind
{
    /// <summary>4740.</summary>
    Lockout,

    /// <summary>4767.</summary>
    Unlock,

    /// <summary>4771, 4776, 4625 (4768) failure.</summary>
    Failure,
}

/// <summary>One record of the account on the timeline.</summary>
/// <param name="Time">When it was logged.</param>
/// <param name="Host">The DC that logged it (normalized host).</param>
/// <param name="Kind">What it is.</param>
/// <param name="EventId">The event ID.</param>
/// <param name="Source">Where it came from (the caller of a lockout), null for an unlock or a lockout without a caller.</param>
/// <param name="Record">The record.</param>
public sealed record LockoutTimelineEntry(DateTimeOffset Time, string Host, LockoutTimelineKind Kind, int EventId, AttemptSource? Source, EventRecordRef Record)
{
    /// <summary>The failure code (<c>0x18</c>, <c>0xC000006A</c> …), null for lockouts and unlocks.</summary>
    public string? Code { get; init; }

    public LogonFailureKind FailureKind { get; init; }

    /// <summary>4625 <c>LogonType</c>.</summary>
    public int? LogonType { get; init; }

    /// <summary>Who unlocked the account (4767 <c>SubjectDomainName\SubjectUserName</c>).</summary>
    public string? Actor { get; init; }

    public override string ToString() => Kind + " " + EventId.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>One DC (or another host records came from): its lockout state, its log's coverage and what was read from it.</summary>
/// <param name="Host">The host (normalized: lower case, no trailing dot).</param>
public sealed record LockoutDcRow(string Host)
{
    /// <summary>The lockout state, null for a host that is not among the DCs given.</summary>
    public AccountLockoutState? State { get; init; }

    public bool IsPdcEmulator { get; init; }

    /// <summary>The DC wrote <c>lockoutTime</c>.</summary>
    public bool IsOriginating { get; init; }

    /// <summary>The coverage of its Security log, null when not read.</summary>
    public EventLogCoverage? Coverage { get; init; }

    /// <summary>The log reaches the window start (false when it starts later or is disabled, null when unknown).</summary>
    public bool? CoversWindow { get; init; }

    /// <summary>Why its log could not be read, null when it was.</summary>
    public EventLogAccessError? ReadError { get; init; }

    /// <summary>Records of the account read from it (lockouts, unlocks, failures).</summary>
    public int RecordsSeen { get; init; }

    public int LockoutsSeen { get; init; }

    public int FailuresSeen { get; init; }

    public override string ToString() => "lockout DC row";
}

/// <summary>The keys of <see cref="LockoutReport.Notes"/> (the UI localizes <c>EventLogs.Lockout.&lt;Key&gt;</c>); the arguments per key in order.</summary>
public static class LockoutNoteKeys
{
    /// <summary>The policy threshold is 0 – the policy never locks accounts (a PSO or another domain may). Arguments: none.</summary>
    public const string LockoutPolicyDisabled = nameof(LockoutPolicyDisabled);

    /// <summary>No DC reports the account locked at the window end (<c>lockoutTime</c> 0 or the duration passed). Arguments: none.</summary>
    public const string AccountNotLockedNow = nameof(AccountNotLockedNow);

    /// <summary>No 4740 of the account was read. A warning when a DC reports a lockout inside the window. Arguments: none.</summary>
    public const string NoLockoutEventSeen = nameof(NoLockoutEventSeen);

    /// <summary>A 4740 names no caller computer (the client did not say). Arguments: DC, time.</summary>
    public const string EmptyCaller = nameof(EmptyCaller);

    /// <summary>A source looks like a front-end server authenticating for users. Arguments: source kind, source value, <see cref="ProxyReasonKeys"/> key.</summary>
    public const string LikelyProxy = nameof(LikelyProxy);

    /// <summary>A source is a domain controller: it relayed the attempt – look at its own log. Arguments: source kind, source value.</summary>
    public const string DomainControllerSource = nameof(DomainControllerSource);

    /// <summary>Failures because the clocks differ (Kerberos <c>0x25</c>, <c>0xC0000133</c>). Arguments: source kind, source value, count.</summary>
    public const string ClockSkewSuspected = nameof(ClockSkewSuspected);

    /// <summary>A source keeps trying while the account is locked. Arguments: source kind, source value, count.</summary>
    public const string FailuresWhileLocked = nameof(FailuresWhileLocked);

    /// <summary>No failure of the account was read although a DC reports a bad password or a lockout inside the window. Arguments: none.</summary>
    public const string NoFailuresSeen = nameof(NoFailuresSeen);

    /// <summary>
    /// A DC reports a bad password inside the window, its log reaches that time, yet no failure of the account was read
    /// from it – the failure audit is probably off there. Arguments: DC.
    /// </summary>
    public const string FailuresNotSeenOnDc = nameof(FailuresNotSeenOnDc);

    /// <summary>A DC's Security log starts after the window start (or is disabled). Arguments: DC, oldest record time (empty when unknown).</summary>
    public const string CoverageGap = nameof(CoverageGap);

    /// <summary>The coverage of a DC's Security log is not known. Arguments: DC.</summary>
    public const string CoverageUnknown = nameof(CoverageUnknown);

    /// <summary>A DC's log could not be read. Arguments: DC, <see cref="EventLogAccessError"/> name.</summary>
    public const string LogReadFailed = nameof(LogReadFailed);

    /// <summary>The lockout state of a DC could not be read. Arguments: DC, error key.</summary>
    public const string DcStateUnavailable = nameof(DcStateUnavailable);

    /// <summary>
    /// Failures were read from the PDC emulator and from other DCs: a bad password is retried at the PDC emulator, so the
    /// counts of the DCs overlap. Arguments: none.
    /// </summary>
    public const string PdcRetries = nameof(PdcRetries);

    /// <summary>
    /// General hint: a wrong password equal to one of the last two passwords in the history does not increase
    /// <c>badPwdCount</c> – failures may be logged without counting toward the lockout. Arguments: none.
    /// </summary>
    public const string BadPasswordHistoryHint = nameof(BadPasswordHistoryHint);
}

/// <summary>
/// The answer to "who locks this account and from where" (design note e-event-logs.md 4.1). Directory values (names,
/// addresses, hosts) in memory only while shown; <see cref="ToString"/> carries none.
/// </summary>
public sealed record LockoutReport
{
    public required LockoutAccount Account { get; init; }

    public DateTimeOffset From { get; init; }

    public DateTimeOffset To { get; init; }

    /// <summary>The lockouts read (4740 of every DC – the PDC emulator logs them too), oldest first.</summary>
    public IReadOnlyList<LockoutEventEntry> Lockouts { get; init; } = [];

    /// <summary>The sources, ranked (see <see cref="LockoutSource"/>).</summary>
    public IReadOnlyList<LockoutSource> Sources { get; init; } = [];

    /// <summary>Every record of the account, oldest first (then host, record ID).</summary>
    public IReadOnlyList<LockoutTimelineEntry> Timeline { get; init; } = [];

    /// <summary>The DCs (PDC emulator first, then the originating DC, then by name), then other hosts records came from.</summary>
    public IReadOnlyList<LockoutDcRow> DomainControllers { get; init; } = [];

    /// <summary>Notes (<see cref="LockoutNoteKeys"/>), warnings first.</summary>
    public IReadOnlyList<AnalysisNote> Notes { get; init; } = [];

    /// <summary>Which required events were seen per DC (<see cref="AuditEvidence.LockoutRequirements"/>).</summary>
    public IReadOnlyList<AuditEvidenceEntry> AuditEvidence { get; init; } = [];

    /// <summary>A DC reports the account locked at <see cref="To"/>.</summary>
    public bool IsLockedNow { get; init; }

    /// <summary>The latest lockout: of the records, else of the DCs' <c>lockoutTime</c>.</summary>
    public DateTimeOffset? LastLockout { get; init; }

    /// <summary>Failures of the account read.</summary>
    public int FailureCount { get; init; }

    /// <summary>Of them, wrong passwords.</summary>
    public int BadPasswordCount { get; init; }

    public override string ToString() => System.FormattableString.Invariant(
        $"lockout report: {Lockouts.Count} lockouts, {Sources.Count} sources, {Timeline.Count} records, {Notes.Count} notes");
}
