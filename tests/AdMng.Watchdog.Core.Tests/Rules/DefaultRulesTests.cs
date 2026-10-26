using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Rules;

namespace AdMng.Watchdog.Core.Tests.Rules;

public class DefaultRulesTests
{
    [Fact]
    public void Ids_AreUniqueAndValid_AndAllRulesAreBuiltInAndEnabled()
    {
        var rules = DefaultRules.Create();

        Assert.Equal(28, rules.Count);
        Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(rules, r =>
        {
            Assert.Matches("^[a-z0-9][a-z0-9-]{0,59}$", r.Id);
            Assert.True(r.BuiltIn);
            Assert.True(r.Enabled);
            Assert.True(r.Kinds.Count > 0 || r.Id == DefaultRules.WatchdogSelf);   // watchdog-self: every kind of the account
            Assert.True(r.Delivery.IncludeInDigest);
        });
        Assert.Equal(rules.Select(r => r.Id), DefaultRules.Ids);
    }

    [Fact]
    public void HealthRule_IsTheOnlyLockedRule_AndSendsToEveryEnabledChannelAlways()
    {
        var rules = DefaultRules.Create();

        var health = Assert.Single(rules, r => r.Locked);
        Assert.Equal(DefaultRules.Health, health.Id);
        Assert.True(health.Delivery.Immediate);
        Assert.True(health.Delivery.AllEnabledChannels);
        Assert.Equal(DeliveryHours.Always, health.Delivery.Hours);
        Assert.Equal([DefaultRules.ItAdminsGroup], health.Delivery.RecipientGroupIds);
        var healthKinds = EventKindCatalog.All.Where(k => k.IsHealth && k.Kind != WatchEventKind.Test).Select(k => k.Kind);
        Assert.Equal(healthKinds.Order(), health.Kinds.Order());
        Assert.Contains(WatchEventKind.WatchdogConfigurationChanged, health.Kinds);
    }

    [Fact]
    public void EveryKindRecordedByDefault_IsCoveredByAtLeastTheDigest()
    {
        var rules = DefaultRules.Create();
        var recorded = EventKindCatalog.All.Where(k => k.RecordedByDefault && k.Kind != WatchEventKind.Test);

        Assert.All(recorded, kind =>
            Assert.Contains(rules, r => r.Delivery.IncludeInDigest && r.Kinds.Contains(kind.Kind)));
        Assert.DoesNotContain(rules, r => r.Kinds.Contains(WatchEventKind.Test));
    }

    [Fact]
    public void Tier0Rules_NotifyImmediatelyAlways()
    {
        var tier0 = DefaultRules.Create().Where(r => Tier0Catalog.IsTier0Rule(r.Id)).ToList();

        Assert.Equal(
        [
            DefaultRules.Tier0Privilege, DefaultRules.Tier0Security, DefaultRules.Tier0Domain, DefaultRules.Tier0EventLog,
            DefaultRules.Tier0BadSuccessor, DefaultRules.Tier0MachineQuota, DefaultRules.WatchdogSelf, DefaultRules.Tier0Gpo,
        ], tier0.Select(r => r.Id));
        Assert.All(tier0, r =>
        {
            Assert.True(r.Delivery.Immediate);
            Assert.Equal(DeliveryHours.Always, r.Delivery.Hours);
            Assert.Empty(r.Exclusions);
        });
    }

