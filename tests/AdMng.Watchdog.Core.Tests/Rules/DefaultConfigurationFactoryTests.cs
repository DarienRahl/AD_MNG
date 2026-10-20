using System.Text.Json;
using System.Text.Json.Nodes;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Rules;

namespace AdMng.Watchdog.Core.Tests.Rules;

public class DefaultConfigurationFactoryTests
{
    /// <summary>The default document of section 8.1 without the rules (compared as a subset: properties the
    /// specification lists must have exactly these values), with the defaults of the Linux service (stage B design 7.8,
    /// 8.5, 14.2): schema version 2, attribution source OpenWec with the collector fields, directory.gpoContent, retention
    /// 1095 days / logon records 48 h / 4096 MB.</summary>
    private const string Section81 = """
        {
          "schemaVersion": 2,
          "general": { "instanceName": "AD Watchdog", "language": "pl", "timeZone": "Europe/Warsaw", "publicBaseUrl": null,
                       "rolloutDays": 7, "rolloutEnded": false },
          "directory": {
            "pollInterval": "00:00:15", "fullReconciliationTime": "02:30", "usnOverlap": 0,
            "trackDeletedObjects": true, "membershipTracking": "AllGroups",
            "watchConfigurationPartition": true, "watchSchemaPartition": true,
            "wakeUpContainers": [], "excludedSubtrees": [],
            "criticalObjects": ["{domain}", "CN=AdminSDHolder,CN=System,{domain}", "OU=Domain Controllers,{domain}"],
            "serviceAccountSubtrees": [], "disabledAccountsSubtrees": [],
            "additionalWatchedAttributes": [], "ignoredAttributes": [],
            "enabledOptionalKinds": [], "disabledKinds": [],
            "trackComputerPasswordChanges": false, "trackManagedPasswordRollover": false, "trackComputerOsChanges": false,
            "maxValuesPerAttribute": 100, "failoverAfter": "00:10:00", "creationSettleWindow": "00:02:00",
            "gpoContent": { "enabled": true, "sysvolLagTolerance": "02:00:00", "maxFileMb": 5, "maxTotalMb": 50, "maxFiles": 2000 }
          },
          "attribution": {
            "enabled": true, "source": "OpenWec", "collector": null, "domainControllers": [],
            "pollInterval": "00:00:30", "matchWindow": "00:00:03", "fallbackWindow": "00:02:00",
            "replicationLatencyBound": "00:15:00",
            "pendingRetries": ["00:00:30", "00:02:00", "00:10:00", "00:30:00"],
            "lookupLogonSource": true, "lookupLockoutSources": true, "logonLookback": "12:00:00",
            "sourceQueryTimeout": "00:00:10", "maxRecordsPerPoll": 50000,
            "resolveReverseDns": true, "suppressComputerSelfChanges": true,
            "dropComputerLogons": true, "dropAnonymousLogons": true,
            "collectorSilenceWarning": "00:15:00", "collectorSilenceError": "01:00:00"
          },
          "scans": {
            "accountExpiry": { "enabled": true, "thresholdsDays": [14, 7, 1], "includeDisabled": false, "interval": "01:00:00",
                               "fireOnBusinessDays": true },
            "passwordExpiry": { "scope": "PrivilegedAndService", "thresholdsDays": [14, 7, 1], "interval": "01:00:00" },
            "lockoutRecheckInterval": "00:05:00",
            "inactiveAccounts": { "enabled": true, "days": 90, "privilegedDays": 180, "minAgeDays": 30, "reportDay": "Monday" },
            "massLockout": { "threshold": 5, "window": "00:10:00", "percentOfEnabledUsers": 0.5 },
            "massDeletion": { "threshold": 10, "window": "00:10:00", "percentOfEnabledUsers": 0 },
            "massComputerDeletion": { "threshold": 50, "window": "00:10:00", "percentOfEnabledUsers": 0 },
            "passwordSpray": { "enabled": false, "distinctAccounts": 20, "window": "00:10:00" },
            "permissionSelfCheckInterval": "1.00:00:00"
          },
          "privilegedGroups": {
            "criticalSids": ["S-1-5-32-544", "{domainSid}-512", "{domainSid}-516", "{rootDomainSid}-518",
                             "{rootDomainSid}-519", "{domainSid}-526", "{rootDomainSid}-527"],
            "highSids": ["S-1-5-32-548", "S-1-5-32-549", "S-1-5-32-550", "S-1-5-32-551", "S-1-5-32-552",
                         "{domainSid}-520", "{domainSid}-517", "{domainSid}-521"],
            "highSamAccountNames": ["DnsAdmins"],
            "additionalCriticalDns": [], "additionalHighDns": [],
            "protectRemovalSids": ["{domainSid}-525"], "watchListDns": []
          },
          "businessHours": {
            "days": ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"],
            "start": "07:00", "end": "17:00", "holidayCalendar": "PL", "extraHolidays": []
          },
          "notifications": {
            "subjectPrefix": "[AD Watchdog]", "attributionGrace": "00:01:30", "aggregationWindow": "00:05:00",
            "highSeverityFlushDelay": "00:00:10", "maxItemsPerMessage": 50, "maxMessagesPerRulePerHour": 10,
            "followUpWhenAttributed": true, "maxValueLength": 200
          },
          "smtp": {
            "enabled": false, "host": null, "port": 587, "security": "StartTls", "authentication": "None",
            "userName": null, "password": { "isSet": false, "hint": null },
            "oAuth": { "cloud": "Public", "tenantId": null, "clientId": null, "certificateThumbprint": null,
                       "clientSecret": { "isSet": false, "hint": null } },
            "from": null, "replyTo": null, "localDomain": null,
            "certificateValidation": "System", "pinnedSha256": null, "checkRevocation": true,
            "maxMessagesPerMinute": 20, "maxRecipientsPerMessage": 50, "timeout": "00:01:00"
          },
          "channels": [],
          "recipientGroups": [
            { "id": "it-admins", "name": "Administratorzy IT", "addresses": [], "detailLevel": "Full" },
            { "id": "helpdesk", "name": "Helpdesk", "addresses": [], "detailLevel": "Standard" }
          ],
          "digest": {
            "enabled": true, "time": "07:30",
            "days": ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"],
            "recipientGroupIds": ["it-admins"], "channelIds": [], "sendWhenEmpty": true,
            "maxRowsPerSection": 50, "weeklySectionsDay": "Monday", "upcomingExpiryDays": 14, "coverNonBusinessDays": true
          },
          "retention": {
            "eventsDays": 1095, "securityEventsDays": 30, "logonRecordsHours": 48, "outboxSentDays": 30, "outboxDeadDays": 90,
            "deletedSnapshotDays": 180, "configAuditDays": 1095, "backupsToKeep": 7, "preMigrationBackupDays": 30,
            "maxDatabaseMb": 4096, "maintenanceTime": "03:15"
          }
        }
        """;

