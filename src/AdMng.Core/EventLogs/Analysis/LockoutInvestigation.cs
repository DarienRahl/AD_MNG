namespace AdMng.Core.EventLogs.Analysis;

/// <summary>
/// "Skąd blokada konta" (design note e-event-logs.md 4.1): the query plan for every DC and the pure analysis of what was
/// read – lockouts with their caller computer, ranked sources of the failed attempts, a timeline, the per-DC state and
/// coverage, notes and audit evidence. Nothing here reads LDAP or an event log; the Presentation orchestrates.
/// </summary>
public static class LockoutInvestigation
{
    /// <summary>The events the investigation needs and their audit subcategories.</summary>
    public static IReadOnlyList<AuditRequirement> Requirements => AuditEvidence.LockoutRequirements;

    /// <summary>
    /// The queries to run on <b>every</b> DC's <c>Security</c> log for <paramref name="account"/> in the window:
    /// <list type="number">
    /// <item>4740 (lockout), 4767 (unlock) and 4771 (Kerberos pre-authentication failed) by <c>TargetSid</c> when the SID
    /// is known – these events carry it and a SID does not depend on how the name was typed – else by
    /// <c>TargetUserName</c>;</item>
    /// <item>4776 and 4625 failures by <c>TargetUserName</c> (4776 has no SID, 4625 failures carry the NULL SID).</item>
    /// </list>
    /// The event log compares data case-sensitively, so the names are the sAMAccountName as stored, its lower- and
    /// upper-case forms (NTLM logs the name as typed) and the UPN as stored and in lower case; values a query literal
    /// cannot hold are left out (CLAUDE.md rule 3). <see cref="Analyze"/> matches case-insensitively in memory. Run the
    /// PDC emulator and the originating DC first (<see cref="OrderHosts"/>).
    /// </summary>
    public static IReadOnlyList<EventLogQuery> Queries(LockoutAccount account, DateTimeOffset from, DateTimeOffset to)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(account.SamAccountName, nameof(account));
        if (to < from)
        {
            throw new ArgumentException("The window ends before it starts.", nameof(to));
        }

        var names = QueryNames(account);
        var byName = names.Count > 0 ? new EventDataCondition("TargetUserName", names) : null;
        var queries = new List<EventLogQuery>();
        // A SID is "S-" and digits; the log writes the upper-case form and compares case-sensitively.
        if (account.Sid?.Trim().ToUpperInvariant() is { Length: > 0 } sid && AnalysisValues.IsQueryable(sid))
        {
            queries.Add(Query([AnalysisCodes.AccountLockedOut, AnalysisCodes.AccountUnlocked, AnalysisCodes.PreAuthenticationFailed],
                null, new EventDataCondition("TargetSid", [sid]), from, to));
        }
        else if (byName is not null)
        {
            queries.Add(Query([AnalysisCodes.AccountLockedOut, AnalysisCodes.AccountUnlocked, AnalysisCodes.PreAuthenticationFailed],
                null, byName, from, to));
        }

        if (byName is not null)
        {
            queries.Add(Query([AnalysisCodes.CredentialValidation, AnalysisCodes.LogonFailed], EventAuditOutcome.Failure, byName, from, to));
        }

