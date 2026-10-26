using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Testing.Fixtures.OpenWec;
using static AdMng.Watchdog.Core.Tests.Attribution.Collector.CollectorReplay;
using static AdMng.Watchdog.Core.Tests.Attribution.Records;
using static AdMng.Watchdog.Core.Tests.Rules.TestEvents;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Watchdog.Core.Tests.Attribution.Collector;

/// <summary>
/// B4 acceptance (stage B design 21): OpenWEC RawJson replays of real-shaped Windows records give the same attribution
/// results as the live DC-log path – actor, confidence, logon source (4624 type 3 Kerberos and NTLM, type 10 RDP, a
/// loopback ADWS logon), lockout sources (4740 + 4771/4776/4625 on the PDC), event-log-only detections (1102), directory
/// changes (5136), memberships (4728), ambiguity and the unresolved reasons. Each scenario runs both paths on identical
/// records and compares the operator-visible outcome; the expected values are asserted once more on the collector run.
/// </summary>
public sealed class CollectorReplayTests
{
    private static readonly DateTimeOffset T = Now.AddMinutes(-2);
    private static readonly IEnumerable<(string, string?)> AdminSubject = WindowsEventXml.Subject(AdminSid, "adm.jkowalska", AdminLogonId);
    private static readonly IEnumerable<(string, string?)> OtherSubject = WindowsEventXml.Subject(OtherAdminSid, "adm.zzak", "0x77aa1");
    private static long _id = 7_000_000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static long Id() => Interlocked.Increment(ref _id);

    private static WatchEvent Disabled(string dc = Dc1) => Create(WatchEventKind.AccountDisabled) with
    {
        OriginatingTime = T,
        OriginatingDc = dc,
        Changes = [new AttributeChange { Attribute = "userAccountControl", OldValues = ["512"], NewValues = ["514"] }],
    };

    /// <summary>Noise every DC forwards: logons of computer accounts and ANONYMOUS LOGON, another user's session.</summary>
    private static IEnumerable<DcRecord> Noise(string dc) =>
    [
        new(dc, WindowsEventXml.Logon(Id(), T.AddMinutes(-3), dc, "S-1-5-21-1-2-3-1001", "DC02$", "0x9a11", 3, "10.0.1.11", null)),
        new(dc, WindowsEventXml.Logon(Id(), T.AddMinutes(-3), dc, "S-1-5-7", "ANONYMOUS LOGON", "0x9a12", 3, "10.0.8.8", null, "NTLM")),
        new(dc, WindowsEventXml.Logon(Id(), T.AddMinutes(-4), dc, UserSid, "lwisniewski", "0x51b0", 3, "10.0.6.40", null)),
    ];

    private static async Task<(Outcome Live, Outcome Collected, AttributionHarness Harness)> BothAsync(IReadOnlyList<DcRecord> records,
        IReadOnlyList<WatchEvent> pending, int cycles = 1, TimeSpan? between = null)
    {
        var (live, liveHarness) = await RunAsync(collector: false, records, pending, Ct, cycles, between);
        liveHarness.Dispose();
        var (collected, harness) = await RunAsync(collector: true, records, pending, Ct, cycles, between);
        Assert.Equal(live.Pending, collected.Pending);
        Assert.Equal(live.Detections, collected.Detections);
        Assert.Empty(harness.Source.Queries);   // collector mode never reads a DC log
        return (live, collected, harness);
    }

    [Fact]
    public async Task KerberosNetworkLogon_ResolvesActorAndSource()
    {
        var pending = Disabled();
        var (_, _, h) = await BothAsync(
        [
            .. Noise(Dc1),
            new(Dc1, WindowsEventXml.Logon(Id(), T.AddSeconds(-40), Dc1, AdminSid, "adm.jkowalska", AdminLogonId, 3, "10.0.5.21", null)),
            new(Dc1, WindowsEventXml.Account(4725, Id(), T, Dc1, UserSid, "lwisniewski", AdminSubject)),
        ], [pending]);
        using (h)
        {
            var attribution = Single(h);
            Assert.Equal(AttributionStatus.Resolved, attribution.Status);
            Assert.Equal(AdminSid, attribution.Actor!.Sid);
            Assert.Equal("10.0.5.21", attribution.IpAddress);
            Assert.Equal("4624", attribution.SourceMethod);
            Assert.Equal(3, attribution.LogonType);
            Assert.Equal(4725, Assert.Single(attribution.Evidence).EventCode);
        }
    }

    [Fact]
    public async Task RdpLogon_IsLogonType10WithTheClientAddress()
    {
        var (_, _, h) = await BothAsync(
        [
            .. Noise(Dc1),
            new(Dc1, WindowsEventXml.Logon(Id(), T.AddMinutes(-20), Dc1, AdminSid, "adm.jkowalska", AdminLogonId, 10, "10.0.9.5", "ADM-WS01")),
            new(Dc1, WindowsEventXml.Account(4725, Id(), T, Dc1, UserSid, "lwisniewski", AdminSubject)),
        ], [Disabled()]);
        using (h)
        {
            var attribution = Single(h);
            Assert.Equal(10, attribution.LogonType);
            Assert.Equal("10.0.9.5", attribution.IpAddress);
            Assert.Equal("ADM-WS01", attribution.Workstation);
        }
    }

