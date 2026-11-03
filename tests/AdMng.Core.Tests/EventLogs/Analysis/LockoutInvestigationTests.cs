using AdMng.Core.EventLogs;
using AdMng.Core.EventLogs.Analysis;
using static AdMng.Core.Tests.EventLogs.Analysis.SecurityRecords;

namespace AdMng.Core.Tests.EventLogs.Analysis;

/// <summary>"Skąd blokada konta" – query plan and analysis (design note e-event-logs.md 4.1).</summary>
public sealed class LockoutInvestigationTests
{
    private static readonly LockoutAccount Kowalski = new("JKowalski", KowalskiSid, "jan.kowalski@firma.test", "CN=Jan Kowalski,OU=Pracownicy,DC=firma,DC=test");
    private static readonly DateTimeOffset Start = T0 + TimeSpan.FromHours(8);
    private static readonly DateTimeOffset LockedAt = Start + TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(1);
    private static readonly LockoutPolicy Policy = new(5, TimeSpan.FromMinutes(30));

    [Fact]
    public void Queries_BySidForLockoutsAndKerberos_ByNameForNtlmAndLogons()
    {
        var to = T0 + TimeSpan.FromHours(24);

        var queries = LockoutInvestigation.Queries(Kowalski, T0, to);

        Assert.Equal(2, queries.Count);
        Assert.All(queries, q => Assert.Equal(("Security", (DateTimeOffset?)T0, (DateTimeOffset?)to, true, (string?)null), (q.Channel, q.From, q.To, q.NewestFirst, q.RawXPath)));
        Assert.Equal([4740, 4767, 4771], queries[0].EventIds);
        Assert.Null(queries[0].Outcome);
        Assert.Equal("TargetSid", Assert.Single(queries[0].Data).Name);
        Assert.Equal([KowalskiSid], queries[0].Data[0].Values);

        Assert.Equal([4776, 4625], queries[1].EventIds);
        Assert.Equal(EventAuditOutcome.Failure, queries[1].Outcome);
        var names = Assert.Single(queries[1].Data);
        Assert.Equal("TargetUserName", names.Name);
        Assert.Equal(["JKowalski", "jkowalski", "JKOWALSKI", "jan.kowalski@firma.test"], names.Values);
    }

