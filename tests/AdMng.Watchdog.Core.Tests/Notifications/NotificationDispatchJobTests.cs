using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Notifications;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;
using static AdMng.Watchdog.Core.Tests.Notifications.NotificationHarness;

namespace AdMng.Watchdog.Core.Tests.Notifications;

public sealed class NotificationDispatchJobTests
{
    private static readonly RuleSettings NewUsers = Rule("new-users", "Nowe konta", channels: [TeamsChannel, SiemChannel]);

    [Fact]
    public void Job_IsDispatchEveryTenSecondsWakingOnQueuedNotificationsAndAttribution()
    {
        var job = new NotificationHarness().Dispatch;

        Assert.Equal(("dispatch", TimeSpan.FromSeconds(10)), (job.Name, job.DefaultInterval));
        Assert.Equal([WatchdogSignal.NotificationsQueued, WatchdogSignal.AttributionUpdated], job.WakeSignals);
    }

    [Fact]
    public async Task BucketNotDue_StaysQueued_AndTheJobWakesAtItsDueTime()
    {
        var harness = new NotificationHarness(ConfigWith(Rule("tier0-privilege")));
        await harness.PlanAsync(Event(severity: Severity.Critical), new PlannedMatch(harness.Current.Rules[0], Severity.Critical));
        harness.Clock.Advance(TimeSpan.FromSeconds(4));

        var result = await harness.Dispatch.RunOnceAsync(Ct);

        Assert.Equal(TimeSpan.FromSeconds(6), result.NextRunIn);
        Assert.Single(await harness.Store.GetAllAsync(Ct));
    }

    [Fact]
    public async Task DueBucket_BecomesOneAlertPerTarget_AndWakesTheOutbox()
    {
        var harness = new NotificationHarness(ConfigWith(NewUsers));
        using var outboxWake = harness.Signals.Subscribe([WatchdogSignal.OutboxEnqueued]);
        var (e, _) = await harness.PlanAsync(Event(severity: Severity.High), new PlannedMatch(NewUsers, Severity.High));
        harness.Clock.Advance(TimeSpan.FromSeconds(10));

        var result = await harness.Dispatch.RunOnceAsync(Ct);

        var messages = await harness.MessagesAsync(e);
        Assert.False(result.Failed);
        Assert.Equal([DeliveryChannelType.Email, DeliveryChannelType.Teams, DeliveryChannelType.Webhook], messages.Select(m => m.ChannelType).Order());
        Assert.All(messages, m => Assert.Equal((DeliveryKind.Alert, "new-users", Severity.High), (m.Kind, m.RuleId, m.Severity)));
        Assert.Empty(await harness.Store.GetAllAsync(Ct));
        Assert.True(await outboxWake.WaitAsync(TimeSpan.Zero, Ct));
    }

