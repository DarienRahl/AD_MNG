namespace AdMng.Core.EventLogs.Analysis;

/// <summary>
/// "Nieudane logowania" (design note e-event-logs.md 4.2): the query plan for all DCs and the pure analysis of what was
/// read – brute force, password spraying, account enumeration, success after failures – with summaries. Linear in the
/// records apart from sorting (sliding windows with two pointers; no pairwise comparisons). Deterministic for the same
/// records in any order.
/// </summary>
/// <remarks>
/// The plan has two steps so that the successes (every TGT and network logon of the domain – far more than the
/// failures) never crowd the failures out of the bounded record list: <see cref="Queries"/> reads the failures,
/// <see cref="SuccessCandidates"/> names the accounts that could show "success after failures", and
/// <see cref="SuccessQueries"/> reads only their successes. <see cref="Analyze"/> takes all records together.
/// </remarks>
public static class FailedLogonAnalysis
{
    /// <summary><c>TargetUserName</c> values per success query (an XPath expression holds about 20 terms).</summary>
    public const int MaxAccountsPerQuery = 16;

    /// <summary>Accounts <see cref="SuccessCandidates"/> returns at most (the ones with the most failures).</summary>
    public const int MaxSuccessAccounts = 160;

    /// <summary>The events the analysis needs (with the successes when their queries ran) and their audit subcategories.</summary>
    public static IReadOnlyList<AuditRequirement> Requirements(bool successesRead) =>
        successesRead ? [.. AuditEvidence.FailedLogonRequirements, .. AuditEvidence.SuccessRequirements] : AuditEvidence.FailedLogonRequirements;

    /// <summary>
    /// Step 1, on every DC's <c>Security</c> log: 4625, 4771, 4776 and 4768 failures in the window – one query (one pass
    /// over the log); 4768 failures other than <c>0x6</c> (revoked, expired …) are counted, only <c>0x6</c> feeds the
    /// enumeration finding.
    /// </summary>
    public static IReadOnlyList<EventLogQuery> Queries(DateTimeOffset from, DateTimeOffset to)
    {
        if (to < from)
        {
            throw new ArgumentException("The window ends before it starts.", nameof(to));
        }

        return
        [
            new EventLogQuery
            {
                Channel = AnalysisCodes.SecurityChannel,
                From = from,
                To = to,
                EventIds = [AnalysisCodes.LogonFailed, AnalysisCodes.PreAuthenticationFailed, AnalysisCodes.CredentialValidation, AnalysisCodes.TicketGrantingTicket],
                Outcome = EventAuditOutcome.Failure,
            },
        ];
    }

    /// <summary>
    /// Step 2a: the account names whose successes are worth reading – accounts with at least
    /// <see cref="FailedLogonOptions.SuccessAfterFailures"/> password failures from one source (most failures first, at
    /// most <see cref="MaxSuccessAccounts"/>), each as logged and in lower case. The event log compares names
    /// case-sensitively and 4624 logs the name as stored: the Presentation may add the sAMAccountNames resolved through
    /// the window's DC.
    /// </summary>
    public static IReadOnlyList<string> SuccessCandidates(IReadOnlyList<EventLogRecord> records, FailedLogonOptions options)
    {
        ArgumentNullException.ThrowIfNull(records);
        Validate(options);
        var pairs = new Dictionary<(string Account, AttemptSource Source), int>();
        var spellings = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var attempt in Parse(records, options))
        {
            if (attempt.IsSuccess || attempt.Relayed || attempt.AccountKey is not { } account || attempt.Source.Kind == AttemptSourceKind.Unknown
                || !AnalysisCodes.IsPasswordAttempt(attempt.Kind))
            {
                continue;
            }

            pairs[(account, attempt.Source)] = pairs.GetValueOrDefault((account, attempt.Source)) + 1;
            if (!spellings.TryGetValue(account, out var names))
            {
                spellings[account] = names = new SortedSet<string>(StringComparer.Ordinal);
            }

            names.Add(attempt.AccountDisplay!);
        }