    [Fact]
    public void Queries_WithoutSid_AllByName_LeavingOutValuesALiteralCannotHold()
    {
        var account = new LockoutAccount("o'brien", null, "o'b\"rien@firma.test");

        var queries = LockoutInvestigation.Queries(account, T0, T0 + TimeSpan.FromHours(1));
        var lowerSid = LockoutInvestigation.Queries(Kowalski with { Sid = " " + KowalskiSid.ToLowerInvariant() }, T0, T0);

        Assert.Equal([KowalskiSid], lowerSid[0].Data[0].Values);
        Assert.Equal(2, queries.Count);
        Assert.Equal("TargetUserName", queries[0].Data[0].Name);
        Assert.Equal(["o'brien", "O'BRIEN"], queries[0].Data[0].Values);
        Assert.Equal(["o'brien", "O'BRIEN"], queries[1].Data[0].Values);
        Assert.Throws<ArgumentException>(() => LockoutInvestigation.Queries(Kowalski, T0, T0 - TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentException>(() => LockoutInvestigation.Queries(new LockoutAccount(" "), T0, T0));
    }

    [Fact]
    public void OrderHosts_PdcThenOriginatingThenByName()
    {
        AccountLockoutState[] states = [new(Dc3), new(Dc2), new("DC01.firma.test", true)];

        Assert.Equal(["DC01.firma.test", Dc3, Dc2], LockoutInvestigation.OrderHosts(states, "dc03.firma.test."));
        Assert.Equal(["DC01.firma.test", Dc2, Dc3], LockoutInvestigation.OrderHosts(states, null));
    }

    [Fact]
    public void DefaultWindow_TwentyFourHoursBeforeTheLastLockout()
    {
        var now = T0 + TimeSpan.FromDays(2);
        AccountLockoutState[] locked = [new(Pdc, true, LockoutTime: LockedAt), new(Dc2, LockoutTime: LockedAt - TimeSpan.FromHours(3))];

        Assert.Equal((LockedAt - TimeSpan.FromHours(24), now), LockoutInvestigation.DefaultWindow(locked, now));
        Assert.Equal((now - TimeSpan.FromHours(24), now), LockoutInvestigation.DefaultWindow([new(Pdc, true)], now));
        Assert.Equal((LockedAt - TimeSpan.FromHours(24), LockedAt), LockoutInvestigation.DefaultWindow([new(Pdc, true, LockoutTime: LockedAt + TimeSpan.FromMinutes(5))], LockedAt));
    }

    [Fact]
    public void LockoutFromOneWorkstation_RetriedAtThePdc()
    {
        var records = OneWorkstationScenario();

        var report = LockoutInvestigation.Analyze(Input(records));

        Assert.Equal(
            [(Dc2, "WS-KRAKOW-07", LockedAt, 10), (Pdc, "WS-KRAKOW-07", LockedAt + TimeSpan.FromSeconds(1), 10)],
            report.Lockouts.Select(l => (l.DomainController, l.CallerComputer, l.Time, l.BadPasswordsBefore)));
        var source = Assert.Single(report.Sources);
        Assert.Equal(new AttemptSource(AttemptSourceKind.Workstation, "WS-KRAKOW-07"), source.Source);
        Assert.Equal((13, 11, 10, 2, 2), (source.FailureCount, source.BadPasswordCount, source.ContributingCount, source.LockoutCount, source.WhileLockedCount));
        Assert.Equal([Pdc, Dc2], source.DomainControllers);
        Assert.Equal(
            [(4776, "0xC000006A", LogonFailureKind.BadPassword, 11), (4776, "0xC0000234", LogonFailureKind.AccountLocked, 2)],
            source.Codes.Select(c => (c.EventId, c.Code, c.Kind, c.Count)));
        Assert.Equal((T0 + TimeSpan.FromHours(1), LockedAt + TimeSpan.FromMinutes(10)), (source.First, source.Last));
        Assert.False(source.LikelyProxy);
        Assert.False(source.IsDomainController);

        Assert.Equal((13, 11, true), (report.FailureCount, report.BadPasswordCount, report.IsLockedNow));
        Assert.Equal(LockedAt + TimeSpan.FromSeconds(1), report.LastLockout);
        Assert.Equal(15, report.Timeline.Count);
        Assert.Equal(report.Timeline.OrderBy(t => t.Time).Select(t => t.Record), report.Timeline.Select(t => t.Record));
        Assert.Equal((LockoutTimelineKind.Failure, "0xC000006A", LogonFailureKind.BadPassword), (report.Timeline[0].Kind, report.Timeline[0].Code, report.Timeline[0].FailureKind));
        Assert.Equal(2, report.Timeline.Count(t => t.Kind == LockoutTimelineKind.Lockout));

        Assert.Equal(
            [(Pdc, true, false, true, 6, 1, 5), (Dc2, false, true, true, 9, 1, 8)],
            report.DomainControllers.Select(r => (r.Host, r.IsPdcEmulator, r.IsOriginating, r.CoversWindow == true, r.RecordsSeen, r.LockoutsSeen, r.FailuresSeen)));
        Assert.Equal(
            [LockoutNoteKeys.FailuresWhileLocked, LockoutNoteKeys.PdcRetries, LockoutNoteKeys.BadPasswordHistoryHint],
            report.Notes.Select(n => n.Key));
        Assert.All(report.Notes, n => Assert.Equal(AnalysisNoteSeverity.Info, n.Severity));
        Assert.Equal(["WS-KRAKOW-07", "2"], report.Notes.Single(n => n.Key == LockoutNoteKeys.FailuresWhileLocked).Arguments.Skip(1));
    }

    [Fact]
    public void LockoutFromOneWorkstation_AuditEvidencePerDc()
    {
        var report = LockoutInvestigation.Analyze(Input(OneWorkstationScenario()));

        Assert.Equal(
            [
                (Pdc, 4740, AuditEvidenceState.Seen), (Pdc, 4771, AuditEvidenceState.NotSeenWhileCovered),
                (Pdc, 4776, AuditEvidenceState.Seen), (Pdc, 4625, AuditEvidenceState.NotSeenWhileCovered),
                (Dc2, 4740, AuditEvidenceState.Seen), (Dc2, 4771, AuditEvidenceState.NotSeenWhileCovered),
                (Dc2, 4776, AuditEvidenceState.Seen), (Dc2, 4625, AuditEvidenceState.NotSeenWhileCovered),
            ],
            report.AuditEvidence.Select(e => (e.Host, e.Requirement.EventId, e.State)));
    }

    [Fact]
    public void Kerberos_AddressStripped_NameFormsMatchedCaseInsensitively()
    {
        var r = new SecurityRecords();
        var account = new LockoutAccount("JKowalski", null, "jan.kowalski@firma.test");
        EventLogRecord[] records =
        [
            r.Kerberos(Pdc, Start, "JKOWALSKI", null, "::ffff:10.20.30.40"),
            r.Kerberos(Pdc, Start + TimeSpan.FromMinutes(1), "jkowalski", null, "::ffff:10.20.30.40"),
            r.LogonFailure(Dc2, Start + TimeSpan.FromMinutes(2), @"FIRMA\jkowalski", "10.20.30.40", "WS-WROCŁAW-12"),
            r.LogonFailure(Dc2, Start + TimeSpan.FromMinutes(3), "Jan.Kowalski@firma.test", "10.20.30.40", "WS-WROCŁAW-12"),
            r.LogonFailure(Dc2, Start + TimeSpan.FromMinutes(4), "jkowalski@FIRMA", "10.20.30.41", null),
            r.LogonFailure(Dc2, Start + TimeSpan.FromMinutes(5), "jkowalski2", "10.20.30.40", null),
            r.Ntlm(Dc2, Start + TimeSpan.FromMinutes(6), "kowalski", "WS-WROCŁAW-12"),
        ];

        var report = LockoutInvestigation.Analyze(Input(records) with { Account = account });

        Assert.Equal(5, report.FailureCount);
        Assert.Equal(
            [("10.20.30.40", 4, 4, 1), ("10.20.30.41", 1, 1, 0)],
            report.Sources.Select(s => (s.Source.Value, s.FailureCount, s.BadPasswordCount, s.OtherAccountCount)));
        Assert.All(report.Sources, s => Assert.Equal(AttemptSourceKind.IpAddress, s.Source.Kind));
        Assert.Equal(["WS-WROCŁAW-12"], report.Sources[0].RelatedNames);
        Assert.Equal([Pdc, Dc2], report.Sources[0].DomainControllers);
        Assert.Equal(
            [(4625, "0xC000006A", LogonFailureKind.BadPassword, 2), (4771, "0x18", LogonFailureKind.BadPassword, 2)],
            report.Sources[0].Codes.Select(c => (c.EventId, c.Code, c.Kind, c.Count)));
        Assert.Contains(report.Timeline, t => t.LogonType == 3);
    }

    [Fact]
    public void SidDecidesWhenBothSidesHaveOne()
    {
        var r = new SecurityRecords();
        EventLogRecord[] records =
        [
            r.Kerberos(Pdc, Start, "jkowalski", NowakSid, "::ffff:10.0.0.8"),
            r.Lockout(Pdc, LockedAt, "zmieniona.nazwa", KowalskiSid, "WS-KATOWICE-02"),
            r.LogonFailure(Pdc, Start + TimeSpan.FromMinutes(1), "jkowalski", "10.0.0.9", null),
        ];

        var report = LockoutInvestigation.Analyze(Input(records));

        Assert.Single(report.Lockouts);
        Assert.Equal(1, report.FailureCount);
        Assert.Equal(["10.0.0.9", "WS-KATOWICE-02"], report.Sources.Select(s => s.Source.Value));
        Assert.Equal((1, 0), (report.Sources[0].FailureCount, report.Sources[0].OtherAccountCount));
        Assert.DoesNotContain(report.Sources, s => s.Source.Value == "10.0.0.8");
    }

    [Fact]
    public void EmptyCaller_Noted()
    {
        var r = new SecurityRecords();
        EventLogRecord[] records =
        [
            r.Kerberos(Dc2, LockedAt - TimeSpan.FromMinutes(1), "jkowalski", KowalskiSid, "::ffff:10.0.0.8"),
            r.Lockout(Dc2, LockedAt, "jkowalski", KowalskiSid, null),
        ];

        var report = LockoutInvestigation.Analyze(Input(records));

        var lockout = Assert.Single(report.Lockouts);
        Assert.Null(lockout.CallerComputer);
        Assert.Null(report.Timeline.Single(t => t.Kind == LockoutTimelineKind.Lockout).Source);
        var note = report.Notes.Single(n => n.Key == LockoutNoteKeys.EmptyCaller);
        Assert.Equal(AnalysisNoteSeverity.Warning, note.Severity);
        Assert.Equal([Dc2, "2026-11-03T08:04:01.0000000+00:00"], note.Arguments);
        Assert.Equal(AnalysisNoteSeverity.Warning, report.Notes[0].Severity);
        Assert.Equal(new AttemptSource(AttemptSourceKind.IpAddress, "10.0.0.8"), Assert.Single(report.Sources).Source);
    }

    [Fact]
    public void Proxy_ByNamePatternAndByManyAccounts()
    {
        var r = new SecurityRecords();
        var records = new List<EventLogRecord> { r.Lockout(Dc2, LockedAt, "jkowalski", KowalskiSid, "SRV-EXCH01") };
        for (var i = 0; i < 6; i++)
        {
            records.Add(r.Ntlm(Dc2, Start + TimeSpan.FromSeconds(30 * i), "jkowalski", "SRV-EXCH01"));
        }

        for (var i = 0; i < 3; i++)
        {
            records.Add(r.Kerberos(Pdc, Start + TimeSpan.FromMinutes(i), "jkowalski", KowalskiSid, "::ffff:10.0.5.17"));
        }

        records.Add(r.Kerberos(Pdc, Start, "anowak", NowakSid, "::ffff:10.0.5.17"));
        records.Add(r.Kerberos(Pdc, Start, "mzając", "S-1-5-21-1004336348-1177238915-682003330-1107", "::ffff:10.0.5.17"));
        records.Add(r.Kerberos(Pdc, Start, "łwiśniewski", "S-1-5-21-1004336348-1177238915-682003330-1108", "::ffff:10.0.5.17"));

        var report = LockoutInvestigation.Analyze(Input(records));

        var exchange = report.Sources.Single(s => s.Source.Value == "SRV-EXCH01");
        Assert.Equal((AttemptSourceKind.Workstation, 6, 1, true, ProxyReasonKeys.NamePattern), (exchange.Source.Kind, exchange.FailureCount, exchange.LockoutCount,
            exchange.LikelyProxy, exchange.ProxyReasonKey));
        var address = report.Sources.Single(s => s.Source.Value == "10.0.5.17");
        Assert.Equal((3, true, ProxyReasonKeys.ManyAccounts), (address.OtherAccountCount, address.LikelyProxy, address.ProxyReasonKey));
        Assert.Equal(
            [["Workstation", "SRV-EXCH01", "NamePattern"], ["IpAddress", "10.0.5.17", "ManyAccounts"]],
            report.Notes.Where(n => n.Key == LockoutNoteKeys.LikelyProxy).Select(n => n.Arguments.ToArray()));

        Assert.Same(LockoutInvestigationOptions.DefaultProxyNamePatterns, LockoutInvestigationOptions.Default.ProxyNamePatterns);
        var custom = LockoutInvestigation.Analyze(Input(records) with
        {
            Options = new LockoutInvestigationOptions { ProxyNamePatterns = ["BRAMA-*"], ProxyAccountThreshold = 4 },
        });
        Assert.DoesNotContain(custom.Sources, s => s.LikelyProxy);
    }

    [Fact]
    public void CoverageGap_UnknownCoverage_ReadFailure_StateError()
    {
        var r = new SecurityRecords();
        const string dc4 = "dc04.firma.test";
        EventLogRecord[] records =
        [
            r.Kerberos(Pdc, LockedAt - TimeSpan.FromMinutes(1), "jkowalski", KowalskiSid, "::ffff:10.0.0.8"),
            r.Lockout(Pdc, LockedAt, "jkowalski", KowalskiSid, "WS-OPOLE-05"),
        ];
        var input = Input(records,
            new AccountLockoutState(Pdc, true, 5, LockedAt, LockedAt),
            new AccountLockoutState(Dc2, false, 0),
            new AccountLockoutState(Dc3, ErrorKey: "DcUnreachable"),
            new AccountLockoutState(dc4, false, 0)) with
        {
            Coverage = [Covered(Pdc, T0), Covered(Dc2, T0) with { Oldest = T0 + TimeSpan.FromHours(6) }],
            ReadFailures = new Dictionary<string, EventLogAccessError> { ["DC04.firma.test"] = EventLogAccessError.AccessDenied },
        };

        var report = LockoutInvestigation.Analyze(input);

        Assert.Equal(
            [(Pdc, (bool?)true, (EventLogAccessError?)null), (Dc2, false, null), (Dc3, null, null), (dc4, null, EventLogAccessError.AccessDenied)],
            report.DomainControllers.Select(d => (d.Host, d.CoversWindow, d.ReadError)));
        Assert.Equal([Dc2, "2026-11-03T06:00:00.0000000+00:00"], Note(report, LockoutNoteKeys.CoverageGap).Arguments);
        Assert.Equal(AnalysisNoteSeverity.Warning, Note(report, LockoutNoteKeys.CoverageGap).Severity);
        Assert.Equal([Dc3], Note(report, LockoutNoteKeys.CoverageUnknown).Arguments);
        Assert.Equal([dc4, "AccessDenied"], Note(report, LockoutNoteKeys.LogReadFailed).Arguments);
        Assert.Equal([Dc3, "DcUnreachable"], Note(report, LockoutNoteKeys.DcStateUnavailable).Arguments);
        Assert.Equal(AuditEvidenceState.Unknown, report.AuditEvidence.Single(e => e.Host == Dc2 && e.Requirement.EventId == 4740).State);
    }

    [Fact]
    public void ManyDcs_PdcFirstOriginatingMarked_LockedNowByDuration()
    {
        var r = new SecurityRecords();
        AccountLockoutState[] states = [new(Dc3, BadPasswordCount: 0), new(Dc2, false, 5, LockedAt, LockedAt), new(Pdc, true, 5, LockedAt, LockedAt)];
        EventLogRecord[] records = [r.Lockout(Dc2, LockedAt, "jkowalski", KowalskiSid, "WS-KIELCE-01")];
        var policy = Policy with { Duration = TimeSpan.FromMinutes(30) };

        var locked = LockoutInvestigation.Analyze(Input(records, states) with { Policy = policy, To = LockedAt + TimeSpan.FromMinutes(20) });
        var expired = LockoutInvestigation.Analyze(Input(records, states) with { Policy = policy, To = LockedAt + TimeSpan.FromMinutes(31) });
        var never = LockoutInvestigation.Analyze(Input(records, [new(Pdc, true), new(Dc2)]));

        Assert.Equal(
            [(Pdc, true, false), (Dc2, false, true), (Dc3, false, false)],
            locked.DomainControllers.Select(d => (d.Host, d.IsPdcEmulator, d.IsOriginating)));
        Assert.Same(states[2], locked.DomainControllers[0].State);
        Assert.True(locked.IsLockedNow);
        Assert.DoesNotContain(locked.Notes, n => n.Key == LockoutNoteKeys.AccountNotLockedNow);
        Assert.False(expired.IsLockedNow);
        Assert.Contains(expired.Notes, n => n.Key == LockoutNoteKeys.AccountNotLockedNow);
        Assert.False(never.IsLockedNow);
        Assert.Contains(never.Notes, n => n.Key == LockoutNoteKeys.AccountNotLockedNow);
    }

    [Fact]
    public void NoLockoutEventSeen_FailuresNotSeenOnADcThatReportsBadPasswords()
    {
        var r = new SecurityRecords();
        var records = Enumerable.Range(0, 5)
            .Select(i => r.Kerberos(Pdc, LockedAt - TimeSpan.FromMinutes(5 - i), "jkowalski", KowalskiSid, "::ffff:10.0.7.70"))
            .ToArray();
        var input = Input(records,
            new AccountLockoutState(Pdc, true, 5, LockedAt, LockedAt),
            new AccountLockoutState(Dc2, false, 3, LockedAt - TimeSpan.FromMinutes(2)),
            new AccountLockoutState(Dc3, false, 0)) with
        {
            Coverage = [Covered(Pdc, T0), Covered(Dc2, T0), Covered(Dc3, T0)],
        };

        var report = LockoutInvestigation.Analyze(input);

        Assert.Empty(report.Lockouts);
        Assert.Equal(LockedAt, report.LastLockout);
        Assert.Equal(AnalysisNoteSeverity.Warning, Note(report, LockoutNoteKeys.NoLockoutEventSeen).Severity);
        Assert.Equal([Dc2], Note(report, LockoutNoteKeys.FailuresNotSeenOnDc).Arguments);
        Assert.Single(report.Notes, n => n.Key == LockoutNoteKeys.FailuresNotSeenOnDc);
        Assert.DoesNotContain(report.Notes, n => n.Key == LockoutNoteKeys.NoFailuresSeen);
        Assert.Equal(5, report.Sources[0].ContributingCount);
    }

    [Fact]
    public void NoFailuresSeen_WhenOnlyTheLockoutWasRead_AndPolicyDisabledNoted()
    {
        var r = new SecurityRecords();
        var report = LockoutInvestigation.Analyze(Input([r.Lockout(Pdc, LockedAt, "jkowalski", KowalskiSid, "WS-RZESZOW-03")]) with
        {
            Policy = new LockoutPolicy(0),
        });

        Assert.Equal(AnalysisNoteSeverity.Warning, Note(report, LockoutNoteKeys.NoFailuresSeen).Severity);
        Assert.Contains(report.Notes, n => n.Key == LockoutNoteKeys.LockoutPolicyDisabled);
        Assert.DoesNotContain(report.Notes, n => n.Key == LockoutNoteKeys.NoLockoutEventSeen);
        var caller = Assert.Single(report.Sources);
        Assert.Equal((AttemptSourceKind.CallerComputer, "WS-RZESZOW-03", 1, 0), (caller.Source.Kind, caller.Source.Value, caller.LockoutCount, caller.FailureCount));
    }

    [Fact]
    public void DomainControllerSources_RelayedAddressRankedLast_CallerDcNoted()
    {
        var r = new SecurityRecords();
        var records = new List<EventLogRecord> { r.Lockout(Pdc, LockedAt, "jkowalski", KowalskiSid, "DC02") };
        for (var i = 0; i < 8; i++)
        {
            records.Add(r.Kerberos(Pdc, Start + TimeSpan.FromSeconds(10 * i), "jkowalski", KowalskiSid, "::ffff:10.0.0.12"));
        }

        for (var i = 0; i < 3; i++)
        {
            records.Add(r.Ntlm(Dc2, Start + TimeSpan.FromSeconds(10 * i), "jkowalski", "WS-LUBLIN-04"));
        }

        var input = Input(records, new AccountLockoutState(Pdc, true, 5, LockedAt, LockedAt),
            new AccountLockoutState(Dc2, false, 3, LockedAt) { Addresses = ["10.0.0.12", "fd00::12"] });

        var report = LockoutInvestigation.Analyze(input);

        Assert.Equal(
            [("WS-LUBLIN-04", false), ("DC02", true), ("10.0.0.12", true)],
            report.Sources.Select(s => (s.Source.Value, s.IsDomainController)));
        Assert.True(Assert.Single(report.Lockouts).CallerIsDomainController);
        Assert.Equal(
            [["CallerComputer", "DC02"], ["IpAddress", "10.0.0.12"]],
            report.Notes.Where(n => n.Key == LockoutNoteKeys.DomainControllerSource).Select(n => n.Arguments.ToArray()));
        Assert.DoesNotContain(report.Sources, s => s.LikelyProxy);
    }

    [Fact]
    public void ClockSkew_Noted()
    {
        var r = new SecurityRecords();
        EventLogRecord[] records =
        [
            r.Kerberos(Dc2, Start, "jkowalski", KowalskiSid, "::ffff:10.0.0.30", "0x25"),
            r.Kerberos(Dc2, Start + TimeSpan.FromMinutes(1), "jkowalski", KowalskiSid, "::ffff:10.0.0.30", "0x25"),
        ];

        var report = LockoutInvestigation.Analyze(Input(records));

        Assert.Equal(["IpAddress", "10.0.0.30", "2"], Note(report, LockoutNoteKeys.ClockSkewSuspected).Arguments);
        Assert.Equal((2, 0), (report.Sources[0].ClockSkewCount, report.Sources[0].BadPasswordCount));
    }

    [Fact]
    public void Unlock_OnTheTimelineWithItsActor()
    {
        var r = new SecurityRecords();
        EventLogRecord[] records =
        [
            r.Lockout(Pdc, LockedAt, "jkowalski", KowalskiSid, "WS-TORUN-09"),
            r.Unlock(Pdc, LockedAt + TimeSpan.FromMinutes(15), "jkowalski", KowalskiSid, "admin.kowalczyk"),
        ];

        var report = LockoutInvestigation.Analyze(Input(records));

        var unlock = report.Timeline[^1];
        Assert.Equal((LockoutTimelineKind.Unlock, 4767, @"FIRMA\admin.kowalczyk", (AttemptSource?)null), (unlock.Kind, unlock.EventId, unlock.Actor, unlock.Source));
    }

    [Fact]
    public void Duplicates_CountedOnce_AnyRecordOrder_SameReport()
    {
        var records = OneWorkstationScenario();
        var random = new Random(20261103);
        var shuffled = records.Concat(records.Take(6)).OrderBy(_ => random.Next()).ToList();

        var expected = Describe(LockoutInvestigation.Analyze(Input(records)));
        var actual = Describe(LockoutInvestigation.Analyze(Input(shuffled)));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void FiftyThousandRecords_AnalyzedQuickly()
    {
        var r = new SecurityRecords();
        var random = new Random(4740);
        var records = new List<EventLogRecord>(EventLogLimits.MaxRecordsInMemory);
        while (records.Count < EventLogLimits.MaxRecordsInMemory)
        {
            var mine = records.Count % 10 == 0;
            var account = mine ? "jkowalski" : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"użytkownik{random.Next(3_000):0000}");
            var time = T0 + TimeSpan.FromSeconds(random.Next(86_400));
            var host = (records.Count % 3) switch { 0 => Pdc, 1 => Dc2, _ => Dc3 };
            records.Add((records.Count % 4) switch
            {
                0 => r.Kerberos(host, time, account, mine ? KowalskiSid : null, "::ffff:10.3.0." + random.Next(250).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                1 => r.Ntlm(host, time, account, "WS-" + random.Next(400).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                2 => r.LogonFailure(host, time, account, "10.3.1." + random.Next(250).ToString(System.Globalization.CultureInfo.InvariantCulture), null),
                _ => r.Lockout(host, time, account, mine ? KowalskiSid : null, "WS-" + random.Next(400).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            });
        }

        var input = Input(records, new AccountLockoutState(Pdc, true, 5, LockedAt, LockedAt), new AccountLockoutState(Dc2), new AccountLockoutState(Dc3)) with
        {
            Coverage = [Covered(Pdc, T0), Covered(Dc2, T0), Covered(Dc3, T0)],
        };

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var report = LockoutInvestigation.Analyze(input);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Analyze took {stopwatch.Elapsed}.");
        Assert.Equal(EventLogLimits.MaxRecordsInMemory / 10, report.Timeline.Count);
        Assert.Equal(report.Timeline.Count, report.Lockouts.Count + report.FailureCount);
        Assert.Equal(3, report.DomainControllers.Count);
    }

    [Fact]
    public void Analyze_RejectsAReversedWindowAndAMissingAccountName()
    {
        Assert.Throws<ArgumentException>(() => LockoutInvestigation.Analyze(Input([]) with { To = T0 - TimeSpan.FromSeconds(1) }));
        Assert.Throws<ArgumentException>(() => LockoutInvestigation.Analyze(Input([]) with { Account = new LockoutAccount("") }));
    }

    [Fact]
    public void ToString_CarriesNoDirectoryValue()
    {
        var report = LockoutInvestigation.Analyze(Input(OneWorkstationScenario()));
        var texts = new List<string> { report.ToString(), report.Account.ToString(), Input([]).ToString(), Policy.ToString() };
        texts.AddRange(report.Lockouts.Select(l => l.ToString()));
        texts.AddRange(report.Sources.Select(s => s.ToString()));
        texts.AddRange(report.Timeline.Select(t => t.ToString()));
        texts.AddRange(report.DomainControllers.Select(d => d.ToString()));
        texts.AddRange(report.DomainControllers.Select(d => d.State?.ToString() ?? string.Empty));
        texts.AddRange(report.Notes.Select(n => n.ToString()));
        texts.AddRange(report.AuditEvidence.Select(e => e.ToString()));

        foreach (var text in texts)
        {
            Assert.DoesNotContain("kowalski", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("KRAKOW", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("firma", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("S-1-5-21", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Jan Kowalski's stale session on WS-KRAKOW-07: one bad password at 01:00, five at 08:00–08:04 on DC02 (each retried
    /// at the PDC emulator a second later), the lockout on DC02 and the PDC, two more tries after it.
    /// </summary>
    private static List<EventLogRecord> OneWorkstationScenario()
    {
        var r = new SecurityRecords();
        var records = new List<EventLogRecord> { r.Ntlm(Dc2, T0 + TimeSpan.FromHours(1), "jkowalski", "WS-KRAKOW-07") };
        for (var i = 0; i < 5; i++)
        {
            records.Add(r.Ntlm(Dc2, Start + TimeSpan.FromMinutes(i), "jkowalski", @"\\ws-krakow-07"));
            records.Add(r.Ntlm(Pdc, Start + TimeSpan.FromMinutes(i) + TimeSpan.FromSeconds(1), "JKowalski", "WS-KRAKOW-07"));
        }

        records.Add(r.Lockout(Dc2, LockedAt, "JKowalski", KowalskiSid, "WS-KRAKOW-07"));
        records.Add(r.Lockout(Pdc, LockedAt + TimeSpan.FromSeconds(1), "JKowalski", KowalskiSid, "WS-KRAKOW-07"));
        records.Add(r.Ntlm(Dc2, LockedAt + TimeSpan.FromMinutes(5), "jkowalski", "WS-KRAKOW-07", "0xc0000234"));
        records.Add(r.Ntlm(Dc2, LockedAt + TimeSpan.FromMinutes(10), "jkowalski", "WS-KRAKOW-07", "0xc0000234"));
        return records;
    }

    private static LockoutInvestigationInput Input(IReadOnlyList<EventLogRecord> records, params AccountLockoutState[] states) => new()
    {
        Account = Kowalski,
        DomainControllers = states.Length > 0 ? states : [new AccountLockoutState(Pdc, true, 5, LockedAt, LockedAt), new AccountLockoutState(Dc2, false, 5, LockedAt, LockedAt)],
        OriginatingDc = Dc2,
        Policy = Policy,
        From = T0,
        To = T0 + TimeSpan.FromHours(24),
        Records = records,
        Coverage = [Covered(Pdc, T0), Covered(Dc2, T0)],
    };

    private static AnalysisNote Note(LockoutReport report, string key) => report.Notes.Single(n => n.Key == key);

    private static List<string> Describe(LockoutReport report)
    {
        var lines = new List<string>();
        lines.AddRange(report.Lockouts.Select(l => $"L {l.Time:O} {l.DomainController} {l.CallerComputer} {l.Record} {l.BadPasswordsBefore}"));
        lines.AddRange(report.Sources.Select(s => $"S {s.Source.Kind} {s.Source.Value} {s.FailureCount} {s.BadPasswordCount} {s.ContributingCount} {s.LockoutCount} "
            + $"{s.WhileLockedCount} {s.First:O} {s.Last:O} {string.Join(',', s.DomainControllers)} {string.Join(',', s.Codes)}"));
        lines.AddRange(report.Timeline.Select(t => $"T {t.Time:O} {t.Host} {t.Kind} {t.Code} {t.Record.Host} {t.Record.RecordId}"));
        lines.AddRange(report.DomainControllers.Select(d => $"D {d.Host} {d.RecordsSeen} {d.FailuresSeen} {d.LockoutsSeen}"));
        lines.AddRange(report.Notes.Select(n => $"N {n.Key} {string.Join('|', n.Arguments)}"));
        lines.AddRange(report.AuditEvidence.Select(e => $"E {e.Host} {e.Requirement.EventId} {e.State} {e.RecordCount}"));
        lines.Add($"R {report.FailureCount} {report.BadPasswordCount} {report.IsLockedNow} {report.LastLockout:O}");
        return lines;
    }
}
