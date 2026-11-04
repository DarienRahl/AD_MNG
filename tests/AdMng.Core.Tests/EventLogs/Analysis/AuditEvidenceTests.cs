using AdMng.Core.EventLogs;
using AdMng.Core.EventLogs.Analysis;
using AdMng.Core.GroupPolicy.Security;
using static AdMng.Core.Tests.EventLogs.Analysis.SecurityRecords;

namespace AdMng.Core.Tests.EventLogs.Analysis;

/// <summary>Which required events were seen per host against the coverage of its log (design note e-event-logs.md 4.3).</summary>
public sealed class AuditEvidenceTests
{
    [Fact]
    public void Requirements_AreTheCatalogSubcategories()
    {
        var all = AuditEvidence.LockoutRequirements.Concat(AuditEvidence.FailedLogonRequirements).Concat(AuditEvidence.SuccessRequirements);
        foreach (var requirement in all)
        {
            Assert.Contains(requirement.Subcategory, SecuritySettingsCatalog.AuditSubcategories);
            Assert.Same(SecuritySettingsCatalog.FindSubcategory(requirement.Subcategory.Guid), requirement.Subcategory);
            Assert.Equal("Security", requirement.Channel);
        }

        Assert.Equal(
            [
                (4740, "UserAccountManagement", EventAuditOutcome.Success),
                (4771, "KerberosAuthenticationService", EventAuditOutcome.Failure),
                (4776, "CredentialValidation", EventAuditOutcome.Failure),
                (4625, "Logon", EventAuditOutcome.Failure),
            ],
            AuditEvidence.LockoutRequirements.Select(r => (r.EventId, r.Subcategory.Id, r.Outcome)));
        Assert.Equal(new Guid("0cce9235-69ae-11d9-bed3-505054503030"), AuditEvidence.LockoutRequirements[0].Subcategory.Guid);
        Assert.Equal(new Guid("0cce9215-69ae-11d9-bed3-505054503030"), AuditEvidence.LockoutRequirements[3].Subcategory.Guid);
        Assert.Equal(
            [(4624, "Logon", EventAuditOutcome.Success), (4768, "KerberosAuthenticationService", EventAuditOutcome.Success)],
            AuditEvidence.SuccessRequirements.Select(r => (r.EventId, r.Subcategory.Id, r.Outcome)));
    }

    [Fact]
    public void Evaluate_SeenNotSeenWhileCoveredUnknown()
    {
        var records = new SecurityRecords();
        var read = new[]
        {
            records.Lockout(Pdc, T0 + TimeSpan.FromHours(3), "anowak", NowakSid, "WS-POZNAN-11"),
            records.Ntlm(Pdc, T0 + TimeSpan.FromHours(3), "anowak", "WS-POZNAN-11"),
            records.Ntlm(Pdc, T0 + TimeSpan.FromHours(4), "anowak", "WS-POZNAN-11"),
            records.Kerberos(Dc2, T0 + TimeSpan.FromHours(5), "anowak", NowakSid, "::ffff:10.0.4.21"),
        };
        IReadOnlyList<EventLogCoverage> coverage =
        [
            Covered(Pdc, T0),
            Covered(Dc2, T0) with { Oldest = T0 + TimeSpan.FromHours(2) },
        ];

        // The PDC and DC02 were read without an account condition (DC02's log starts too late), DC03 not at all.
        AuditRead[] reads = [new(Pdc.ToUpperInvariant(), AuditEvidence.LockoutRequirements), new(Dc2, AuditEvidence.LockoutRequirements)];

        var evidence = AuditEvidence.Evaluate(AuditEvidence.LockoutRequirements, read, coverage, T0, T0 + TimeSpan.FromHours(24), reads,
            [Pdc, "DC02.firma.test.", Dc3]);

        Assert.Equal(12, evidence.Count);
        Assert.Equal(
            [
                (Pdc, 4740, AuditEvidenceState.Seen, 1),
                (Pdc, 4771, AuditEvidenceState.NotSeenWhileCovered, 0),
                (Pdc, 4776, AuditEvidenceState.Seen, 2),
                (Pdc, 4625, AuditEvidenceState.NotSeenWhileCovered, 0),
                ("DC02.firma.test.", 4740, AuditEvidenceState.Unknown, 0),
                ("DC02.firma.test.", 4771, AuditEvidenceState.Seen, 1),
                ("DC02.firma.test.", 4776, AuditEvidenceState.Unknown, 0),
                ("DC02.firma.test.", 4625, AuditEvidenceState.Unknown, 0),
                (Dc3, 4740, AuditEvidenceState.Unknown, 0),
                (Dc3, 4771, AuditEvidenceState.Unknown, 0),
                (Dc3, 4776, AuditEvidenceState.Unknown, 0),
                (Dc3, 4625, AuditEvidenceState.Unknown, 0),
            ],
            evidence.Select(e => (e.Host, e.Requirement.EventId, e.State, e.RecordCount)));
    }