        return queries;
    }

    /// <summary>
    /// The DCs in the order to read them: the PDC emulator first, then the originating DC of <c>lockoutTime</c>, then the
    /// others by name (each once, spelled as in <paramref name="domainControllers"/>).
    /// </summary>
    public static IReadOnlyList<string> OrderHosts(IReadOnlyList<AccountLockoutState> domainControllers, string? originatingDc)
    {
        ArgumentNullException.ThrowIfNull(domainControllers);
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? host)
        {
            if (!string.IsNullOrWhiteSpace(host) && seen.Add(AnalysisValues.Host(host)))
            {
                ordered.Add(host.Trim());
            }
        }

        foreach (var pdc in domainControllers.Where(d => d.IsPdcEmulator).OrderBy(d => AnalysisValues.Host(d.Server), StringComparer.Ordinal))
        {
            Add(pdc.Server);
        }

        if (!string.IsNullOrWhiteSpace(originatingDc))
        {
            var originating = AnalysisValues.Host(originatingDc);
            Add(domainControllers.FirstOrDefault(d => AnalysisValues.Host(d.Server) == originating)?.Server ?? originatingDc);
        }

        foreach (var dc in domainControllers.OrderBy(d => AnalysisValues.Host(d.Server), StringComparer.Ordinal))
        {
            Add(dc.Server);
        }

        return ordered;
    }

    /// <summary>
    /// The default window: <see cref="EventLogLimits.DefaultWindow"/> (24 h) before the last lockout any DC reports (not
    /// after <paramref name="now"/>), else before <paramref name="now"/>; up to <paramref name="now"/>.
    /// </summary>
    public static (DateTimeOffset From, DateTimeOffset To) DefaultWindow(IReadOnlyList<AccountLockoutState> domainControllers, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(domainControllers);
        var last = domainControllers.Select(d => d.LockoutTime).Where(t => t is not null).Max();
        var anchor = last is { } lockout && lockout < now ? lockout : now;
        return (anchor - EventLogLimits.DefaultWindow, now);
    }

    /// <summary>The report of <paramref name="input"/> (pure; deterministic for the same input in any record order).</summary>
    public static LockoutReport Analyze(LockoutInvestigationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Account, nameof(input));
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Account.SamAccountName, nameof(input));
        ArgumentNullException.ThrowIfNull(input.Policy, nameof(input));
        if (input.To < input.From)
        {
            throw new ArgumentException("The window ends before it starts.", nameof(input));
        }

        var options = input.Options ?? LockoutInvestigationOptions.Default;
        var states = input.DomainControllers ?? [];
        var matcher = new AccountMatcher(input.Account);
        var dcNames = new DomainControllerNames(states.SelectMany(s => s.Addresses.Prepend(s.Server)));
        var pdcHosts = states.Where(s => s.IsPdcEmulator).Select(s => AnalysisValues.Host(s.Server)).ToHashSet(StringComparer.Ordinal);
        var observation = input.Policy.ObservationWindow is { } window && window > TimeSpan.Zero ? window : options.DefaultObservationWindow;
        var records = AnalysisRecords.Prepare(input.Records ?? []);

        var lockouts = new List<(EventLogRecord Record, ParsedSource Source)>();
        var unlocks = new List<EventLogRecord>();
        var failures = new List<Failure>();
        var otherAccounts = new Dictionary<SourceKey, HashSet<string>>();
        foreach (var record in records)
        {
            switch (record.EventId)
            {
                case AnalysisCodes.AccountLockedOut when matcher.Matches(record):
                    lockouts.Add((record, ParsedSource.Of(record)));
                    break;
                case AnalysisCodes.AccountUnlocked when matcher.Matches(record):
                    unlocks.Add(record);
                    break;
                case AnalysisCodes.PreAuthenticationFailed or AnalysisCodes.CredentialValidation or AnalysisCodes.LogonFailed
                    or AnalysisCodes.TicketGrantingTicket when AnalysisCodes.IsFailure(record):
                {
                    var source = ParsedSource.Of(record);
                    if (matcher.Matches(record))
                    {
                        var (kind, code) = AnalysisCodes.Classify(record);
                        failures.Add(new Failure(record, source, kind, code));
                    }
                    else if (AnalysisValues.AccountKey(record.Get("TargetUserName")) is { } other)
                    {
                        var key = SourceKey.Of(source.Source);
                        if (!otherAccounts.TryGetValue(key, out var accounts))
                        {
                            otherAccounts[key] = accounts = new HashSet<string>(StringComparer.Ordinal);
                        }

                        accounts.Add(other);
                    }

                    break;
                }
            }
        }

        // The lockouts the failures are measured against: the 4740s read, else the DCs' lockoutTime.
        var lockoutTimes = lockouts.Count > 0
            ? lockouts.ConvertAll(l => l.Record.TimeCreated)
            : [.. states.Select(s => s.LockoutTime).OfType<DateTimeOffset>().Distinct().Order()];
        var badPasswordTimes = failures.Where(f => f.Kind == LogonFailureKind.BadPassword).Select(f => f.Record.TimeCreated).ToList();

        var sources = BuildSources(lockouts, failures, otherAccounts, lockoutTimes, observation, options, dcNames);
        var rows = BuildRows(input, states, records, lockouts, unlocks, failures);
        var lockoutEntries = lockouts.ConvertAll(l => new LockoutEventEntry(l.Record.TimeCreated, AnalysisValues.Host(l.Record.Host),
            l.Source.Source.Kind == AttemptSourceKind.CallerComputer ? l.Source.Source.Value : null, new EventRecordRef(l.Record.Host, l.Record.RecordId))
        {
            BadPasswordsBefore = AnalysisRecords.UpperBound(badPasswordTimes, l.Record.TimeCreated + options.LockoutTolerance)
                - AnalysisRecords.LowerBound(badPasswordTimes, l.Record.TimeCreated - observation),
            CallerIsDomainController = l.Source.Source.Kind == AttemptSourceKind.CallerComputer && dcNames.Contains(l.Source.Source),
        });

        var isLockedNow = states.Any(s => s.LockoutTime is { } locked
            && (input.Policy.Duration is not { } duration || duration <= TimeSpan.Zero || locked + duration > input.To));
        var lastLockout = lockouts.Count > 0
            ? lockouts.Max(l => l.Record.TimeCreated)
            : states.Select(s => s.LockoutTime).Where(t => t is not null).Max();

        return new LockoutReport
        {
            Account = input.Account,
            From = input.From,
            To = input.To,
            Lockouts = lockoutEntries,
            Sources = sources,
            Timeline = BuildTimeline(lockouts, unlocks, failures),
            DomainControllers = rows,
            Notes = BuildNotes(input, states, lockouts, failures, sources, rows, isLockedNow, pdcHosts),
            AuditEvidence = AuditEvidence.Evaluate(AuditEvidence.LockoutRequirements, records, input.Coverage ?? [], input.From,
                [.. rows.Select(r => r.Host)]),
            IsLockedNow = isLockedNow,
            LastLockout = lastLockout,
            FailureCount = failures.Count,
            BadPasswordCount = badPasswordTimes.Count,
        };
    }

    private static EventLogQuery Query(IReadOnlyList<int> eventIds, EventAuditOutcome? outcome, EventDataCondition condition, DateTimeOffset from, DateTimeOffset to) => new()
    {
        Channel = AnalysisCodes.SecurityChannel,
        From = from,
        To = to,
        EventIds = eventIds,
        Outcome = outcome,
        Data = [condition],
    };

    private static List<string> QueryNames(LockoutAccount account)
    {
        var names = new List<string>();
        void Add(string? value)
        {
            var text = value?.Trim();
            if (!string.IsNullOrEmpty(text) && AnalysisValues.IsQueryable(text) && !names.Contains(text, StringComparer.Ordinal))
            {
                names.Add(text);
            }
        }

        var sam = account.SamAccountName.Trim();
        Add(sam);
        Add(sam.ToLowerInvariant());
        Add(sam.ToUpperInvariant());
        Add(account.UserPrincipalName);
        Add(account.UserPrincipalName?.Trim().ToLowerInvariant());
        return names;
    }

    private static List<LockoutSource> BuildSources(List<(EventLogRecord Record, ParsedSource Source)> lockouts, List<Failure> failures,
        Dictionary<SourceKey, HashSet<string>> otherAccounts, List<DateTimeOffset> lockoutTimes, TimeSpan observation,
        LockoutInvestigationOptions options, DomainControllerNames dcNames)
    {
        var accumulators = new Dictionary<SourceKey, SourceAccumulator>();
        SourceAccumulator Get(AttemptSource source)
        {
            var key = SourceKey.Of(source);
            if (!accumulators.TryGetValue(key, out var accumulator))
            {
                accumulators[key] = accumulator = new SourceAccumulator(key);
            }

            return accumulator;
        }

        foreach (var (record, parsed) in lockouts)
        {
            if (parsed.Source.Kind == AttemptSourceKind.CallerComputer)
            {
                var accumulator = Get(parsed.Source);
                accumulator.Lockouts++;
                accumulator.Seen(record);
            }
        }

        foreach (var failure in failures)
        {
            var accumulator = Get(failure.Source.Source);
            accumulator.Failures++;
            accumulator.FromWorkstation |= failure.Source.Source.Kind == AttemptSourceKind.Workstation;
            accumulator.Seen(failure.Record);
            AnalysisRecords.Count(accumulator.Codes, failure.Record.EventId, failure.Code, failure.Kind);
            if (failure.Source.Source.Kind == AttemptSourceKind.IpAddress && failure.Source.Workstation is { } workstation)
            {
                accumulator.RelatedNames.Add(workstation);
            }

            switch (failure.Kind)
            {
                case LogonFailureKind.BadPassword:
                    accumulator.BadPasswords++;
                    var index = AnalysisRecords.LowerBound(lockoutTimes, failure.Record.TimeCreated - options.LockoutTolerance);
                    if (index < lockoutTimes.Count && lockoutTimes[index] - observation <= failure.Record.TimeCreated)
                    {
                        accumulator.Contributing++;
                    }

                    break;
                case LogonFailureKind.ClockSkew:
                    accumulator.ClockSkew++;
                    break;
                case var kind when AnalysisCodes.IsWhileLocked(kind):
                    accumulator.WhileLocked++;
                    break;
            }
        }

        var sources = new List<LockoutSource>();
        foreach (var accumulator in accumulators.Values)
        {
            var source = accumulator.ToSource();
            var isDc = dcNames.Contains(source);
            var others = otherAccounts.TryGetValue(accumulator.Key, out var set) ? set.Count : 0;
            var reason = isDc ? null : ProxyReason(source, accumulator.RelatedNames, others, options);
            sources.Add(new LockoutSource
            {
                Source = source,
                FailureCount = accumulator.Failures,
                BadPasswordCount = accumulator.BadPasswords,
                ContributingCount = accumulator.Contributing,
                LockoutCount = accumulator.Lockouts,
                WhileLockedCount = accumulator.WhileLocked,
                ClockSkewCount = accumulator.ClockSkew,
                First = accumulator.First,
                Last = accumulator.Last,
                DomainControllers = [.. accumulator.Hosts],
                Codes = AnalysisRecords.CodeCounts(accumulator.Codes),
                RelatedNames = [.. accumulator.RelatedNames],
                IsDomainController = isDc,
                OtherAccountCount = others,
                LikelyProxy = reason is not null,
                ProxyReasonKey = reason,
            });
        }

        return
        [
            .. sources
                .OrderBy(s => s.IsDomainController && s.Source.Kind == AttemptSourceKind.IpAddress ? 1 : 0)
                .ThenByDescending(s => s.ContributingCount)
                .ThenByDescending(s => s.BadPasswordCount)
                .ThenByDescending(s => s.FailureCount)
                .ThenByDescending(s => s.LockoutCount)
                .ThenByDescending(s => s.Last)
                .ThenBy(s => s.Source.Kind)
                .ThenBy(s => s.Source.Value, StringComparer.Ordinal),
        ];
    }

    private static string? ProxyReason(AttemptSource source, SortedSet<string> relatedNames, int otherAccounts, LockoutInvestigationOptions options)
    {
        IEnumerable<string> names = source.Kind switch
        {
            AttemptSourceKind.CallerComputer or AttemptSourceKind.Workstation => [source.Value],
            AttemptSourceKind.IpAddress => relatedNames,
            _ => [],
        };
        var patterns = options.ProxyNamePatterns ?? [];
        if (names.Any(name => patterns.Any(pattern => !string.IsNullOrWhiteSpace(pattern)
            && (AnalysisValues.WildcardMatch(pattern.Trim(), name) || AnalysisValues.WildcardMatch(pattern.Trim(), AnalysisValues.ShortName(name))))))
        {
            return ProxyReasonKeys.NamePattern;
        }

        return source.Kind != AttemptSourceKind.Unknown && options.ProxyAccountThreshold > 0 && otherAccounts >= options.ProxyAccountThreshold
            ? ProxyReasonKeys.ManyAccounts
            : null;
    }

    private static List<LockoutTimelineEntry> BuildTimeline(List<(EventLogRecord Record, ParsedSource Source)> lockouts, List<EventLogRecord> unlocks,
        List<Failure> failures)
    {
        var timeline = new List<LockoutTimelineEntry>(lockouts.Count + unlocks.Count + failures.Count);
        foreach (var (record, parsed) in lockouts)
        {
            timeline.Add(new LockoutTimelineEntry(record.TimeCreated, AnalysisValues.Host(record.Host), LockoutTimelineKind.Lockout, record.EventId,
                parsed.Source.Kind == AttemptSourceKind.CallerComputer ? parsed.Source : null, new EventRecordRef(record.Host, record.RecordId)));
        }

        foreach (var record in unlocks)
        {
            var subject = record.Get("SubjectUserName");
            var domain = record.Get("SubjectDomainName");
            timeline.Add(new LockoutTimelineEntry(record.TimeCreated, AnalysisValues.Host(record.Host), LockoutTimelineKind.Unlock, record.EventId, null,
                new EventRecordRef(record.Host, record.RecordId))
            {
                Actor = subject is null ? null : domain is null ? subject : domain + "\\" + subject,
            });
        }

        foreach (var failure in failures)
        {
            var record = failure.Record;
            timeline.Add(new LockoutTimelineEntry(record.TimeCreated, AnalysisValues.Host(record.Host), LockoutTimelineKind.Failure, record.EventId,
                failure.Source.Source, new EventRecordRef(record.Host, record.RecordId))
            {
                Code = failure.Code,
                FailureKind = failure.Kind,
                LogonType = int.TryParse(record.Get("LogonType"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture,
                    out var logonType) ? logonType : null,
            });
        }

        return
        [
            .. timeline
                .OrderBy(t => t.Time)
                .ThenBy(t => t.Host, StringComparer.Ordinal)
                .ThenBy(t => t.Record.RecordId),
        ];
    }

    private static List<LockoutDcRow> BuildRows(LockoutInvestigationInput input, IReadOnlyList<AccountLockoutState> states, List<EventLogRecord> records,
        List<(EventLogRecord Record, ParsedSource Source)> lockouts, List<EventLogRecord> unlocks, List<Failure> failures)
    {
        var hosts = OrderHosts(states, input.OriginatingDc).Select(AnalysisValues.Host).ToList();
        var known = hosts.ToHashSet(StringComparer.Ordinal);
        var readFailures = (input.ReadFailures ?? new Dictionary<string, EventLogAccessError>())
            .GroupBy(f => AnalysisValues.Host(f.Key), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
        var coverage = input.Coverage ?? [];
        hosts.AddRange(coverage.Where(c => string.Equals(c.Channel, AnalysisCodes.SecurityChannel, StringComparison.OrdinalIgnoreCase))
            .Select(c => AnalysisValues.Host(c.Host))
            .Concat(records.Select(r => AnalysisValues.Host(r.Host)))
            .Concat(readFailures.Keys)
            .Where(h => !known.Contains(h))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal));

        var lockoutCounts = lockouts.CountBy(l => AnalysisValues.Host(l.Record.Host)).ToDictionary(StringComparer.Ordinal);
        var unlockCounts = unlocks.CountBy(r => AnalysisValues.Host(r.Host)).ToDictionary(StringComparer.Ordinal);
        var failureCounts = failures.CountBy(f => AnalysisValues.Host(f.Record.Host)).ToDictionary(StringComparer.Ordinal);
        var originating = string.IsNullOrWhiteSpace(input.OriginatingDc) ? null : AnalysisValues.Host(input.OriginatingDc);

        var rows = new List<LockoutDcRow>(hosts.Count);
        foreach (var host in hosts)
        {
            var state = states.FirstOrDefault(s => string.Equals(AnalysisValues.Host(s.Server), host, StringComparison.Ordinal));
            var log = AuditEvidence.CoverageOf(coverage, host, AnalysisCodes.SecurityChannel);
            var lockoutCount = lockoutCounts.GetValueOrDefault(host);
            var failureCount = failureCounts.GetValueOrDefault(host);
            rows.Add(new LockoutDcRow(host)
            {
                State = state,
                IsPdcEmulator = state?.IsPdcEmulator == true,
                IsOriginating = host == originating,
                Coverage = log,
                CoversWindow = log is null ? null : log.IsEnabled && log.Covers(input.From),
                ReadError = readFailures.TryGetValue(host, out var error) ? error : null,
                RecordsSeen = lockoutCount + unlockCounts.GetValueOrDefault(host) + failureCount,
                LockoutsSeen = lockoutCount,
                FailuresSeen = failureCount,
            });
        }

        return rows;
    }

    private static List<AnalysisNote> BuildNotes(LockoutInvestigationInput input, IReadOnlyList<AccountLockoutState> states,
        List<(EventLogRecord Record, ParsedSource Source)> lockouts, List<Failure> failures, List<LockoutSource> sources, List<LockoutDcRow> rows,
        bool isLockedNow, HashSet<string> pdcHosts)
    {
        var notes = new List<AnalysisNote>();
        void Add(string key, AnalysisNoteSeverity severity, params string[] arguments) => notes.Add(new AnalysisNote(key, arguments, severity));
        bool InWindow(DateTimeOffset? time) => time is { } t && t >= input.From && t <= input.To;

        if (input.Policy.Threshold <= 0)
        {
            Add(LockoutNoteKeys.LockoutPolicyDisabled, AnalysisNoteSeverity.Info);
        }

        if (!isLockedNow)
        {
            Add(LockoutNoteKeys.AccountNotLockedNow, AnalysisNoteSeverity.Info);
        }

        var lockoutInWindow = states.Any(s => InWindow(s.LockoutTime));
        if (lockouts.Count == 0)
        {
            Add(LockoutNoteKeys.NoLockoutEventSeen, lockoutInWindow ? AnalysisNoteSeverity.Warning : AnalysisNoteSeverity.Info);
        }

        foreach (var (record, parsed) in lockouts.Where(l => l.Source.Source.Kind != AttemptSourceKind.CallerComputer))
        {
            Add(LockoutNoteKeys.EmptyCaller, AnalysisNoteSeverity.Warning, AnalysisValues.Host(record.Host), AnalysisValues.Time(record.TimeCreated));
        }

        var failuresExpected = lockouts.Count > 0 || lockoutInWindow || states.Any(s => s.BadPasswordCount > 0 && InWindow(s.BadPasswordTime));
        if (failures.Count == 0 && failuresExpected)
        {
            Add(LockoutNoteKeys.NoFailuresSeen, AnalysisNoteSeverity.Warning);
        }

        foreach (var source in sources)
        {
            var kind = source.Source.Kind.ToString();
            if (source.IsDomainController)
            {
                Add(LockoutNoteKeys.DomainControllerSource, AnalysisNoteSeverity.Info, kind, source.Source.Value);
            }

            if (source.LikelyProxy)
            {
                Add(LockoutNoteKeys.LikelyProxy, AnalysisNoteSeverity.Warning, kind, source.Source.Value, source.ProxyReasonKey!);
            }

            if (source.ClockSkewCount > 0)
            {
                Add(LockoutNoteKeys.ClockSkewSuspected, AnalysisNoteSeverity.Warning, kind, source.Source.Value, AnalysisValues.Number(source.ClockSkewCount));
            }

            if (source.WhileLockedCount > 0)
            {
                Add(LockoutNoteKeys.FailuresWhileLocked, AnalysisNoteSeverity.Info, kind, source.Source.Value, AnalysisValues.Number(source.WhileLockedCount));
            }
        }

        if (pdcHosts.Count > 0 && failures.Any(f => pdcHosts.Contains(AnalysisValues.Host(f.Record.Host)))
            && failures.Any(f => !pdcHosts.Contains(AnalysisValues.Host(f.Record.Host))))
        {
            Add(LockoutNoteKeys.PdcRetries, AnalysisNoteSeverity.Info);
        }

        foreach (var row in rows)
        {
            if (row.ReadError is { } error)
            {
                Add(LockoutNoteKeys.LogReadFailed, AnalysisNoteSeverity.Warning, row.Host, error.ToString());
            }
            else if (row.Coverage is null)
            {
                Add(LockoutNoteKeys.CoverageUnknown, AnalysisNoteSeverity.Info, row.Host);
            }
            else if (row.CoversWindow == false)
            {
                Add(LockoutNoteKeys.CoverageGap, AnalysisNoteSeverity.Warning, row.Host,
                    row.Coverage.Oldest is { } oldest ? AnalysisValues.Time(oldest) : string.Empty);
            }

            if (row.State?.ErrorKey is { Length: > 0 } errorKey)
            {
                Add(LockoutNoteKeys.DcStateUnavailable, AnalysisNoteSeverity.Info, row.Host, errorKey);
            }

            if (row is { ReadError: null, FailuresSeen: 0, State: { BadPasswordCount: > 0, BadPasswordTime: { } badPasswordTime }, Coverage: { IsEnabled: true, Oldest: { } logStart } }
                && InWindow(badPasswordTime) && logStart <= badPasswordTime)
            {
                Add(LockoutNoteKeys.FailuresNotSeenOnDc, AnalysisNoteSeverity.Warning, row.Host);
            }
        }

        Add(LockoutNoteKeys.BadPasswordHistoryHint, AnalysisNoteSeverity.Info);
        return [.. notes.OrderByDescending(n => n.Severity)];
    }

    /// <summary>One failed attempt of the account.</summary>
    private sealed record Failure(EventLogRecord Record, ParsedSource Source, LogonFailureKind Kind, string? Code)
    {
        public override string ToString() => Record.ToString();
    }

    /// <summary>A source as grouped: computer names (callers and workstations together), addresses, the logging host, unknown.</summary>
    private readonly record struct SourceKey(int Group, string Value)
    {
        public static SourceKey Of(AttemptSource source) => source.Kind switch
        {
            AttemptSourceKind.CallerComputer or AttemptSourceKind.Workstation => new SourceKey(0, source.Value),
            AttemptSourceKind.IpAddress => new SourceKey(1, source.Value),
            AttemptSourceKind.Local => new SourceKey(2, source.Value),
            _ => new SourceKey(3, string.Empty),
        };

        public override string ToString() => "source key";
    }

    private sealed class SourceAccumulator(SourceKey key)
    {
        public SourceKey Key { get; } = key;

        public int Failures { get; set; }

        public int BadPasswords { get; set; }

        public int Contributing { get; set; }

        public int Lockouts { get; set; }

        public int WhileLocked { get; set; }

        public int ClockSkew { get; set; }

        public bool FromWorkstation { get; set; }

        public DateTimeOffset First { get; private set; } = DateTimeOffset.MaxValue;

        public DateTimeOffset Last { get; private set; } = DateTimeOffset.MinValue;

        public SortedSet<string> Hosts { get; } = new(StringComparer.Ordinal);

        public SortedSet<string> RelatedNames { get; } = new(StringComparer.Ordinal);

        public Dictionary<(int EventId, string? Code), (LogonFailureKind Kind, int Count)> Codes { get; } = [];

        public void Seen(EventLogRecord record)
        {
            if (record.TimeCreated < First)
            {
                First = record.TimeCreated;
            }

            if (record.TimeCreated > Last)
            {
                Last = record.TimeCreated;
            }

            Hosts.Add(AnalysisValues.Host(record.Host));
        }

        public AttemptSource ToSource() => Key.Group switch
        {
            0 => new AttemptSource(FromWorkstation ? AttemptSourceKind.Workstation : AttemptSourceKind.CallerComputer, Key.Value),
            1 => new AttemptSource(AttemptSourceKind.IpAddress, Key.Value),
            2 => new AttemptSource(AttemptSourceKind.Local, Key.Value),
            _ => new AttemptSource(AttemptSourceKind.Unknown, string.Empty),
        };

        public override string ToString() => "source accumulator";
    }

    /// <summary>
    /// The account in records: by SID when both sides have one (4625's NULL SID does not count), else by
    /// <c>TargetUserName</c> case-insensitively – the sAMAccountName, also after a <c>DOMAIN\</c> prefix, the UPN, or
    /// <c>sam@realm</c>.
    /// </summary>
    private sealed class AccountMatcher(LockoutAccount account)
    {
        private readonly string _sam = account.SamAccountName.Trim();
        private readonly string? _sid = string.IsNullOrWhiteSpace(account.Sid) ? null : account.Sid.Trim();
        private readonly string? _upn = string.IsNullOrWhiteSpace(account.UserPrincipalName) ? null : account.UserPrincipalName.Trim();

        public bool Matches(EventLogRecord record)
        {
            var recordSid = record.Get("TargetSid") ?? record.Get("TargetUserSid");
            if (_sid is not null && recordSid is not null && !string.Equals(recordSid, AnalysisCodes.NullSid, StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(recordSid.Trim(), _sid, StringComparison.OrdinalIgnoreCase);
            }

            if (AnalysisValues.AccountDisplay(record.Get("TargetUserName")) is not { } name)
            {
                return false;
            }

            if (string.Equals(name, _sam, StringComparison.OrdinalIgnoreCase) || string.Equals(name, _upn, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var at = name.IndexOf('@', StringComparison.Ordinal);
            return at > 0 && string.Equals(name[..at], _sam, StringComparison.OrdinalIgnoreCase);
        }

        public override string ToString() => "account matcher";
    }
}
