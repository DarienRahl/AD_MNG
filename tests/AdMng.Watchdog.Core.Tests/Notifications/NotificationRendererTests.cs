using System.Text.Json;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Notifications;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;
using static AdMng.Watchdog.Core.Tests.Notifications.NotificationHarness;

namespace AdMng.Watchdog.Core.Tests.Notifications;

public sealed class NotificationRendererTests
{
    private static readonly RuleSettings NewUsers = Rule("new-users", "Nowe konta", channels: [TeamsChannel, SiemChannel]);

    private static readonly WatchdogConfiguration Config = ConfigWith(NewUsers);

    [Fact]
    public void SingleEmail_IsAnAlertWithTheMessageMetadata()
    {
        var e = Event(WatchEventKind.UserCreated, Severity.High) with { Id = 501 };
        var target = AdminsTarget(Config);

        var message = NotificationRenderer.Alert(target, NewUsers, [e], 0, [e.Id], Config, Now);

        Assert.Matches("^[0-9a-f]{32}$", message.MessageId);
        Assert.Equal(7, Guid.ParseExact(message.MessageId, "N").Version);
        Assert.Equal((DeliveryChannelType.Email, "email", target.Key, DeliveryKind.Alert), (message.ChannelType, message.ChannelId, message.TargetKey, message.Kind));
        Assert.Equal(target.Recipients, message.Recipients);
        Assert.Equal(("new-users", Severity.High, Now, Now), (message.RuleId, message.Severity, message.CreatedAt, message.NextAttemptAt));
        Assert.Equal([501L], message.EventIds);
        Assert.False(message.SentWithPendingAttribution);
        Assert.Null(message.Http);
        Assert.Contains("Grzegorz Brzęczyszczykiewicz", message.Email!.Subject, StringComparison.Ordinal);
        Assert.Equal("high", message.Email.Headers["X-AdMng-Severity"], ignoreCase: true);
    }

    [Fact]
    public void SeveralEmails_AreOneAggregatedAlertMentioningTheRest()
    {
        var events = Enumerable.Range(1, 3).Select(i => Event(name: $"Użytkownik {i}") with { Id = 600 + i }).ToList();

        var message = NotificationRenderer.Alert(AdminsTarget(Config), NewUsers, events, 4, [.. events.Select(e => e.Id), 700, 701, 702, 703],
            Config, Now);

        Assert.Equal(7, message.EventIds.Count);
        Assert.Contains("(+4 kolejnych)", message.Email!.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Użytkownik 1", message.Email.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public void Teams_SingleIsAnAlertCardAndSeveralABurstCard()
    {
        var teams = NotificationTargetResolver.ChannelTarget(Config.Channels[0]);
        var events = Enumerable.Range(1, 5).Select(i => Event(name: $"Użytkownik {i}") with { Id = 800 + i }).ToList();

        var single = NotificationRenderer.Alert(teams, NewUsers, [events[0]], 0, [events[0].Id], Config, Now);
        var burst = NotificationRenderer.Alert(teams, NewUsers, events, 0, [.. events.Select(e => e.Id)], Config, Now);

        Assert.Null(single.Email);
        Assert.Equal("message", Json(single.Http!.Body).GetProperty("type").GetString());
        Assert.Contains("5 zdarzeń: Nowe konta", Text(burst.Http!.Body), StringComparison.Ordinal);
        Assert.DoesNotContain("5 zdarzeń", Text(single.Http.Body), StringComparison.Ordinal);
    }

    [Fact]
    public void Webhook_IsTheEventPayloadWithItsRules()
    {
        var siem = NotificationTargetResolver.ChannelTarget(Config.Channels[1]);
        var e = Event(WatchEventKind.UserCreated) with { Id = 900, MatchedRules = ["new-users", "unknown-rule"] };

        var message = NotificationRenderer.Alert(siem, NewUsers, [e], 0, [e.Id], Config, Now);

        var root = Json(message.Http!.Body);
        Assert.Equal("ad.user.created", root.GetProperty("type").GetString());
        Assert.Equal(["new-users"], root.GetProperty("data").GetProperty("rules").EnumerateArray().Select(r => r.GetProperty("id").GetString()));
        Assert.Equal("ad.user.created", message.Http.WebhookEventType);
    }

    [Fact]
    public void PendingActor_IsFlaggedForFollowUp()
    {
        var e = Event(severity: Severity.High, attribution: EventAttribution.Pending) with { Id = 950 };

        var message = NotificationRenderer.Alert(AdminsTarget(Config), NewUsers, [e], 0, [e.Id], Config, Now);

        Assert.True(message.SentWithPendingAttribution);
        Assert.Equal(message.Email!.Subject, NotificationRenderer.OriginalSubject([e with { Attribution = Resolved() }], NewUsers, Config,
            AdminsTarget(Config).DetailLevel));
    }

    [Fact]
    public void FollowUp_RepliesInTheSameThread()
    {
        var e = Event(severity: Severity.High) with { Id = 960 };
        const string original = "0192f3a45b6c7d8e9f00112233445566";
        var subject = NotificationRenderer.OriginalSubject([e], NewUsers, Config, AdminsTarget(Config).DetailLevel);
        var siem = NotificationTargetResolver.ChannelTarget(Config.Channels[1]);

        var email = NotificationRenderer.FollowUp(AdminsTarget(Config), NewUsers, [e], original, subject, Config, Now);
        var webhook = NotificationRenderer.FollowUp(siem, NewUsers, [e], original, subject, Config, Now);

        Assert.Equal(DeliveryKind.FollowUp, email.Kind);
        Assert.Equal(original, email.Email!.InReplyTo);
        Assert.Equal("Re: " + subject, email.Email.Subject);
        Assert.Equal("ad.attribution.updated", Json(webhook.Http!.Body).GetProperty("type").GetString());
        Assert.False(email.SentWithPendingAttribution);
    }

    [Fact]
    public void EmptyEventList_IsRejected() =>
        Assert.Throws<ArgumentException>(() => NotificationRenderer.Alert(AdminsTarget(Config), NewUsers, [], 0, [], Config, Now));

    private static JsonElement Json(byte[] body) => JsonDocument.Parse(body).RootElement.Clone();

    private static string Text(byte[] body) => JsonSerializer.Serialize(Json(body), new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
