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
}
