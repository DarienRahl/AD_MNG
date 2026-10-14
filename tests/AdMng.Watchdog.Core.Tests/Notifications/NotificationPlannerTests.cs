using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Notifications;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;
using static AdMng.Watchdog.Core.Tests.Notifications.NotificationHarness;

namespace AdMng.Watchdog.Core.Tests.Notifications;

public sealed class NotificationPlannerTests
{
    [Fact]
    public async Task ImmediateRule_PlansOneRowPerTarget_WithTheFlushDelayOfItsSeverity()
    {
        var rule = Rule("tier0-privilege", channels: [TeamsChannel, SiemChannel]);
        var harness = new NotificationHarness(ConfigWith(rule));

        var (stored, planned) = await harness.PlanAsync(Event(WatchEventKind.PrivilegeGranted, Severity.Critical), new PlannedMatch(rule, Severity.Critical));

        Assert.Equal(3, planned.Count);
        Assert.Equal([DeliveryChannelType.Email, DeliveryChannelType.Teams, DeliveryChannelType.Webhook], planned.Select(p => p.Target.ChannelType));
        Assert.All(planned, p =>
        {
            Assert.Equal(stored.Id, p.EventId);
            Assert.Equal("tier0-privilege", p.RuleId);
            Assert.Equal(Severity.Critical, p.Severity);
            Assert.Equal(Now.AddSeconds(10), p.DueAt);
            Assert.Null(p.HoldForAttributionUntil);
        });
        Assert.Equal(["zbigniew.żak@contoso.test", "łucja.nowak@contoso.test"], planned[0].Target.Recipients);
        Assert.Equal(DetailLevel.Minimal, planned[2].Target.DetailLevel);
        Assert.Equal($"{stored.Id}|{planned[2].Target.Key}", planned[2].BucketKey);
        Assert.Equal($"tier0-privilege|{planned[0].Target.Key}", planned[0].BucketKey);
        Assert.Equal(3, (await harness.Store.GetAllAsync(Ct)).Count);
    }

    [Fact]
    public async Task LowSeverity_UsesTheRuleAggregationWindow()
    {
        var rule = Rule("new-users") with { Delivery = Rule("new-users").Delivery with { AggregationWindow = TimeSpan.FromMinutes(2) } };
        var harness = new NotificationHarness(ConfigWith(rule));

        var (_, planned) = await harness.PlanAsync(Event(severity: Severity.Low), new PlannedMatch(rule, Severity.Low));

        Assert.Equal(Now.AddMinutes(2), Assert.Single(planned).DueAt);
    }

    [Fact]
    public async Task DigestOnlyRule_PlansNothing()
    {
        var rule = Rule("account-attributes", immediate: false);
        var harness = new NotificationHarness(ConfigWith(rule));

        var (_, planned) = await harness.PlanAsync(Event(severity: Severity.Critical), new PlannedMatch(rule, Severity.Critical));

        Assert.Empty(planned);
    }

    [Fact]
    public async Task SameTargetOfTwoRules_IsOwnedByTheFirstRule()
    {
        var first = Rule("tier0-privilege");
        var second = Rule("privileged-changes", channels: [TeamsChannel]);
        var harness = new NotificationHarness(ConfigWith(first, second));

        var (_, planned) = await harness.PlanAsync(Event(severity: Severity.High), new PlannedMatch(first, Severity.High), new PlannedMatch(second, Severity.High));

        Assert.Equal(2, planned.Count);
        Assert.Equal(("tier0-privilege", DeliveryChannelType.Email), (planned[0].RuleId, planned[0].Target.ChannelType));
        Assert.Equal(("privileged-changes", DeliveryChannelType.Teams), (planned[1].RuleId, planned[1].Target.ChannelType));
    }