    [Fact]
    public async Task Burst_IsAggregatedUpToTheCap_AndCompletesEveryItem()
    {
        var rule = Rule("new-users", "Nowe konta");
        var harness = new NotificationHarness(ConfigWith(rule) with { Notifications = new NotificationSettings { MaxItemsPerMessage = 3 } });
        var events = new List<WatchEvent>();
        for (var i = 1; i <= 5; i++)
        {
            events.Add((await harness.PlanAsync(Event(severity: Severity.Medium, name: $"Zażółć Gęślą {i}"), new PlannedMatch(rule, Severity.Medium))).Event);
            harness.Clock.Advance(TimeSpan.FromSeconds(30));
        }

        await harness.Dispatch.RunOnceAsync(Ct);
        Assert.Empty(await harness.MessagesAsync([.. events]));    // the first item is due 5 minutes after planning
        harness.Clock.Advance(TimeSpan.FromMinutes(3));
        await harness.Dispatch.RunOnceAsync(Ct);

        var message = Assert.Single(await harness.MessagesAsync([.. events]));
        Assert.Equal(events.Select(e => e.Id), message.EventIds.Order());
        Assert.Contains("Zażółć Gęślą 3", message.Email!.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Zażółć Gęślą 4", message.Email.TextBody, StringComparison.Ordinal);
        Assert.Contains("(+2 kolejnych)", message.Email.TextBody, StringComparison.Ordinal);
        Assert.Empty(await harness.Store.GetAllAsync(Ct));
    }

    [Fact]
    public async Task Webhook_GetsOnePayloadPerEvent()
    {
        var rule = Rule("new-users", "Nowe konta", groups: [], channels: [SiemChannel]);
        var harness = new NotificationHarness(ConfigWith(rule));
        var (first, _) = await harness.PlanAsync(Event(severity: Severity.Medium), new PlannedMatch(rule, Severity.Medium));
        var (second, _) = await harness.PlanAsync(Event(severity: Severity.Medium), new PlannedMatch(rule, Severity.Medium));
        harness.Clock.Advance(TimeSpan.FromMinutes(5));

        await harness.Dispatch.RunOnceAsync(Ct);

        Assert.Equal([first.Id, second.Id], (await harness.MessagesAsync(first, second)).Select(m => Assert.Single(m.EventIds)));
    }

    [Fact]
    public async Task HeldItem_WaitsForTheActor_AndGoesOutOnceItIsResolved()
    {
        var harness = new NotificationHarness(ConfigWith(NewUsers));
        var (e, planned) = await harness.PlanAsync(Event(severity: Severity.High, attribution: EventAttribution.Pending), new PlannedMatch(NewUsers, Severity.High, true));
        Assert.All(planned, p => Assert.Equal(Now.AddSeconds(90), p.HoldForAttributionUntil));
        harness.Clock.Advance(TimeSpan.FromSeconds(10));

        var waiting = await harness.Dispatch.RunOnceAsync(Ct);
        Assert.Empty(await harness.MessagesAsync(e));
        Assert.Equal(TimeSpan.FromSeconds(10), waiting.NextRunIn);

        await harness.Store.UpdateAttributionAsync(e.Id, new AttributionUpdate { Attribution = Resolved() }, Ct);
        await harness.Dispatch.RunOnceAsync(Ct);

        var messages = await harness.MessagesAsync(e);
        Assert.Equal(3, messages.Count);
        Assert.All(messages, m => Assert.False(m.SentWithPendingAttribution));
        Assert.Contains("Paweł Wójcik", messages.Single(m => m.ChannelType == DeliveryChannelType.Email).Email!.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrivilegedOwnPasswordChange_IsDroppedWhenTheActorTurnsOutToBeTheUser()
    {
        var rule = Rule("tier0-password", "Hasła kont uprzywilejowanych") with { Actors = new ActorFilter { ExcludeSelfChanges = true } };
        var harness = new NotificationHarness(ConfigWith(rule));
        var (e, planned) = await harness.PlanAsync(
            Event(WatchEventKind.PasswordChanged, Severity.Critical, EventAttribution.Pending, tags: [EventTags.Privileged]),
            new PlannedMatch(rule, Severity.Critical, IsProvisional: true));
        Assert.NotNull(Assert.Single(planned).HoldForAttributionUntil);

        await harness.Store.UpdateAttributionAsync(e.Id, new AttributionUpdate
        {
            Attribution = Resolved(e.Object.Sid!),
            AddedTags = [EventTags.SelfChange],
            Severity = Severity.Medium,
        }, Ct);
        harness.Clock.Advance(TimeSpan.FromSeconds(10));
        await harness.Dispatch.RunOnceAsync(Ct);

        Assert.Empty(await harness.MessagesAsync(e));
        Assert.Empty(await harness.Store.GetAllAsync(Ct));
    }

    [Fact]
    public async Task GraceExpired_SendsWithThePendingActor_AndTheFollowUpRepliesInTheThread()
    {
        var rule = Rule("tier0-privilege", "Dostęp uprzywilejowany");
        var harness = new NotificationHarness(ConfigWith(rule));
        var (e, _) = await harness.PlanAsync(Event(WatchEventKind.PrivilegeGranted, Severity.Critical, EventAttribution.Pending), new PlannedMatch(rule, Severity.Critical, true));
        harness.Clock.Advance(TimeSpan.FromSeconds(90));

        await harness.Dispatch.RunOnceAsync(Ct);
        var alert = Assert.Single(await harness.MessagesAsync(e));
        Assert.True(alert.SentWithPendingAttribution);
        await harness.MarkSentAsync(alert.Id, harness.Clock.GetUtcNow());

        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        await harness.Store.UpdateAttributionAsync(e.Id, new AttributionUpdate { Attribution = Resolved() }, Ct);
        await harness.Store.AddAsync(await harness.FollowUps.PlanAsync((await harness.Store.GetAsync(e.Id, Ct))!, Ct), Ct);
        await harness.Dispatch.RunOnceAsync(Ct);

        var followUp = (await harness.MessagesAsync(e)).Single(m => m.Kind == DeliveryKind.FollowUp);
        Assert.Equal(alert.MessageId, followUp.Email!.InReplyTo);
        Assert.Equal("Re: " + alert.Email!.Subject, followUp.Email.Subject);
        Assert.Equal(alert.TargetKey, followUp.TargetKey);
    }

    [Fact]
    public async Task ReleasedOutsideDeliveryHours_IsLeftToTheDigest()
    {
        var rule = Rule("new-users", "Nowe konta");
        var harness = new NotificationHarness(ConfigWith(rule));
        harness.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 16, 14, 58, 0, TimeSpan.Zero));     // Friday 16:58 in Warsaw
        var (e, _) = await harness.PlanAsync(Event(severity: Severity.Medium, attribution: EventAttribution.Pending, detectedAt: harness.Clock.GetUtcNow()),
            new PlannedMatch(rule, Severity.Medium));
        harness.Clock.Advance(TimeSpan.FromMinutes(5));

        await harness.Dispatch.RunOnceAsync(Ct);

        Assert.Empty(await harness.MessagesAsync(e));
        Assert.Empty(await harness.Store.GetAllAsync(Ct));
    }

    [Fact]
    public async Task ThrottledRule_KeepsCollectingForFifteenMinutes_UntilACriticalItemFlushesTheBucket()
    {
        var rule = Rule("lockouts", "Blokady kont") with { Delivery = Rule("lockouts").Delivery with { MaxMessagesPerHour = 1 } };
        var harness = new NotificationHarness(ConfigWith(rule));
        var earlier = await harness.StoreAsync(Event(WatchEventKind.AccountLockedOut, Severity.High));
        var sentId = await harness.Store.EnqueueAsync(NotificationRenderer.Alert(AdminsTarget(harness.Current), rule, [earlier], 0, [earlier.Id], harness.Current, Now), Ct);
        await harness.MarkSentAsync(sentId, Now);
        var (high, _) = await harness.PlanAsync(Event(WatchEventKind.AccountLockedOut, Severity.High), new PlannedMatch(rule, Severity.High));
        harness.Clock.Advance(TimeSpan.FromSeconds(10));

        await harness.Dispatch.RunOnceAsync(Ct);

        Assert.Empty(await harness.MessagesAsync(high));
        Assert.Equal(Now.AddSeconds(10) + TimeSpan.FromMinutes(15), Assert.Single(await harness.Store.GetAllAsync(Ct)).DueAt);

        var (critical, _) = await harness.PlanAsync(Event(WatchEventKind.AccountLockedOut, Severity.Critical), new PlannedMatch(rule, Severity.Critical));
        harness.Clock.Advance(TimeSpan.FromSeconds(10));
        await harness.Dispatch.RunOnceAsync(Ct);

        var message = Assert.Single(await harness.MessagesAsync(high, critical));
        Assert.Equal([high.Id, critical.Id], message.EventIds.Order());
        Assert.Equal(Severity.Critical, message.Severity);
    }

    [Fact]
    public async Task RuleRemovedFromTheConfiguration_DropsItsBucket()
    {
        var harness = new NotificationHarness(ConfigWith(NewUsers));
        var (e, _) = await harness.PlanAsync(Event(severity: Severity.High), new PlannedMatch(NewUsers, Severity.High));
        harness.Config.Set(ConfigWith());
        harness.Clock.Advance(TimeSpan.FromSeconds(10));

        var result = await harness.Dispatch.RunOnceAsync(Ct);

        Assert.False(result.Failed);
        Assert.Empty(await harness.MessagesAsync(e));
        Assert.Empty(await harness.Store.GetAllAsync(Ct));
    }

    [Fact]
    public async Task RolloutStartedWhileHeld_DropsNonTier0Items()
    {
        var harness = new NotificationHarness(ConfigWith(NewUsers));
        var (e, _) = await harness.PlanAsync(Event(severity: Severity.Medium, attribution: EventAttribution.Pending), new PlannedMatch(NewUsers, Severity.Medium));
        harness.Health.ReportDirectory(new DirectoryStatusDto { State = HealthState.Ok, BaselineCompletedAt = Now });
        harness.Clock.Advance(TimeSpan.FromMinutes(5));

        await harness.Dispatch.RunOnceAsync(Ct);

        Assert.Empty(await harness.MessagesAsync(e));
        Assert.Empty(await harness.Store.GetAllAsync(Ct));
    }

    [Fact]
    public async Task AggregatedItemDueAfterBusinessHours_IsLeftToTheDigest()
    {
        var rule = Rule("new-users", "Nowe konta");      // Default hours: Medium only in business hours
        var harness = new NotificationHarness(ConfigWith(rule));
        harness.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 14, 14, 58, 0, TimeSpan.Zero));     // Wednesday 16:58 in Warsaw
        var (e, _) = await harness.PlanAsync(Event(severity: Severity.Medium, detectedAt: harness.Clock.GetUtcNow()), new PlannedMatch(rule, Severity.Medium));
        harness.Clock.Advance(TimeSpan.FromMinutes(5));                                          // 17:03

