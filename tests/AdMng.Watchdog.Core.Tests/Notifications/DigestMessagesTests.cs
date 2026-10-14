using System.Text.Json;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Notifications;
using AdMng.Watchdog.Core.Tests.Formatting;
using static AdMng.Watchdog.Core.Tests.Notifications.NotificationHarness;

namespace AdMng.Watchdog.Core.Tests.Notifications;

public sealed class DigestMessagesTests
{
    private static WatchdogConfiguration Configuration()
    {
        var configuration = ConfigWith();
        return configuration with
        {
            Channels =
            [
                .. configuration.Channels,
                new ChannelSettings { Id = "teams-off", Type = ChannelType.Teams, Name = "Wyłączony kanał", Enabled = false },
                new ChannelSettings { Id = "hook-no-digest", Type = ChannelType.Webhook, Name = "Bez raportu", Webhook = new WebhookChannelOptions { IncludeDigest = false } },
            ],
            Digest = new DigestSettings
            {
                RecipientGroupIds = [SecurityGroup, AdminsGroup, SecurityGroup, "nieznana"],
                ChannelIds = [TeamsChannel, SiemChannel, "teams-off", "hook-no-digest"],
            },
        };
    }

    [Fact]
    public void Digest_GoesToEveryRecipientGroupAndDigestChannel()
    {
        var model = DigestSamples.Full();

        var messages = DigestMessages.Create(model, Configuration(), Now);

        Assert.Equal(
            [(DeliveryChannelType.Email, "email"), (DeliveryChannelType.Email, "email"), (DeliveryChannelType.Teams, TeamsChannel), (DeliveryChannelType.Webhook, SiemChannel)],
            messages.Select(m => (m.ChannelType, m.ChannelId)));
        Assert.All(messages, m =>
        {
            Assert.Equal(DeliveryKind.Digest, m.Kind);
            Assert.Equal(model.ShownEventIds, m.EventIds);
            Assert.Equal(Severity.Critical, m.Severity);
            Assert.Null(m.RuleId);
            Assert.Equal((Now, Now), (m.CreatedAt, m.NextAttemptAt));
            Assert.Matches("^[0-9a-f]{32}$", m.MessageId);
        });
        Assert.Equal(["soc@contoso.test"], messages[0].Recipients);
        Assert.Equal(messages.Count, messages.Select(m => m.MessageId).Distinct().Count());
    }

    [Fact]
    public void EmailPerGroup_UsesTheGroupDetailLevel()
    {
        var messages = DigestMessages.Create(DigestSamples.Full(), Configuration(), Now);

        Assert.DoesNotContain("WS-ŻÓŁW-01", messages[0].Email!.TextBody, StringComparison.Ordinal);    // security: Minimal
        Assert.Contains("WS-ŻÓŁW-01", messages[1].Email!.TextBody, StringComparison.Ordinal);          // it-admins: Full
    }

    [Fact]
    public void Webhook_GetsTheCountsAndEventIds()
    {
        var model = DigestSamples.Full();

        var webhook = DigestMessages.Create(model, Configuration(), Now).Single(m => m.ChannelType == DeliveryChannelType.Webhook);

        using var document = JsonDocument.Parse(webhook.Http!.Body);
        var data = document.RootElement.GetProperty("data");
        Assert.Equal("watchdog.digest", document.RootElement.GetProperty("type").GetString());
        Assert.Equal(7, data.GetProperty("counts").GetProperty("total").GetInt32());
        Assert.Equal(model.EventIds.Count, data.GetProperty("eventIds").GetArrayLength());
        Assert.Equal("watchdog.digest", webhook.Http.WebhookEventType);
    }

    [Fact]
    public void WithoutSmtpOrChannels_ThereIsNothingToSend()
    {
        var configuration = Configuration() with { Smtp = new SmtpSettings { Enabled = false }, Digest = new DigestSettings { RecipientGroupIds = [AdminsGroup], ChannelIds = [] } };

        Assert.Empty(DigestMessages.Create(DigestSamples.Full(), configuration, Now));
    }
}
