using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Tests.Contracts;

public class QueryStringTests
{
    [Fact]
    public void EventQuery_Defaults_ProduceAnEmptyQueryString()
    {
        Assert.Equal(string.Empty, new EventQuery().ToQueryString());
    }

    [Fact]
    public void EventQuery_RepeatsListKeys_AndOmitsDefaults()
    {
        var query = new EventQuery { After = 1, Kinds = [WatchEventKind.UserCreated, WatchEventKind.GroupMemberAdded], WaitSeconds = 25 };

        Assert.Equal("after=1&kind=UserCreated&kind=GroupMemberAdded&wait=25", query.ToQueryString());
    }

    [Fact]
    public void EventQuery_EscapesValues_AndWritesIso8601Times()
    {
        var query = new EventQuery
        {
            Before = 900,
            Limit = 50,
            Categories = [EventCategory.Security],
            ObjectKinds = [WatchObjectKind.User],
            MinSeverity = Severity.High,
            Text = "Żak & syn",
            Subtree = "OU=Księgowość,DC=contoso,DC=test",
            Tags = [EventTags.Privileged],
            AttributionStatuses = [AttributionStatus.Unknown],
            From = new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.FromHours(2)),
            RuleId = "tier0-security",
            ObjectGuid = Guid.Parse("9b0f4e3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f"),
            ActorSid = "S-1-5-21-1-2-3-1105",
            UnacknowledgedOnly = true,
        };

        Assert.Equal(
            "before=900&limit=50&category=Security&objectKind=User&minSeverity=High"
            + "&text=%C5%BBak%20%26%20syn&subtree=OU%3DKsi%C4%99gowo%C5%9B%C4%87%2CDC%3Dcontoso%2CDC%3Dtest"
            + "&tag=privileged&attributionStatus=Unknown&from=2026-10-14T08%3A00%3A00.0000000%2B02%3A00"
            + "&ruleId=tier0-security&objectGuid=9b0f4e3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f&actorSid=S-1-5-21-1-2-3-1105"
            + "&unacknowledgedOnly=true",
            query.ToQueryString());
    }

    [Fact]
    public void OutboxQuery_WritesOnlySetValues()
    {
        Assert.Equal(string.Empty, new OutboxQuery().ToQueryString());
        Assert.Equal(
            "status=Dead&channelId=teams-it&before=77&limit=20",
            new OutboxQuery { Status = OutboxStatus.Dead, ChannelId = "teams-it", Before = 77, Limit = 20 }.ToQueryString());
    }

    [Fact]
    public void Routes_BuildConcretePaths()
    {
        Assert.Equal("/api/v1/events/42", WatchdogRoutes.EventByIdPath(42));
        Assert.Equal("/api/v1/events/42/ack", WatchdogRoutes.EventAcknowledgePath(42));
        Assert.Equal("/api/v1/test/channels/teams%20it", WatchdogRoutes.TestChannelPath("teams it"));
        Assert.Equal("/api/v1/outbox/7/retry", WatchdogRoutes.OutboxRetryPath(7));
        Assert.Equal(
            "/events/9b0f4e3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f",
            WatchdogRoutes.EventPagePath(Guid.Parse("9b0f4e3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f")));
    }
}
