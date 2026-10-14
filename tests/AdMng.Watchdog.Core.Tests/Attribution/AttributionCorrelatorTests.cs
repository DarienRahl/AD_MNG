using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Core.Tests.Attribution.Fakes;
using AdMng.Watchdog.Testing.Fixtures.SecurityEvents;
using static AdMng.Watchdog.Core.Tests.Attribution.Records;
using static AdMng.Watchdog.Core.Tests.Rules.TestEvents;

namespace AdMng.Watchdog.Core.Tests.Attribution;

public class AttributionCorrelatorTests
{
    private static readonly DateTimeOffset T = Now.AddMinutes(-2);
    private static readonly string[] Sources = [Dc1, Dc2];

    private readonly FakeSecurityEventStore _store = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WatchEvent Disabled(string? originatingDc = Dc1) =>
        Create(WatchEventKind.AccountDisabled) with
        {
            OriginatingTime = T,
            OriginatingDc = originatingDc,
            Changes = [new AttributeChange { Attribute = "userAccountControl", OldValues = ["512"], NewValues = ["514"] }],
        };

    private Task<CorrelationOutcome> CorrelateAsync(WatchEvent e) =>
        new AttributionCorrelator(_store).CorrelateAsync(e, OriginatingDcResolver.Plan(e, Sources, new AttributionSettings()), Ct);

    [Fact]
    public async Task TheAccountRecordOfTheOriginatingDc_Resolves()
    {
        _store.Add(Account(4725, T.AddSeconds(1), Admin));

        var outcome = await CorrelateAsync(Disabled());

        Assert.Equal(CorrelationStatus.Resolved, outcome.Status);
        Assert.Equal(AdminSid, outcome.Best!.Event.SubjectSid);
        Assert.Equal(100, outcome.Best.Score);
        Assert.True(outcome.Best.StrongKey);
    }

    [Fact]
    public async Task RecordsOutsideTheWindowOrOnOtherDcs_AreIgnored()
    {
        _store.Add(
            Account(4725, T.AddSeconds(10), Admin),
            Account(4725, T, Admin, computer: Dc2),
            Parse(SecurityEventXml.Account(4725, NextId(), T, Dc1, OtherAdminSid, "zzak", Admin)));

        var outcome = await CorrelateAsync(Disabled());

        Assert.Equal(CorrelationStatus.NoCandidate, outcome.Status);
        Assert.Empty(outcome.Candidates);
    }

    [Fact]
    public async Task TwoDifferentSubjectsWithinFivePoints_AreAmbiguous()
    {
        _store.Add(Account(4725, T, Admin, computer: Dc1), Account(4725, T.AddSeconds(1), OtherAdmin, computer: Dc2));

        var outcome = await CorrelateAsync(Disabled(originatingDc: null));

        Assert.Equal(CorrelationStatus.Ambiguous, outcome.Status);
        Assert.Equal(2, outcome.Candidates.Count);
    }

    [Fact]
    public async Task TheSameSubjectTwice_IsNotAmbiguous()
    {
        _store.Add(Account(4725, T, Admin), Modified(T, Admin, "userAccountControl", "514", added: true));

        var outcome = await CorrelateAsync(Disabled());

        Assert.Equal(CorrelationStatus.Resolved, outcome.Status);
        Assert.Equal(2, outcome.Candidates.Count);
    }

    [Fact]
    public async Task WithoutStrongKeyMatches_TheWeakKeysAreTried()
    {
        // The record carries a different (pre-migration) SID but the same sAMAccountName.
        _store.Add(Account(4725, T, Admin, targetSid: DomainSid + "-9999"));

        var outcome = await CorrelateAsync(Disabled());

        Assert.Equal(CorrelationStatus.Resolved, outcome.Status);
        Assert.False(outcome.Best!.StrongKey);
        Assert.Equal(20 + 30 + 10 + 10, outcome.Best.Score);
        Assert.Contains(_store.Queries, q => q.TargetUserName == "lwisniewski");
    }

    [Fact]
    public async Task Membership_UsesTheGroupSidAndTheMemberSid()
    {
        var group = DomainSid + "-1500";
        var e = Membership(WatchEventKind.GroupMemberAdded, "CN=Księgowość,OU=Grupy,DC=contoso,DC=test", group) with
        {
            Member = new ObjectRef { Dn = UserDn, Kind = WatchObjectKind.User, Sid = UserSid },
            LocalChangeTime = T,
        };
        _store.Add(Parse(SecurityEventXml.Membership(4728, NextId(), T.AddSeconds(5), Dc1, group, "Księgowość", UserSid, UserDn, Admin)));

        var outcome = await CorrelateAsync(e);

        Assert.Equal(CorrelationStatus.Resolved, outcome.Status);
        Assert.Contains(_store.Queries, q => q.TargetSid == group && q.MemberSid == UserSid);
    }

    [Fact]
    public async Task KindsWithoutRecords_DoNotQuery()
    {
        var outcome = await CorrelateAsync(Create(WatchEventKind.PasswordExpiring));

        Assert.Equal(CorrelationStatus.NoCandidate, outcome.Status);
        Assert.Empty(_store.Queries);
    }

    [Fact]
    public void Decide_BelowTheResolveScore_IsNoCandidate()
    {
        var plan = OriginatingDcResolver.Plan(Disabled(), Sources, new AttributionSettings());
        var weak = new ScoredCandidate(Account(4725, T, Admin), 55, StrongKey: false, Confirmed: true);

        var outcome = AttributionCorrelator.Decide([weak], plan);

        Assert.Equal(CorrelationStatus.NoCandidate, outcome.Status);
        Assert.Same(weak, outcome.Best);
    }

    [Fact]
    public void Decide_ARivalSixPointsBehind_DoesNotBlock()
    {
        var plan = OriginatingDcResolver.Plan(Disabled(), Sources, new AttributionSettings());

        var outcome = AttributionCorrelator.Decide(
            [new ScoredCandidate(Account(4725, T, OtherAdmin), 84, true, true), new ScoredCandidate(Account(4725, T, Admin), 90, true, true)], plan);

        Assert.Equal(CorrelationStatus.Resolved, outcome.Status);
        Assert.Equal(AdminSid, outcome.Best!.Event.SubjectSid);
    }
}
