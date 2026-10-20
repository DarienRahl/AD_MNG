using System.Text.Json;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Tests.Contracts;

/// <summary>
/// The shared contract items of stage B (docs/dev/watchdog-linux-design.md 4.6–4.7, 7.8–7.9, 8.x, 9.x, 10, 11, 14, 18):
/// stable numbers and names, record defaults and their JSON shape. Behaviour is tested by the tracks that implement it.
/// </summary>
public sealed class StageBContractTests
{
    private static readonly Guid Gpo = Guid.Parse("31b2f340-016d-11d2-945f-00c04fb984f9");

    [Theory]
    [InlineData(WatchEventKind.GroupPolicySettingsChanged, 510, EventCategory.Domain, Severity.Low)]
    [InlineData(WatchEventKind.GroupPolicyPathRedirected, 511, EventCategory.Domain, Severity.Critical)]
    [InlineData(WatchEventKind.GroupPolicyContentOutOfBand, 512, EventCategory.Domain, Severity.High)]
    [InlineData(WatchEventKind.HealthServiceAccount, 909, EventCategory.Health, Severity.High)]
    public void NewKinds_HaveStableNumbers_AndCatalogDefaults(WatchEventKind kind, int number, EventCategory category, Severity severity)
    {
        var info = EventKindCatalog.Get(kind);

        Assert.Equal(number, (int)kind);
        Assert.Equal(category, info.Category);
        Assert.Equal(severity, info.DefaultSeverity);
        Assert.True(info.RecordedByDefault);
        Assert.False(info.NeedsSecurityLog);
        Assert.Equal(category == EventCategory.Health, info.IsHealth);
    }

    [Fact]
    public void GpoContentKinds_AreAboutGroupPolicyObjects_AndTheServiceAccountKindAboutTheWatchdog()
    {
        foreach (var kind in new[] { WatchEventKind.GroupPolicySettingsChanged, WatchEventKind.GroupPolicyPathRedirected, WatchEventKind.GroupPolicyContentOutOfBand })
        {
            Assert.Equal([WatchObjectKind.GroupPolicy], EventKindCatalog.Get(kind).ObjectKinds);
        }

        Assert.Equal([WatchObjectKind.Watchdog], EventKindCatalog.Get(WatchEventKind.HealthServiceAccount).ObjectKinds);
        Assert.Equal("GroupPolicySettingsChanged", JsonSerializer.Serialize(WatchEventKind.GroupPolicySettingsChanged, WatchdogJson.Options).Trim('"'));
    }

    [Fact]
    public void NewTags_AndReason_HaveTheirStableValues()
    {
        Assert.Equal("badSuccessor", EventTags.BadSuccessor);
        Assert.Equal("machineAccountQuota", EventTags.MachineAccountQuota);
        Assert.Equal("watchdogAccount", EventTags.WatchdogAccount);
        Assert.Equal("gppPassword", EventTags.GppPassword);
        Assert.Equal("auditSettings", EventTags.AuditSettings);
        Assert.Equal("SYSVOL_NOT_AUDITED", AttributionReasons.SysvolNotAudited);
        Assert.Equal("DC_UNREACHABLE.Collector", AttributionReasons.CollectorVariant(AttributionReasons.DcUnreachable));
    }

    [Fact]
    public void AttributionSource_OpenWec_IsValueThree_AndTheDefault()
    {
        Assert.Equal(0, (int)AttributionSourceKind.DomainControllers);
        Assert.Equal(1, (int)AttributionSourceKind.ForwardedEvents);
        Assert.Equal(2, (int)AttributionSourceKind.None);
        Assert.Equal(3, (int)AttributionSourceKind.OpenWec);

        var settings = new AttributionSettings();

        Assert.Equal(AttributionSourceKind.OpenWec, settings.Source);
        Assert.True(settings.DropComputerLogons);
        Assert.True(settings.DropAnonymousLogons);
        Assert.Equal(TimeSpan.FromMinutes(15), settings.CollectorSilenceWarning);
        Assert.Equal(TimeSpan.FromMinutes(60), settings.CollectorSilenceError);
    }

    [Fact]
    public void Retention_HasTheLinuxDefaults()
    {
        var retention = new RetentionSettings();

        Assert.Equal(1095, retention.EventsDays);
        Assert.Equal(48, retention.LogonRecordsHours);
        Assert.Equal(30, retention.SecurityEventsDays);
        Assert.Equal(7, retention.BackupsToKeep);
        Assert.Equal(4096, retention.MaxDatabaseMb);
    }

