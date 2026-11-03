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

    /// <summary>None was read although the host's log covers the window – the subcategory is probably not audited there.</summary>
    NotSeenWhileCovered,

    /// <summary>None was read and the log's coverage is missing, does not reach the window start or the log is disabled.</summary>
    Unknown,
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
/// wins on the Domain Controllers OU), or unknown.
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
    /// <param name="records">The records read (any account: one record of the event proves the audit).</param>
    /// <param name="coverage">The coverage of the hosts' logs.</param>
    /// <param name="from">The window start the coverage must reach.</param>
    /// <param name="hosts">The hosts that were read, or null.</param>
    public static IReadOnlyList<AuditEvidenceEntry> Evaluate(IReadOnlyList<AuditRequirement> requirements, IEnumerable<EventLogRecord> records,
        IReadOnlyList<EventLogCoverage> coverage, DateTimeOffset from, IReadOnlyList<string>? hosts = null)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(coverage);

        var counts = new Dictionary<(string Host, int Requirement), int>();
        var recordHosts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            var host = AnalysisValues.Host(record.Host);
            recordHosts.Add(host);
            for (var i = 0; i < requirements.Count; i++)
            {
                if (Satisfies(record, requirements[i]))
                {
                    counts[(host, i)] = counts.GetValueOrDefault((host, i)) + 1;
                }
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
                    : CoverageOf(coverage, key, requirement.Channel) is { IsEnabled: true } log && log.Covers(from)
                        ? AuditEvidenceState.NotSeenWhileCovered
                        : AuditEvidenceState.Unknown;
                entries.Add(new AuditEvidenceEntry(display, requirement, state, count));
            }
        }

        return entries;
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

    private static AuditSubcategoryDefinition Subcategory(string id) =>
        SecuritySettingsCatalog.AuditSubcategories.Single(s => s.Id == id);
}
