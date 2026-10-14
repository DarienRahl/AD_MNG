using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Notifications;

namespace AdMng.Watchdog.Core.Tests.Notifications;

public sealed class AggregationTests
{
    private static readonly NotificationTarget Helpdesk = new()
    {
        ChannelType = DeliveryChannelType.Email,
        ChannelId = "email",
        Recipients = ["pomoc@contoso.test"],
    };

    private static readonly NotificationTarget Siem = new() { ChannelType = DeliveryChannelType.Webhook, ChannelId = "siem" };

    private static readonly NotificationTarget Teams = new() { ChannelType = DeliveryChannelType.Teams, ChannelId = "teams-it" };

    [Fact]
    public void EmailAndTeams_AggregatePerRuleAndTarget_WebhooksPerEvent()
    {
        Assert.Equal("lockouts|" + Helpdesk.Key, AggregationPolicy.BucketKey("lockouts", Helpdesk, 17));
        Assert.Equal(AggregationPolicy.BucketKey("lockouts", Teams, 17), AggregationPolicy.BucketKey("lockouts", Teams, 18));
        Assert.Equal("17|" + Siem.Key, AggregationPolicy.BucketKey("lockouts", Siem, 17));
        Assert.NotEqual(AggregationPolicy.BucketKey("lockouts", Siem, 17), AggregationPolicy.BucketKey("lockouts", Siem, 18));
    }

    [Fact]
    public void FollowUps_AggregatePerOriginalMessage()
    {
        var key = AggregationPolicy.FollowUpBucketKey("0192f3a45b6c7d8e9f00112233445566", Helpdesk);

        Assert.StartsWith(AggregationPolicy.FollowUpPrefix, key, StringComparison.Ordinal);
        Assert.EndsWith(Helpdesk.Key, key, StringComparison.Ordinal);
        Assert.DoesNotContain("pomoc@", key, StringComparison.Ordinal);
    }

    [Fact]
    public void Window_IsTheHighSeverityDelayForHighAndCritical()
    {
        var settings = new NotificationSettings();
        var rule = new RuleSettings { Id = "lockouts", Name = "Blokady kont", Delivery = new RuleDelivery { AggregationWindow = TimeSpan.FromMinutes(60) } };

        Assert.Equal(TimeSpan.FromSeconds(10), AggregationPolicy.Window(rule, settings, Severity.High));
        Assert.Equal(TimeSpan.FromSeconds(10), AggregationPolicy.Window(rule, settings, Severity.Critical));
        Assert.Equal(TimeSpan.FromMinutes(60), AggregationPolicy.Window(rule, settings, Severity.Medium));
        Assert.Equal(TimeSpan.FromMinutes(5), AggregationPolicy.Window(rule with { Delivery = new RuleDelivery() }, settings, Severity.Low));
        Assert.Equal(TimeSpan.FromMinutes(5), AggregationPolicy.Window(null, null, Severity.Info));
    }

    [Fact]
    public void MaxItems_IsAtLeastOne()
    {
        Assert.Equal(50, AggregationPolicy.MaxItems(new NotificationSettings()));
        Assert.Equal(1, AggregationPolicy.MaxItems(new NotificationSettings { MaxItemsPerMessage = 0 }));
    }
}
