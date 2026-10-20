using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Notifications;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;
using static AdMng.Watchdog.Core.Tests.Notifications.NotificationHarness;

namespace AdMng.Watchdog.Core.Tests.Notifications;

public sealed class FollowUpPlannerTests
{
    private static readonly RuleSettings Privileged = Rule("tier0-privilege", "Dostęp uprzywilejowany", channels: [SiemChannel]);

    [Fact]
    public async Task HighAlertSentWithPendingActor_GetsAFollowUpPerMessage()
    {
        var harness = new NotificationHarness(ConfigWith(Privileged));
        var (e, email) = await SendAlertAsync(harness, Severity.High, AdminsTarget(harness.Current));
        var (_, webhook) = await SendAlertAsync(harness, Severity.High, NotificationTargetResolver.ChannelTarget(harness.Current.Channels[1]), e);
        harness.Clock.Advance(TimeSpan.FromSeconds(40));

        var rows = await harness.FollowUps.PlanAsync(e with { Attribution = Resolved() }, Ct);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.True(r.IsFollowUp);
            Assert.Equal(e.Id, r.EventId);
            Assert.Equal("tier0-privilege", r.RuleId);
            Assert.Equal(Now.AddSeconds(40), r.DueAt);
            Assert.Null(r.HoldForAttributionUntil);
        });
        Assert.Equal(email.MessageId, rows[0].ReplyToMessageId);
        Assert.Equal(AggregationPolicy.FollowUpBucketKey(email.MessageId, rows[0].Target), rows[0].BucketKey);
        Assert.Equal((AdminsTarget(harness.Current).Key, DetailLevel.Full), (rows[0].Target.Key, rows[0].Target.DetailLevel));
        Assert.Equal((webhook.MessageId, DeliveryChannelType.Webhook, DetailLevel.Minimal), (rows[1].ReplyToMessageId, rows[1].Target.ChannelType, rows[1].Target.DetailLevel));
    }

    [Fact]
    public async Task MediumAlert_ResolvedAlert_OrDisabledSetting_HaveNoFollowUp()
    {
        var harness = new NotificationHarness(ConfigWith(Privileged));
        var (medium, _) = await SendAlertAsync(harness, Severity.Medium, AdminsTarget(harness.Current));
        var (high, _) = await SendAlertAsync(harness, Severity.High, AdminsTarget(harness.Current));
        var (resolvedAtSend, _) = await SendAlertAsync(harness, Severity.Critical, AdminsTarget(harness.Current), attribution: Resolved());

        Assert.Empty(await harness.FollowUps.PlanAsync(medium with { Attribution = Resolved() }, Ct));
        Assert.Empty(await harness.FollowUps.PlanAsync(resolvedAtSend, Ct));
        Assert.Empty(await harness.FollowUps.PlanAsync(high, Ct));    // still pending
        harness.Config.Set(harness.Current with { Notifications = new NotificationSettings { FollowUpWhenAttributed = false } });
        Assert.Empty(await harness.FollowUps.PlanAsync(high with { Attribution = Resolved() }, Ct));
    }

    [Fact]
    public async Task FailedAttribution_AlsoGetsAFollowUp()
    {
        var harness = new NotificationHarness(ConfigWith(Privileged));
        var (e, _) = await SendAlertAsync(harness, Severity.Critical, AdminsTarget(harness.Current));

        var rows = await harness.FollowUps.PlanAsync(e with { Attribution = new EventAttribution { Status = AttributionStatus.Unknown } }, Ct);

        Assert.Single(rows);
    }

    [Fact]
    public void Target_OfARemovedGroup_KeepsTheStoredRecipientsWithMinimalDetail()
    {
        var message = new OutboxMessage
        {
            MessageId = Guid.CreateVersion7(Now).ToString("N"),
            ChannelType = DeliveryChannelType.Email,
            ChannelId = "email",
            Kind = DeliveryKind.Alert,
            Recipients = ["byly.zespol@contoso.test"],
            CreatedAt = Now,
            NextAttemptAt = Now,
        };

        var target = FollowUpPlanner.Target(message, ConfigWith());

        Assert.Equal(["byly.zespol@contoso.test"], target.Recipients);
        Assert.Equal(DetailLevel.Minimal, target.DetailLevel);
    }

    [Fact]
    public async Task EachEventOfAnAggregatedAlert_IsFollowedUpWithItsOwnSeverity_NotTheAlertsMaximum()
    {
        // One High alert showed a privilege grant as High and a second one as Medium, both actors pending.
        var harness = new NotificationHarness(ConfigWith(Privileged));
        var high = await harness.StoreAsync(Event(WatchEventKind.PrivilegeGranted, Severity.High, EventAttribution.Pending, "Zofia Ćwiklińska"));
        var medium = await harness.StoreAsync(Event(WatchEventKind.PrivilegeGranted, Severity.Medium, EventAttribution.Pending, "Jędrzej Łęcki"));
        var target = AdminsTarget(harness.Current);
        var alert = NotificationRenderer.Alert(target, Privileged, [high, medium], 0, [high.Id, medium.Id], harness.Current, Now) with
        {
            EventStates = [new(high.Id, Severity.High, true), new(medium.Id, Severity.Medium, true)],
        };
        await harness.Store.EnqueueAsync(alert, Ct);

        var forHigh = Assert.Single(await harness.FollowUps.PlanAsync(high with { Attribution = Resolved() }, Ct));
        var forMedium = Assert.Single(await harness.FollowUps.PlanAsync(medium with { Attribution = Resolved() }, Ct));

        Assert.Equal((Severity.High, Severity.Medium), (forHigh.Severity, forMedium.Severity));
        Assert.Equal(alert.MessageId, forMedium.ReplyToMessageId);
    }

    [Fact]
    public async Task AFollowUp_ShowsTheEventLowered_WhenTheAttributionLoweredIt()
    {
        // A raise is planned per target by the re-evaluation; the follow-up planner only ever lowers to the final severity.
        var harness = new NotificationHarness(ConfigWith(Privileged));
        var (e, _) = await SendAlertAsync(harness, Severity.High, AdminsTarget(harness.Current));

        var lowered = Assert.Single(await harness.FollowUps.PlanAsync(e with { Attribution = Resolved(), Severity = Severity.Info }, Ct));
        var raised = Assert.Single(await harness.FollowUps.PlanAsync(e with { Attribution = Resolved(), Severity = Severity.Critical }, Ct));

        Assert.Equal((Severity.Info, Severity.High), (lowered.Severity, raised.Severity));
    }

    private static async Task<(WatchEvent Event, OutboxMessage Message)> SendAlertAsync(NotificationHarness harness, Severity severity, NotificationTarget target,
        WatchEvent? existing = null, EventAttribution? attribution = null)
    {
        var e = existing ?? await harness.StoreAsync(Event(WatchEventKind.PrivilegeGranted, severity, attribution ?? EventAttribution.Pending));
        var message = NotificationRenderer.Alert(target, Privileged, [e], 0, [e.Id], harness.Current, harness.Clock.GetUtcNow());
        await harness.Store.EnqueueAsync(message, Ct);
        return (e, message);
    }
}
