using System.Diagnostics;
using System.Globalization;
using AdMng.Core.EventLogs;
using AdMng.Core.EventLogs.Analysis;
using static AdMng.Core.Tests.EventLogs.Analysis.SecurityRecords;

namespace AdMng.Core.Tests.EventLogs.Analysis;

/// <summary>"Nieudane logowania" – brute force, spraying, enumeration, success after failures (design note e-event-logs.md 4.2).</summary>
public sealed class FailedLogonAnalysisTests
{
    private static readonly DateTimeOffset Start = T0 + TimeSpan.FromHours(9);
    private static readonly FailedLogonOptions Options = FailedLogonOptions.Default;

    private static readonly string[] Pracownicy =
    [
        "anowak", "jkowalski", "mzając", "łwiśniewski", "kwójcik", "pkamińska", "tlewandowski", "ezielińska", "rszymański", "bdąbrowska",
        "gkozłowska", "hjankowski",
    ];

    [Fact]
    public void Queries_AllFailuresInOnePass()
    {
        var to = T0 + TimeSpan.FromHours(24);

        var query = Assert.Single(FailedLogonAnalysis.Queries(T0, to));

        Assert.Equal(("Security", (DateTimeOffset?)T0, (DateTimeOffset?)to, (EventAuditOutcome?)EventAuditOutcome.Failure), (query.Channel, query.From, query.To, query.Outcome));
        Assert.Equal([4625, 4771, 4776, 4768], query.EventIds);
        Assert.Empty(query.Data);
        Assert.Throws<ArgumentException>(() => FailedLogonAnalysis.Queries(to, T0));
    }

