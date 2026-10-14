using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Rules;
using static AdMng.Watchdog.Core.Tests.Rules.TestEvents;

namespace AdMng.Watchdog.Core.Tests.Rules;

public class ExclusionMatcherTests
{
    private static readonly RuleMatchContext Context = RuleMatchContext.Create(WatchdogConfigProvider.Effective(new WatchdogConfiguration()), Tokens);
    private static readonly RuleSettings AnyRule = new() { Id = "dowolna", Name = "Dowolna" };

    private static RuleExclusion Exclusion(Guid? objectGuid = null, string? subtree = null, string? actor = null, string? attribute = null,
        DateTimeOffset? expiresAt = null) => new()
    {
        Id = "wyjątek",
        ObjectGuid = objectGuid,
        SubtreeDn = subtree,
        Actor = actor,
        Attribute = attribute,
        ExpiresAt = expiresAt,
        Reason = "Synchronizacja kadr – zatwierdził Łukasz",
        CreatedBy = "CONTOSO\\adm.jkowalska",
        CreatedAt = Now.AddDays(-1),
    };

    private static ExclusionOutcome Evaluate(RuleExclusion exclusion, WatchEvent watchEvent, RuleSettings? rule = null, bool graceExpired = false) =>
        ExclusionMatcher.Evaluate(exclusion, rule ?? AnyRule, watchEvent, Context, Now, graceExpired);

    [Fact]
    public void ObjectGuid_MatchesTheObjectOrTheMember()
    {
        var member = Membership(WatchEventKind.GroupMemberAdded, "CN=Księgowość,OU=Grupy,DC=contoso,DC=test");

        Assert.Equal(ExclusionOutcome.Applies, Evaluate(Exclusion(UserGuid), Create(WatchEventKind.AttributesChanged)));
        Assert.Equal(ExclusionOutcome.Applies, Evaluate(Exclusion(UserGuid), member));
        Assert.Equal(ExclusionOutcome.DoesNotApply, Evaluate(Exclusion(Guid.NewGuid()), member));
    }

    [Fact]
    public void Subtree_ContainsTheObjectOrItsPreviousDn()
    {
        var exclusion = Exclusion(subtree: "OU=Kraków,{domain}");

        Assert.Equal(ExclusionOutcome.Applies, Evaluate(exclusion, Create(WatchEventKind.AttributesChanged)));
        Assert.Equal(ExclusionOutcome.Applies, Evaluate(exclusion, Create(WatchEventKind.ObjectMoved, "CN=x,OU=Gdańsk,DC=contoso,DC=test") with { PreviousDn = UserDn }));
        Assert.Equal(ExclusionOutcome.DoesNotApply, Evaluate(exclusion, Create(WatchEventKind.AttributesChanged, "CN=x,OU=Gdańsk,DC=contoso,DC=test")));
        Assert.Equal(ExclusionOutcome.DoesNotApply, Evaluate(Exclusion(subtree: "zły"), Create(WatchEventKind.AttributesChanged)));
    }

    [Fact]
    public void Attribute_RemovesOnlyThatAttributeFromConsideration()
    {
        var department = Exclusion(attribute: "department");

        Assert.Equal(ExclusionOutcome.Applies, Evaluate(department, Create(WatchEventKind.AttributesChanged, changedAttributes: ["Department"])));
        Assert.Equal(ExclusionOutcome.DoesNotApply, Evaluate(department, Create(WatchEventKind.AttributesChanged, changedAttributes: ["department", "scriptPath"])));
        Assert.Equal(ExclusionOutcome.DoesNotApply, Evaluate(department, Create(WatchEventKind.AccountLockedOut)));
    }

    [Fact]
    public void Attribute_ConsidersOnlyTheChangesTheRuleSelects()
    {
        var logonSettings = DefaultRules.Find(DefaultRules.LogonSettings)!;
        var both = Create(WatchEventKind.AttributesChanged, changedAttributes: ["department", "scriptPath"]);

        Assert.Equal(ExclusionOutcome.Applies, Evaluate(Exclusion(attribute: "scriptPath"), both, logonSettings));
        Assert.Equal(ExclusionOutcome.DoesNotApply, Evaluate(Exclusion(attribute: "scriptPath"), Create(WatchEventKind.AttributesChanged, changedAttributes: ["scriptPath", "profilePath"]), logonSettings));
    }

    [Fact]
    public void Actor_ComparesTheResolvedActorSid()
    {
        var sync = Exclusion(actor: SyncSid);

        Assert.Equal(ExclusionOutcome.Applies, Evaluate(sync, Create(WatchEventKind.KeyCredentialChanged, attribution: Resolved(SyncSid))));
        Assert.Equal(ExclusionOutcome.DoesNotApply, Evaluate(sync, Create(WatchEventKind.KeyCredentialChanged, attribution: Resolved(AdminSid))));
        Assert.Equal(ExclusionOutcome.AwaitingActor, Evaluate(sync, Create(WatchEventKind.KeyCredentialChanged)));
        Assert.Equal(ExclusionOutcome.DoesNotApply, Evaluate(sync, Create(WatchEventKind.KeyCredentialChanged), graceExpired: true));
        Assert.Equal(ExclusionOutcome.DoesNotApply, Evaluate(sync, Create(WatchEventKind.KeyCredentialChanged, attribution: Unknown(AttributionReasons.Ambiguous))));
    }

    [Fact]
    public void EveryFieldItSetsMustMatch()
    {
        var combined = Exclusion(subtree: "OU=Kraków,{domain}", attribute: "department", actor: SyncSid);
        var resolved = Resolved(SyncSid);

        Assert.Equal(ExclusionOutcome.Applies, Evaluate(combined, Create(WatchEventKind.AttributesChanged, changedAttributes: ["department"], attribution: resolved)));
        Assert.Equal(ExclusionOutcome.DoesNotApply, Evaluate(combined, Create(WatchEventKind.AttributesChanged, changedAttributes: ["title"], attribution: resolved)));
        Assert.Equal(ExclusionOutcome.DoesNotApply, Evaluate(combined, Create(WatchEventKind.AttributesChanged, "CN=x,OU=Gdańsk,DC=contoso,DC=test", changedAttributes: ["department"], attribution: resolved)));
        Assert.Equal(ExclusionOutcome.AwaitingActor, Evaluate(combined, Create(WatchEventKind.AttributesChanged, changedAttributes: ["department"])));
    }

    [Fact]
    public void ExpiredExclusions_AreIgnored()
    {
        Assert.Equal(ExclusionOutcome.DoesNotApply, Evaluate(Exclusion(UserGuid, expiresAt: Now.AddSeconds(-1)), Create(WatchEventKind.AttributesChanged)));
        Assert.Equal(ExclusionOutcome.Applies, Evaluate(Exclusion(UserGuid, expiresAt: Now), Create(WatchEventKind.AttributesChanged)));
        Assert.Equal(ExclusionOutcome.Applies, Evaluate(Exclusion(UserGuid, expiresAt: Now.AddDays(30)), Create(WatchEventKind.AttributesChanged)));
    }

    [Fact]
    public void AnExclusionWithoutConditions_NeverApplies() =>
        Assert.Equal(ExclusionOutcome.DoesNotApply, Evaluate(Exclusion(), Create(WatchEventKind.AttributesChanged)));
}