    [Fact]
    public void Evaluate_OutcomeAndChannelMustMatch()
    {
        var records = new SecurityRecords();
        var read = new[]
        {
            records.Ntlm(Pdc, T0 + TimeSpan.FromHours(1), "jkowalski", "WS-01", "0x0"),
            records.Tgt(Pdc, T0 + TimeSpan.FromHours(1), "jkowalski", "::ffff:10.0.0.5"),
            records.Kerberos(Pdc, T0 + TimeSpan.FromHours(1), "jkowalski", KowalskiSid, "10.0.0.5") with { Channel = "ForwardedEvents" },
        };

        AuditRead[] reads = [new(Pdc, [.. AuditEvidence.FailedLogonRequirements, .. AuditEvidence.SuccessRequirements])];
        var failures = AuditEvidence.Evaluate(AuditEvidence.FailedLogonRequirements, read, [Covered(Pdc, T0)], T0, T0 + TimeSpan.FromHours(2), reads);
        var successes = AuditEvidence.Evaluate(AuditEvidence.SuccessRequirements, read, [Covered(Pdc, T0)], T0, T0 + TimeSpan.FromHours(2), reads);

        Assert.All(failures, e => Assert.Equal(AuditEvidenceState.NotSeenWhileCovered, e.State));
        Assert.Equal(
            [(4624, AuditEvidenceState.NotSeenWhileCovered), (4768, AuditEvidenceState.Seen)],
            successes.Select(e => (e.Requirement.EventId, e.State)));
    }

    [Fact]
    public void Evaluate_DisabledLogIsUnknown_HostsFromCoverageAndRecordsWhenNoneGiven()
    {
        var records = new SecurityRecords();
        var read = new[] { records.LogonFailure("DC03.FIRMA.TEST", T0 + TimeSpan.FromHours(1), "mzając", "10.0.0.7", "WS-SOPOT-01") };
        IReadOnlyList<EventLogCoverage> coverage = [Covered(Dc2, T0) with { IsEnabled = false }, Covered(Pdc, T0)];

        AuditRead[] reads = [new(Pdc, [AuditEvidence.LockoutRequirements[3]]), new(Dc2, [AuditEvidence.LockoutRequirements[3]])];

        var evidence = AuditEvidence.Evaluate([AuditEvidence.LockoutRequirements[3]], read, coverage, T0, T0 + TimeSpan.FromHours(2), reads);

        Assert.Equal(
            [(Pdc, AuditEvidenceState.NotSeenWhileCovered), (Dc2, AuditEvidenceState.Unknown), (Dc3, AuditEvidenceState.Seen)],
            evidence.Select(e => (e.Host, e.State)));
    }

    /// <summary>
    /// E-AN-01/E-AN-04/E-PRES-02: records read for some accounts only (the lockout plan, the success step) prove an event
    /// is audited, never that it is not – without an unfiltered read every missing requirement is unknown.
    /// </summary>
    [Fact]
    public void Evaluate_FilteredRecordsAlone_NeverNotSeenWhileCovered()
    {
        var records = new SecurityRecords();
        var read = new[] { records.Ntlm(Pdc, T0 + TimeSpan.FromHours(3), "jkowalski", "WS-KRAKOW-07") };
        IReadOnlyList<EventLogCoverage> coverage = [Covered(Pdc, T0), Covered(Dc2, T0), Covered(Dc3, T0)];

        var evidence = AuditEvidence.Evaluate(AuditEvidence.LockoutRequirements, read, coverage, T0, T0 + TimeSpan.FromHours(24), [], [Pdc, Dc2, Dc3]);

        Assert.Equal((Pdc, 4776, AuditEvidenceState.Seen), evidence.Select(e => (e.Host, e.Requirement.EventId, e.State)).Single(e => e.State == AuditEvidenceState.Seen));
        Assert.Equal(11, evidence.Count(e => e.State == AuditEvidenceState.Unknown));
        Assert.DoesNotContain(evidence, e => e.State == AuditEvidenceState.NotSeenWhileCovered);
    }

    /// <summary>E-AN-06: a DC whose read failed or was cut off after its coverage was read has no complete read – unknown.</summary>
    [Fact]
    public void Evaluate_HostWithoutACompleteRead_IsUnknownAlthoughCovered()
    {
        IReadOnlyList<EventLogCoverage> coverage = [Covered(Pdc, T0), Covered(Dc2, T0)];
        AuditRead[] reads = [new(Pdc, AuditEvidence.FailedLogonRequirements)];

        var evidence = AuditEvidence.Evaluate(AuditEvidence.FailedLogonRequirements, [], coverage, T0, T0 + TimeSpan.FromHours(24), reads, [Pdc, Dc2]);

        Assert.All(evidence.Where(e => e.Host == Pdc), e => Assert.Equal(AuditEvidenceState.NotSeenWhileCovered, e.State));
        Assert.All(evidence.Where(e => e.Host == Dc2), e => Assert.Equal(AuditEvidenceState.Unknown, e.State));
    }