        await harness.Dispatch.RunOnceAsync(Ct);

        Assert.Empty(await harness.MessagesAsync(e));
        Assert.Empty(await harness.Store.GetAllAsync(Ct));
    }

    [Fact]
    public async Task ThrottledBucketOfABusinessHoursRule_IsNotFlushedAfterHours()
    {
        var rule = Rule("lockouts", "Blokady kont", hours: DeliveryHours.BusinessHours) with
        {
            Delivery = Rule("lockouts", hours: DeliveryHours.BusinessHours).Delivery with { MaxMessagesPerHour = 1 },
        };
        var harness = new NotificationHarness(ConfigWith(rule));
        harness.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 14, 14, 50, 0, TimeSpan.Zero));     // Wednesday 16:50 in Warsaw
        var earlier = await harness.StoreAsync(Event(WatchEventKind.AccountLockedOut, Severity.High));
        var sentId = await harness.Store.EnqueueAsync(
            NotificationRenderer.Alert(AdminsTarget(harness.Current), rule, [earlier], 0, [earlier.Id], harness.Current, harness.Clock.GetUtcNow()), Ct);
        await harness.MarkSentAsync(sentId, harness.Clock.GetUtcNow());
        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        var (late, _) = await harness.PlanAsync(Event(WatchEventKind.AccountLockedOut, Severity.High), new PlannedMatch(rule, Severity.High));
        harness.Clock.Advance(TimeSpan.FromSeconds(10));
        await harness.Dispatch.RunOnceAsync(Ct);                                                 // throttled until 17:07
        Assert.Single(await harness.Store.GetAllAsync(Ct));

        harness.Clock.Advance(ThrottlePolicy.RescheduleDelay);
        await harness.Dispatch.RunOnceAsync(Ct);

        Assert.Empty(await harness.MessagesAsync(late));
        Assert.Empty(await harness.Store.GetAllAsync(Ct));
    }

    [Fact]
    public async Task HourlyCap_CountsMessagesStillWaitingForDelivery()
    {
        // SMTP is down: nothing is Sent, yet the rule must not enqueue a new message every flush.
        var rule = Rule("new-users", "Nowe konta") with { Delivery = Rule("new-users").Delivery with { MaxMessagesPerHour = 2 } };
        var harness = new NotificationHarness(ConfigWith(rule));
        var events = new List<WatchEvent>();
        for (var i = 0; i < 6; i++)
        {
            events.Add((await harness.PlanAsync(Event(severity: Severity.Medium, name: $"Łukasz Żółć {i}"), new PlannedMatch(rule, Severity.Medium))).Event);
            harness.Clock.Advance(TimeSpan.FromMinutes(6));
            await harness.Dispatch.RunOnceAsync(Ct);
        }

        Assert.Equal(2, (await harness.MessagesAsync([.. events])).Count);
        Assert.Equal(4, (await harness.Store.GetAllAsync(Ct)).Count);
    }

    [Fact]
    public async Task FollowUp_WaitsUntilTheOriginalIsSent()
    {
        var rule = Rule("tier0-privilege", "Dostęp uprzywilejowany");
        var harness = new NotificationHarness(ConfigWith(rule));
        var (e, alert) = await SendWithPendingActorAsync(harness, rule);
        await harness.Store.UpdateAttributionAsync(e.Id, new AttributionUpdate { Attribution = Resolved() }, Ct);
        await harness.Store.AddAsync(await harness.FollowUps.PlanAsync((await harness.Store.GetAsync(e.Id, Ct))!, Ct), Ct);

        await harness.Dispatch.RunOnceAsync(Ct);                  // the original still waits in the outbox (e.g. 421, retry in 2 h)

        Assert.DoesNotContain(await harness.MessagesAsync(e), m => m.Kind == DeliveryKind.FollowUp);
        var waiting = Assert.Single(await harness.Store.GetAllAsync(Ct));
        Assert.True(waiting.DueAt > harness.Clock.GetUtcNow());

        await harness.MarkSentAsync(alert.Id, harness.Clock.GetUtcNow());
        harness.Clock.Advance(NotificationDispatchJob.FollowUpWait);
        await harness.Dispatch.RunOnceAsync(Ct);

        var followUp = Assert.Single(await harness.MessagesAsync(e), m => m.Kind == DeliveryKind.FollowUp);
        Assert.Equal(alert.MessageId, followUp.Email!.InReplyTo);
        Assert.Empty(await harness.Store.GetAllAsync(Ct));
    }

    [Fact]
    public async Task FollowUp_OfADeadOriginal_IsDropped()
    {
        var rule = Rule("tier0-privilege", "Dostęp uprzywilejowany");
        var harness = new NotificationHarness(ConfigWith(rule));
        var (e, alert) = await SendWithPendingActorAsync(harness, rule);
        await harness.Store.LeaseDueAsync(harness.Clock.GetUtcNow(), 100, TimeSpan.FromMinutes(5), Ct);
        await harness.Store.MarkDeadAsync(alert.Id, "smtp.550.5.1.1", null, Ct);
        await harness.Store.UpdateAttributionAsync(e.Id, new AttributionUpdate { Attribution = Resolved() }, Ct);
        await harness.Store.AddAsync(await harness.FollowUps.PlanAsync((await harness.Store.GetAsync(e.Id, Ct))!, Ct), Ct);

        await harness.Dispatch.RunOnceAsync(Ct);

        Assert.DoesNotContain(await harness.MessagesAsync(e), m => m.Kind == DeliveryKind.FollowUp);
        Assert.Empty(await harness.Store.GetAllAsync(Ct));
    }

    [Fact]
    public async Task AttributionResolvedWhileTheAlertIsRendered_StillGetsItsFollowUp()
    {
        var rule = Rule("tier0-privilege", "Dostęp uprzywilejowany");
        var harness = new NotificationHarness(ConfigWith(rule));
        var (e, _) = await harness.PlanAsync(Event(WatchEventKind.PrivilegeGranted, Severity.Critical, EventAttribution.Pending), new PlannedMatch(rule, Severity.Critical, true));
        harness.Clock.Advance(TimeSpan.FromSeconds(90));
        var plannedByTheAttributionEngine = -1;
        var racing = new RacingQueue(harness.Store, async () =>
        {
            // The AttributionEngine commits the actor and plans follow-ups before the alert is in the outbox.
            await harness.Store.UpdateAttributionAsync(e.Id, new AttributionUpdate { Attribution = Resolved() }, Ct);
            var rows = await harness.FollowUps.PlanAsync((await harness.Store.GetAsync(e.Id, Ct))!, Ct);
            plannedByTheAttributionEngine = rows.Count;
            await harness.Store.AddAsync(rows, Ct);
        });
        var dispatch = new NotificationDispatchJob(racing, harness.Store, harness.Store, harness.Config, harness.Matcher, harness.Planner, harness.Rollout,
            new ThrottlePolicy(harness.Store), harness.Signals, harness.Clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationDispatchJob>.Instance);

        await dispatch.RunOnceAsync(Ct);

        Assert.Equal(0, plannedByTheAttributionEngine);
        var alert = Assert.Single(await harness.MessagesAsync(e));
        Assert.True(alert.SentWithPendingAttribution);
        var followUp = Assert.Single(await harness.Store.GetAllAsync(Ct));
        Assert.Equal((true, alert.MessageId), (followUp.IsFollowUp, followUp.ReplyToMessageId));

        await harness.MarkSentAsync(alert.Id, harness.Clock.GetUtcNow());
        await harness.Dispatch.RunOnceAsync(Ct);
        Assert.Single(await harness.MessagesAsync(e), m => m.Kind == DeliveryKind.FollowUp);
    }

    [Fact]
    public async Task FollowUpPlannedTwice_IsSentOnce()
    {
        var rule = Rule("tier0-privilege", "Dostęp uprzywilejowany");
        var harness = new NotificationHarness(ConfigWith(rule));
        var (e, alert) = await SendWithPendingActorAsync(harness, rule);
        await harness.MarkSentAsync(alert.Id, harness.Clock.GetUtcNow());
        await harness.Store.UpdateAttributionAsync(e.Id, new AttributionUpdate { Attribution = Resolved() }, Ct);
        var resolved = (await harness.Store.GetAsync(e.Id, Ct))!;
        await harness.Store.AddAsync(await harness.FollowUps.PlanAsync(resolved, Ct), Ct);
        await harness.Dispatch.RunOnceAsync(Ct);

        await harness.Store.AddAsync(await harness.FollowUps.PlanAsync(resolved, Ct), Ct);     // the other side of the race, later
        await harness.Dispatch.RunOnceAsync(Ct);

        Assert.Single(await harness.MessagesAsync(e), m => m.Kind == DeliveryKind.FollowUp);
        Assert.Empty(await harness.Store.GetAllAsync(Ct));
    }

    [Fact]
    public async Task RuleSeverityOverride_IsTheSeverityOfTheMessage()
    {
        var rule = Rule("watchlist-groups", "Grupy obserwowane") with { Severity = Severity.Critical };
        var harness = new NotificationHarness(ConfigWith(rule));
        var (e, _) = await harness.PlanAsync(Event(WatchEventKind.GroupMemberAdded, Severity.Medium), new PlannedMatch(rule, Severity.Critical));
        harness.Clock.Advance(TimeSpan.FromSeconds(10));

        await harness.Dispatch.RunOnceAsync(Ct);

        var message = Assert.Single(await harness.MessagesAsync(e));
        Assert.Equal(Severity.Critical, message.Severity);
        Assert.StartsWith("[AD Watchdog][KRYTYCZNE]", message.Email!.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RowOfAnEventAlreadyAlertedToTheTarget_IsDroppedUnlessItEscalates()
    {
        // 13.2 #1: one alert per event and target – a row inserted after the earlier alert was completed (re-evaluation,
        // second planning of a configuration change) is checked again when it is flushed.
        var first = Rule("konta-vip", "Konta VIP");
        var second = Rule("hasla", "Hasła");
        var harness = new NotificationHarness(ConfigWith(first, second));
        var (e, _) = await harness.PlanAsync(Event(WatchEventKind.PasswordChanged, Severity.Medium), new PlannedMatch(first, Severity.Medium));
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await harness.Dispatch.RunOnceAsync(Ct);
        var alert = Assert.Single(await harness.MessagesAsync(e));
        Assert.Equal([new OutboxEventState(e.Id, Severity.Medium, false)], alert.EventStates);

        var repeat = await harness.Planner.PlanAsync(e, [new PlannedMatch(second, Severity.Medium)], harness.Current, harness.Clock.GetUtcNow(), Ct);
        await harness.Store.AddAsync(repeat, Ct);
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await harness.Dispatch.RunOnceAsync(Ct);
        Assert.Single(await harness.MessagesAsync(e));
        Assert.Empty(await harness.Store.GetAllAsync(Ct));

        var escalation = await harness.Planner.PlanAsync(e, [new PlannedMatch(second, Severity.High)], harness.Current, harness.Clock.GetUtcNow(), Ct);
        await harness.Store.AddAsync(escalation, Ct);
        harness.Clock.Advance(TimeSpan.FromSeconds(10));
        await harness.Dispatch.RunOnceAsync(Ct);
        var messages = await harness.MessagesAsync(e);
        Assert.Equal([Severity.Medium, Severity.High], messages.Select(m => m.Severity));
    }

    [Fact]
    public async Task ReleasedRowWhoseRuleNoLongerMatches_GoesOutUnderTheNextRuleThatStillMatches()
    {
        // konta-vip (first, excludes Paweł Wójcik) led the held row; opisy-kont matched the same mailbox. The actor is the
        // excluded administrator: the target passes to opisy-kont instead of being dropped.
        var vip = Rule("konta-vip", "Konta VIP", hours: DeliveryHours.Always) with
        {
            Actors = new ActorFilter { ExcludeActors = [Resolved().Actor!.Sid!] },
        };
        var descriptions = Rule("opisy-kont", "Opisy kont", hours: DeliveryHours.Always);
        var harness = new NotificationHarness(ConfigWith(vip, descriptions));
        var (e, planned) = await harness.PlanAsync(Event(WatchEventKind.AttributesChanged, Severity.Medium, EventAttribution.Pending),
            new PlannedMatch(vip, Severity.Medium, IsProvisional: true), new PlannedMatch(descriptions, Severity.Medium));
        Assert.Equal("konta-vip", Assert.Single(planned).RuleId);
        await harness.Store.UpdateAttributionAsync(e.Id, new AttributionUpdate { Attribution = Resolved() }, Ct);
        harness.Clock.Advance(TimeSpan.FromMinutes(5));

        var result = await harness.Dispatch.RunOnceAsync(Ct);

        Assert.False(result.Failed);
        var alert = Assert.Single(await harness.MessagesAsync(e));
        Assert.Equal((DeliveryKind.Alert, "opisy-kont", Severity.Medium), (alert.Kind, alert.RuleId, alert.Severity));
        Assert.Empty(await harness.Store.GetAllAsync(Ct));
    }

    [Fact]
    public async Task RowWhoseRuleLeftItsDeliveryHours_GoesOutUnderARuleThatAlwaysDelivers()
    {
        // 16:58: konta-vip (business hours) leads the row with opisy-kont (Always) for the same mailbox; the window ends at
        // 17:03, after business hours – opisy-kont still delivers, so the alert goes out instead of only the digest.
        var vip = Rule("konta-vip", "Konta VIP", hours: DeliveryHours.BusinessHours);
        var descriptions = Rule("opisy-kont", "Opisy kont", hours: DeliveryHours.Always);
        var harness = new NotificationHarness(ConfigWith(vip, descriptions));
        harness.Clock.Advance(TimeSpan.FromHours(6) + TimeSpan.FromMinutes(58));
        var (e, planned) = await harness.PlanAsync(Event(WatchEventKind.AttributesChanged, Severity.Medium, detectedAt: harness.Clock.GetUtcNow()),
            new PlannedMatch(vip, Severity.Medium), new PlannedMatch(descriptions, Severity.Medium));
        Assert.Equal("konta-vip", Assert.Single(planned).RuleId);
        harness.Clock.Advance(TimeSpan.FromMinutes(5));

        await harness.Dispatch.RunOnceAsync(Ct);

        var alert = Assert.Single(await harness.MessagesAsync(e));
        Assert.Equal(("opisy-kont", Severity.Medium), (alert.RuleId, alert.Severity));
        Assert.Empty(await harness.Store.GetAllAsync(Ct));
    }

    [Fact]
    public async Task AggregatedAlert_RecordsHowItShowedEachEvent()
    {
        var rule = Rule("new-users", "Nowe konta");
        var harness = new NotificationHarness(ConfigWith(rule));
        var (high, _) = await harness.PlanAsync(Event(severity: Severity.High), new PlannedMatch(rule, Severity.High));
        var (low, _) = await harness.PlanAsync(Event(severity: Severity.Low, name: "Żaneta Łęcka"), new PlannedMatch(rule, Severity.Low));
        harness.Clock.Advance(TimeSpan.FromSeconds(10));

        await harness.Dispatch.RunOnceAsync(Ct);

        var alert = Assert.Single(await harness.MessagesAsync(high, low));
        Assert.Equal(Severity.High, alert.Severity);
        Assert.Equal([new OutboxEventState(high.Id, Severity.High, false), new OutboxEventState(low.Id, Severity.Low, false)], alert.EventStates);
        Assert.False(AlertCoverage.Covers(alert, low.Id, Severity.Medium, harness.Current));
        Assert.True(AlertCoverage.Covers(alert, high.Id, Severity.High, harness.Current));
    }

    /// <summary>A Critical alert sent with the actor pending (grace expired); the outbox row is still Pending.</summary>
    private static async Task<(WatchEvent Event, OutboxMessage Alert)> SendWithPendingActorAsync(NotificationHarness harness, RuleSettings rule)
    {
        var (e, _) = await harness.PlanAsync(Event(WatchEventKind.PrivilegeGranted, Severity.Critical, EventAttribution.Pending), new PlannedMatch(rule, Severity.Critical, true));
        harness.Clock.Advance(TimeSpan.FromSeconds(90));
        await harness.Dispatch.RunOnceAsync(Ct);
        var alert = Assert.Single(await harness.MessagesAsync(e));
        Assert.True(alert.SentWithPendingAttribution);
        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        return (e, alert);
    }

    /// <summary>The queue store with a hook that runs just before the dispatch job commits its message.</summary>
    private sealed class RacingQueue(INotificationQueueStore inner, Func<Task> beforeComplete) : INotificationQueueStore
    {
        private Func<Task>? _beforeComplete = beforeComplete;

        public Task<IReadOnlyList<PendingNotification>> GetAllAsync(CancellationToken cancellationToken) => inner.GetAllAsync(cancellationToken);

        public Task AddAsync(IReadOnlyList<PendingNotification> notifications, CancellationToken cancellationToken) => inner.AddAsync(notifications, cancellationToken);

        public Task ReplanAsync(IReadOnlyList<PendingNotification> notifications, bool insertMissing, CancellationToken cancellationToken) =>
            inner.ReplanAsync(notifications, insertMissing, cancellationToken);

        public async Task CompleteAsync(IReadOnlyList<long> pendingIds, OutboxMessage? message, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _beforeComplete, null) is { } hook)
            {
                await hook();
            }

            await inner.CompleteAsync(pendingIds, message, cancellationToken);
        }

        public Task DropAsync(IReadOnlyList<long> pendingIds, CancellationToken cancellationToken) => inner.DropAsync(pendingIds, cancellationToken);

        public Task RescheduleAsync(IReadOnlyList<long> pendingIds, DateTimeOffset dueAt, CancellationToken cancellationToken) =>
            inner.RescheduleAsync(pendingIds, dueAt, cancellationToken);
    }
}
