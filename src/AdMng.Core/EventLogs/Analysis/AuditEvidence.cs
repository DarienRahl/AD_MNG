using System.Globalization;
using AdMng.Core.GroupPolicy.Security;

namespace AdMng.Core.EventLogs.Analysis;

/// <summary>
/// An event an analysis needs and the advanced audit subcategory that produces it (design note e-event-logs.md 4.3).
/// <see cref="Subcategory"/> is the entry of <see cref="SecuritySettingsCatalog.AuditSubcategories"/> (its id, GUID and
/// name are what the K4 editor and the K5 model use).
/// </summary>
/// <param name="EventId">The event ID.</param>
/// <param name="Subcategory">The audit subcategory behind it.</param>
/// <param name="Outcome">Whether the subcategory must audit successes or failures for it.</param>
public sealed record AuditRequirement(int EventId, AuditSubcategoryDefinition Subcategory, EventAuditOutcome Outcome)
{
    /// <summary>The channel the event is logged to.</summary>
    public string Channel { get; init; } = AnalysisCodes.SecurityChannel;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{EventId} {Outcome} ({Subcategory.Id})");
}

/// <summary>What the records say about one requirement on one host. Never stated as certain (design note 4.3).</summary>
public enum AuditEvidenceState
{
    /// <summary>At least one record of the event (with the outcome) was read from the host.</summary>
    Seen,

    /// <summary>
    /// None was read by a read that would have seen any account's record (<see cref="AuditRead"/>) although the host's log
    /// covers the window – the subcategory is probably not audited there.
    /// </summary>
    NotSeenWhileCovered,

    /// <summary>
    /// None was read, and either no read of the host could have seen any account's record (the reads were filtered to
    /// accounts, failed, were cut off or dropped records) or the log's coverage is missing, does not cover the window or the
    /// log is disabled.
    /// </summary>
    Unknown,
}

/// <summary>
/// A read that can prove a requirement's absence on one host (design note e-event-logs.md 4.3): the events of
/// <see cref="Requirements"/> were read from the host's log in the window <b>without an account or other data condition</b>
/// and the read completed with nothing dropped – so when it returned no record, none of any account was logged. Reads
/// filtered to accounts (the lockout plan, the success step of the failed logons), failed or cut-off reads never are; a
/// completed <see cref="AuditProbe"/> is.
/// </summary>
/// <param name="Host">The host (compared case-insensitively without a trailing dot).</param>
/// <param name="Requirements">The requirements whose events it read.</param>
public sealed record AuditRead(string Host, IReadOnlyList<AuditRequirement> Requirements)
{
    /// <summary>Never the host.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"audit read: {Requirements.Count} requirements");
}

/// <summary>
/// The unfiltered probe of one host (<see cref="AuditEvidence.Probes"/>): per requirement one query – its event ID and
/// outcome in the window, no data condition, newest first, at most one record. Once every query completed on the host, add
/// <see cref="Completed"/> to the reads and the records to the evidence (they are of any account – keep them out of an
/// analysis that counts one account's records).
/// </summary>
/// <param name="Host">The host as given to <see cref="AuditEvidence.Probes"/>.</param>
/// <param name="Requirements">The requirements probed.</param>
/// <param name="Queries">The queries, one per requirement in the same order.</param>
public sealed record AuditProbe(string Host, IReadOnlyList<AuditRequirement> Requirements, IReadOnlyList<EventLogQuery> Queries)
{
    /// <summary>What the probe proves once every query of it completed on <see cref="Host"/>.</summary>
    public AuditRead Completed => new(Host, Requirements);

    /// <summary>Never the host.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"audit probe: {Queries.Count} queries");
}

/// <summary>The evidence for one requirement on one host.</summary>
/// <param name="Host">The host (as given to <see cref="AuditEvidence.Evaluate"/>, else normalized: lower case, no trailing dot).</param>
/// <param name="Requirement">The requirement.</param>
/// <param name="State">What the records say.</param>
/// <param name="RecordCount">Records of the requirement read from the host.</param>
public sealed record AuditEvidenceEntry(string Host, AuditRequirement Requirement, AuditEvidenceState State, int RecordCount)
{
    /// <summary>Never the host.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Requirement}: {State}");
}