    [Fact]
    public void ShippedConditions_FollowTheTableOfSection12_4()
    {
        var rules = DefaultRules.Create().ToDictionary(r => r.Id);

        Assert.Equal(Severity.High, rules[DefaultRules.Tier0Security].MinSeverity);
        Assert.True(rules[DefaultRules.Tier0Security].Actors.ExcludeSelfChanges);
        Assert.Equal(rules[DefaultRules.Tier0Security].Kinds, rules[DefaultRules.SecurityReview].Kinds);
        Assert.False(rules[DefaultRules.SecurityReview].Delivery.Immediate);
        Assert.Equal(Severity.High, rules[DefaultRules.Tier0Domain].MinSeverity);
        Assert.Equal([EventTags.Privileged, EventTags.ServiceAccount], rules[DefaultRules.PrivilegedAccounts].AnyTags);
        Assert.Contains(WatchEventKind.PasswordExpiring, rules[DefaultRules.PrivilegedAccounts].Kinds);
        Assert.Contains(WatchEventKind.AccountLockedOut, rules[DefaultRules.PrivilegedAccounts].Kinds);

        var lockouts = rules[DefaultRules.Lockouts].Delivery;
        Assert.Equal([DefaultRules.HelpdeskGroup], lockouts.RecipientGroupIds);
        Assert.Equal(DeliveryHours.BusinessHours, lockouts.Hours);
        Assert.Equal(TimeSpan.FromMinutes(5), lockouts.AggregationWindow);
        Assert.Equal(10, lockouts.MaxMessagesPerHour);
        Assert.Equal(TimeSpan.FromHours(1), lockouts.RepeatSuppression);

        Assert.Equal(DefaultRules.LogonSettingsAttributes, rules[DefaultRules.LogonSettings].Attributes);
        Assert.Equal(Severity.Medium, rules[DefaultRules.LogonSettings].Severity);
        Assert.Equal(GroupFilter.WatchList, rules[DefaultRules.WatchListGroups].Groups);
        Assert.Equal(TimeSpan.FromMinutes(60), rules[DefaultRules.Expiry].Delivery.AggregationWindow);
        Assert.False(rules[DefaultRules.Expiry].Delivery.NotifyManager);
        Assert.Equal([EventTags.CreatedByNonAdmin, EventTags.UnexpectedContainer, EventTags.Server], rules[DefaultRules.ComputersUnusual].AnyTags);
    }

    [Fact]
    public void Create_ReturnsNewInstancesEveryTime()
    {
        Assert.NotSame(DefaultRules.Create()[0], DefaultRules.Create()[0]);
        Assert.NotSame(DefaultRules.Find("TIER0-SECURITY"), DefaultRules.Find(DefaultRules.Tier0Security));
        Assert.Equal(DefaultRules.Tier0Security, DefaultRules.Find("TIER0-SECURITY")!.Id);
        Assert.Null(DefaultRules.Find("nie-ma-takiej"));
        Assert.Null(DefaultRules.Find(null));
    }

    [Fact]
    public void MergeMissing_KeepsStoredRulesAndAppendsMissingBuiltIns()
    {
        var editedLockouts = DefaultRules.Find(DefaultRules.Lockouts)! with { Enabled = false, Name = "Blokady – zmienione przez Łukasza" };
        var custom = new RuleSettings { Id = "moja-reguła-łódź", Name = "Własna reguła" };
        RuleSettings[] stored = [custom, editedLockouts];

        var merged = DefaultRules.MergeMissing(stored);

        Assert.Same(custom, merged[0]);
        Assert.Same(editedLockouts, merged[1]);
        Assert.Equal(DefaultRules.Ids.Count + 1, merged.Count);
        Assert.Single(merged, r => r.Id == DefaultRules.Lockouts);
        Assert.Equal(DefaultRules.Ids.Where(id => id != DefaultRules.Lockouts), merged.Skip(2).Select(r => r.Id));
        Assert.All(merged.Skip(2), r => Assert.True(r.Enabled));
    }

    [Fact]
    public void MergeMissing_MatchesIdsCaseInsensitively_AndDropsNullEntries()
    {
        RuleSettings[] stored = [null!, DefaultRules.Find(DefaultRules.Health)! with { Id = "HEALTH" }];

        var merged = DefaultRules.MergeMissing(stored);

        Assert.DoesNotContain(merged, r => r is null);
        Assert.Single(merged, r => string.Equals(r.Id, DefaultRules.Health, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(DefaultRules.Ids.Count, DefaultRules.MergeMissing(null).Count);
    }
}