    [Fact]
    public void GpoContent_HasTheDesignDefaults_UnderDirectory()
    {
        var gpo = new DirectorySettings().GpoContent;

        Assert.True(gpo.Enabled);
        Assert.Equal(TimeSpan.FromHours(2), gpo.SysvolLagTolerance);
        Assert.Equal(5, gpo.MaxFileMb);
        Assert.Equal(50, gpo.MaxTotalMb);
        Assert.Equal(2000, gpo.MaxFiles);
        Assert.Equal(12, GpoContentSettings.MaxDepth);
        Assert.Equal(10_000, GpoContentSettings.MaxSettings);
    }

    [Fact]
    public void Configuration_SchemaVersion2_IsCurrent()
    {
        Assert.Equal(2, WatchdogConfiguration.CurrentSchemaVersion);
        Assert.Equal(2, new WatchdogConfiguration().SchemaVersion);
    }

    [Fact]
    public void AStoredVersion1Document_KeepsItsValues_AndGetsTheNewDefaults()
    {
        // A 0.2.0 document: no collector fields, no gpoContent, no logonRecordsHours.
        const string Stored = """
            { "schemaVersion": 1,
              "attribution": { "enabled": true, "source": "DomainControllers" },
              "retention": { "eventsDays": 365 },
              "directory": { "pollInterval": "00:00:15" } }
            """;

        var configuration = JsonSerializer.Deserialize<WatchdogConfiguration>(Stored, WatchdogJson.Options)!;

        Assert.Equal(1, configuration.SchemaVersion);
        Assert.Equal(AttributionSourceKind.DomainControllers, configuration.Attribution.Source);
        Assert.True(configuration.Attribution.DropComputerLogons);
        Assert.Equal(TimeSpan.FromMinutes(15), configuration.Attribution.CollectorSilenceWarning);
        Assert.Equal(365, configuration.Retention.EventsDays);
        Assert.Equal(48, configuration.Retention.LogonRecordsHours);
        Assert.NotNull(configuration.Directory.GpoContent);
        Assert.Equal(2000, configuration.Directory.GpoContent.MaxFiles);
    }

