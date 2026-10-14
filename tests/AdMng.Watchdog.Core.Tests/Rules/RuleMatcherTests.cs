using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Rules;
using static AdMng.Watchdog.Core.Tests.Rules.TestEvents;

namespace AdMng.Watchdog.Core.Tests.Rules;

public class RuleMatcherTests
{
    private static readonly RuleMatchContext Context = RuleMatchContext.Create(
        WatchdogConfigProvider.Effective(new DefaultConfigurationFactory().Create() with
        {
            PrivilegedGroups = new PrivilegedGroupSettings
            {
                AdditionalHighDns = ["CN=Administratorzy SQL,OU=Grupy,{domain}"],
                WatchListDns = ["CN=Księgowość,OU=Grupy,{domain}"],
            },
        }),
        Tokens);

    private static RuleSettings Rule(params WatchEventKind[] kinds) => new() { Id = "test-rule", Name = "Reguła testowa", Kinds = kinds };

    private static RuleMatchOutcome Match(RuleSettings rule, WatchEvent watchEvent, bool graceExpired = false) =>
        RuleMatcher.Match(rule, watchEvent, Context, Now, graceExpired);

    [Fact]
    public void DisabledRule_NeverMatches() =>
        Assert.Equal(RuleMatchOutcome.NoMatch, Match(Rule() with { Enabled = false }, Create(WatchEventKind.UserCreated)));

    [Fact]
    public void KindsAndObjectKinds_EmptyMeansAny()
    {
        var created = Create(WatchEventKind.UserCreated);

        Assert.Equal(RuleMatchOutcome.Match, Match(Rule(), created));
        Assert.Equal(RuleMatchOutcome.Match, Match(Rule(WatchEventKind.UserCreated, WatchEventKind.GroupCreated), created));
        Assert.Equal(RuleMatchOutcome.NoMatch, Match(Rule(WatchEventKind.GroupCreated), created));
        Assert.Equal(RuleMatchOutcome.Match, Match(Rule() with { ObjectKinds = [WatchObjectKind.User] }, created));
        Assert.Equal(RuleMatchOutcome.NoMatch, Match(Rule() with { ObjectKinds = [WatchObjectKind.Computer] }, created));
    }

    [Fact]
    public void TestKind_NeverMatches_EvenTheHealthRule()
    {
        var test = Create(WatchEventKind.Test, objectKind: WatchObjectKind.Watchdog);

        Assert.Equal(RuleMatchOutcome.NoMatch, Match(Rule(), test));
        Assert.Equal(RuleMatchOutcome.NoMatch, Match(DefaultRules.Find(DefaultRules.Health)!, test));
    }

    [Fact]
    public void HealthEvents_AlwaysMatchTheLockedHealthRule()
    {
        var health = DefaultRules.Find(DefaultRules.Health)! with { Enabled = false, Kinds = [WatchEventKind.HealthStorage], MinSeverity = Severity.Critical };
        var resync = Create(WatchEventKind.HealthResync, "CN=Watchdog", WatchObjectKind.Watchdog);

        Assert.Equal(RuleMatchOutcome.Match, Match(health, resync));
        Assert.Equal(RuleMatchOutcome.NoMatch, Match(health, Create(WatchEventKind.UserCreated)));
    }

    [Theory]
    [InlineData("OU=Kraków,{domain}", null, true)]
    [InlineData("OU=Kraków,DC=contoso,DC=test", null, true)]
    [InlineData("CN=Łukasz Wiśniewski,OU=Kraków,{domain}", null, true)]
    [InlineData("OU=Gdańsk,{domain}", null, false)]
    [InlineData(null, "OU=Kraków,{domain}", false)]
    [InlineData("{domain}", "CN=Łukasz Wiśniewski,OU=KRAKÓW,{domain}", false)]
    [InlineData("OU=Kraków,{domain}", "OU=Gdańsk,{domain}", true)]
    [InlineData("nie jest DN", null, false)]
    public void Scope_IncludeAndExcludeSubtrees(string? include, string? exclude, bool matches)
    {
        var rule = Rule() with
        {
            Scope = new RuleScope { IncludeSubtrees = include is null ? [] : [include], ExcludeSubtrees = exclude is null ? [] : [exclude] },
        };

        Assert.Equal(matches ? RuleMatchOutcome.Match : RuleMatchOutcome.NoMatch, Match(rule, Create(WatchEventKind.AttributesChanged)));
    }