    [Fact]
    public async Task LoopbackLogon_IsLocalOnTheDc()
    {
        // ADWS / AD PowerShell: the change reaches the DS from the DC itself (stage B design 7.8).
        var (_, _, h) = await BothAsync(
        [
            .. Noise(Dc1),
            new(Dc1, WindowsEventXml.Logon(Id(), T.AddSeconds(-2), Dc1, AdminSid, "adm.jkowalska", AdminLogonId, 3, "::1", null)),
            new(Dc1, WindowsEventXml.Account(4725, Id(), T, Dc1, UserSid, "lwisniewski", AdminSubject)),
        ], [Disabled()]);
        using (h)
        {
            var attribution = Single(h);
            Assert.Equal("local", attribution.SourceMethod);
            Assert.Null(attribution.IpAddress);
        }
    }

    [Fact]
    public async Task NtlmLogonWithAWorkstationName_IsLowSourceConfidence()
    {
        var (_, _, h) = await BothAsync(
        [
            .. Noise(Dc2),
            new(Dc2, WindowsEventXml.Logon(Id(), T.AddMinutes(-1), Dc2, AdminSid, "adm.jkowalska", AdminLogonId, 3, "10.0.5.77", "WS-GDAŃSK-03", "NTLM")),
            new(Dc2, WindowsEventXml.Account(4725, Id(), T, Dc2, UserSid, "lwisniewski", AdminSubject)),
        ], [Disabled(Dc2)]);
        using (h)
        {
            var attribution = Single(h);
            Assert.Equal("WS-GDAŃSK-03", attribution.Workstation);
            Assert.Equal(AttributionConfidence.Low, attribution.SourceConfidence);
        }
    }

    [Fact]
    public async Task SessionNotForwarded_IsSourceLogonNotFound()
    {
        var (_, _, h) = await BothAsync(
        [
            .. Noise(Dc1),
            new(Dc1, WindowsEventXml.Account(4725, Id(), T, Dc1, UserSid, "lwisniewski", AdminSubject)),
        ], [Disabled()]);
        using (h)
        {
            var attribution = Single(h);
            Assert.Equal(AttributionStatus.Resolved, attribution.Status);
            Assert.Equal(AttributionReasons.SourceLogonNotFound, attribution.SourceReason);
        }
    }

    [Fact]
    public async Task DirectoryChangeAndMembership_AreCorrelatedByObjectGuidAndMemberSid()
    {
        const string groupDn = "CN=Księgowość,OU=Grupy,DC=contoso,DC=test";
        var groupSid = DomainSid + "-1500";
        var changed = Create(WatchEventKind.AttributesChanged, changedAttributes: ["department"]) with { OriginatingTime = T, OriginatingDc = Dc2 };
        var member = Membership(WatchEventKind.GroupMemberAdded, groupDn, groupSid) with { OriginatingTime = T.AddSeconds(5), OriginatingDc = Dc2 };
        member = member with { Member = member.Member! with { Sid = UserSid } };
        var correlation = Guid.NewGuid();
        var (_, _, h) = await BothAsync(
        [
            new(Dc2, WindowsEventXml.Logon(Id(), T.AddMinutes(-5), Dc2, OtherAdminSid, "adm.zzak", "0x77aa1", 3, "10.0.5.30", null)),
            new(Dc2, WindowsEventXml.DirectoryModified(Id(), T, Dc2, UserGuid, UserDn, "user", "department", "Kadry", added: false, OtherSubject, correlation)),
            new(Dc2, WindowsEventXml.DirectoryModified(Id(), T, Dc2, UserGuid, UserDn, "user", "department", "Księgowość", added: true, OtherSubject, correlation)),
            new(Dc2, WindowsEventXml.Membership(4728, Id(), T.AddSeconds(5), Dc2, groupSid, "Księgowość", UserSid, UserDn, OtherSubject)),
        ], [changed, member]);
        using (h)
        {
            Assert.All(h.Events.All.Where(e => e.Kind is WatchEventKind.AttributesChanged or WatchEventKind.GroupMemberAdded), e =>
            {
                Assert.Equal(AttributionStatus.Resolved, e.Attribution.Status);
                Assert.Equal(OtherAdminSid, e.Attribution.Actor!.Sid);
                Assert.Equal("10.0.5.30", e.Attribution.IpAddress);
            });
        }
    }

