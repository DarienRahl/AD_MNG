using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Notifications;
using AdMng.Watchdog.Core.Rules;
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
    public async Task SameTargetOfTwoRules_IsOneRowLedByTheFirstRule()
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
    public async Task SameTargetOfTwoRules_GetsTheHighestSeverityAndTheShortestWindow()
    {
        // konta-vip (first) lowers its notifications to Low; hasla-adm notifies the same mailbox with the event's High.
        var first = Rule("konta-vip", "Konta VIP") with { Severity = Severity.Low };
        var second = Rule("hasla-adm", "Hasła administratorów", hours: DeliveryHours.Always);
        var harness = new NotificationHarness(ConfigWith(first, second));

        var (_, planned) = await harness.PlanAsync(Event(severity: Severity.High), new PlannedMatch(first, Severity.Low), new PlannedMatch(second, Severity.High));

        var row = Assert.Single(planned);
        Assert.Equal(("konta-vip", Severity.High, Now.AddSeconds(10)), (row.RuleId, row.Severity, row.DueAt));
        Assert.Equal($"konta-vip|{AdminsTarget(harness.Current).Key}", row.BucketKey);
        Assert.Null(row.HoldForAttributionUntil);
    }

    [Fact]
    public async Task ARuleThatNotifiesAtOnce_DecidesOverOneWaitingForTheActor()
    {
        // konta-vip excludes an actor (provisional while pending, held); hasla-adm notifies the same mailbox at once: the row
        // goes out now with hasla-adm's treatment – the excluded actor cannot silence a rule without that condition.
        var first = Rule("konta-vip", "Konta VIP", hours: DeliveryHours.Always) with { Severity = Severity.Critical };
        var second = Rule("hasla-adm", "Hasła administratorów", hours: DeliveryHours.Always);
        var harness = new NotificationHarness(ConfigWith(first, second));

        var (_, planned) = await harness.PlanAsync(Event(severity: Severity.High, attribution: EventAttribution.Pending),
            new PlannedMatch(first, Severity.Critical, IsProvisional: true), new PlannedMatch(second, Severity.High));

        var row = Assert.Single(planned);
        Assert.Equal(("hasla-adm", Severity.High), (row.RuleId, row.Severity));
        Assert.Null(row.HoldForAttributionUntil);
    }

    [Fact]
    public async Task RulesThatAllWaitForTheActor_HoldOneRowWithTheirStrongestTreatment()
    {
        var first = Rule("konta-vip", "Konta VIP") with { Severity = Severity.Low };
        var second = Rule("opisy-kont", "Opisy kont", hours: DeliveryHours.Always) with
        {
            Delivery = Rule("opisy-kont").Delivery with { Hours = DeliveryHours.Always, AggregationWindow = TimeSpan.FromMinutes(2) },
        };
        var harness = new NotificationHarness(ConfigWith(first, second));

        var (stored, planned) = await harness.PlanAsync(Event(severity: Severity.Medium, attribution: EventAttribution.Pending),
            new PlannedMatch(first, Severity.Low), new PlannedMatch(second, Severity.Medium));

        var row = Assert.Single(planned);
        Assert.Equal(("konta-vip", Severity.Medium, Now.AddMinutes(2)), (row.RuleId, row.Severity, row.DueAt));
        Assert.Equal(stored.DetectedAt.AddSeconds(90), row.HoldForAttributionUntil);
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

    [Theory]
    [InlineData("tier0-badsuccessor")]
    [InlineData("tier0-machine-quota")]
    [InlineData("watchdog-self")]
    [InlineData("tier0-gpo")]
    public async Task RolloutMode_StillNotifiesTheStageBRules(string ruleId)
    {
        var stageB = Rule(ruleId);
        var other = Rule("new-users", groups: [SecurityGroup]);
        var harness = new NotificationHarness(ConfigWith(stageB, other));
        harness.Health.ReportDirectory(new DirectoryStatusDto { State = HealthState.Ok, BaselineCompletedAt = Now.AddDays(-2) });

        var (_, planned) = await harness.PlanAsync(Event(severity: Severity.Critical), new PlannedMatch(stageB, Severity.Critical), new PlannedMatch(other, Severity.Critical));

        Assert.Equal(ruleId, Assert.Single(planned).RuleId);
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

    // ---- Rules added by the re-evaluation after attribution (11.4 #9, PlanReevaluationAsync) ------------------------------

    private static readonly RuleSettings FirstRule = Rule("konta-vip", "Konta VIP", hours: DeliveryHours.Always);
    private static readonly RuleSettings AddedRule = Rule("tier0-security", "Atrybuty krytyczne", hours: DeliveryHours.Always);

    /// <summary>The event alerted under <see cref="FirstRule"/> to the it-admins mailbox (sent after the grace, 5 min).</summary>
    private static async Task<(NotificationHarness Harness, WatchEvent Event, OutboxMessage Alert)> AlertedAsync(Severity severity, EventAttribution attribution,
        WatchdogConfiguration? configuration = null)
    {
        var harness = new NotificationHarness(configuration ?? ConfigWith(FirstRule, AddedRule));
        var (e, _) = await harness.PlanAsync(Event(WatchEventKind.KeyCredentialChanged, severity, attribution), new PlannedMatch(FirstRule, severity));
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await harness.Dispatch.RunOnceAsync(Ct);
        return (harness, e, Assert.Single(await harness.MessagesAsync(e)));
    }

    /// <summary>The new rows of a re-evaluation that added <see cref="AddedRule"/> (the alert went out, nothing waits).</summary>
    private static async Task<IReadOnlyList<PendingNotification>> PlanAddedAsync(NotificationHarness harness, WatchEvent e, Severity severity) =>
        (await PlanAddedRuleAsync(harness, e, severity)).Insert;

    /// <summary>The plan of a re-evaluation that added <see cref="AddedRule"/> (the alert went out, nothing waits).</summary>
    private static async Task<ReevaluationPlan> PlanAddedRuleAsync(NotificationHarness harness, WatchEvent e, Severity severity) =>
        await ((IRuleMatchPlanner)harness.Planner).PlanReevaluationAsync(
            new RuleEvaluationResult
            {
                Event = e with { Severity = severity, OwnSeverity = severity, Attribution = Resolved(), MatchedRules = [FirstRule.Id] },
                Severity = severity,
                Matches = [new RuleMatch { Rule = AddedRule, NotificationSeverity = severity }],
            },
            new HashSet<string>([AddedRule.Id], StringComparer.OrdinalIgnoreCase),
            await harness.Store.GetAllAsync(Ct),
            harness.Current,
            harness.Clock.GetUtcNow(),
            Ct);

    [Fact]
    public async Task Reevaluation_ReplansEveryWaitingRow_FromTheRulesThatMatchNow()
    {
        // While the actor was pending: konta-vip (Low) held the it-admins row, opisy-kont the security row and stara-regula
        // the Teams row. Final state: tier0-security (earlier in the configuration, High) is added for it-admins, opisy-kont
        // still matches, stara-regula no longer does.
        var tier0 = Rule("tier0-security", "Atrybuty krytyczne", hours: DeliveryHours.Always);
        var vip = Rule("konta-vip", "Konta VIP") with { Severity = Severity.Low };
        var descriptions = Rule("opisy-kont", "Opisy kont", groups: [SecurityGroup], hours: DeliveryHours.Always);
        var old = Rule("stara-regula", "Stara reguła", groups: [], channels: [TeamsChannel], hours: DeliveryHours.Always);
        var harness = new NotificationHarness(ConfigWith(tier0, vip, descriptions, old));
        var (e, held) = await harness.PlanAsync(Event(WatchEventKind.KeyCredentialChanged, Severity.Medium, EventAttribution.Pending),
            new PlannedMatch(vip, Severity.Low), new PlannedMatch(descriptions, Severity.Medium), new PlannedMatch(old, Severity.Medium));
        Assert.Equal(3, held.Count);
        var waiting = await harness.Store.GetAllAsync(Ct);
        harness.Clock.Advance(TimeSpan.FromSeconds(30));
        var final = e with { Severity = Severity.High, OwnSeverity = Severity.High, Attribution = Resolved(), MatchedRules = [vip.Id, descriptions.Id, old.Id] };

        var plan = await ((IRuleMatchPlanner)harness.Planner).PlanReevaluationAsync(
            new RuleEvaluationResult
            {
                Event = final,
                Severity = Severity.High,
                Matches =
                [
                    new RuleMatch { Rule = tier0, NotificationSeverity = Severity.High },
                    new RuleMatch { Rule = vip, NotificationSeverity = Severity.Low },
                    new RuleMatch { Rule = descriptions, NotificationSeverity = Severity.High },
                ],
            },
            new HashSet<string>([tier0.Id], StringComparer.OrdinalIgnoreCase),
            waiting,
            harness.Current,
            harness.Clock.GetUtcNow(),
            Ct);

        var admins = waiting.Single(w => w.RuleId == vip.Id);
        var merged = Assert.Single(plan.Merge);
        Assert.Equal((admins.Id, "tier0-security", Severity.High, harness.Clock.GetUtcNow().AddSeconds(10)), (merged.Id, merged.RuleId, merged.Severity, merged.DueAt));
        Assert.Null(merged.HoldForAttributionUntil);
        var updated = Assert.Single(plan.Update);
        Assert.Equal((waiting.Single(w => w.RuleId == descriptions.Id).Id, "opisy-kont", Severity.High), (updated.Id, updated.RuleId, updated.Severity));
        Assert.Null(updated.HoldForAttributionUntil);
        Assert.Equal([waiting.Single(w => w.RuleId == old.Id).Id], plan.Drop);
        Assert.Empty(plan.Insert);

        await harness.Store.ReplanAsync(plan.Merge, insertMissing: true, Ct);
        await harness.Store.ReplanAsync(plan.Update, insertMissing: false, Ct);
        await harness.Store.DropAsync(plan.Drop, Ct);
        var rows = await harness.Store.GetAllAsync(Ct);
        Assert.Equal(["opisy-kont", "tier0-security"], rows.Select(r => r.RuleId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AddedRule_LeavesOutATargetThatGotTheFinalState_ButEscalatesInItsThread()
    {
        var (harness, e, alert) = await AlertedAsync(Severity.Medium, Resolved());
        Assert.False(alert.SentWithPendingAttribution);

        Assert.True((await PlanAddedRuleAsync(harness, e, Severity.Medium)).IsEmpty);
        var plan = await PlanAddedRuleAsync(harness, e, Severity.High);
        Assert.Empty(plan.Insert);
        var escalation = Assert.Single(plan.FollowUps);
        Assert.Equal((AdminsTarget(harness.Current).Key, true, alert.MessageId, Severity.High, AddedRule.Id),
            (escalation.Target.Key, escalation.IsFollowUp, escalation.ReplyToMessageId, escalation.Severity, escalation.RuleId));
        Assert.Equal((harness.Clock.GetUtcNow(), AggregationPolicy.FollowUpBucketKey(alert.MessageId, escalation.Target)), (escalation.DueAt, escalation.BucketKey));
        Assert.Null(escalation.HoldForAttributionUntil);
    }

    [Fact]
    public async Task AddedRule_LeavesOutATargetWhoseAlertGetsTheFollowUp()
    {
        var (harness, e, alert) = await AlertedAsync(Severity.High, EventAttribution.Pending);
        Assert.True(alert.SentWithPendingAttribution);

        Assert.Empty(await PlanAddedAsync(harness, e, Severity.High));
    }

    [Fact]
    public async Task AddedRule_NotifiesWhenNoFollowUpBringsTheActor()
    {
        // Below High there is no follow-up; with follow-ups switched off neither.
        var (medium, mediumEvent, _) = await AlertedAsync(Severity.Medium, EventAttribution.Pending);
        Assert.Single(await PlanAddedAsync(medium, mediumEvent, Severity.Medium));

        var noFollowUps = ConfigWith(FirstRule, AddedRule) with { Notifications = new NotificationSettings { FollowUpWhenAttributed = false } };
        var (high, highEvent, _) = await AlertedAsync(Severity.High, EventAttribution.Pending, noFollowUps);
        Assert.Single(await PlanAddedAsync(high, highEvent, Severity.High));
    }

    [Fact]
    public async Task AddedRule_NotifiesWhenTheEarlierAlertWasLost()
    {
        var (harness, e, alert) = await AlertedAsync(Severity.High, Resolved());
        await harness.Store.LeaseDueAsync(harness.Clock.GetUtcNow(), 100, TimeSpan.FromMinutes(5), Ct);
        await harness.Store.MarkDeadAsync(alert.Id, "smtp.550.5.1.1", null, Ct);

        Assert.Single(await PlanAddedAsync(harness, e, Severity.High));
    }

    // ---- Escalation of a target that already got an alert (13.2 #7) -----------------------------------------------------

    /// <summary>The plan of a re-evaluation in which only the stored <paramref name="rule"/> matches, with
    /// <paramref name="severity"/> (the final attribution changed the own severity).</summary>
    private static async Task<ReevaluationPlan> PlanStoredAsync(NotificationHarness harness, WatchEvent e, RuleSettings rule, Severity severity) =>
        await ((IRuleMatchPlanner)harness.Planner).PlanReevaluationAsync(
            new RuleEvaluationResult
            {
                Event = e with { Severity = severity, OwnSeverity = severity, Attribution = Resolved(), MatchedRules = [rule.Id] },
                Severity = severity,
                Matches = [new RuleMatch { Rule = rule, NotificationSeverity = severity }],
            },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            await harness.Store.GetAllAsync(Ct),
            harness.Current,
            harness.Clock.GetUtcNow(),
            Ct);

    [Fact]
    public async Task StoredRule_EscalatesItsTarget_InTheThreadOfTheAlert_DespiteItsRepeatSuppression()
    {
        // The stored rule's own alert is a notification about the object within its repeat suppression – an escalation of
        // that very alert is no repeat.
        var suppressing = FirstRule with { Delivery = FirstRule.Delivery! with { RepeatSuppression = TimeSpan.FromHours(1) } };
        var (harness, e, alert) = await AlertedAsync(Severity.Medium, EventAttribution.Pending, ConfigWith(suppressing));
        Assert.Equal((Severity.Medium, true), (alert.Severity, alert.SentWithPendingAttribution));

        var plan = await PlanStoredAsync(harness, e, suppressing, Severity.High);

        Assert.Empty(plan.Insert);
        var escalation = Assert.Single(plan.FollowUps);
        Assert.Equal((suppressing.Id, Severity.High, true, alert.MessageId), (escalation.RuleId, escalation.Severity, escalation.IsFollowUp, escalation.ReplyToMessageId));
        Assert.Equal(AdminsTarget(harness.Current).Key, escalation.Target.Key);
    }

    [Theory]
    [InlineData(Severity.Medium, Severity.Medium)]     // unchanged
    [InlineData(Severity.Medium, Severity.Low)]        // lowered: nothing is sent for it
    [InlineData(Severity.Low, Severity.Medium)]        // raised, but below High: the digest reports it
    [InlineData(Severity.High, Severity.High)]         // already High: only the attribution follow-up
    public async Task StoredRule_DoesNotEscalate_BelowHigh_OrWithoutARaise(Severity shown, Severity final)
    {
        var (harness, e, _) = await AlertedAsync(shown, EventAttribution.Pending);

        Assert.True((await PlanStoredAsync(harness, e, FirstRule, final)).IsEmpty);
    }

    [Fact]
    public async Task TheComingFollowUp_TakesTheFinalSeverity_AlsoALowerOne()
    {
        // The High alert gets its attribution follow-up anyway; it must show the event as Medium now, not High.
        var (harness, e, alert) = await AlertedAsync(Severity.High, EventAttribution.Pending);

        var followUp = Assert.Single((await PlanStoredAsync(harness, e, FirstRule, Severity.Medium)).FollowUps);

        Assert.Equal((Severity.Medium, alert.MessageId), (followUp.Severity, followUp.ReplyToMessageId));
        Assert.True((await PlanStoredAsync(harness, e, FirstRule, Severity.High)).IsEmpty);
    }

    [Fact]
    public async Task AnEscalation_MergesIntoTheWaitingFollowUpOfTheTarget()
    {
        // A follow-up row already waits for the target (a pass that stored it was interrupted): the escalation replies to
        // the same message and updates that row – never a second row or a second thread.
        var (harness, e, alert) = await AlertedAsync(Severity.Medium, EventAttribution.Pending);
        var target = AdminsTarget(harness.Current);
        await harness.Store.AddAsync(
        [
            new PendingNotification
            {
                EventId = e.Id, RuleId = FirstRule.Id, Target = target, Severity = Severity.Medium, IsFollowUp = true, ReplyToMessageId = alert.MessageId,
                BucketKey = AggregationPolicy.FollowUpBucketKey(alert.MessageId, target), DueAt = harness.Clock.GetUtcNow(),
            },
        ], Ct);
        var waiting = Assert.Single(await harness.Store.GetAllAsync(Ct));

        var plan = await PlanStoredAsync(harness, e, FirstRule, Severity.Critical);
        await harness.Store.ReplanAsync(plan.FollowUps, insertMissing: true, Ct);

        var row = Assert.Single(await harness.Store.GetAllAsync(Ct));
        Assert.Equal((waiting.Id, Severity.Critical, alert.MessageId, waiting.BucketKey), (row.Id, row.Severity, row.ReplyToMessageId, row.BucketKey));
    }

    [Fact]
    public async Task AnEscalation_IsNotPlannedAgain_OnceAFollowUpShowedTheSeverity_ButAHigherOneIs()
    {
        var (harness, e, alert) = await AlertedAsync(Severity.Medium, EventAttribution.Pending);
        await harness.Store.EnqueueAsync(new OutboxMessage
        {
            MessageId = Guid.CreateVersion7(harness.Clock.GetUtcNow()).ToString("N"),
            ChannelType = DeliveryChannelType.Email,
            ChannelId = "email",
            TargetKey = alert.TargetKey,
            Kind = DeliveryKind.FollowUp,
            Recipients = alert.Recipients,
            RuleId = FirstRule.Id,
            EventIds = [e.Id],
            EventStates = [new OutboxEventState(e.Id, Severity.High, false)],
            Severity = Severity.High,
            CreatedAt = harness.Clock.GetUtcNow(),
            NextAttemptAt = harness.Clock.GetUtcNow(),
        }, Ct);

        Assert.True((await PlanStoredAsync(harness, e, FirstRule, Severity.High)).IsEmpty);
        Assert.Equal(Severity.Critical, Assert.Single((await PlanStoredAsync(harness, e, FirstRule, Severity.Critical)).FollowUps).Severity);
    }

    [Fact]
    public async Task ALostAlert_IsNotEscalated()
    {
        var (harness, e, alert) = await AlertedAsync(Severity.Medium, EventAttribution.Pending);
        await harness.Store.LeaseDueAsync(harness.Clock.GetUtcNow(), 100, TimeSpan.FromMinutes(5), Ct);
        await harness.Store.MarkDeadAsync(alert.Id, "smtp.550.5.1.1", null, Ct);

        Assert.True((await PlanStoredAsync(harness, e, FirstRule, Severity.High)).IsEmpty);
    }
}
