using System.Text.Json;
using System.Text.Json.Nodes;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Formatting.Webhooks;

namespace AdMng.Watchdog.Core.Tests.Formatting.Webhooks;

public sealed class WebhookPayloadFormatterTests
{
    [Fact]
    public void EventPayload_HasTheDocumentedShape()
    {
        var message = WebhookPayloadFormatter.Event(FormattingSamples.AttributesChanged(), [FormattingSamples.Rule("account-attributes", "Zmiany parametrów kont")],
            FormattingSamples.Context(level: DetailLevel.Standard));

        var root = JsonNode.Parse(message.Body)!;
        Assert.Equal("ad.user.attributes_changed", root["type"]!.GetValue<string>());
        Assert.Equal("ad.user.attributes_changed", message.WebhookEventType);
        Assert.Equal("2026-10-14T12:32:05Z", root["timestamp"]!.GetValue<string>());
        var data = root["data"]!;
        Assert.Equal(1, data["schemaVersion"]!.GetValue<int>());
        Assert.Equal("0192f3a4-5b6c-7d8e-9f00-112233445567", data["eventId"]!.GetValue<string>());
        Assert.Equal(18343, data["id"]!.GetValue<long>());
        Assert.Equal("AttributesChanged", data["kind"]!.GetValue<string>());
        Assert.Equal("info", data["severity"]!.GetValue<string>());
        Assert.Equal("contoso.com", data["domain"]!.GetValue<string>());
        Assert.Equal("Standard", data["detailLevel"]!.GetValue<string>());
        Assert.Equal("Zmiany parametrów kont", data["rules"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("lwisniewski", data["object"]!["sAMAccountName"]!.GetValue<string>());
        Assert.Equal("User", data["object"]!["kind"]!.GetValue<string>());
        Assert.Null(data["previousDn"]);
        Assert.Null(data["member"]);
        var changes = data["changes"]!.AsArray();
        Assert.Equal("Sprzedaż", changes[0]!["before"]![0]!.GetValue<string>());
        Assert.Equal("Księgowość", changes[0]!["after"]![0]!.GetValue<string>());
        Assert.Equal("ACCOUNTDISABLE", changes[1]!["flagsAdded"]![0]!.GetValue<string>());
        Assert.True(changes[2]!["redacted"]!.GetValue<bool>());
        Assert.Null(changes[2]!["after"]);
        Assert.True(changes[3]!["valueHidden"]!.GetValue<bool>());
        Assert.Contains("hxxps://evil[.]example/x", changes[4]!["after"]![0]!.GetValue<string>(), StringComparison.Ordinal);
        var actor = data["actor"]!;
        Assert.Equal("Resolved", actor["status"]!.GetValue<string>());
        Assert.Equal("High", actor["confidence"]!.GetValue<string>());
        Assert.Equal(@"CONTOSO\adm.nowak", actor["account"]!.GetValue<string>());
        Assert.Equal("10.0.12.34", actor["ipAddress"]!.GetValue<string>());
        Assert.Equal(4728, actor["evidence"]![0]!["eventCode"]!.GetValue<int>());
        Assert.Equal("2026-10-14T12:32:05.000Z", actor["evidence"]![0]!["timeCreated"]!.GetValue<string>());
        Assert.Equal("IncrementalPoll", data["detection"]!["source"]!.GetValue<string>());
        Assert.Equal("2026-10-14T12:31:35Z", data["detection"]!["originatingTime"]!.GetValue<string>());
        Assert.Equal("https://admng-wd.contoso.com:8443/events/0192f3a4-5b6c-7d8e-9f00-112233445567", data["links"]!["event"]!.GetValue<string>());
    }

    [Fact]
    public void MinimalLevel_KeepsAttributeNamesAndTheActorIdentityOnly()
    {
        var message = WebhookPayloadFormatter.Event(FormattingSamples.AttributesChanged(), [], FormattingSamples.Context(level: DetailLevel.Minimal));

        var data = JsonNode.Parse(message.Body)!["data"]!;
        Assert.All(data["changes"]!.AsArray(), c => Assert.Equal(c!.AsObject().ContainsKey("redacted") ? 2 : 1, c.AsObject().Count));
        var actor = data["actor"]!.AsObject();
        Assert.Equal(["status", "account", "sid"], actor.Select(p => p.Key));
        Assert.NotNull(data["links"]!["event"]);
        Assert.DoesNotContain("Księgowość", System.Text.Encoding.UTF8.GetString(message.Body), StringComparison.Ordinal);
    }

    [Fact]
    public void BodyBytes_AreCompactUtf8_AndPolishStaysReadable()
    {
        var message = WebhookPayloadFormatter.Event(FormattingSamples.AttributesChanged(), [], FormattingSamples.Context());
        var text = System.Text.Encoding.UTF8.GetString(message.Body);

        Assert.DoesNotContain("\n", text, StringComparison.Ordinal);
        Assert.Contains("Łukasz Wiśniewski", text, StringComparison.Ordinal);
        Assert.Equal("application/json; charset=utf-8", message.ContentType);
        using var _ = JsonDocument.Parse(message.Body);
    }

    [Fact]
    public void Privileges_CarryTheMemberAndVia()
    {
        var data = JsonNode.Parse(WebhookPayloadFormatter.Event(FormattingSamples.PrivilegeGranted(), [], FormattingSamples.Context()).Body)!;

        Assert.Equal("ad.privilege.granted", data["type"]!.GetValue<string>());
        Assert.Equal("Domain Admins", data["data"]!["member"]!["displayName"]!.GetValue<string>());
        Assert.Equal("GG_Admins_Serwery", data["data"]!["via"]![0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void FollowUps_AreAttributionUpdates_OfTheSameEvent()
    {
        var data = JsonNode.Parse(WebhookPayloadFormatter.Event(FormattingSamples.PrivilegeGranted(), [], FormattingSamples.Context(), followUp: true).Body)!;

        Assert.Equal("ad.attribution.updated", data["type"]!.GetValue<string>());
        Assert.Equal("0192f3a4-5b6c-7d8e-9f00-112233445566", data["data"]!["eventId"]!.GetValue<string>());
    }

    [Fact]
    public void AFollowUpThatRaisesTheSeverity_IsAnEscalation_WithThePreviousSeverity()
    {
        var e = FormattingSamples.PrivilegeGranted();

        var escalated = WebhookPayloadFormatter.Event(e, [], FormattingSamples.Context(level: DetailLevel.Minimal), followUp: true, previousSeverity: Severity.Medium);
        var lowered = JsonNode.Parse(WebhookPayloadFormatter.Event(e with { Severity = Severity.Info }, [], FormattingSamples.Context(), followUp: true,
            previousSeverity: Severity.High).Body)!;
        var unchanged = JsonNode.Parse(WebhookPayloadFormatter.Event(e, [], FormattingSamples.Context(), followUp: true, previousSeverity: Severity.Critical).Body)!;
        var alert = JsonNode.Parse(WebhookPayloadFormatter.Event(e, [], FormattingSamples.Context(), previousSeverity: Severity.Medium).Body)!;

        Assert.Equal("ad.severity.escalated", escalated.WebhookEventType);
        var data = JsonNode.Parse(escalated.Body)!;
        Assert.Equal(("ad.severity.escalated", "critical", "medium"),
            (data["type"]!.GetValue<string>(), data["data"]!["severity"]!.GetValue<string>(), data["data"]!["previousSeverity"]!.GetValue<string>()));
        Assert.Equal("Resolved", data["data"]!["actor"]!["status"]!.GetValue<string>());
        Assert.Equal(("ad.attribution.updated", "info", "high"),
            (lowered["type"]!.GetValue<string>(), lowered["data"]!["severity"]!.GetValue<string>(), lowered["data"]!["previousSeverity"]!.GetValue<string>()));
        Assert.Equal("ad.attribution.updated", unchanged["type"]!.GetValue<string>());
        Assert.Null(unchanged["data"]!["previousSeverity"]);
        Assert.Equal("ad.privilege.granted", alert["type"]!.GetValue<string>());
        Assert.Null(alert["data"]!["previousSeverity"]);
    }

    [Fact]
    public void HealthEvents_DescribeTheWatchdogInsteadOfAnObject()
    {
        var health = FormattingSamples.PrivilegeGranted() with
        {
            Kind = WatchEventKind.HealthDeliveryFailing,
            Severity = Severity.High,
            Object = new ObjectRef { Dn = string.Empty, Kind = WatchObjectKind.Watchdog },
            Member = null,
            Via = [],
            Changes =
            [
                new AttributeChange { Attribute = "code", NewValues = ["delivery.channelBroken"] },
                new AttributeChange { Attribute = "channel", NewValues = ["teams-it"] },
            ],
        };

        var data = JsonNode.Parse(WebhookPayloadFormatter.Event(health, [], FormattingSamples.Context()).Body)!;

        Assert.Equal("watchdog.health.delivery_failing", data["type"]!.GetValue<string>());
        Assert.Equal("Watchdog", data["data"]!["object"]!["kind"]!.GetValue<string>());
        Assert.Single(data["data"]!["object"]!.AsObject());
        Assert.Equal("delivery.channelBroken", data["data"]!["health"]!["code"]!.GetValue<string>());
        Assert.Equal("Error", data["data"]!["health"]!["state"]!.GetValue<string>());
        Assert.Equal("channel=teams-it", data["data"]!["health"]!["args"]![0]!.GetValue<string>());
    }

    [Fact]
    public void DigestHeartbeatAndTest_HaveTheirOwnTypes()
    {
        var context = FormattingSamples.Context();
        var digest = WebhookPayloadFormatter.Digest(FormattingSamples.Now.AddDays(-1), FormattingSamples.Now, "contoso.com",
            new Dictionary<string, int> { ["total"] = 71, ["critical"] = 2 }, [Guid.Parse("0192f3a4-5b6c-7d8e-9f00-112233445566")], context);
        var heartbeat = WebhookPayloadFormatter.Heartbeat(new HeartbeatPayload(FormattingSamples.Now, "0.1.0", "Ok", FormattingSamples.Now, 3), context);
        var test = WebhookPayloadFormatter.Test(context, FormattingSamples.Now);

        var digestData = JsonNode.Parse(digest.Body)!;
        Assert.Equal("watchdog.digest", digestData["type"]!.GetValue<string>());
        Assert.Equal(71, digestData["data"]!["counts"]!["total"]!.GetValue<int>());
        Assert.Single(digestData["data"]!["eventIds"]!.AsArray());
        var heartbeatData = JsonNode.Parse(heartbeat.Body)!;
        Assert.Equal("watchdog.heartbeat", heartbeatData["type"]!.GetValue<string>());
        Assert.Equal(3, heartbeatData["data"]!["pendingOutbox"]!.GetValue<int>());
        Assert.Equal("watchdog.test", JsonNode.Parse(test.Body)!["type"]!.GetValue<string>());
    }

    [Fact]
    public void MultiValuedChanges_ShowTheFirstThreeValuesAndTheCount()
    {
        var spns = new AttributeChange
        {
            Attribute = "servicePrincipalName",
            OldValues = ["HTTP/a"],
            NewValues = ["HTTP/a", "HTTP/b", "HTTP/c", "HTTP/d", "HTTP/e"],
            Added = ["HTTP/b", "HTTP/c", "HTTP/d", "HTTP/e"],
            OldCount = 1,
            NewCount = 5,
        };
        var e = FormattingSamples.AttributesChanged() with { Changes = [spns] };

        var change = JsonNode.Parse(WebhookPayloadFormatter.Event(e, [], FormattingSamples.Context()).Body)!["data"]!["changes"]![0]!;

        Assert.Equal(3, change["after"]!.AsArray().Count);
        Assert.Equal(5, change["afterCount"]!.GetValue<int>());
        Assert.Equal(3, change["added"]!.AsArray().Count);
        Assert.Equal(4, change["addedCount"]!.GetValue<int>());
        Assert.Null(change["beforeCount"]);
    }
}
