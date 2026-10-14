using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Notifications;
using AdMng.Watchdog.Testing.Stores;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Watchdog.Core.Tests.Notifications;

public sealed class ThrottleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    private static readonly NotificationTarget Admins = new()
    {
        ChannelType = DeliveryChannelType.Email,
        ChannelId = "email",
        Recipients = ["łucja.nowak@contoso.test"],
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly InMemoryWatchdogStore _store = new(new FakeTimeProvider(Now));

    [Fact]
    public void Cap_IsTheRuleCapOrTheGlobalOne()
    {
        var rule = new RuleSettings { Id = "lockouts", Name = "Blokady kont", Delivery = new RuleDelivery { MaxMessagesPerHour = 3 } };

        Assert.Equal(3, ThrottlePolicy.Cap(rule, new NotificationSettings()));
        Assert.Equal(10, ThrottlePolicy.Cap(rule with { Delivery = new RuleDelivery() }, new NotificationSettings()));
        Assert.Equal(1, ThrottlePolicy.Cap(null, new NotificationSettings { MaxMessagesPerRulePerHour = 0 }));
    }

    [Fact]
    public async Task Exceeded_WhenTheRuleSentTheCapInTheLastHourOverTheChannel()
    {
        var throttle = new ThrottlePolicy(_store);
        await SendAsync("lockouts", "email", Now.AddMinutes(-70));
        await SendAsync("lockouts", "email", Now.AddMinutes(-50));
        await SendAsync("lockouts", "email", Now.AddMinutes(-5));
        await SendAsync("other", "email", Now.AddMinutes(-5));
        await SendAsync("lockouts", "teams-it", Now.AddMinutes(-5));

        Assert.True(await throttle.IsExceededAsync("lockouts", 2, Admins, Severity.Medium, Now, Ct));
        Assert.False(await throttle.IsExceededAsync("lockouts", 3, Admins, Severity.Medium, Now, Ct));
        Assert.False(await throttle.IsExceededAsync("other", 2, Admins, Severity.Medium, Now, Ct));
    }

    [Fact]
    public async Task Critical_IsNeverThrottled()
    {
        var throttle = new ThrottlePolicy(_store);
        await SendAsync("tier0-privilege", "email", Now.AddMinutes(-1));

        Assert.True(await throttle.IsExceededAsync("tier0-privilege", 1, Admins, Severity.High, Now, Ct));
        Assert.False(await throttle.IsExceededAsync("tier0-privilege", 1, Admins, Severity.Critical, Now, Ct));
    }

    private async Task SendAsync(string ruleId, string channelId, DateTimeOffset sentAt)
    {
        var id = await _store.EnqueueAsync(new OutboxMessage
        {
            MessageId = Guid.NewGuid().ToString("N"),
            ChannelType = channelId == "email" ? DeliveryChannelType.Email : DeliveryChannelType.Teams,
            ChannelId = channelId,
            Kind = DeliveryKind.Alert,
            RuleId = ruleId,
            CreatedAt = sentAt,
            NextAttemptAt = sentAt,
        }, Ct);
        await _store.LeaseDueAsync(Now, 100, TimeSpan.FromMinutes(5), Ct);
        await _store.MarkSentAsync(id, sentAt, Ct);
    }
}