    /// <summary>E-AN-11: a full log that does not overwrite stopped recording – it does not cover the window after its newest record.</summary>
    [Fact]
    public void Evaluate_FullRetainLogThatStoppedBeforeTheWindowEnd_IsUnknown()
    {
        var to = T0 + TimeSpan.FromHours(24);
        var stopped = Covered(Dc2, T0) with { Mode = EventLogMode.Retain, IsFull = true, Newest = T0 - TimeSpan.FromMinutes(30) };
        var stillRoom = stopped with { IsFull = false };
        var fullLater = stopped with { Newest = to + TimeSpan.FromMinutes(1) };

        var evidence = AuditEvidence.Evaluate([AuditEvidence.LockoutRequirements[0]], [], [stopped], T0, to, [new AuditRead(Dc2, AuditEvidence.LockoutRequirements)]);

        Assert.Equal(AuditEvidenceState.Unknown, Assert.Single(evidence).State);
        Assert.True(stopped.StoppedBefore(to));
        Assert.False(stopped.Covers(T0, to));
        Assert.True(stillRoom.Covers(T0, to));
        Assert.True(fullLater.Covers(T0, to));
        Assert.False(Covered(Dc2, T0).StoppedBefore(to));
        Assert.False((Covered(Dc2, T0) with { IsEnabled = false }).Covers(T0, to));
        Assert.False((Covered(Dc2, T0) with { Oldest = null }).Covers(T0, to));
    }

    [Fact]
    public void Probes_UnfilteredOneRecordQueries_ForWhatWasNotSeenOnCoveredHosts()
    {
        var records = new SecurityRecords();
        var read = new[]
        {
            records.Lockout(Pdc, T0 + TimeSpan.FromHours(3), "jkowalski", KowalskiSid, "WS-KRAKOW-07"),
            records.Ntlm(Pdc, T0 + TimeSpan.FromHours(3), "jkowalski", "WS-KRAKOW-07"),
        };
        IReadOnlyList<EventLogCoverage> coverage = [Covered(Pdc, T0), Covered(Dc2, T0) with { Oldest = T0 + TimeSpan.FromHours(2) }, Covered(Dc3, T0)];
        var to = T0 + TimeSpan.FromHours(24);

        var probes = AuditEvidence.Probes(AuditEvidence.LockoutRequirements, read, coverage, T0, to, ["DC01.firma.test", Dc2, Dc3, "dc03.firma.test."]);

        Assert.Equal(["DC01.firma.test", Dc3], probes.Select(p => p.Host));
        Assert.Equal([4771, 4625], probes[0].Requirements.Select(r => r.EventId));
        Assert.Equal([4740, 4771, 4776, 4625], probes[1].Requirements.Select(r => r.EventId));
        Assert.All(probes.SelectMany(p => p.Queries), q =>
        {
            Assert.Equal(("Security", (DateTimeOffset?)T0, (DateTimeOffset?)to, (int?)1, true), (q.Channel, q.From, q.To, q.MaxRecords, q.NewestFirst));
            Assert.Empty(q.Data);
            Assert.Null(q.RawXPath);
            Assert.Single(q.EventIds);
        });
        Assert.Equal([(4771, EventAuditOutcome.Failure), (4625, EventAuditOutcome.Failure)], probes[0].Queries.Select(q => (q.EventIds[0], q.Outcome!.Value)));
        Assert.Equal(EventAuditOutcome.Success, probes[1].Queries[0].Outcome);

        // Once a probe completed, what it did not find is "probably not audited"; what it found is seen.
        var found = records.Kerberos(Pdc, T0 + TimeSpan.FromHours(5), "anowak", NowakSid, "::ffff:10.0.0.9");
        var evidence = AuditEvidence.Evaluate(AuditEvidence.LockoutRequirements, [.. read, found], coverage, T0, to, [probes[0].Completed], [Pdc]);
        Assert.Equal(
            [AuditEvidenceState.Seen, AuditEvidenceState.Seen, AuditEvidenceState.Seen, AuditEvidenceState.NotSeenWhileCovered],
            evidence.Select(e => e.State));
        Assert.Throws<ArgumentException>(() => AuditEvidence.Probes(AuditEvidence.LockoutRequirements, read, coverage, to, T0, [Pdc]));
    }

    [Fact]
    public void Entry_ToStringCarriesNoHost()
    {
        var entry = new AuditEvidenceEntry(Dc2, AuditEvidence.LockoutRequirements[0], AuditEvidenceState.Seen, 3);

        Assert.DoesNotContain("dc02", entry.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("4740", entry.ToString(), StringComparison.Ordinal);
    }
}