    [Fact]
    public void SuccessQueries_ChunkedDistinctAndOnlyQueryableValues()
    {
        var names = Enumerable.Range(1, 40).Select(i => string.Create(CultureInfo.InvariantCulture, $"użytkownik{i:00}"))
            .Concat(["użytkownik01", "o'b\"rien", " ", "wiersz\nnowy"]);

        var queries = FailedLogonAnalysis.SuccessQueries(names, T0, T0 + TimeSpan.FromHours(24));

        Assert.Equal([16, 16, 8], queries.Select(q => Assert.Single(q.Data).Values.Count));
        Assert.All(queries, q =>
        {
            Assert.Equal([4624, 4768], q.EventIds);
            Assert.Equal(EventAuditOutcome.Success, q.Outcome);
            Assert.Equal("TargetUserName", q.Data[0].Name);
        });
        Assert.Equal(40, queries.SelectMany(q => q.Data[0].Values).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void SuccessCandidates_AccountsWithEnoughPasswordFailuresFromOneSource()
    {
        var r = new SecurityRecords();
        var records = new List<EventLogRecord>();
        records.AddRange(Times(5).Select(t => r.Kerberos(Pdc, t, "ANowak", NowakSid, "::ffff:10.0.0.50")));
        records.AddRange(Times(4).Select(t => r.Kerberos(Pdc, t, "jkowalski", KowalskiSid, "::ffff:10.0.0.51")));
        records.AddRange(Times(6).Select(t => r.Kerberos(Pdc, t, "mzając", null, "::ffff:10.0.0.12")));
        records.AddRange(Times(7).Select(t => r.Tgt(Pdc, t, "nieznany", "::ffff:10.0.0.52", "0x6")));

        var candidates = FailedLogonAnalysis.SuccessCandidates(records, Options with { DomainControllers = ["10.0.0.12"] });

        Assert.Equal(["ANowak", "anowak"], candidates);
    }

    [Fact]
    public void BruteForce_AtTheThreshold()
    {
        var r = new SecurityRecords();
        var records = Times(10).Select(t => r.Kerberos(Pdc, t, "anowak", NowakSid, "::ffff:10.0.0.50"))
            .Concat(Times(9).Select(t => r.Kerberos(Pdc, t, "mzając", null, "::ffff:10.0.0.51")))
            .ToList();

        var report = FailedLogonAnalysis.Analyze(records, Options);

        var finding = Assert.Single(report.Findings);
        Assert.Equal((FailedLogonFindingKind.BruteForce, FailedLogonSeverity.Medium, new AttemptSource(AttemptSourceKind.IpAddress, "10.0.0.50"), 10),
            (finding.Kind, finding.Severity, finding.Source, finding.Count));
        Assert.Equal([("anowak", 10, false)], finding.Accounts.Select(a => (a.Account, a.Count, a.IsUnknownAccount)));
        Assert.Equal((Start, Start + TimeSpan.FromSeconds(9 * 20)), (finding.First, finding.Last));
        Assert.Equal([(4771, "0x18", LogonFailureKind.BadPassword, 10)], finding.Codes.Select(c => (c.EventId, c.Code, c.Kind, c.Count)));
        Assert.Equal(records.Take(10).Select(x => new EventRecordRef(x.Host, x.RecordId)), finding.Records);
        Assert.False(finding.RecordsTruncated);
        Assert.Contains(report.Notes, n => n.Key == FailedLogonNoteKeys.SharedSourceCaveat);
    }

    [Fact]
    public void BruteForce_HighFromFiveTimesTheThreshold_RecordReferencesCapped()
    {
        var r = new SecurityRecords();
        var records = Times(50).Select(t => r.Ntlm(Dc2, t, "łukasz.wiśniewski", @"\\ws-biuro-12")).ToList();

        var report = FailedLogonAnalysis.Analyze(records, Options with { MaxRecordsPerFinding = 20 });

        var finding = Assert.Single(report.Findings);
        Assert.Equal((FailedLogonSeverity.High, new AttemptSource(AttemptSourceKind.Workstation, "WS-BIURO-12"), 50, 20, true),
            (finding.Severity, finding.Source, finding.Count, finding.Records.Count, finding.RecordsTruncated));
        Assert.Contains(report.Notes, n => n.Key == FailedLogonNoteKeys.ClientSuppliedWorkstation);
    }

    [Fact]
    public void BruteForce_FailuresWhileLockedCount_UnknownSourceAllowed()
    {
        var r = new SecurityRecords();
        var records = Times(6).Select(t => r.Ntlm(Dc2, t, "anowak", null))
            .Concat(Times(4, Start + TimeSpan.FromMinutes(10)).Select(t => r.Ntlm(Dc2, t, "anowak", null, "0xc0000234")))
            .ToList();

        var finding = Assert.Single(FailedLogonAnalysis.Analyze(records, Options).Findings);

        Assert.Equal((FailedLogonFindingKind.BruteForce, AttemptSourceKind.Unknown, 10), (finding.Kind, finding.Source.Kind, finding.Count));
        Assert.Equal(
            [(LogonFailureKind.BadPassword, 6), (LogonFailureKind.AccountLocked, 4)],
            finding.Codes.Select(c => (c.Kind, c.Count)));
    }

    [Fact]
    public void PasswordSpray_TenAccountsWithinTheHour_AHeavilyTriedAccountLeftOut()
    {
        var r = new SecurityRecords();
        var records = new List<EventLogRecord>();
        for (var i = 0; i < 10; i++)
        {
            records.Add(r.Kerberos(Dc2, Start + TimeSpan.FromMinutes(5 * i), Pracownicy[i], null, "::ffff:10.0.0.66"));
            records.Add(r.Kerberos(Dc2, Start + TimeSpan.FromMinutes(5 * i) + TimeSpan.FromSeconds(30), Pracownicy[i], null, "::ffff:10.0.0.66"));
        }

        records.AddRange(Times(4, Start + TimeSpan.FromMinutes(7)).Select(t => r.Kerberos(Dc2, t, "admin.kowalczyk", null, "::ffff:10.0.0.66")));

        var report = FailedLogonAnalysis.Analyze(records, Options);

        var finding = Assert.Single(report.Findings);
        Assert.Equal((FailedLogonFindingKind.PasswordSpray, FailedLogonSeverity.High, new AttemptSource(AttemptSourceKind.IpAddress, "10.0.0.66"), 20),
            (finding.Kind, finding.Severity, finding.Source, finding.Count));
        Assert.Equal(Pracownicy.Take(10).Order(StringComparer.Ordinal), finding.Accounts.Select(a => a.Account));
        Assert.All(finding.Accounts, a => Assert.Equal(2, a.Count));
        Assert.Equal((Start, Start + TimeSpan.FromMinutes(45) + TimeSpan.FromSeconds(30)), (finding.First, finding.Last));
        Assert.Equal(20, finding.Records.Count);
    }

    [Theory]
    [InlineData(10, 400, true)]
    [InlineData(10, 407, false)]
    [InlineData(9, 60, false)]
    public void PasswordSpray_WindowAndAccountEdges(int accounts, int spacingSeconds, bool expected)
    {
        var r = new SecurityRecords();
        var records = Enumerable.Range(0, accounts)
            .Select(i => r.LogonFailure(Dc2, Start + TimeSpan.FromSeconds(spacingSeconds * i), Pracownicy[i], "10.0.0.67", "PC-NIEZNANY"))
            .ToList();

        var report = FailedLogonAnalysis.Analyze(records, Options);

        Assert.Equal(expected, report.Findings.Any(f => f.Kind == FailedLogonFindingKind.PasswordSpray));
    }

    [Fact]
    public void PasswordSpray_TwoSeparateSpansAreTwoFindings()
    {
        var r = new SecurityRecords();
        var records = new List<EventLogRecord>();
        for (var i = 0; i < 10; i++)
        {
            records.Add(r.Ntlm(Pdc, Start + TimeSpan.FromMinutes(i), Pracownicy[i], "PC-NIEZNANY"));
            records.Add(r.Ntlm(Pdc, Start + TimeSpan.FromHours(5) + TimeSpan.FromMinutes(i), Pracownicy[i], "PC-NIEZNANY"));
        }

        var sprays = FailedLogonAnalysis.Analyze(records, Options).Findings.Where(f => f.Kind == FailedLogonFindingKind.PasswordSpray).ToList();

        Assert.Equal([(Start, 10), (Start + TimeSpan.FromHours(5), 10)], sprays.Select(f => (f.First, f.Count)));
    }

    [Fact]
    public void AccountEnumeration_DistinctUnknownNames()
    {
        var r = new SecurityRecords();
        var records = new List<EventLogRecord>();
        for (var i = 0; i < 5; i++)
        {
            records.Add(r.LogonFailure(Dc2, Start + TimeSpan.FromMinutes(i), $"test{i}", "10.0.0.77", null, subStatus: "0xc0000064"));
            records.Add(r.Tgt(Dc2, Start + TimeSpan.FromMinutes(i) + TimeSpan.FromSeconds(5), $"próba{i}", "::ffff:10.0.0.77", "0x6"));
        }

        for (var i = 0; i < 27; i++)
        {
            records.Add(r.LogonFailure(Dc2, Start + TimeSpan.FromSeconds(i), $"usunięty{i % 9}", "10.0.0.78", null, subStatus: "0xc0000064"));
        }

        var report = FailedLogonAnalysis.Analyze(records, Options);

        var finding = Assert.Single(report.Findings);
        Assert.Equal((FailedLogonFindingKind.AccountEnumeration, FailedLogonSeverity.Medium, "10.0.0.77", 10, 10),
            (finding.Kind, finding.Severity, finding.Source.Value, finding.Count, finding.Accounts.Count));
        Assert.All(finding.Accounts, a => Assert.True(a.IsUnknownAccount));
        Assert.Equal(
            [(4625, "0xC0000064", LogonFailureKind.UnknownAccount, 5), (4768, "0x6", LogonFailureKind.UnknownAccount, 5)],
            finding.Codes.Select(c => (c.EventId, c.Code, c.Kind, c.Count)));
        Assert.Contains(report.Notes, n => n.Key == FailedLogonNoteKeys.UnknownAccountNames);
    }

    [Fact]
    public void SuccessAfterFailures_OnlyAfterEnoughFailuresFromTheSameSourceSinceTheLastSuccess()
    {
        var r = new SecurityRecords();
        var records = new List<EventLogRecord>();
        // Found: five failures, then a TGT from the same address.
        records.AddRange(Times(5).Select(t => r.Kerberos(Pdc, t, "pwiśniewski", null, "::ffff:10.1.1.1")));
        var success = r.Tgt(Pdc, Start + TimeSpan.FromMinutes(5), "pwiśniewski", "::ffff:10.1.1.1");
        records.Add(success);
        // Not found: four failures; another address; success first; reset by a success in between.
        records.AddRange(Times(4).Select(t => r.Kerberos(Pdc, t, "kzielińska", null, "::ffff:10.1.1.4")));
        records.Add(r.Tgt(Pdc, Start + TimeSpan.FromMinutes(5), "kzielińska", "::ffff:10.1.1.4"));
        records.AddRange(Times(5).Select(t => r.Kerberos(Pdc, t, "tmazur", null, "::ffff:10.1.1.2")));
        records.Add(r.Tgt(Pdc, Start + TimeSpan.FromMinutes(5), "tmazur", "::ffff:10.1.1.3"));
        records.Add(r.Tgt(Pdc, Start - TimeSpan.FromMinutes(1), "ewójcik", "::ffff:10.1.1.5"));
        records.AddRange(Times(5).Select(t => r.Kerberos(Pdc, t, "ewójcik", null, "::ffff:10.1.1.5")));
        records.AddRange(Times(3).Select(t => r.Kerberos(Pdc, t, "rkaczmarek", null, "::ffff:10.1.1.6")));
        records.Add(r.Tgt(Pdc, Start + TimeSpan.FromMinutes(2), "rkaczmarek", "::ffff:10.1.1.6"));
        records.AddRange(Times(3, Start + TimeSpan.FromMinutes(3)).Select(t => r.Kerberos(Pdc, t, "rkaczmarek", null, "::ffff:10.1.1.6")));
        records.Add(r.Tgt(Pdc, Start + TimeSpan.FromMinutes(5), "rkaczmarek", "::ffff:10.1.1.6"));
        // Found: NTLM failures from a workstation, then a network logon naming it.
        records.AddRange(Times(5).Select(t => r.Ntlm(Dc2, t, "bdudek", "WS-ZAMOŚĆ-02")));
        records.Add(r.LogonSuccess(Dc2, Start + TimeSpan.FromMinutes(9), "BDudek", "10.1.1.9", "WS-ZAMOŚĆ-02"));

        var report = FailedLogonAnalysis.Analyze(records, Options);

        var found = report.Findings.Where(f => f.Kind == FailedLogonFindingKind.SuccessAfterFailures).ToList();
        Assert.Equal(
            [("bdudek", "WS-ZAMOŚĆ-02", 5), ("pwiśniewski", "10.1.1.1", 5)],
            found.Select(f => (f.Accounts[0].Account, f.Source.Value, f.Count)).OrderBy(f => f.Item1, StringComparer.Ordinal));
        var kerberos = found.Single(f => f.Source.Value == "10.1.1.1");
        Assert.Equal((FailedLogonSeverity.High, success.TimeCreated, 6), (kerberos.Severity, kerberos.Last, kerberos.Records.Count));
        Assert.Equal(new EventRecordRef(success.Host, success.RecordId), kerberos.Records[^1]);
        Assert.Equal(7, report.SuccessCount);
        Assert.DoesNotContain(report.Notes, n => n.Key == FailedLogonNoteKeys.NoSuccessRecords);
    }

    [Fact]
    public void SuccessAfterFailures_CriticalAfterBruteForceOrSpray()
    {
        var r = new SecurityRecords();
        var brute = Times(12).Select(t => r.Kerberos(Pdc, t, "anowak", NowakSid, "::ffff:10.0.0.50")).ToList();
        brute.Add(r.LogonSuccess(Pdc, Start + TimeSpan.FromMinutes(10), "anowak", "10.0.0.50", null));

        var spray = new List<EventLogRecord>();
        for (var i = 0; i < 10; i++)
        {
            spray.AddRange(Times(2, Start + TimeSpan.FromMinutes(i)).Select(t => r.Kerberos(Dc2, t, Pracownicy[i], null, "::ffff:10.0.0.66")));
        }

        spray.Add(r.Tgt(Dc2, Start + TimeSpan.FromMinutes(20), Pracownicy[3], "::ffff:10.0.0.66"));

        var bruteReport = FailedLogonAnalysis.Analyze(brute, Options);
        var sprayReport = FailedLogonAnalysis.Analyze(spray, Options with { SuccessAfterFailures = 2 });

        Assert.Equal(
            [(FailedLogonFindingKind.SuccessAfterFailures, FailedLogonSeverity.Critical), (FailedLogonFindingKind.BruteForce, FailedLogonSeverity.Medium)],
            bruteReport.Findings.Select(f => (f.Kind, f.Severity)));
        Assert.Equal(
            [(FailedLogonFindingKind.SuccessAfterFailures, FailedLogonSeverity.Critical), (FailedLogonFindingKind.PasswordSpray, FailedLogonSeverity.High)],
            sprayReport.Findings.Select(f => (f.Kind, f.Severity)));
    }

    [Fact]
    public void Summaries_CodesAccountsSources()
    {
        var r = new SecurityRecords();
        var records = new List<EventLogRecord>();
        records.AddRange(Times(3).Select(t => r.LogonFailure(Dc2, t, "anowak", "10.0.0.5", "WS-ELBLAG-01")));
        records.AddRange(Times(2, Start + TimeSpan.FromMinutes(5)).Select(t => r.LogonFailure(Dc2, t, "anowak", "10.0.0.5", "WS-ELBLAG-01", "0xc0000234", "0x0")));
        records.Add(r.LogonFailure(Dc2, Start + TimeSpan.FromMinutes(7), "konto.wyłączone", "10.0.0.5", null, "0xc000006e", "0xc0000072"));
        records.AddRange(Times(4).Select(t => r.Ntlm(Pdc, t, "jkowalski", "WS-01")));
        records.AddRange(Times(2).Select(t => r.Tgt(Pdc, t, "JKowalski", "::ffff:10.0.0.6", "0x12")));

        var report = FailedLogonAnalysis.Analyze(records, Options);

        Assert.Empty(report.Findings);
        Assert.Equal((12, 0, 0, (DateTimeOffset?)Start, (DateTimeOffset?)(Start + TimeSpan.FromMinutes(7))),
            (report.FailureCount, report.SuccessCount, report.RelayedCount, report.First, report.Last));
        Assert.Equal(
            [
                (4776, "0xC000006A", LogonFailureKind.BadPassword, 4),
                (4625, "0xC000006A", LogonFailureKind.BadPassword, 3),
                (4625, "0xC0000234", LogonFailureKind.AccountLocked, 2),
                (4768, "0x12", LogonFailureKind.AccountRevoked, 2),
                (4625, "0xC0000072", LogonFailureKind.AccountDisabled, 1),
            ],
            report.CodeCounts.Select(c => (c.EventId, c.Code, c.Kind, c.Count)));
        Assert.Equal([("jkowalski", 6), ("anowak", 5), ("konto.wyłączone", 1)], report.TopAccounts.Select(a => (a.Account, a.Count)));
        Assert.Equal(
            [("10.0.0.5", 6, 2), ("WS-01", 4, 1), ("10.0.0.6", 2, 1)],
            report.TopSources.Select(s => (s.Source.Value, s.Count, s.AccountCount)));
        Assert.Equal([FailedLogonNoteKeys.NoSuccessRecords], report.Notes.Select(n => n.Key));
    }

    [Fact]
    public void RelayedFailuresFromADcAddress_CountedNotMadeFindings()
    {
        var r = new SecurityRecords();
        var records = Times(12).Select(t => r.Kerberos(Pdc, t + TimeSpan.FromSeconds(1), "anowak", NowakSid, "::ffff:10.0.0.12"))
            .Concat(Times(12).Select(t => r.Kerberos(Dc2, t, "anowak", NowakSid, "::ffff:10.0.0.50")))
            .ToList();

        var report = FailedLogonAnalysis.Analyze(records, Options with { DomainControllers = [Dc2, "10.0.0.12"] });

        var finding = Assert.Single(report.Findings);
        Assert.Equal("10.0.0.50", finding.Source.Value);
        Assert.Equal((24, 12), (report.FailureCount, report.RelayedCount));
        Assert.Equal([("10.0.0.12", true), ("10.0.0.50", false)], report.TopSources.Select(s => (s.Source.Value, s.IsDomainController)));
        Assert.Equal([("anowak", 12)], report.TopAccounts.Select(a => (a.Account, a.Count)));
        Assert.Equal(["12"], report.Notes.Single(n => n.Key == FailedLogonNoteKeys.DomainControllerRelays).Arguments);
    }

    [Fact]
    public void MaxFindings_KeepsTheMostSevere()
    {
        var r = new SecurityRecords();
        var records = new List<EventLogRecord>();
        for (var s = 0; s < 5; s++)
        {
            records.AddRange(Times(10).Select(t => r.Kerberos(Pdc, t, "anowak", NowakSid, $"::ffff:10.0.1.{s}")));
        }

        for (var i = 0; i < 10; i++)
        {
            records.Add(r.Kerberos(Dc2, Start + TimeSpan.FromMinutes(i), Pracownicy[i], null, "::ffff:10.0.0.66"));
        }

        var report = FailedLogonAnalysis.Analyze(records, Options with { MaxFindings = 3 });

        Assert.True(report.Truncated);
        Assert.Equal(
            [(FailedLogonFindingKind.PasswordSpray, "10.0.0.66"), (FailedLogonFindingKind.BruteForce, "10.0.1.0"), (FailedLogonFindingKind.BruteForce, "10.0.1.1")],
            report.Findings.Select(f => (f.Kind, f.Source.Value)));
        var note = report.Notes[0];
        Assert.Equal((FailedLogonNoteKeys.FindingsTruncated, AnalysisNoteSeverity.Warning), (note.Key, note.Severity));
        Assert.Equal(["6", "3"], note.Arguments);
    }

    [Fact]
    public void Deterministic_AnyRecordOrder_DuplicatesOnce()
    {
        var records = MixedScenario();
        var random = new Random(4625);
        var shuffled = records.Concat(records.Take(40)).OrderBy(_ => random.Next()).ToList();

        Assert.Equal(Describe(FailedLogonAnalysis.Analyze(records, Options)), Describe(FailedLogonAnalysis.Analyze(shuffled, Options)));
    }

    [Fact]
    public void FiftyThousandRecords_AnalyzedQuickly()
    {
        var r = new SecurityRecords();
        var random = new Random(50_000);
        var records = new List<EventLogRecord>(EventLogLimits.MaxRecordsInMemory);
        for (var i = 0; i < 300; i++)
        {
            records.Add(r.Kerberos(Dc2, Start + TimeSpan.FromSeconds(6 * i), string.Create(CultureInfo.InvariantCulture, $"pracownik{i:000}"), null, "::ffff:10.9.9.9"));
        }

        while (records.Count < EventLogLimits.MaxRecordsInMemory)
        {
            var account = string.Create(CultureInfo.InvariantCulture, $"użytkownik{random.Next(2_000):0000}");
            var source = string.Create(CultureInfo.InvariantCulture, $"10.2.{random.Next(2)}.{random.Next(200)}");
            var time = T0 + TimeSpan.FromSeconds(random.Next(86_400));
            records.Add((records.Count % 4) switch
            {
                0 => r.Kerberos(Pdc, time, account, null, "::ffff:" + source),
                1 => r.Ntlm(Dc2, time, account, "WS-" + source.Replace('.', '-')),
                2 => r.LogonFailure(Dc3, time, account, source, null, subStatus: random.Next(10) == 0 ? "0xc0000064" : "0xc000006a"),
                _ => r.Tgt(Pdc, time, account, "::ffff:" + source, random.Next(3) == 0 ? "0x0" : "0x6"),
            });
        }

        var stopwatch = Stopwatch.StartNew();
        var report = FailedLogonAnalysis.Analyze(records, Options);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Analyze took {stopwatch.Elapsed}.");
        Assert.Contains(report.Findings, f => f.Kind == FailedLogonFindingKind.PasswordSpray && f.Source.Value == "10.9.9.9");
        Assert.True(report.FailureCount + report.SuccessCount == EventLogLimits.MaxRecordsInMemory);
    }

    [Fact]
    public void Options_Validated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FailedLogonAnalysis.Analyze([], Options with { SprayWindow = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => FailedLogonAnalysis.Analyze([], Options with { BruteForceThreshold = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => FailedLogonAnalysis.SuccessCandidates([], Options with { MaxFindings = 0 }));
        Assert.Throws<ArgumentNullException>(() => FailedLogonAnalysis.Analyze([], null!));
        Assert.Empty(FailedLogonAnalysis.Analyze([], Options).Findings);
    }

    [Fact]
    public void Requirements_WithAndWithoutSuccesses()
    {
        Assert.Equal([4625, 4771, 4776, 4768], FailedLogonAnalysis.Requirements(false).Select(r => r.EventId));
        Assert.Equal([4625, 4771, 4776, 4768, 4624, 4768], FailedLogonAnalysis.Requirements(true).Select(r => r.EventId));
    }

    [Fact]
    public void ToString_CarriesNoValue()
    {
        var report = FailedLogonAnalysis.Analyze(MixedScenario(), Options);
        var texts = new List<string> { report.ToString(), Options.ToString() };
        texts.AddRange(report.Findings.Select(f => f.ToString()));
        texts.AddRange(report.TopAccounts.Select(a => a.ToString()));
        texts.AddRange(report.TopSources.Select(s => s.ToString()));
        texts.AddRange(report.Notes.Select(n => n.ToString()));

        Assert.NotEmpty(report.Findings);
        foreach (var text in texts)
        {
            Assert.DoesNotContain("anowak", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("10.0.", text, StringComparison.Ordinal);
            Assert.DoesNotContain("PC-NIEZNANY", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static IEnumerable<DateTimeOffset> Times(int count, DateTimeOffset? from = null) =>
        Enumerable.Range(0, count).Select(i => (from ?? Start) + TimeSpan.FromSeconds(20 * i));

    private static List<EventLogRecord> MixedScenario()
    {
        var r = new SecurityRecords();
        var records = new List<EventLogRecord>();
        records.AddRange(Times(12).Select(t => r.Kerberos(Pdc, t, "anowak", NowakSid, "::ffff:10.0.0.50")));
        records.Add(r.LogonSuccess(Pdc, Start + TimeSpan.FromMinutes(10), "anowak", "10.0.0.50", null));
        for (var i = 0; i < 10; i++)
        {
            records.Add(r.Ntlm(Dc2, Start + TimeSpan.FromMinutes(3 * i), Pracownicy[i], "PC-NIEZNANY"));
            records.Add(r.LogonFailure(Dc3, Start + TimeSpan.FromMinutes(i), $"nieznany{i}", "10.0.0.77", null, subStatus: "0xc0000064"));
        }

        return records;
    }

    private static List<string> Describe(FailedLogonReport report)
    {
        var lines = report.Findings.Select(f => $"F {f.Kind} {f.Severity} {f.Source.Kind} {f.Source.Value} {f.Count} {f.First:O} {f.Last:O} "
            + $"{string.Join(',', f.Accounts.Select(a => a.Account + ':' + a.Count))} {string.Join(',', f.Codes)} {string.Join(',', f.Records.Select(x => x.Host + '#' + x.RecordId))}").ToList();
        lines.AddRange(report.TopAccounts.Select(a => $"A {a.Account} {a.Count} {a.IsUnknownAccount}"));
        lines.AddRange(report.TopSources.Select(s => $"S {s.Source.Kind} {s.Source.Value} {s.Count} {s.AccountCount}"));
        lines.AddRange(report.CodeCounts.Select(c => $"C {c.EventId} {c.Code} {c.Kind} {c.Count}"));
        lines.AddRange(report.Notes.Select(n => $"N {n.Key} {string.Join('|', n.Arguments)}"));
        lines.Add($"R {report.FailureCount} {report.SuccessCount} {report.RelayedCount} {report.First:O} {report.Last:O} {report.Truncated}");
        return lines;
    }
}