    [Fact]
    public void NewConfigurationFields_AreCamelCase()
    {
        var json = JsonSerializer.Serialize(new WatchdogConfiguration(), WatchdogJson.Options);

        Assert.Contains("\"schemaVersion\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"source\":\"OpenWec\"", json, StringComparison.Ordinal);
        Assert.Contains("\"dropComputerLogons\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"dropAnonymousLogons\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"collectorSilenceWarning\":\"00:15:00\"", json, StringComparison.Ordinal);
        Assert.Contains("\"collectorSilenceError\":\"01:00:00\"", json, StringComparison.Ordinal);
        Assert.Contains("\"gpoContent\":{\"enabled\":true,\"sysvolLagTolerance\":\"02:00:00\",\"maxFileMb\":5,\"maxTotalMb\":50,\"maxFiles\":2000}", json, StringComparison.Ordinal);
        Assert.Contains("\"logonRecordsHours\":48", json, StringComparison.Ordinal);
        Assert.Contains("\"eventsDays\":1095", json, StringComparison.Ordinal);
        Assert.Contains("\"maxDatabaseMb\":4096", json, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusSections_AreOptional_AndRoundTrip()
    {
        var empty = Status();
        var json = JsonSerializer.Serialize(empty, WatchdogJson.Options);
        Assert.Contains("\"serviceAccount\":null", json, StringComparison.Ordinal);
        Assert.Null(JsonSerializer.Deserialize<StatusDto>("""{ "serviceVersion": "0.2.0", "instanceName": "AD Watchdog", "startedAt": "2026-10-20T06:00:00Z", "now": "2026-10-20T06:00:00Z", "directory": { "state": "Ok" }, "attribution": {}, "storage": { "dataDirectory": "" }, "last24Hours": {} }""", WatchdogJson.Options)!.Host);

        var full = Status() with
        {
            ServiceAccount = new ServiceAccountStatusDto
            {
                State = HealthState.Warning,
                Principal = "ADMNG-WD$@CONTOSO.COM",
                AccountDn = "CN=ADMNG-WD,OU=Konta usług,OU=Tier 0,OU=Admin,DC=contoso,DC=com",
                KeytabKvno = 4,
                DirectoryKvno = 4,
                RotationResult = KeytabStatusResults.ReplicationPending,
                SupportedEncryptionTypes = 0x18,
                KeytabEncryptionTypes = ["aes256-cts-hmac-sha1-96", "aes128-cts-hmac-sha1-96"],
                NotDelegated = true,
                Privileged = false,
                DenySelfAcePresent = true,
                KeytabOrderValid = true,
            },
            Collector = new CollectorStatusDto
            {
                State = HealthState.Ok,
                SubscriptionUuid = Guid.Parse("4f7c2e1a-9b3d-4c55-8e21-6a0d9c7b3e10"),
                DomainControllers =
                [
                    new CollectorDcStatusDto
                    {
                        Dc = "dc01.contoso.com", State = HealthState.Ok, Client = "DC01@CONTOSO.COM",
                        MedianLag = TimeSpan.FromSeconds(4), BacklogBytes = 1024, RejectedRecords24h = 0,
                    },
                ],
            },
            GpoContent = new GpoContentStatusDto { State = HealthState.Ok, Enabled = true, TotalGpos = 12, ReadGpos = 12, SysvolLaggingGpos = 0 },
            Host = new HostStatusDto
            {
                OperatingSystem = "Debian GNU/Linux 13 (trixie)", Runtime = ".NET 10.0.13", CredentialKey = HostCredentialKeys.Tpm2Host,
                ClockSkew = TimeSpan.FromSeconds(-2), LdapSsf = 256, LdapSecurity = "SignAndSeal", Systemd = true,
            },
            Storage = Status().Storage with { PendingEventsDays = 365, PendingEventsDaysFrom = new DateTimeOffset(2026, 10, 27, 3, 15, 0, TimeSpan.Zero) },
        };

        var fullJson = JsonSerializer.Serialize(full, WatchdogJson.Options);
        var back = JsonSerializer.Deserialize<StatusDto>(fullJson, WatchdogJson.Options)!;

        Assert.Equal(fullJson, JsonSerializer.Serialize(back, WatchdogJson.Options));
        Assert.Contains("\"keytabKvno\":4", fullJson, StringComparison.Ordinal);
        Assert.Contains("\"rotationResult\":\"replicationPending\"", fullJson, StringComparison.Ordinal);
        Assert.Contains("\"medianLag\":\"00:00:04\"", fullJson, StringComparison.Ordinal);
        Assert.Contains("\"credentialKey\":", fullJson, StringComparison.Ordinal);
        Assert.Equal(HostCredentialKeys.Tpm2Host, back.Host!.CredentialKey);
        Assert.Contains("\"pendingEventsDays\":365", fullJson, StringComparison.Ordinal);
        Assert.Equal("OpenWec", back.Collector!.Kind);
        Assert.Equal("DC01@CONTOSO.COM", Assert.Single(back.Collector.DomainControllers).Client);
    }

    [Theory]
    [InlineData(HealthCheckCodes.WecSilent, "wec.dc01.contoso.com.silent")]
    [InlineData(HealthCheckCodes.WecNotForwarding, "wec.dc01.contoso.com.notForwarding")]
    [InlineData(HealthCheckCodes.WecLag, "wec.dc01.contoso.com.lag")]
    [InlineData(HealthCheckCodes.WecIdentityMismatch, "wec.dc01.contoso.com.identityMismatch")]
    [InlineData(HealthCheckCodes.WecMalformedLines, "wec.dc01.contoso.com.malformedLines")]
    [InlineData(HealthCheckCodes.WecBacklog, "wec.dc01.contoso.com.backlog")]
    [InlineData(HealthCheckCodes.WecCollectorFileLost, "wec.dc01.contoso.com.collectorFileLost")]
    public void CollectorCodes_NameTheDcInTheMiddle(string check, string expected) =>
        Assert.Equal(expected, HealthCheckCodes.Wec("DC01.contoso.com", check));

    [Theory]
    [InlineData(HealthCheckCodes.GpoTooLarge, "gpo.31b2f340-016d-11d2-945f-00c04fb984f9.tooLarge")]
    [InlineData(HealthCheckCodes.GpoParseError, "gpo.31b2f340-016d-11d2-945f-00c04fb984f9.parseError")]
    [InlineData(HealthCheckCodes.GpoSysvolLag, "gpo.31b2f340-016d-11d2-945f-00c04fb984f9.sysvolLag")]
    public void GpoCodes_NameTheGpoGuidInTheMiddle(string check, string expected) =>
        Assert.Equal(expected, HealthCheckCodes.Gpo(Gpo, check));

    [Fact]
    public void HealthCodeBuilders_RejectSubjectsThatWouldBreakTheCode()
    {
        Assert.Throws<ArgumentException>(() => HealthCheckCodes.Wec("", HealthCheckCodes.WecSilent));
        Assert.Throws<ArgumentException>(() => HealthCheckCodes.Wec("dc01 .contoso.com", HealthCheckCodes.WecSilent));
        Assert.Throws<ArgumentException>(() => HealthCheckCodes.Wec("dc01.contoso.com", "wec.silent"));
        Assert.Throws<ArgumentException>(() => HealthCheckCodes.Gpo(Gpo, ""));
    }

    [Fact]
    public void FixedHealthCodes_HaveTheDesignNames()
    {
        Assert.Equal("serviceAccount.rotation", HealthCheckCodes.ServiceAccountRotation);
        Assert.Equal("serviceAccount.passwordAge", HealthCheckCodes.ServiceAccountPasswordAge);
        Assert.Equal("serviceAccount.keytabStale", HealthCheckCodes.ServiceAccountKeytabStale);
        Assert.Equal("serviceAccount.keytabOrder", HealthCheckCodes.ServiceAccountKeytabOrder);
        Assert.Equal("serviceAccount.privileged", HealthCheckCodes.ServiceAccountPrivileged);
        Assert.Equal("serviceAccount.drift", HealthCheckCodes.ServiceAccountDrift);
        Assert.Equal("denySelfAceMissing", HealthCheckCodes.DenySelfAceMissing);
        Assert.Equal("host.clockSkew", HealthCheckCodes.HostClockSkew);
        Assert.Equal("host.primaryKdcNotPdc", HealthCheckCodes.HostPrimaryKdcNotPdc);
        Assert.Equal("host.credentialKey", HealthCheckCodes.HostCredentialKey);
        Assert.Equal("host.hardening", HealthCheckCodes.HostHardening);
        Assert.Equal("directory.notSealed", HealthCheckCodes.DirectoryNotSealed);
        Assert.Equal("permission.tokenGroupsUnreadable", HealthCheckCodes.PermissionTokenGroupsUnreadable);
        Assert.Equal("wec.service", HealthCheckCodes.WecService);
        Assert.Equal("sysvol.accessDenied", HealthCheckCodes.SysvolAccessDenied);
        Assert.Equal("domain.machineAccountQuota", HealthCheckCodes.DomainMachineAccountQuota);
        Assert.Equal("storage.backupSkipped", HealthCheckCodes.StorageBackupSkipped);
    }

    [Fact]
    public void KeytabResults_CredentialKeys_AndTheRoleLookupProblem_HaveTheirWireValues()
    {
        Assert.Equal(
            ["ok", "otpActive", "changeFailed", "keytabWriteFailed", "replicationPending", "verifyFailed"],
            KeytabStatusResults.All);
        Assert.Equal("tpm2+host", HostCredentialKeys.Tpm2Host);
        Assert.Equal("host", HostCredentialKeys.Host);
        Assert.Equal("https://admng/problems/role-lookup-unavailable", ApiProblem.RoleLookupUnavailable);
    }

    private static StatusDto Status() => new()
    {
        ServiceVersion = "0.3.0",
        InstanceName = "AD Watchdog – Łódź",
        StartedAt = new DateTimeOffset(2026, 10, 20, 6, 0, 0, TimeSpan.Zero),
        Now = new DateTimeOffset(2026, 10, 20, 6, 5, 0, TimeSpan.Zero),
        Directory = new DirectoryStatusDto { State = HealthState.Ok, DcHostName = "dc01.contoso.com", IsPdc = true },
        Attribution = new AttributionStatusDto { Enabled = true, Source = nameof(AttributionSourceKind.OpenWec) },
        Storage = new StorageStatusDto { DataDirectory = "/var/lib/admng-watchdog" },
        Last24Hours = new EventCountsDto(),
    };
}
