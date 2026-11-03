using System.Globalization;

namespace AdMng.Core.EventLogs.Analysis;

/// <summary>What a finding of <see cref="FailedLogonAnalysis"/> is (design note e-event-logs.md 4.2).</summary>
public enum FailedLogonFindingKind
{
    /// <summary>At least <see cref="FailedLogonOptions.BruteForceThreshold"/> password failures for one account from one source.</summary>
    BruteForce,

    /// <summary>
    /// At least <see cref="FailedLogonOptions.SprayAccounts"/> accounts from one source within
    /// <see cref="FailedLogonOptions.SprayWindow"/>, each with at most <see cref="FailedLogonOptions.SprayMaxPerAccount"/> failures.
    /// </summary>
    PasswordSpray,

    /// <summary>At least <see cref="FailedLogonOptions.EnumerationThreshold"/> distinct unknown account names from one source.</summary>
    AccountEnumeration,

    /// <summary>A success for an account from a source after at least <see cref="FailedLogonOptions.SuccessAfterFailures"/> failures since its previous success.</summary>
    SuccessAfterFailures,
}

/// <summary>How serious a finding is.</summary>
public enum FailedLogonSeverity
{
    Low,
    Medium,
    High,
    Critical,
}

/// <summary>Thresholds of <see cref="FailedLogonAnalysis.Analyze"/> (design note 4.2).</summary>
public sealed record FailedLogonOptions
{
    public static FailedLogonOptions Default { get; } = new();

    public int BruteForceThreshold { get; init; } = 10;

    public int SprayAccounts { get; init; } = 10;

    /// <summary>The sliding window of a spray (inclusive: attempts exactly this far apart are in one window).</summary>
    public TimeSpan SprayWindow { get; init; } = TimeSpan.FromHours(1);

    public int SprayMaxPerAccount { get; init; } = 3;

    public int EnumerationThreshold { get; init; } = 10;

    public int SuccessAfterFailures { get; init; } = 5;

    /// <summary>Findings kept (the most severe); <see cref="FailedLogonReport.Truncated"/> says more were found.</summary>
    public int MaxFindings { get; init; } = 500;

    /// <summary>Record references kept per finding (<see cref="FailedLogonFinding.RecordsTruncated"/> says more exist).</summary>
    public int MaxRecordsPerFinding { get; init; } = 1000;

    /// <summary>Entries of <see cref="FailedLogonReport.TopAccounts"/> and <see cref="FailedLogonReport.TopSources"/>.</summary>
    public int TopCount { get; init; } = 20;

    /// <summary>
    /// Names and addresses of the domain's DCs. Failures "from" a DC address were relayed by that DC (a bad password is
    /// retried at the PDC emulator, which then sees the DC as the client): they are counted, not made findings.
    /// </summary>
    public IReadOnlyList<string> DomainControllers { get; init; } = [];

    public override string ToString() => "failed logon options";
}

/// <summary>An account and its failures.</summary>
/// <param name="Account">The name as logged (without a <c>DOMAIN\</c> prefix; the first spelling read).</param>
/// <param name="Count">Its failures.</param>
public sealed record FailedLogonAccountCount(string Account, int Count)
{
    /// <summary>
    /// Every failure said "no such account" – the name may be a password typed into the user name field: the UI shows it
    /// masked until clicked.
    /// </summary>
    public bool IsUnknownAccount { get; init; }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"account: {Count}");
}

/// <summary>A source and its failures.</summary>
/// <param name="Source">The source.</param>
/// <param name="Count">Its failures.</param>
/// <param name="AccountCount">Distinct accounts failing from it.</param>
public sealed record FailedLogonSourceCount(AttemptSource Source, int Count, int AccountCount)
{
    /// <summary>The source is a DC address – its failures were relayed (see <see cref="FailedLogonOptions.DomainControllers"/>).</summary>
    public bool IsDomainController { get; init; }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Source}: {Count}");
}

/// <summary>One finding. Directory values (accounts, sources) in memory only; <see cref="ToString"/> carries none.</summary>
public sealed record FailedLogonFinding
{
    public required FailedLogonFindingKind Kind { get; init; }

    public required FailedLogonSeverity Severity { get; init; }

    public required AttemptSource Source { get; init; }

    /// <summary>The accounts (by failures, then name).</summary>
    public IReadOnlyList<FailedLogonAccountCount> Accounts { get; init; } = [];

