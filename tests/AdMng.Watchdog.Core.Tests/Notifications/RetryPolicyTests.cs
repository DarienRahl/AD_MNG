using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Notifications;

namespace AdMng.Watchdog.Core.Tests.Notifications;

public sealed class RetryPolicyTests
{
    private readonly RetryPolicy _exact = new(new FixedRandom());

    [Fact]
    public void Email_FollowsTheScheduleAndGivesUpAfterAbout22Hours()
    {
        TimeSpan[] expected =
        [
            TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1),
            TimeSpan.FromHours(2), TimeSpan.FromHours(4), TimeSpan.FromHours(6), TimeSpan.FromHours(8),
        ];

        var delays = Enumerable.Range(1, 9).Select(a => _exact.Next(DeliveryChannelType.Email, a)!.Value).ToList();

        Assert.Equal(expected, delays);
        Assert.Null(_exact.Next(DeliveryChannelType.Email, 10));
        Assert.InRange(delays.Aggregate(TimeSpan.Zero, (a, b) => a + b), TimeSpan.FromHours(21), TimeSpan.FromHours(22));
    }

    [Fact]
    public void Teams_GivesUpAfterAbout16Hours()
    {
        var total = Enumerable.Range(1, 9).Aggregate(TimeSpan.Zero, (sum, a) => sum + _exact.Next(DeliveryChannelType.Teams, a)!.Value);

        Assert.Equal(TimeSpan.FromSeconds(5), _exact.Next(DeliveryChannelType.Teams, 1));
        Assert.InRange(total, TimeSpan.FromHours(15), TimeSpan.FromHours(16));
        Assert.Null(_exact.Next(DeliveryChannelType.Teams, 10));
    }

    [Fact]
    public void Webhook_FollowsTheStandardWebhooksSchedule()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), _exact.Next(DeliveryChannelType.Webhook, 1));
        Assert.Equal(TimeSpan.FromMinutes(5), _exact.Next(DeliveryChannelType.Webhook, 2));
        Assert.Equal(TimeSpan.FromHours(24), _exact.Next(DeliveryChannelType.Webhook, 9));
        Assert.Null(_exact.Next(DeliveryChannelType.Webhook, 10));
    }

    [Fact]
    public void Jitter_StaysWithinTwentyPercent()
    {
        var low = new RetryPolicy(new FixedRandom(0)).Next(DeliveryChannelType.Email, 5);
        var high = new RetryPolicy(new FixedRandom(0.999999)).Next(DeliveryChannelType.Email, 5);
        var seeded = new RetryPolicy(new Random(1234));

        Assert.Equal(TimeSpan.FromMinutes(48), low);
        Assert.InRange(high!.Value, TimeSpan.FromMinutes(71.99), TimeSpan.FromMinutes(72));
        for (var i = 0; i < 100; i++)
        {
            Assert.InRange(seeded.Next(DeliveryChannelType.Teams, 3)!.Value, TimeSpan.FromSeconds(96), TimeSpan.FromSeconds(144));
        }
    }

    [Fact]
    public void RetryAfter_LongerThanTheStep_IsHonouredUpToOneHour()
    {
        Assert.Equal(TimeSpan.FromMinutes(3), _exact.Next(DeliveryChannelType.Teams, 1, TimeSpan.FromMinutes(3)));
        Assert.Equal(TimeSpan.FromHours(1), _exact.Next(DeliveryChannelType.Teams, 1, TimeSpan.FromHours(6)));
        Assert.Equal(TimeSpan.FromMinutes(10), _exact.Next(DeliveryChannelType.Teams, 4, TimeSpan.FromSeconds(1)));
        Assert.Equal(TimeSpan.FromHours(8), _exact.Next(DeliveryChannelType.Teams, 9, TimeSpan.FromHours(3)));
        Assert.Null(_exact.Next(DeliveryChannelType.Teams, 10, TimeSpan.FromMinutes(1)));
    }
}