    [Fact]
    public async Task Lockout_SourcesComeFromTheFailuresOnThePdc()
    {
        var lockedAt = Now.AddMinutes(-3);
        var lockout = Create(WatchEventKind.AccountLockedOut) with
        {
            Lockout = new LockoutInfo { LockedAt = lockedAt, LockedUntil = lockedAt.AddMinutes(30) },
        };
        var records = new List<DcRecord>
        {
            new(Dc1, WindowsEventXml.LockedOut(Id(), lockedAt, Dc1, UserSid, "lwisniewski", callerComputer: null)),
        };
        for (var i = 0; i < 3; i++)
        {
            records.Add(new(Dc2, WindowsEventXml.KerberosPreAuthFailed(Id(), lockedAt.AddMinutes(-5 + i), Dc2, "lwisniewski", UserSid, "10.0.7.7")));
        }

        records.Add(new(Dc2, WindowsEventXml.CredentialValidation(Id(), lockedAt.AddMinutes(-2), Dc2, "lwisniewski", "WS-ŁÓDŹ-01")));
        records.Add(new(Dc2, WindowsEventXml.CredentialValidation(Id(), lockedAt.AddMinutes(-1), Dc2, "lwisniewski", "WS-ŁÓDŹ-01")));
        records.Add(new(Dc2, WindowsEventXml.LogonFailed(Id(), lockedAt.AddSeconds(-30), Dc2, "lwisniewski", "10.0.7.8", "WS-ŁÓDŹ-02")));
        records.Add(new(Dc2, WindowsEventXml.KerberosPreAuthFailed(Id(), lockedAt.AddMinutes(-4), Dc2, "lwisniewski", UserSid, "10.0.7.9", status: "0x6")));

        var (_, _, h) = await BothAsync(records, [lockout]);
        using (h)
        {
            var locked = Assert.Single(h.Events.All, e => e.Kind == WatchEventKind.AccountLockedOut);
            Assert.Equal(AttributionStatus.NotApplicable, locked.Attribution.Status);
            Assert.Equal(4740, Assert.Single(locked.Attribution.Evidence).EventCode);
            var info = locked.Lockout!;
            Assert.Contains(info.Sources, s => s.IpAddress == "10.0.7.7" && s.Count == 3 && s.EventCode == 4771);
            Assert.Contains(info.Sources, s => s.Workstation == "WS-ŁÓDŹ-01" && s.Count == 2 && s.EventCode == 4776);
            Assert.Contains(info.Sources, s => s.IpAddress == "10.0.7.8" && s.EventCode == 4625);
            Assert.DoesNotContain(info.Sources, s => s.IpAddress == "10.0.7.9");   // 0x6: unknown account, not a bad password
        }
    }

    [Fact]
    public async Task LogCleared_IsDetectedOnceWithItsActor()
    {
        var (_, _, h) = await BothAsync(
        [
            new(Dc2, WindowsEventXml.Logon(Id(), T.AddMinutes(-10), Dc2, AdminSid, "adm.jkowalska", AdminLogonId, 10, "10.0.9.5", "ADM-WS01")),
            new(Dc2, WindowsEventXml.LogCleared(Id(), T, Dc2, AdminSid, "adm.jkowalska", AdminLogonId)),
        ], []);
        using (h)
        {
            var cleared = Assert.Single(h.Events.All, e => e.Kind == WatchEventKind.SecurityLogCleared);
            Assert.Equal(AdminSid, cleared.Attribution.Actor!.Sid);
            Assert.Single(h.Events.All, e => e.Kind == WatchEventKind.HealthEventLogGap);
        }
    }

    [Fact]
    public async Task TwoAdminsOnTheSameObject_AreAmbiguousInBothPaths()
    {
        var (_, _, h) = await BothAsync(
        [
            new(Dc1, WindowsEventXml.Account(4725, Id(), T, Dc1, UserSid, "lwisniewski", AdminSubject)),
            new(Dc2, WindowsEventXml.Account(4725, Id(), T.AddSeconds(1), Dc2, UserSid, "lwisniewski", OtherSubject)),
        ], [Create(WatchEventKind.AccountDisabled) with { LocalChangeTime = T }]);
        using (h)
        {
            Assert.Equal(AttributionStatus.Ambiguous, Single(h).Status);
        }
    }

    [Fact]
    public async Task NothingLogged_EndsWithTheSameReasonAfterTheLastRetry()
    {
        var (_, _, h) = await BothAsync([.. Noise(Dc1), .. Noise(Dc2)], [Disabled()], cycles: 6, between: TimeSpan.FromMinutes(10));
        using (h)
        {
            var attribution = Single(h);
            Assert.Equal(AttributionStatus.Unknown, attribution.Status);
            Assert.Equal(AttributionReasons.AuditPolicyMissing, attribution.Reason);
        }
    }

    [Fact]
    public async Task CollectorOnly_NoLogonOfThatDcStored_IsSourceNotCollected()
    {
        // The live path has no such check (it reads the DC log directly); in collector mode a DC that forwards no 4624 at
        // all cannot give a source (SOURCE_NOT_COLLECTED, stage B design 7.8).
        var (collected, h) = await RunAsync(collector: true,
            [new(Dc1, WindowsEventXml.Account(4725, Id(), T, Dc1, UserSid, "lwisniewski", AdminSubject))], [Disabled()], Ct);
        using (h)
        {
            Assert.Single(collected.Pending);
            Assert.Equal(AttributionReasons.SourceNotCollected, Single(h).SourceReason);
        }
    }

    private static EventAttribution Single(AttributionHarness h) =>
        Assert.Single(h.Events.All, e => e.Kind is not (WatchEventKind.HealthEventLogGap or WatchEventKind.HealthAttributionDegraded)).Attribution;
}