    [Fact]
    public void Scope_ConsidersThePreviousDnOfMoves()
    {
        var moved = Create(WatchEventKind.ObjectMoved, "CN=Łukasz Wiśniewski,OU=Archiwum,DC=contoso,DC=test") with { PreviousDn = UserDn };
        var krakow = Rule() with { Scope = new RuleScope { IncludeSubtrees = ["OU=Kraków,{domain}"] } };
        var notKrakow = Rule() with { Scope = new RuleScope { ExcludeSubtrees = ["OU=Kraków,{domain}"] } };

        Assert.Equal(RuleMatchOutcome.Match, Match(krakow, moved));
        Assert.Equal(RuleMatchOutcome.NoMatch, Match(notKrakow, moved));
    }

    [Fact]
    public void Scope_UnknownDomainFallsBackToTheEventsDomain()
    {
        var unknown = RuleMatchContext.Create(Context.Configuration, ConfigTokens.Unknown);
        var rule = Rule() with { Scope = new RuleScope { IncludeSubtrees = ["OU=Kraków,{domain}"] } };

        Assert.Equal(RuleMatchOutcome.Match, RuleMatcher.Match(rule, Create(WatchEventKind.UserCreated), unknown, Now));
    }

    [Fact]
    public void Attributes_AnyOfThemChanged_CaseInsensitive()
    {
        var rule = Rule(WatchEventKind.AttributesChanged) with { Attributes = ["scriptPath", "profilePath"] };

        Assert.Equal(RuleMatchOutcome.Match, Match(rule, Create(WatchEventKind.AttributesChanged, changedAttributes: ["department", "SCRIPTPATH"])));
        Assert.Equal(RuleMatchOutcome.NoMatch, Match(rule, Create(WatchEventKind.AttributesChanged, changedAttributes: ["department", "title"])));
        Assert.Equal(RuleMatchOutcome.NoMatch, Match(rule, Create(WatchEventKind.AttributesChanged)));
    }

    [Fact]
    public void Tags_RequiredAllAndAnyOne()
    {
        var both = Create(WatchEventKind.AccountEnabled, tags: [EventTags.Privileged, EventTags.LongDisabled]);
        var one = Create(WatchEventKind.AccountEnabled, tags: [EventTags.ServiceAccount]);

        Assert.Equal(RuleMatchOutcome.Match, Match(Rule() with { RequiredTags = [EventTags.Privileged, EventTags.LongDisabled] }, both));
        Assert.Equal(RuleMatchOutcome.NoMatch, Match(Rule() with { RequiredTags = [EventTags.Privileged, EventTags.LongDisabled] }, one));
        Assert.Equal(RuleMatchOutcome.Match, Match(Rule() with { AnyTags = [EventTags.Privileged, EventTags.ServiceAccount] }, one));
        Assert.Equal(RuleMatchOutcome.NoMatch, Match(Rule() with { AnyTags = [EventTags.Privileged] }, one));
    }

    [Fact]
    public void GroupFilter_Privileged_BySidNameOrDn()
    {
        var rule = Rule(WatchEventKind.GroupMemberAdded) with { Groups = GroupFilter.Privileged };

        Assert.Equal(RuleMatchOutcome.Match, Match(rule, Membership(WatchEventKind.GroupMemberAdded, "CN=Domain Admins,CN=Users,DC=contoso,DC=test", DomainSid + "-512")));
        Assert.Equal(RuleMatchOutcome.Match, Match(rule, Membership(WatchEventKind.GroupMemberAdded, "CN=Administrators,CN=Builtin,DC=contoso,DC=test", "S-1-5-32-544")));
        Assert.Equal(RuleMatchOutcome.Match, Match(rule, Membership(WatchEventKind.GroupMemberAdded, "CN=DnsAdmins,CN=Users,DC=contoso,DC=test", groupSam: "dnsadmins")));
        Assert.Equal(RuleMatchOutcome.Match, Match(rule, Membership(WatchEventKind.GroupMemberAdded, "CN=Administratorzy SQL,OU=Grupy,DC=contoso,DC=test")));
        Assert.Equal(RuleMatchOutcome.NoMatch, Match(rule, Membership(WatchEventKind.GroupMemberAdded, "CN=Księgowość,OU=Grupy,DC=contoso,DC=test")));
    }