        var accounts = pairs.Where(p => p.Value >= options.SuccessAfterFailures)
            .GroupBy(p => p.Key.Account, StringComparer.Ordinal)
            .Select(g => (Account: g.Key, Count: g.Max(p => p.Value)))
            .OrderByDescending(a => a.Count)
            .ThenBy(a => a.Account, StringComparer.Ordinal)
            .Take(MaxSuccessAccounts);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (account, _) in accounts)
        {
            foreach (var name in spellings[account].Append(account))
            {
                if (seen.Add(name))
                {
                    result.Add(name);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Step 2b, on every DC: 4624 and 4768 successes of <paramref name="accounts"/> (<c>TargetUserName</c>, at most
    /// <see cref="MaxAccountsPerQuery"/> per query; values a query literal cannot hold are left out – CLAUDE.md rule 3).
    /// </summary>
    public static IReadOnlyList<EventLogQuery> SuccessQueries(IEnumerable<string> accounts, DateTimeOffset from, DateTimeOffset to)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        if (to < from)
        {
            throw new ArgumentException("The window ends before it starts.", nameof(to));
        }

        var values = accounts.Select(a => a?.Trim()).OfType<string>().Where(AnalysisValues.IsQueryable).Distinct(StringComparer.Ordinal).ToList();
        return
        [
            .. values.Chunk(MaxAccountsPerQuery).Select(chunk => new EventLogQuery
            {
                Channel = AnalysisCodes.SecurityChannel,
                From = from,
                To = to,
                EventIds = [AnalysisCodes.LogonSucceeded, AnalysisCodes.TicketGrantingTicket],
                Outcome = EventAuditOutcome.Success,
                Data = [new EventDataCondition("TargetUserName", chunk)],
            }),
        ];
    }

    /// <summary>The report of <paramref name="records"/> (failures and successes of any DC; duplicates are counted once).</summary>
    public static FailedLogonReport Analyze(IReadOnlyList<EventLogRecord> records, FailedLogonOptions options)
    {
        ArgumentNullException.ThrowIfNull(records);
        Validate(options);
        var attempts = Parse(records, options);
        var failures = attempts.Where(a => !a.IsSuccess).ToList();
        var candidates = failures.Where(a => !a.Relayed).ToList();

        var findings = new List<FailedLogonFinding>();
        var bruteForced = BruteForce(candidates, options, findings);
        var sprayed = Spray(candidates, options, findings);
        Enumeration(candidates, options, findings);
        SuccessAfterFailures(attempts, options, findings, bruteForced, sprayed);

        var ordered = findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Kind)
            .ThenByDescending(f => f.Count)
            .ThenBy(f => f.First)
            .ThenBy(f => f.Source.Kind)
            .ThenBy(f => f.Source.Value, StringComparer.Ordinal)
            .ThenBy(f => f.Accounts.Count > 0 ? f.Accounts[0].Account : string.Empty, StringComparer.Ordinal)
            .ToList();
        var truncated = ordered.Count > options.MaxFindings;
        var kept = truncated ? ordered.GetRange(0, options.MaxFindings) : ordered;

        var codes = new Dictionary<(int EventId, string? Code), (LogonFailureKind Kind, int Count)>();
        foreach (var failure in candidates)
        {
            AnalysisRecords.Count(codes, failure.Record.EventId, failure.Code, failure.Kind);
        }

        var relayed = failures.Count - candidates.Count;
        var topAccounts = TopAccounts(candidates, options.TopCount);
        return new FailedLogonReport
        {
            Findings = kept,
            TopAccounts = topAccounts,
            TopSources = TopSources(failures, options.TopCount),
            CodeCounts = AnalysisRecords.CodeCounts(codes),
            FailureCount = failures.Count,
            SuccessCount = attempts.Count - failures.Count,
            RelayedCount = relayed,
            First = failures.Count > 0 ? failures[0].Record.TimeCreated : null,
            Last = failures.Count > 0 ? failures[^1].Record.TimeCreated : null,
            Truncated = truncated,
            Notes = Notes(attempts.Count - failures.Count, relayed, ordered, kept, topAccounts),
        };
    }

    private static void Validate(FailedLogonOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.BruteForceThreshold < 1 || options.SprayAccounts < 1 || options.SprayMaxPerAccount < 1 || options.EnumerationThreshold < 1
            || options.SuccessAfterFailures < 1 || options.MaxFindings < 1 || options.MaxRecordsPerFinding < 1 || options.TopCount < 0
            || options.SprayWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Thresholds and limits must be positive, the spray window longer than zero.");
        }
    }

    /// <summary>The failures and successes among <paramref name="records"/>, without duplicates, in time order.</summary>
    private static List<Attempt> Parse(IReadOnlyList<EventLogRecord> records, FailedLogonOptions options)
    {
        var domainControllers = new DomainControllerNames(options.DomainControllers ?? []);
        var attempts = new List<Attempt>();
        foreach (var record in AnalysisRecords.Prepare(records))
        {
            if (record.EventId is not (AnalysisCodes.LogonFailed or AnalysisCodes.PreAuthenticationFailed or AnalysisCodes.CredentialValidation
                or AnalysisCodes.TicketGrantingTicket or AnalysisCodes.LogonSucceeded))
            {
                continue;
            }

            var failure = AnalysisCodes.IsFailure(record);
            if (!failure && !AnalysisCodes.IsSuccess(record))
            {
                continue;
            }

            var source = ParsedSource.Of(record);
            var (kind, code) = failure ? AnalysisCodes.Classify(record) : (LogonFailureKind.None, null);
            var name = record.Get("TargetUserName");
            attempts.Add(new Attempt(record, AnalysisValues.AccountKey(name), AnalysisValues.AccountDisplay(name), source.Source, source.Address,
                source.Workstation, kind, code, !failure,
                failure && source.Source.Kind == AttemptSourceKind.IpAddress && domainControllers.Contains(source.Source)));
        }

        return attempts;
    }

    private static HashSet<(string Account, AttemptSource Source)> BruteForce(List<Attempt> failures, FailedLogonOptions options, List<FailedLogonFinding> findings)
    {
        var groups = new Dictionary<(string Account, AttemptSource Source), List<Attempt>>();
        foreach (var failure in failures)
        {
            if (failure.AccountKey is { } account && AnalysisCodes.IsPasswordAttempt(failure.Kind))
            {
                Add(groups, (account, failure.Source), failure);
            }
        }

        var found = new HashSet<(string Account, AttemptSource Source)>();
        foreach (var (key, group) in groups)
        {
            if (group.Count < options.BruteForceThreshold)
            {
                continue;
            }

            found.Add(key);
            var severity = group.Count >= 5L * options.BruteForceThreshold ? FailedLogonSeverity.High : FailedLogonSeverity.Medium;
            findings.Add(Finding(FailedLogonFindingKind.BruteForce, severity, key.Source, group, group, options));
        }

        return found;
    }

    /// <summary>
    /// Per source, a window sliding over its password failures in time order keeps the failures per account and the
    /// number of accounts with 1..<see cref="FailedLogonOptions.SprayMaxPerAccount"/> of them; while that number reaches
    /// <see cref="FailedLogonOptions.SprayAccounts"/> the window belongs to a spray span – one finding per span.
    /// </summary>
    private static HashSet<AttemptSource> Spray(List<Attempt> failures, FailedLogonOptions options, List<FailedLogonFinding> findings)
    {
        var bySource = new Dictionary<AttemptSource, List<Attempt>>();
        foreach (var failure in failures)
        {
            if (failure.AccountKey is not null && failure.Source.Kind != AttemptSourceKind.Unknown && AnalysisCodes.IsPasswordAttempt(failure.Kind))
            {
                Add(bySource, failure.Source, failure);
            }
        }

        var found = new HashSet<AttemptSource>();
        foreach (var (source, list) in bySource)
        {
            if (list.Select(a => a.AccountKey!).Distinct(StringComparer.Ordinal).Take(options.SprayAccounts).Count() < options.SprayAccounts)
            {
                continue;
            }

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var low = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string>? spanAccounts = null;
            int left = 0, spanStart = 0, spanEnd = 0;
            for (var right = 0; right < list.Count; right++)
            {
                var account = list[right].AccountKey!;
                var count = counts.GetValueOrDefault(account) + 1;
                counts[account] = count;
                Track(low, account, count, options.SprayMaxPerAccount);
                var windowStart = list[right].Record.TimeCreated - options.SprayWindow;
                while (list[left].Record.TimeCreated < windowStart)
                {
                    var leaving = list[left].AccountKey!;
                    var remaining = counts[leaving] - 1;
                    if (remaining == 0)
                    {
                        counts.Remove(leaving);
                    }
                    else
                    {
                        counts[leaving] = remaining;
                    }

                    Track(low, leaving, remaining, options.SprayMaxPerAccount);
                    left++;
                }

                if (low.Count >= options.SprayAccounts)
                {
                    if (spanAccounts is null)
                    {
                        spanAccounts = new HashSet<string>(low, StringComparer.Ordinal);
                        spanStart = left;
                    }
                    else if (low.Contains(account))
                    {
                        spanAccounts.Add(account);
                    }

                    spanEnd = right;
                }
                else if (spanAccounts is not null)
                {
                    findings.Add(SprayFinding(source, list, spanStart, spanEnd, spanAccounts, options));
                    found.Add(source);
                    spanAccounts = null;
                }
            }

            if (spanAccounts is not null)
            {
                findings.Add(SprayFinding(source, list, spanStart, spanEnd, spanAccounts, options));
                found.Add(source);
            }
        }

        return found;
    }

    private static void Track(HashSet<string> low, string account, int count, int maxPerAccount)
    {
        if (count >= 1 && count <= maxPerAccount)
        {
            low.Add(account);
        }
        else
        {
            low.Remove(account);
        }
    }

    private static FailedLogonFinding SprayFinding(AttemptSource source, List<Attempt> list, int start, int end, HashSet<string> accounts, FailedLogonOptions options)
    {
        var included = new List<Attempt>();
        for (var i = start; i <= end; i++)
        {
            if (accounts.Contains(list[i].AccountKey!))
            {
                included.Add(list[i]);
            }
        }

        return Finding(FailedLogonFindingKind.PasswordSpray, FailedLogonSeverity.High, source, included, included, options);
    }

    private static void Enumeration(List<Attempt> failures, FailedLogonOptions options, List<FailedLogonFinding> findings)
    {
        var bySource = new Dictionary<AttemptSource, List<Attempt>>();
        foreach (var failure in failures)
        {
            if (failure.AccountKey is not null && failure.Source.Kind != AttemptSourceKind.Unknown && failure.Kind == LogonFailureKind.UnknownAccount)
            {
                Add(bySource, failure.Source, failure);
            }
        }

        foreach (var (source, list) in bySource)
        {
            if (list.Select(a => a.AccountKey!).Distinct(StringComparer.Ordinal).Count() >= options.EnumerationThreshold)
            {
                findings.Add(Finding(FailedLogonFindingKind.AccountEnumeration, FailedLogonSeverity.Medium, source, list, list, options));
            }
        }
    }

    /// <summary>
    /// Per account and source, the password failures and successes in time order: a success after at least
    /// <see cref="FailedLogonOptions.SuccessAfterFailures"/> failures since the previous success is a finding (the first
    /// one per account and source). A success matches the failures of its address, its workstation name or – for a
    /// loopback logon – its host. Critical when the source also sprayed, or brute-forced the account.
    /// </summary>
    private static void SuccessAfterFailures(List<Attempt> attempts, FailedLogonOptions options, List<FailedLogonFinding> findings,
        HashSet<(string Account, AttemptSource Source)> bruteForced, HashSet<AttemptSource> sprayed)
    {
        var sequences = new Dictionary<(string Account, AttemptSource Source), List<Attempt>>();
        foreach (var attempt in attempts)
        {
            if (attempt.Relayed || attempt.AccountKey is not { } account)
            {
                continue;
            }

            if (!attempt.IsSuccess)
            {
                if (attempt.Source.Kind != AttemptSourceKind.Unknown && AnalysisCodes.IsPasswordAttempt(attempt.Kind))
                {
                    Add(sequences, (account, attempt.Source), attempt);
                }

                continue;
            }

            foreach (var key in SuccessKeys(attempt))
            {
                if (sequences.TryGetValue((account, key), out var sequence))
                {
                    sequence.Add(attempt);
                }
            }
        }

        foreach (var ((account, source), sequence) in sequences)
        {
            var run = 0;
            var runStart = 0;
            for (var i = 0; i < sequence.Count; i++)
            {
                if (!sequence[i].IsSuccess)
                {
                    if (run++ == 0)
                    {
                        runStart = i;
                    }

                    continue;
                }

                if (run >= options.SuccessAfterFailures)
                {
                    var failed = sequence.GetRange(runStart, i - runStart);
                    var severity = sprayed.Contains(source) || bruteForced.Contains((account, source)) ? FailedLogonSeverity.Critical : FailedLogonSeverity.High;
                    var finding = Finding(FailedLogonFindingKind.SuccessAfterFailures, severity, source, failed, [.. failed, sequence[i]], options);
                    findings.Add(finding with { Last = sequence[i].Record.TimeCreated });
                    break;
                }

                run = 0;
            }
        }
    }

    /// <summary>The sources a success can match: its address, its workstation name, or its host for a local logon.</summary>
    private static IEnumerable<AttemptSource> SuccessKeys(Attempt success)
    {
        if (success.Source.Kind is AttemptSourceKind.IpAddress or AttemptSourceKind.Local)
        {
            yield return success.Source;
        }

        if (success.Workstation is { } workstation)
        {
            yield return new AttemptSource(AttemptSourceKind.Workstation, workstation);
        }
    }

    private static FailedLogonFinding Finding(FailedLogonFindingKind kind, FailedLogonSeverity severity, AttemptSource source, List<Attempt> failures,
        List<Attempt> records, FailedLogonOptions options)
    {
        var codes = new Dictionary<(int EventId, string? Code), (LogonFailureKind Kind, int Count)>();
        var accounts = new Dictionary<string, (string Display, int Count, bool Unknown)>(StringComparer.Ordinal);
        var related = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var failure in failures)
        {
            AnalysisRecords.Count(codes, failure.Record.EventId, failure.Code, failure.Kind);
            if (failure.AccountKey is { } key)
            {
                accounts[key] = accounts.TryGetValue(key, out var current)
                    ? (current.Display, current.Count + 1, current.Unknown && failure.Kind == LogonFailureKind.UnknownAccount)
                    : (failure.AccountDisplay!, 1, failure.Kind == LogonFailureKind.UnknownAccount);
            }

            if (source.Kind == AttemptSourceKind.IpAddress && failure.Workstation is { } workstation)
            {
                related.Add(workstation);
            }
        }

        return new FailedLogonFinding
        {
            Kind = kind,
            Severity = severity,
            Source = source,
            Accounts =
            [
                .. accounts.Values
                    .OrderByDescending(a => a.Count)
                    .ThenBy(a => a.Display, StringComparer.Ordinal)
                    .Select(a => new FailedLogonAccountCount(a.Display, a.Count) { IsUnknownAccount = a.Unknown }),
            ],
            Count = failures.Count,
            First = failures[0].Record.TimeCreated,
            Last = failures[^1].Record.TimeCreated,
            Codes = AnalysisRecords.CodeCounts(codes),
            Records = [.. records.Take(options.MaxRecordsPerFinding).Select(a => new EventRecordRef(a.Record.Host, a.Record.RecordId))],
            RecordsTruncated = records.Count > options.MaxRecordsPerFinding,
            RelatedNames = [.. related],
        };
    }

    private static List<FailedLogonAccountCount> TopAccounts(List<Attempt> failures, int top)
    {
        var accounts = new Dictionary<string, (string Display, int Count, bool Unknown)>(StringComparer.Ordinal);
        foreach (var failure in failures)
        {
            if (failure.AccountKey is { } key)
            {
                accounts[key] = accounts.TryGetValue(key, out var current)
                    ? (current.Display, current.Count + 1, current.Unknown && failure.Kind == LogonFailureKind.UnknownAccount)
                    : (failure.AccountDisplay!, 1, failure.Kind == LogonFailureKind.UnknownAccount);
            }
        }

        return
        [
            .. accounts.Values
                .OrderByDescending(a => a.Count)
                .ThenBy(a => a.Display, StringComparer.Ordinal)
                .Take(top)
                .Select(a => new FailedLogonAccountCount(a.Display, a.Count) { IsUnknownAccount = a.Unknown }),
        ];
    }

    private static List<FailedLogonSourceCount> TopSources(List<Attempt> failures, int top)
    {
        var sources = new Dictionary<AttemptSource, (int Count, HashSet<string> Accounts, bool Relayed)>();
        foreach (var failure in failures)
        {
            if (!sources.TryGetValue(failure.Source, out var current))
            {
                current = (0, new HashSet<string>(StringComparer.Ordinal), failure.Relayed);
            }

            if (failure.AccountKey is { } account)
            {
                current.Accounts.Add(account);
            }

            sources[failure.Source] = (current.Count + 1, current.Accounts, current.Relayed);
        }

        return
        [
            .. sources
                .OrderByDescending(s => s.Value.Count)
                .ThenBy(s => s.Key.Kind)
                .ThenBy(s => s.Key.Value, StringComparer.Ordinal)
                .Take(top)
                .Select(s => new FailedLogonSourceCount(s.Key, s.Value.Count, s.Value.Accounts.Count) { IsDomainController = s.Value.Relayed }),
        ];
    }

    private static List<AnalysisNote> Notes(int successes, int relayed, List<FailedLogonFinding> found, List<FailedLogonFinding> kept,
        List<FailedLogonAccountCount> topAccounts)
    {
        var notes = new List<AnalysisNote>();
        if (found.Count > kept.Count)
        {
            notes.Add(new AnalysisNote(FailedLogonNoteKeys.FindingsTruncated, [AnalysisValues.Number(found.Count), AnalysisValues.Number(kept.Count)],
                AnalysisNoteSeverity.Warning));
        }

        if (successes == 0)
        {
            notes.Add(new AnalysisNote(FailedLogonNoteKeys.NoSuccessRecords, []));
        }

        if (kept.Any(f => f.Source.Kind == AttemptSourceKind.IpAddress))
        {
            notes.Add(new AnalysisNote(FailedLogonNoteKeys.SharedSourceCaveat, []));
        }

        if (kept.Any(f => f.Source.Kind == AttemptSourceKind.Workstation))
        {
            notes.Add(new AnalysisNote(FailedLogonNoteKeys.ClientSuppliedWorkstation, []));
        }

        if (relayed > 0)
        {
            notes.Add(new AnalysisNote(FailedLogonNoteKeys.DomainControllerRelays, [AnalysisValues.Number(relayed)]));
        }

        if (kept.Any(f => f.Accounts.Any(a => a.IsUnknownAccount)) || topAccounts.Any(a => a.IsUnknownAccount))
        {
            notes.Add(new AnalysisNote(FailedLogonNoteKeys.UnknownAccountNames, []));
        }

        return notes;
    }

    private static void Add<TKey>(Dictionary<TKey, List<Attempt>> groups, TKey key, Attempt attempt)
        where TKey : notnull
    {
        if (!groups.TryGetValue(key, out var list))
        {
            groups[key] = list = [];
        }

        list.Add(attempt);
    }

    /// <summary>One failure or success as the analysis compares it.</summary>
    private sealed record Attempt(EventLogRecord Record, string? AccountKey, string? AccountDisplay, AttemptSource Source, string? Address,
        string? Workstation, LogonFailureKind Kind, string? Code, bool IsSuccess, bool Relayed)
    {
        public override string ToString() => Record.ToString();
    }
}