    [Fact]
    public async Task OutsideBusinessHours_OnlyHighOrAboveIsImmediateByDefault()
    {
        var rule = Rule("account-changes");
        var harness = new NotificationHarness(ConfigWith(rule));
        harness.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 17, 10, 0, 0, TimeSpan.Zero));   // Saturday

        var (_, medium) = await harness.PlanAsync(Event(severity: Severity.Medium), new PlannedMatch(rule, Severity.Medium));
        var (_, high) = await harness.PlanAsync(Event(severity: Severity.High), new PlannedMatch(rule, Severity.High));

        Assert.Empty(medium);
        Assert.Single(high);
    }

    [Fact]
    public async Task BusinessHoursRule_WaitsForTheDigestEvenWhenCritical_AndAlwaysRuleNotifiesAtNight()
    {
        var office = Rule("office", hours: DeliveryHours.BusinessHours);
        var always = Rule("always", groups: [SecurityGroup], hours: DeliveryHours.Always);
        var harness = new NotificationHarness(ConfigWith(office, always));
        harness.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero));     // Wednesday, Independence Day

        var (_, critical) = await harness.PlanAsync(Event(severity: Severity.Critical), new PlannedMatch(office, Severity.Critical));
        var (_, low) = await harness.PlanAsync(Event(severity: Severity.Low), new PlannedMatch(always, Severity.Low));

        Assert.Empty(critical);
        Assert.Single(low);
    }

    [Fact]
    public async Task RolloutMode_LetsOnlyTier0BurstsAndHealthThrough()
    {
        var tier0 = Rule("tier0-privilege");
        var other = Rule("new-users", groups: [SecurityGroup]);
        var harness = new NotificationHarness(ConfigWith(tier0, other));
        harness.Health.ReportDirectory(new DirectoryStatusDto { State = HealthState.Ok, BaselineCompletedAt = Now.AddDays(-2) });

        var (_, planned) = await harness.PlanAsync(Event(severity: Severity.High), new PlannedMatch(tier0, Severity.High), new PlannedMatch(other, Severity.High));

        Assert.Equal("tier0-privilege", Assert.Single(planned).RuleId);
    }

    [Fact]
    public async Task RepeatSuppression_SkipsAnObjectNotifiedWithinThePeriod()
    {
        var rule = Rule("lockouts") with { Delivery = Rule("lockouts").Delivery with { RepeatSuppression = TimeSpan.FromHours(1) } };
        var harness = new NotificationHarness(ConfigWith(rule));
        var objectGuid = Guid.NewGuid();
        var (first, firstPlanned) = await harness.PlanAsync(Event(WatchEventKind.AccountLockedOut, Severity.High, objectGuid: objectGuid), new PlannedMatch(rule, Severity.High));
        var pendingIds = (await harness.Store.GetAllAsync(Ct)).Select(p => p.Id).ToList();
        await harness.Store.CompleteAsync(pendingIds, NotificationRenderer.Alert(firstPlanned[0].Target, rule, [first], 0, [first.Id], harness.Current, Now), Ct);

        harness.Clock.Advance(TimeSpan.FromMinutes(30));
        var (_, again) = await harness.PlanAsync(Event(WatchEventKind.AccountLockedOut, Severity.High, objectGuid: objectGuid, detectedAt: harness.Clock.GetUtcNow()),
            new PlannedMatch(rule, Severity.High));
        var (_, otherObject) = await harness.PlanAsync(Event(WatchEventKind.AccountLockedOut, Severity.High), new PlannedMatch(rule, Severity.High));
        harness.Clock.Advance(TimeSpan.FromMinutes(31));
        var (_, later) = await harness.PlanAsync(Event(WatchEventKind.AccountLockedOut, Severity.High, objectGuid: objectGuid), new PlannedMatch(rule, Severity.High));

        Assert.Empty(again);
        Assert.Single(otherObject);
        Assert.Single(later);
    }

    [Theory]
    [InlineData(WatchEventKind.UserCreated, Severity.Medium, false, true)]
    [InlineData(WatchEventKind.UserCreated, Severity.High, false, false)]
    [InlineData(WatchEventKind.PrivilegeGranted, Severity.Critical, false, false)]
    [InlineData(WatchEventKind.PasswordChanged, Severity.Critical, false, true)]
    [InlineData(WatchEventKind.KeyCredentialChanged, Severity.High, false, true)]
    [InlineData(WatchEventKind.UserCreated, Severity.Critical, true, true)]
    public async Task PendingActor_HoldsLowSeverityAttributionDependentKindsAndProvisionalMatches(WatchEventKind kind, Severity severity, bool provisional, bool held)
    {
        var rule = Rule("rule");
        var harness = new NotificationHarness(ConfigWith(rule));

        var (stored, planned) = await harness.PlanAsync(Event(kind, severity, EventAttribution.Pending, detectedAt: Now.AddSeconds(-20)),
            new PlannedMatch(rule, severity, provisional));

        Assert.Equal(held ? stored.DetectedAt.AddSeconds(90) : null, Assert.Single(planned).HoldForAttributionUntil);
    }

    [Fact]
    public async Task ResolvedActor_IsNeverHeld()
    {
        var rule = Rule("rule");
        var harness = new NotificationHarness(ConfigWith(rule));

        var (_, planned) = await harness.PlanAsync(Event(WatchEventKind.PasswordChanged, Severity.Low), new PlannedMatch(rule, Severity.Low, true));

        Assert.Null(Assert.Single(planned).HoldForAttributionUntil);
    }

    [Fact]
    public void AttributionDependentKinds_AreThePasswordAdminCountAndKeyCredentialKinds() =>
        Assert.Equal(
            [WatchEventKind.PasswordChanged, WatchEventKind.AdminCountSet, WatchEventKind.KeyCredentialChanged],
            NotificationPlanner.AttributionDependentKinds.Order());

    [Fact]
    public async Task SmtpDisabled_LeavesOnlyTheChannels()
    {
        var rule = Rule("tier0-privilege", channels: [TeamsChannel]);
        var harness = new NotificationHarness(ConfigWith(rule) with { Smtp = new SmtpSettings { Enabled = false } });

        var (_, planned) = await harness.PlanAsync(Event(severity: Severity.Critical), new PlannedMatch(rule, Severity.Critical));

        Assert.Equal(DeliveryChannelType.Teams, Assert.Single(planned).Target.ChannelType);
    }

    [Fact]
    public async Task ConfigurationChange_IsPlannedWithThePreviousConfiguration()
    {
        var health = Rule("health", "Stan usługi", groups: ["poprzedni-zespol"]);
        var previous = ConfigWith(health) with
        {
            RecipientGroups = [new RecipientGroup { Id = "poprzedni-zespol", Name = "Poprzedni zespół", Addresses = ["poprzedni.zespol@contoso.test"] }],
        };
        var harness = new NotificationHarness(ConfigWith());     // the new configuration removed the group and the rule
        var changed = await harness.StoreAsync(HealthEvents.Create(WatchEventKind.WatchdogConfigurationChanged, Now, ("code", "config.saved")));

        var planned = await harness.Planner.PlanAsync(changed, [new PlannedMatch(health, Severity.High)], previous, Now, Ct);

        Assert.Equal(["poprzedni.zespol@contoso.test"], Assert.Single(planned).Target.Recipients);
    }
}