/// <summary>
/// Which required events were seen against the coverage of each host's log (design note e-event-logs.md 4.3): seen, not
/// seen while the log covers the window (the UI offers the K5 model of the DC and the security settings of the GPO that
/// wins on the Domain Controllers OU), or unknown. Only an unfiltered, complete read (<see cref="AuditRead"/>) proves
/// absence: a read filtered to accounts sees nothing of an account that simply did not log on there, so its records can
/// prove that an event is audited, never that it is not – <see cref="Probes"/> plans the cheap unfiltered reads that can.
/// </summary>
public static class AuditEvidence
{
    /// <summary>4740 – User Account Management success; 4771 – Kerberos Authentication Service failure; 4776 – Credential Validation failure; 4625 – Logon failure.</summary>
    public static IReadOnlyList<AuditRequirement> LockoutRequirements { get; } =
    [
        new(AnalysisCodes.AccountLockedOut, Subcategory("UserAccountManagement"), EventAuditOutcome.Success),
        new(AnalysisCodes.PreAuthenticationFailed, Subcategory("KerberosAuthenticationService"), EventAuditOutcome.Failure),
        new(AnalysisCodes.CredentialValidation, Subcategory("CredentialValidation"), EventAuditOutcome.Failure),
        new(AnalysisCodes.LogonFailed, Subcategory("Logon"), EventAuditOutcome.Failure),
    ];

    /// <summary>4625 – Logon failure; 4771, 4768 – Kerberos Authentication Service failure; 4776 – Credential Validation failure.</summary>
    public static IReadOnlyList<AuditRequirement> FailedLogonRequirements { get; } =
    [
        new(AnalysisCodes.LogonFailed, Subcategory("Logon"), EventAuditOutcome.Failure),
        new(AnalysisCodes.PreAuthenticationFailed, Subcategory("KerberosAuthenticationService"), EventAuditOutcome.Failure),
        new(AnalysisCodes.CredentialValidation, Subcategory("CredentialValidation"), EventAuditOutcome.Failure),
        new(AnalysisCodes.TicketGrantingTicket, Subcategory("KerberosAuthenticationService"), EventAuditOutcome.Failure),
    ];

    /// <summary>4624 – Logon success; 4768 – Kerberos Authentication Service success (the "success after failures" check).</summary>
    public static IReadOnlyList<AuditRequirement> SuccessRequirements { get; } =
    [
        new(AnalysisCodes.LogonSucceeded, Subcategory("Logon"), EventAuditOutcome.Success),
        new(AnalysisCodes.TicketGrantingTicket, Subcategory("KerberosAuthenticationService"), EventAuditOutcome.Success),
    ];

    /// <summary>
    /// The evidence per host (in the order of <paramref name="hosts"/>, else the hosts of <paramref name="coverage"/> and
    /// <paramref name="records"/> normalized and sorted) and requirement (in the given order). Hosts compare
    /// case-insensitively without a trailing dot; a record counts when its event ID, channel and outcome match.
    /// </summary>
    /// <param name="requirements">What the analysis needs – only requirements whose queries actually ran.</param>
    /// <param name="records">Every record read, of any account and from any read (one record of the event proves the audit).</param>
    /// <param name="coverage">The coverage of the hosts' logs.</param>
    /// <param name="from">The window start the coverage must reach.</param>
    /// <param name="to">The window end the log must still have been recording at (<see cref="EventLogCoverage.Covers"/>).</param>
    /// <param name="reads">
    /// The unfiltered, complete reads (<see cref="AuditRead"/>): a requirement not seen on a host is
    /// <see cref="AuditEvidenceState.NotSeenWhileCovered"/> only when one of them read it there and the log covers the
    /// window, else <see cref="AuditEvidenceState.Unknown"/>.
    /// </param>
    /// <param name="hosts">The hosts that were read, or null.</param>
    public static IReadOnlyList<AuditEvidenceEntry> Evaluate(IReadOnlyList<AuditRequirement> requirements, IEnumerable<EventLogRecord> records,
        IReadOnlyList<EventLogCoverage> coverage, DateTimeOffset from, DateTimeOffset to, IReadOnlyList<AuditRead> reads, IReadOnlyList<string>? hosts = null)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(coverage);
        ArgumentNullException.ThrowIfNull(reads);

        var (counts, recordHosts) = Count(requirements, records);
        var proven = new HashSet<(string Host, AuditRequirement Requirement)>();
        foreach (var read in reads)
        {
            ArgumentNullException.ThrowIfNull(read, nameof(reads));
            foreach (var requirement in read.Requirements ?? [])
            {
                proven.Add((AnalysisValues.Host(read.Host), requirement));
            }
        }

