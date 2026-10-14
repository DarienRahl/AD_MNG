using AdMng.Core.ActiveDirectory;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Notifications;
using static AdMng.Watchdog.Core.Tests.Notifications.NotificationHarness;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Watchdog.Core.Tests.Notifications;

public sealed class NotificationTargetResolverTests
{
    private static readonly Guid UserGuid = Guid.Parse("3a6c1d2e-0b4f-4c5d-9e8f-7a6b5c4d3e2f");
    private static readonly Guid ManagerGuid = Guid.Parse("9b8a7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d");

    [Fact]
    public async Task AllEnabledChannels_AddsEveryEnabledChannelOnce()
    {
        var rule = Rule("health", groups: [AdminsGroup, AdminsGroup]) with
        {
            Delivery = Rule("health").Delivery with { AllEnabledChannels = true, ChannelIds = [TeamsChannel] },
        };
        var configuration = ConfigWith(rule) with
        {
            Channels = [.. ConfigWith().Channels, new ChannelSettings { Id = "teams-off", Type = ChannelType.Teams, Name = "Wyłączony", Enabled = false }],
        };
        var harness = new NotificationHarness(configuration);

        var targets = await harness.Targets.ResolveAsync(Event(), rule, configuration, Ct);

        Assert.Equal(["email", TeamsChannel, SiemChannel], targets.Select(t => t.ChannelId));
    }

    [Fact]
    public async Task UnknownOrEmptyGroup_HasNoTarget()
    {
        var configuration = ConfigWith() with
        {
            RecipientGroups = [new RecipientGroup { Id = "pusta", Name = "Pusta grupa", Addresses = [" ", ""] }],
        };
        var harness = new NotificationHarness(configuration);
        var rule = Rule("rule", groups: ["pusta", "nieznana"]);

        Assert.Empty(await harness.Targets.ResolveAsync(Event(), rule, configuration, Ct));
        Assert.Null(NotificationTargetResolver.EmailTarget(configuration, "pusta"));
    }

    [Fact]
    public async Task DeliveryFailing_SkipsTheFailingChannelAndAddsTheDigestRecipients()
    {
        var rule = Rule("health", groups: [AdminsGroup]) with { Delivery = Rule("health").Delivery with { AllEnabledChannels = true } };
        var configuration = ConfigWith(rule);
        var harness = new NotificationHarness(configuration);

        var teamsFailing = await harness.Targets.ResolveAsync(HealthEvent(TeamsChannel), rule, configuration, Ct);
        var emailFailing = await harness.Targets.ResolveAsync(HealthEvent("email"), rule, configuration, Ct);

        Assert.Equal(
            [("email", AdminsTarget(configuration).Key), ("email", NotificationTargetResolver.EmailTarget(configuration, SecurityGroup)!.Key), (SiemChannel, null)],
            teamsFailing.Select(t => (t.ChannelId, t.ChannelType == DeliveryChannelType.Email ? t.Key : null)));
        Assert.Equal([TeamsChannel, SiemChannel], emailFailing.Select(t => t.ChannelId));
    }

    [Fact]
    public async Task NotifyManager_MailsTheManagerOfAnExpiringAccountWithMinimalDetail()
    {
        var rule = Rule("expiry") with { Delivery = Rule("expiry").Delivery with { NotifyManager = true } };
        var configuration = ConfigWith(rule);
        var harness = new NotificationHarness(configuration);
        await harness.Store.CommitAsync(new DetectionCommit
        {
            Upserts =
            [
                Snapshot(UserGuid, "CN=Żaneta Kołodziej,OU=Sprzedaż,DC=contoso,DC=com", (Attr.Manager, "guid:" + ManagerGuid)),
                Snapshot(ManagerGuid, "CN=Bożena Dąbrowska,OU=Sprzedaż,DC=contoso,DC=com", (Attr.Mail, "bozena.dabrowska@contoso.test")),
            ],
        }, Ct);
        var expiring = Event(WatchEventKind.AccountExpiring, Severity.Low, objectGuid: UserGuid);

        var targets = await harness.Targets.ResolveAsync(expiring, rule, configuration, Ct);
        var created = await harness.Targets.ResolveAsync(Event(WatchEventKind.UserCreated, objectGuid: UserGuid), rule, configuration, Ct);

        var manager = targets[^1];
        Assert.Equal(2, targets.Count);
        Assert.Equal(["bozena.dabrowska@contoso.test"], manager.Recipients);
        Assert.Equal(DetailLevel.Minimal, manager.DetailLevel);
        Assert.Single(created);
    }

    [Fact]
    public async Task NotifyManager_ByDn_IgnoresDeletedManagersAndHeaderInjection()
    {
        var rule = Rule("expiry", groups: []) with { Delivery = Rule("expiry", groups: []).Delivery with { NotifyManager = true } };
        var configuration = ConfigWith(rule);
        var harness = new NotificationHarness(configuration);
        const string managerDn = "CN=Bożena Dąbrowska,OU=Sprzedaż,DC=contoso,DC=com";
        await harness.Store.CommitAsync(new DetectionCommit
        {
            Upserts =
            [
                Snapshot(UserGuid, "CN=Żaneta Kołodziej,OU=Sprzedaż,DC=contoso,DC=com", (Attr.Manager, "dn:" + managerDn)),
                Snapshot(ManagerGuid, managerDn, (Attr.Mail, "bozena@contoso.test\r\nBcc: wszyscy@contoso.test")),
            ],
        }, Ct);

        Assert.Empty(await harness.Targets.ResolveAsync(Event(WatchEventKind.PasswordExpiring, objectGuid: UserGuid), rule, configuration, Ct));
    }

    [Fact]
    public void ChannelTarget_KeepsTheChannelDetailLevel()
    {
        var target = NotificationTargetResolver.ChannelTarget(ConfigWith().Channels[1]);

        Assert.Equal((DeliveryChannelType.Webhook, SiemChannel, DetailLevel.Minimal), (target.ChannelType, target.ChannelId, target.DetailLevel));
        Assert.Empty(target.Recipients);
    }

    private static WatchEvent HealthEvent(string channelId) => Event(WatchEventKind.HealthDeliveryFailing, Severity.High) with
    {
        Object = new ObjectRef { Dn = string.Empty, Name = string.Empty, Kind = WatchObjectKind.Watchdog },
        Changes = [new AttributeChange { Attribute = "channel", ValueKind = AttributeValueKind.Text, NewValues = [channelId], NewCount = 1 }],
        Source = DetectionSource.Health,
        Attribution = EventAttribution.NotApplicable,
    };

    private static SnapshotObject Snapshot(Guid guid, string dn, params (string Name, string Value)[] attributes) => new()
    {
        ObjectGuid = guid,
        Dn = dn,
        Kind = WatchObjectKind.User,
        Attributes = attributes.ToDictionary(
            a => a.Name,
            a => new SnapshotAttribute { Values = [a.Value] },
            StringComparer.OrdinalIgnoreCase),
    };
}
