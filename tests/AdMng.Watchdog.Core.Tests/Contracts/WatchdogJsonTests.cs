using System.Text.Json;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Tests.Contracts;

public class WatchdogJsonTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WatchEvent_RoundTrips_WithEnumsAsCamelCaseStrings()
    {
        var watchEvent = new WatchEvent
        {
            Id = 42,
            EventId = Guid.CreateVersion7(Now),
            ChangeSetId = Guid.CreateVersion7(Now),
            Kind = WatchEventKind.AttributesChanged,
            Severity = Severity.Medium,
            Object = new ObjectRef
            {
                ObjectGuid = Guid.Parse("9b0f4e3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f"),
                Dn = "CN=Zofia Żak,OU=Księgowość,DC=contoso,DC=test",
                Name = "Zofia Żak",
                SamAccountName = "zofia.zak",
                Kind = WatchObjectKind.User,
            },
            Changes =
            [
                new AttributeChange
                {
                    Attribute = "department",
                    OldValues = ["Księgowość"],
                    NewValues = ["Sprzedaż"],
                    OldCount = 1,
                    NewCount = 1,
                },
            ],
            Lockout = new LockoutInfo { LockedAt = Now, Sources = [new LockoutSource("10.0.0.12", "PC-ŁÓDŹ-01", 5, 4771)] },
            Expiry = new ExpiryInfo(ExpiryKind.Account, Now.AddDays(7), new DateOnly(2026, 10, 20), 6, 7),
            DetectedAt = Now,
            Source = DetectionSource.IncrementalPoll,
            // Qualified: inside AdMng.Watchdog.Core.* the simple name binds to the namespace AdMng.Watchdog.Core.Attribution.
            Attribution = new AdMng.Watchdog.Contracts.Events.Attribution
            {
                Status = AttributionStatus.Resolved,
                Confidence = AttributionConfidence.High,
                Actor = new PrincipalRef { Account = @"CONTOSO\łukasz.wiśniewski" },
                Evidence = [new EvidenceRef { Dc = "dc01.contoso.test", EventCode = 5136, RecordId = 410204, TimeCreated = Now }],
            },
            Tags = [EventTags.Privileged],
        };

        var json = JsonSerializer.Serialize(watchEvent, WatchdogJson.Options);
        var back = JsonSerializer.Deserialize<WatchEvent>(json, WatchdogJson.Options)!;

        Assert.Contains("\"kind\":\"AttributesChanged\"", json, StringComparison.Ordinal);
        Assert.Contains("\"severity\":\"Medium\"", json, StringComparison.Ordinal);
        Assert.Equal(watchEvent.EventId, back.EventId);
        Assert.Equal(watchEvent.Object, back.Object);
        Assert.Equal("Sprzedaż", Assert.Single(Assert.Single(back.Changes).NewValues));
        Assert.Equal("PC-ŁÓDŹ-01", Assert.Single(back.Lockout!.Sources).Workstation);
        Assert.Equal(watchEvent.Expiry, back.Expiry);
        Assert.Equal(AttributionStatus.Resolved, back.Attribution.Status);
        Assert.Equal(410204, Assert.Single(back.Attribution.Evidence).RecordId);
        Assert.Equal([EventTags.Privileged], back.Tags);
    }

    [Fact]
    public void Configuration_MissingProperties_TakeTheRecordDefaults()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "general": { "instanceName": "AD Watchdog – Kraków" },
              "directory": { "pollInterval": "00:00:30", "fullReconciliationTime": "03:45" },
              "digest": { "time": "06:15:00" },
              "rules": [ { "id": "new-accounts", "name": "Nowe konta", "kinds": [ "UserCreated" ] } ]
            }
            """;

        var configuration = JsonSerializer.Deserialize<WatchdogConfiguration>(json, WatchdogJson.Options)!;

        Assert.Equal("AD Watchdog – Kraków", configuration.General.InstanceName);
        Assert.Equal("Europe/Warsaw", configuration.General.TimeZone);
        Assert.Equal(TimeSpan.FromSeconds(30), configuration.Directory.PollInterval);
        Assert.Equal(new TimeOnly(3, 45), configuration.Directory.FullReconciliationTime);
        Assert.Equal(TimeSpan.FromMinutes(2), configuration.Directory.CreationSettleWindow);
        Assert.Equal(new TimeOnly(6, 15), configuration.Digest.Time);
        Assert.Equal(7, configuration.Digest.Days.Count);
        Assert.Equal([14, 7, 1], configuration.Scans.AccountExpiry.ThresholdsDays);
        Assert.Equal(5, configuration.Scans.MassLockout.Threshold);
        Assert.Equal(587, configuration.Smtp.Port);
        Assert.False(configuration.Smtp.Password.IsSet);
        var rule = Assert.Single(configuration.Rules);
        Assert.Equal([WatchEventKind.UserCreated], rule.Kinds);
        Assert.True(rule.Enabled);
        Assert.True(rule.Delivery.IncludeInDigest);
    }

    [Fact]
    public void MissingCollectionsAndObjects_AreNeverNull()
    {
        var configuration = JsonSerializer.Deserialize<WatchdogConfiguration>("{}", WatchdogJson.Options)!;
        var watchEvent = JsonSerializer.Deserialize<WatchEvent>(
            """{ "eventId": "0199e2a0-1c00-7000-8000-000000000001", "kind": "UserCreated", "object": { "dn": "CN=Jan Kowalski,DC=contoso,DC=test" }, "detectedAt": "2026-10-14T08:00:00Z", "source": "IncrementalPoll" }""",
            WatchdogJson.Options)!;

        Assert.Equal(WatchdogConfiguration.CurrentSchemaVersion, configuration.SchemaVersion);
        Assert.NotNull(configuration.General);
        Assert.Equal("pl", configuration.General.Language);
        Assert.Equal(["it-admins"], configuration.Digest.RecipientGroupIds);
        Assert.Empty(configuration.Rules);
        Assert.NotNull(configuration.Smtp.OAuth.ClientSecret);
        Assert.Empty(watchEvent.Tags);
        Assert.Empty(watchEvent.Changes);
        Assert.Equal(AttributionStatus.Pending, watchEvent.Attribution.Status);
        Assert.Equal(WatchObjectKind.Unknown, watchEvent.Object.Kind);
    }

    [Fact]
    public void Configuration_Defaults_SurviveARoundTrip()
    {
        var json = JsonSerializer.Serialize(new WatchdogConfiguration(), WatchdogJson.Options);
        var back = JsonSerializer.Deserialize<WatchdogConfiguration>(json, WatchdogJson.Options)!;

        Assert.Equal(json, JsonSerializer.Serialize(back, WatchdogJson.Options));
        Assert.Contains("\"pollInterval\":\"00:00:15\"", json, StringComparison.Ordinal);
        Assert.Contains("\"membershipTracking\":\"AllGroups\"", json, StringComparison.Ordinal);
        Assert.Contains("\"criticalSids\":[\"S-1-5-32-544\",\"{domainSid}-512\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusAndCatalog_AreSerializableThroughTheContext()
    {
        var counts = new EventCountsDto { Total = 3, BySeverity = new Dictionary<Severity, int> { [Severity.High] = 2, [Severity.Info] = 1 } };

        var json = JsonSerializer.Serialize(counts, WatchdogJson.Options);
        var catalog = JsonSerializer.Serialize(EventKindCatalog.All, WatchdogJson.Options);

        Assert.Contains("\"High\":2", json, StringComparison.Ordinal);
        Assert.Equal(2, JsonSerializer.Deserialize<EventCountsDto>(json, WatchdogJson.Options)!.BySeverity[Severity.High]);
        Assert.Equal(
            EventKindCatalog.All.Count,
            JsonSerializer.Deserialize<IReadOnlyList<EventKindInfo>>(catalog, WatchdogJson.Options)!.Count);
    }

    [Fact]
    public void SecretCarriers_NeverPrintTheirValues()
    {
        var field = new SecretField { IsSet = true, NewValue = "Tajne!Hasło#2026" };
        var request = new TestEmailRequest
        {
            Recipients = ["admin@contoso.test"],
            Settings = new SmtpSettings { Password = new SecretField { NewValue = "Tajne!Hasło#2026" } },
        };

        Assert.Equal("SecretField(set)", field.ToString());
        Assert.Equal("SecretField(empty)", new SecretField { NewValue = "Tajne!Hasło#2026" }.ToString());
        Assert.DoesNotContain("Tajne", request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("admin@", request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Tajne", new TestChannelRequest { Settings = new ChannelSettings
        {
            Id = "siem",
            Type = ChannelType.Webhook,
            Name = "SIEM",
            Url = new SecretField { NewValue = "https://siem.contoso.test/hooks/Tajne" },
        } }.ToString(), StringComparison.Ordinal);
    }
}