    [Fact]
    public void Create_MatchesTheDefaultDocumentOfSection8_1()
    {
        var actual = JsonSerializer.SerializeToNode(new DefaultConfigurationFactory().Create(), WatchdogJson.Options)!;

        AssertSubset(JsonNode.Parse(Section81)!, actual, "$");
    }

    [Fact]
    public void Create_ShipsTheBuiltInRules_AndRoundTripsThroughJson()
    {
        var configuration = new DefaultConfigurationFactory().Create();

        Assert.Equal(DefaultRules.Ids, configuration.Rules.Select(r => r.Id));
        var json = JsonSerializer.Serialize(configuration, WatchdogJson.Options);
        var back = JsonSerializer.Deserialize<WatchdogConfiguration>(json, WatchdogJson.Options)!;
        Assert.Equal(json, JsonSerializer.Serialize(back, WatchdogJson.Options));
    }

    [Fact]
    public void Create_ReturnsIndependentDocuments()
    {
        var factory = new DefaultConfigurationFactory();

        Assert.NotSame(factory.Create().Rules[0], factory.Create().Rules[0]);
    }

    [Theory]
    [InlineData(ChannelType.Teams, DetailLevel.Standard)]
    [InlineData(ChannelType.Webhook, DetailLevel.Minimal)]
    public void CreateChannel_UsesTheTypeDefaults(ChannelType type, DetailLevel expected)
    {
        var channel = DefaultConfigurationFactory.CreateChannel("kanał-1", type, "Kanał – Łódź");

        Assert.Equal(expected, channel.DetailLevel);
        Assert.Equal(type == ChannelType.Teams, channel.Teams is not null);
        Assert.Equal(type == ChannelType.Webhook, channel.Webhook is not null);
        Assert.True(channel.Enabled);
        Assert.False(channel.Url.IsSet);
    }

    private static void AssertSubset(JsonNode expected, JsonNode? actual, string path)
    {
        switch (expected)
        {
            case JsonObject expectedObject:
                var actualObject = Assert.IsType<JsonObject>(actual, exactMatch: false);
                foreach (var (name, value) in expectedObject)
                {
                    Assert.True(actualObject.ContainsKey(name), $"{path}.{name} missing");
                    if (value is null)
                    {
                        Assert.True(actualObject[name] is null, $"{path}.{name} should be null");
                    }
                    else
                    {
                        AssertSubset(value, actualObject[name], $"{path}.{name}");
                    }
                }

                break;
            case JsonArray expectedArray:
                var actualArray = Assert.IsType<JsonArray>(actual, exactMatch: false);
                Assert.True(expectedArray.Count == actualArray.Count, $"{path}: {expectedArray.Count} items expected, {actualArray.Count} found");
                for (var i = 0; i < expectedArray.Count; i++)
                {
                    AssertSubset(expectedArray[i]!, actualArray[i], $"{path}[{i}]");
                }

                break;
            case JsonValue value when value.TryGetValue<string>(out var text) && text.Length == 5 && text[2] == ':':
                // TimeOnly "HH:mm" in the specification; System.Text.Json writes "HH:mm:ss" and reads both.
                Assert.Equal(text + ":00", actual?.GetValue<string>());
                break;
            default:
                Assert.True(JsonNode.DeepEquals(expected, actual),$"{path}: expected {expected.ToJsonString()}, found {actual?.ToJsonString() ?? "null"}");
                break;
        }
    }
}