    [Fact]
    public void GroupFilter_PrivilegeKinds_UseTheMemberAsTheGroup()
    {
        var rule = Rule(WatchEventKind.PrivilegeGranted) with { Groups = GroupFilter.Explicit, GroupDns = ["CN=Domain Admins,CN=Users,{domain}"] };
        var granted = Create(WatchEventKind.PrivilegeGranted) with
        {
            Member = new ObjectRef { Dn = "CN=Domain Admins,CN=Users,DC=contoso,DC=test", Kind = WatchObjectKind.Group, Sid = DomainSid + "-512" },
        };

        Assert.Equal(RuleMatchOutcome.Match, Match(rule, granted));
        Assert.Equal(RuleMatchOutcome.NoMatch, Match(rule, granted with { Member = null }));
    }

    [Fact]
    public void GroupFilter_WatchListAndExplicit_AndIgnoredForOtherKinds()
    {
        var watchList = Rule() with { Groups = GroupFilter.WatchList };
        var accounting = Membership(WatchEventKind.GroupMemberRemoved, "CN=Księgowość,OU=Grupy,DC=contoso,DC=test");

        Assert.Equal(RuleMatchOutcome.Match, Match(watchList, accounting));
        Assert.Equal(RuleMatchOutcome.NoMatch, Match(watchList, Membership(WatchEventKind.GroupMemberRemoved, "CN=Kadry,OU=Grupy,DC=contoso,DC=test")));
        Assert.Equal(RuleMatchOutcome.Match, Match(watchList, Create(WatchEventKind.UserCreated)));
        var explicitRule = Rule() with { Groups = GroupFilter.Explicit, GroupDns = ["cn=księgowość,ou=grupy,dc=contoso,dc=test"] };
        Assert.Equal(RuleMatchOutcome.Match, Match(explicitRule, accounting));
    }

    [Theory]
    [InlineData(Severity.Low, Severity.Medium, false)]
    [InlineData(Severity.Medium, Severity.Medium, true)]
    [InlineData(Severity.Critical, Severity.High, true)]
    public void MinSeverity_ComparesWithTheEventSeverity(Severity eventSeverity, Severity minimum, bool matches) =>
        Assert.Equal(matches ? RuleMatchOutcome.Match : RuleMatchOutcome.NoMatch,
            Match(Rule() with { MinSeverity = minimum }, Create(WatchEventKind.AttributesChanged, severity: eventSeverity)));

    [Fact]
    public void ExcludeActors_BySidOfAResolvedActorOnly()
    {
        var rule = Rule() with { Actors = new ActorFilter { ExcludeActors = [SyncSid] } };

        Assert.Equal(RuleMatchOutcome.NoMatch, Match(rule, Create(WatchEventKind.KeyCredentialChanged, attribution: Resolved(SyncSid, "CONTOSO\\MSOL_1a2b"))));
        Assert.Equal(RuleMatchOutcome.NoMatch, Match(rule, Create(WatchEventKind.KeyCredentialChanged, attribution: Resolved(SyncSid.ToLowerInvariant()))));
        Assert.Equal(RuleMatchOutcome.Match, Match(rule, Create(WatchEventKind.KeyCredentialChanged, attribution: Resolved(AdminSid))));
        Assert.Equal(RuleMatchOutcome.Match, Match(rule, Create(WatchEventKind.KeyCredentialChanged, attribution: Unknown())));
    }