        IEnumerable<(string Display, string Key)> targets = hosts is not null
            ? hosts.Select(h => (h, AnalysisValues.Host(h))).DistinctBy(h => h.Item2)
            : coverage.Select(c => AnalysisValues.Host(c.Host)).Concat(recordHosts).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).Select(h => (h, h));

        var entries = new List<AuditEvidenceEntry>();
        foreach (var (display, key) in targets)
        {
            for (var i = 0; i < requirements.Count; i++)
            {
                var requirement = requirements[i];
                var count = counts.GetValueOrDefault((key, i));
                var state = count > 0
                    ? AuditEvidenceState.Seen
                    : proven.Contains((key, requirement)) && CoverageOf(coverage, key, requirement.Channel) is { } log && log.Covers(from, to)
                        ? AuditEvidenceState.NotSeenWhileCovered
                        : AuditEvidenceState.Unknown;
                entries.Add(new AuditEvidenceEntry(display, requirement, state, count));
            }
        }

        return entries;
    }

    /// <summary>
    /// The unfiltered probes still worth running (design note 4.3): per host of <paramref name="hosts"/> (in order, each
    /// once, spelled as given) the requirements of which no record was read from it while its log covers the window, each
    /// with its <see cref="ProbeQuery"/>. A host with nothing to probe is left out. Cheap: a probe stops at the first record.
    /// </summary>
    /// <param name="requirements">The requirements.</param>
    /// <param name="records">The records read so far (any account; a requirement seen on a host needs no probe there).</param>
    /// <param name="coverage">The coverage of the hosts' logs.</param>
    /// <param name="from">The window start.</param>
    /// <param name="to">The window end.</param>
    /// <param name="hosts">The hosts whose reads completed (a host that could not be read is not probed).</param>
    public static IReadOnlyList<AuditProbe> Probes(IReadOnlyList<AuditRequirement> requirements, IEnumerable<EventLogRecord> records,
        IReadOnlyList<EventLogCoverage> coverage, DateTimeOffset from, DateTimeOffset to, IEnumerable<string> hosts)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(coverage);
        ArgumentNullException.ThrowIfNull(hosts);
        if (to < from)
        {
            throw new ArgumentException("The window ends before it starts.", nameof(to));
        }

        var (counts, _) = Count(requirements, records);
        var probes = new List<AuditProbe>();
        foreach (var host in hosts.Where(h => !string.IsNullOrWhiteSpace(h)).DistinctBy(AnalysisValues.Host))
        {
            var key = AnalysisValues.Host(host);
            var needed = requirements
                .Where((r, i) => counts.GetValueOrDefault((key, i)) == 0 && CoverageOf(coverage, key, r.Channel) is { } log && log.Covers(from, to))
                .Distinct()
                .ToList();
            if (needed.Count > 0)
            {
                probes.Add(new AuditProbe(host.Trim(), needed, [.. needed.Select(r => ProbeQuery(r, from, to))]));
            }
        }

        return probes;
    }

    /// <summary>The probe of <paramref name="requirement"/>: its event with its outcome in the window, no data condition, newest first, one record at most.</summary>
    public static EventLogQuery ProbeQuery(AuditRequirement requirement, DateTimeOffset from, DateTimeOffset to)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        return new EventLogQuery
        {
            Channel = requirement.Channel,
            From = from,
            To = to,
            EventIds = [requirement.EventId],
            Outcome = requirement.Outcome,
            MaxRecords = 1,
        };
    }

    /// <summary>The record is the requirement's event with its outcome in its channel.</summary>
    public static bool Satisfies(EventLogRecord record, AuditRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(requirement);
        return record.EventId == requirement.EventId
            && string.Equals(record.Channel, requirement.Channel, StringComparison.OrdinalIgnoreCase)
            && (requirement.Outcome == EventAuditOutcome.Failure ? AnalysisCodes.IsFailure(record) : AnalysisCodes.IsSuccess(record));
    }

    /// <summary>The coverage of <paramref name="channel"/> on the normalized <paramref name="host"/>, null when not given.</summary>
    internal static EventLogCoverage? CoverageOf(IReadOnlyList<EventLogCoverage> coverage, string host, string channel)
    {
        foreach (var log in coverage)
        {
            if (string.Equals(AnalysisValues.Host(log.Host), host, StringComparison.Ordinal)
                && string.Equals(log.Channel, channel, StringComparison.OrdinalIgnoreCase))
            {
                return log;
            }
        }

        return null;
    }

    /// <summary>Records per (normalized host, requirement index) and the hosts records came from.</summary>
    private static (Dictionary<(string Host, int Requirement), int> Counts, HashSet<string> Hosts) Count(IReadOnlyList<AuditRequirement> requirements,
        IEnumerable<EventLogRecord> records)
    {
        var counts = new Dictionary<(string Host, int Requirement), int>();
        var hosts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            var host = AnalysisValues.Host(record.Host);
            hosts.Add(host);
            for (var i = 0; i < requirements.Count; i++)
            {
                if (Satisfies(record, requirements[i]))
                {
                    counts[(host, i)] = counts.GetValueOrDefault((host, i)) + 1;
                }
            }
        }

        return (counts, hosts);
    }

    private static AuditSubcategoryDefinition Subcategory(string id) =>
        SecuritySettingsCatalog.AuditSubcategories.Single(s => s.Id == id);
}
