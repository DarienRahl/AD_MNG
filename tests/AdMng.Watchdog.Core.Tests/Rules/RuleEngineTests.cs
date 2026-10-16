using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Rules;
using AdMng.Watchdog.Core.Tests.Rules.Fakes;
using Microsoft.Extensions.Time.Testing;
using static AdMng.Watchdog.Core.Tests.Rules.TestEvents;

namespace AdMng.Watchdog.Core.Tests.Rules;

public class RuleEngineTests
{
    private readonly FakeConfigProvider _config = new();
    private readonly FixedTokenSource _tokens = new(Tokens);
    private readonly RuleEngine _engine;

    public RuleEngineTests() => _engine = new RuleEngine(_config, _tokens, new FakeTimeProvider(Now));

    private static IEnumerable<string> Ids(RuleEvaluationResult result) => result.Matches.Select(m => m.Rule.Id);

    [Fact]
    public void Evaluate_ReturnsEveryMatchingRuleInConfigurationOrder()
    {
        var result = _engine.Evaluate(Create(WatchEventKind.UserCreated));

        Assert.Equal([DefaultRules.NewAccounts], Ids(result));
        Assert.Equal(Severity.Low, result.Severity);
        Assert.Equal([DefaultRules.NewAccounts], result.ToEvaluation().MatchedRules);
    }

    [Fact]
    public void SeverityOverride_RaisesTheEventSeverity_AndAppliesToThatRulesNotificationsOnly()
    {
        var result = _engine.Evaluate(Create(WatchEventKind.AttributesChanged, changedAttributes: ["scriptPath", "department"]));

        Assert.Equal([DefaultRules.LogonSettings, DefaultRules.AccountAttributes], Ids(result));
        Assert.Equal(Severity.Medium, result.Severity);
        Assert.Equal(Severity.Medium, result.Matches[0].NotificationSeverity);
        Assert.Equal(Severity.Info, result.Matches[1].NotificationSeverity);
    }

    [Fact]
    public void ARuleMayLowerItsOwnNotifications_WithoutLoweringTheEvent()
    {
        var c = new DefaultConfigurationFactory().Create();
        _config.Set(c with { Rules = [.. c.Rules, new RuleSettings { Id = "ciche-blokady", Name = "Ciche", Kinds = [WatchEventKind.AccountLockedOut], Severity = Severity.Info }] });

        var result = _engine.Evaluate(Create(WatchEventKind.AccountLockedOut, severity: Severity.High));

        Assert.Equal(Severity.High, result.Severity);
        Assert.Equal(Severity.Info, result.Matches.Single(m => m.Rule.Id == "ciche-blokady").NotificationSeverity);
    }

    [Fact]
    public void HealthEvents_MatchTheHealthRule()
    {
        var result = _engine.Evaluate(Create(WatchEventKind.HealthEventLogGap, "CN=Watchdog", WatchObjectKind.Watchdog));

        Assert.Equal([DefaultRules.Health], Ids(result));
        Assert.Equal(Severity.High, result.Severity);
    }

    [Fact]
    public void Tier0Security_IsProvisionalWhileTheActorIsPending()
    {
        var pending = Create(WatchEventKind.KeyCredentialChanged, severity: Severity.High);

        var result = _engine.Evaluate(pending);

        Assert.Equal([DefaultRules.Tier0Security, DefaultRules.SecurityReview], Ids(result));
        Assert.True(result.Matches[0].IsProvisional);
        Assert.False(result.Matches[1].IsProvisional);
        Assert.Equal([DefaultRules.SecurityReview], Ids(_engine.Evaluate(pending with { Severity = Severity.Medium })));
    }

    [Fact]
    public void Rematch_UsesTheFinalAttributionAndTags()
    {
        var selfChange = Create(WatchEventKind.KeyCredentialChanged, severity: Severity.High, attribution: Resolved(DomainSid + "-6609"), tags: [EventTags.SelfChange]);
        var byAdmin = Create(WatchEventKind.KeyCredentialChanged, severity: Severity.High, attribution: Resolved(AdminSid));

        Assert.Null(_engine.Rematch(selfChange, DefaultRules.Tier0Security, attributionGraceExpired: false));
        var match = _engine.Rematch(byAdmin, DefaultRules.Tier0Security, attributionGraceExpired: false);
        Assert.NotNull(match);
        Assert.False(match.IsProvisional);
        Assert.Null(_engine.Rematch(byAdmin, "nie-istnieje", attributionGraceExpired: false));
        Assert.False(_engine.Rematch(Create(WatchEventKind.KeyCredentialChanged, severity: Severity.High), DefaultRules.Tier0Security, attributionGraceExpired: true)!.IsProvisional);
    }

    [Fact]
    public void Evaluate_WithAnExplicitPreviousConfiguration_UsesItsRules()
    {
        var previous = new WatchdogConfiguration
        {
            Rules = [new RuleSettings { Id = "stara-reguła", Name = "Stara", Kinds = [WatchEventKind.WatchdogConfigurationChanged] }],
        };
        var changed = Create(WatchEventKind.WatchdogConfigurationChanged, "", WatchObjectKind.Watchdog, attribution: Resolved(AdminSid));

        var result = _engine.Evaluate(changed, previous);

        Assert.Equal(["stara-reguła", DefaultRules.Health], Ids(result));   // built-in rules merged as the provider would
    }

    [Fact]
    public void CreateContext_IsCachedPerConfigurationAndTokens()
    {
        var configuration = _config.Current.Configuration;
        var first = _engine.CreateContext(configuration);

        Assert.Same(first, _engine.CreateContext(configuration));
        _tokens.Current = Tokens with { DomainSid = DomainSid.Replace("682003330", "999", StringComparison.Ordinal) };
        Assert.NotSame(first, _engine.CreateContext(configuration));
    }

    [Fact]
    public void Evaluate_ContextOverloadIsUsableWithoutAnEngine()
    {
        var context = RuleMatchContext.Create(WatchdogConfigProvider.Effective(new DefaultConfigurationFactory().Create()), Tokens);

        var result = RuleEngine.Evaluate(Create(WatchEventKind.TrustChanged, "CN=fabrikam.test,CN=System,DC=contoso,DC=test", WatchObjectKind.Trust), context, Now);

        Assert.Equal([DefaultRules.Tier0Domain], Ids(result));
        Assert.Equal(Severity.Critical, result.Severity);
    }
}