    [Fact]
    public void ExcludeActors_NeverMatchesByAccountNameOrWorkstation()
    {
        var rule = Rule() with { Actors = new ActorFilter { ExcludeActors = [SyncSid] } };
        var byName = Resolved(AdminSid, "CONTOSO\\" + SyncSid) with { Workstation = SyncSid, IpAddress = SyncSid };

        Assert.Equal(RuleMatchOutcome.Match, Match(rule, Create(WatchEventKind.KeyCredentialChanged, attribution: byName)));
    }

    [Fact]
    public void ExcludeActors_SupportsTokens()
    {
        var rule = Rule() with { Actors = new ActorFilter { ExcludeActors = ["{domainSid}-1601"] } };

        Assert.Equal(RuleMatchOutcome.NoMatch, Match(rule, Create(WatchEventKind.KeyCredentialChanged, attribution: Resolved(SyncSid))));
    }

    [Fact]
    public void ActorConditions_AreProvisionalWhileAttributionIsPending_AndExcludeNothingAfterTheGracePeriod()
    {
        var rule = Rule() with { Actors = new ActorFilter { ExcludeActors = [SyncSid] } };
        var pending = Create(WatchEventKind.KeyCredentialChanged);

        Assert.Equal(RuleMatchOutcome.Provisional, Match(rule, pending));
        Assert.Equal(RuleMatchOutcome.Match, Match(rule, pending, graceExpired: true));
        Assert.Equal(RuleMatchOutcome.Match, Match(Rule(), pending));
    }

    [Fact]
    public void ExcludeSelfChanges_UsesTheSelfChangeTag()
    {
        var rule = Rule() with { Actors = new ActorFilter { ExcludeSelfChanges = true } };

        Assert.Equal(RuleMatchOutcome.NoMatch, Match(rule, Create(WatchEventKind.KeyCredentialChanged, attribution: Resolved(DomainSid + "-6609"), tags: [EventTags.SelfChange])));
        Assert.Equal(RuleMatchOutcome.Match, Match(rule, Create(WatchEventKind.KeyCredentialChanged, attribution: Resolved(AdminSid))));
        Assert.Equal(RuleMatchOutcome.Provisional, Match(rule, Create(WatchEventKind.KeyCredentialChanged)));
        Assert.Equal(RuleMatchOutcome.Match, Match(rule, Create(WatchEventKind.KeyCredentialChanged), graceExpired: true));
    }

    [Fact]
    public void Exclusions_AnApplyingExclusionPreventsTheMatch()
    {
        var rule = Rule() with
        {
            Exclusions = [new RuleExclusion { Id = "serwery", SubtreeDn = "OU=Kraków,{domain}", Reason = "Konta testowe", CreatedBy = "CONTOSO\\jan", CreatedAt = Now }],
        };

        Assert.Equal(RuleMatchOutcome.NoMatch, Match(rule, Create(WatchEventKind.UserCreated)));
        Assert.Equal(RuleMatchOutcome.Match, Match(rule, Create(WatchEventKind.UserCreated, "CN=Zofia Żak,OU=Gdańsk,DC=contoso,DC=test")));
    }

    [Fact]
    public void Exclusions_WithAnActorAreProvisionalWhilePending()
    {
        var rule = Rule() with
        {
            Exclusions = [new RuleExclusion { Id = "sync", Actor = SyncSid, Reason = "Entra Connect", CreatedBy = "CONTOSO\\jan", CreatedAt = Now }],
        };

        Assert.Equal(RuleMatchOutcome.Provisional, Match(rule, Create(WatchEventKind.KeyCredentialChanged)));
        Assert.Equal(RuleMatchOutcome.NoMatch, Match(rule, Create(WatchEventKind.KeyCredentialChanged, attribution: Resolved(SyncSid))));
        Assert.Equal(RuleMatchOutcome.Match, Match(rule, Create(WatchEventKind.KeyCredentialChanged), graceExpired: true));
        Assert.True(RuleMatcher.HasActorConditions(rule));
        Assert.False(RuleMatcher.HasActorConditions(Rule()));
    }
}