    /// <summary>The failures of the finding (for <see cref="FailedLogonFindingKind.SuccessAfterFailures"/>: before the success).</summary>
    public int Count { get; init; }

    /// <summary>The first failure.</summary>
    public DateTimeOffset First { get; init; }

    /// <summary>The last failure (for <see cref="FailedLogonFindingKind.SuccessAfterFailures"/>: the success).</summary>
    public DateTimeOffset Last { get; init; }

    /// <summary>The failures per event ID and code.</summary>
    public IReadOnlyList<EventCodeCount> Codes { get; init; } = [];

    /// <summary>The records for drill-down, oldest first (the success last), at most <see cref="FailedLogonOptions.MaxRecordsPerFinding"/>.</summary>
    public IReadOnlyList<EventRecordRef> Records { get; init; } = [];

    public bool RecordsTruncated { get; init; }

    /// <summary>Workstation names seen together with an address source, sorted.</summary>
    public IReadOnlyList<string> RelatedNames { get; init; } = [];

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Kind} ({Severity}): {Count} failures, {Accounts.Count} accounts");
}

/// <summary>The keys of <see cref="FailedLogonReport.Notes"/> (the UI localizes <c>EventLogs.FailedLogons.&lt;Key&gt;</c>).</summary>
public static class FailedLogonNoteKeys
{
    /// <summary>No success was read: "success after failures" could not be checked. Arguments: none.</summary>
    public const string NoSuccessRecords = nameof(NoSuccessRecords);

    /// <summary>A finding comes from an address: NAT, VPN concentrators and proxies merge many users behind one address. Arguments: none.</summary>
    public const string SharedSourceCaveat = nameof(SharedSourceCaveat);

    /// <summary>A finding comes from a workstation name: the client supplies it (4776), it may be wrong or forged. Arguments: none.</summary>
    public const string ClientSuppliedWorkstation = nameof(ClientSuppliedWorkstation);

    /// <summary>Failures from DC addresses were counted but not made findings (relayed). Arguments: count.</summary>
    public const string DomainControllerRelays = nameof(DomainControllerRelays);

    /// <summary>More findings than <see cref="FailedLogonOptions.MaxFindings"/>; the most severe are kept. Arguments: found, kept.</summary>
    public const string FindingsTruncated = nameof(FindingsTruncated);

    /// <summary>Names of unknown accounts may be passwords typed into the user name field – shown masked. Arguments: none.</summary>
    public const string UnknownAccountNames = nameof(UnknownAccountNames);
}

/// <summary>
/// The failed-logon analysis of the records read from the DCs (design note e-event-logs.md 4.2). Directory values in
/// memory only while shown; <see cref="ToString"/> carries none.
/// </summary>
public sealed record FailedLogonReport
{
    /// <summary>The findings: severity (descending), kind, failures (descending), first, source.</summary>
    public IReadOnlyList<FailedLogonFinding> Findings { get; init; } = [];

    /// <summary>Accounts with the most failures (then by name).</summary>
    public IReadOnlyList<FailedLogonAccountCount> TopAccounts { get; init; } = [];

    /// <summary>Sources with the most failures (then by kind and value).</summary>
    public IReadOnlyList<FailedLogonSourceCount> TopSources { get; init; } = [];

    /// <summary>Failures per event ID and code (by count).</summary>
    public IReadOnlyList<EventCodeCount> CodeCounts { get; init; } = [];

    /// <summary>Failures read (without duplicates).</summary>
    public int FailureCount { get; init; }

    /// <summary>Successes read (4624, 4768 success).</summary>
    public int SuccessCount { get; init; }

    /// <summary>Failures from DC addresses (counted, not findings).</summary>
    public int RelayedCount { get; init; }

    /// <summary>The first failure read, null when none.</summary>
    public DateTimeOffset? First { get; init; }

    /// <summary>The last failure read, null when none.</summary>
    public DateTimeOffset? Last { get; init; }

    /// <summary>More findings were found than <see cref="FailedLogonOptions.MaxFindings"/>.</summary>
    public bool Truncated { get; init; }

    /// <summary>Notes (<see cref="FailedLogonNoteKeys"/>), warnings first.</summary>
    public IReadOnlyList<AnalysisNote> Notes { get; init; } = [];

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"failed logon report: {FailureCount} failures, {Findings.Count} findings{(Truncated ? " (truncated)" : string.Empty)}");
}
