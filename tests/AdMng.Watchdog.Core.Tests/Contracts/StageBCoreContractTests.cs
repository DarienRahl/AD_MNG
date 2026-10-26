using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Core.Diagnostics;
using AdMng.Watchdog.Core.Formatting.Webhooks;
using AdMng.Watchdog.Core.Notifications;
using AdMng.Watchdog.Core.Notifications.Digest;
using AdMng.Watchdog.Core.Rules;

namespace AdMng.Watchdog.Core.Tests.Contracts;

/// <summary>
/// The Core side of the stage B contracts (docs/dev/watchdog-linux-design.md): validation codes and context, event ids,
/// rule ids, the health registry sections, and where the new kinds land (Tier-0 catalog, digest, webhook types, rules).
/// </summary>
public sealed class StageBCoreContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ValidationCodes_HaveTheDesignNames()
    {
        Assert.Equal("sourceUnsupportedOnHost", ConfigValidationCodes.SourceUnsupportedOnHost);
        Assert.Equal("logonRetentionTooShort", ConfigValidationCodes.LogonRetentionTooShort);
        Assert.Equal("retentionBelowFloor", ConfigValidationCodes.RetentionBelowFloor);
    }

    [Fact]
    public void ValidationContext_DefaultsToNoFloor_AndEverySource()
    {
        var context = new ConfigValidationContext();

        Assert.Equal(0, context.MinEventsDays);
        Assert.Empty(context.SupportedAttributionSources);
    }

    [Fact]
    public void EventIds_OfStageB_AreStable()
    {
        Assert.Equal(1008, WatchdogEventIds.CertificateFingerprint);
        Assert.Equal(1011, WatchdogEventIds.ServiceAccountProblem);
        Assert.Equal(1012, WatchdogEventIds.CollectorProblem);
        Assert.Equal(1013, WatchdogEventIds.GpoContentProblem);
    }

    [Fact]
    public void BuiltInRuleIds_OfStageB_AreStable()
    {
        Assert.Equal("tier0-badsuccessor", DefaultRules.Tier0BadSuccessor);
        Assert.Equal("tier0-machine-quota", DefaultRules.Tier0MachineQuota);
        Assert.Equal("watchdog-self", DefaultRules.WatchdogSelf);
        Assert.Equal("tier0-gpo", DefaultRules.Tier0Gpo);
        Assert.Equal("collectorFileLost", EventLogGapDetector.CollectorFileLost);
    }

    [Theory]
    [InlineData(WatchEventKind.GroupPolicySettingsChanged)]
    [InlineData(WatchEventKind.GroupPolicyPathRedirected)]
    [InlineData(WatchEventKind.GroupPolicyContentOutOfBand)]
    public void GpoContentKinds_AreTier0_InTheDomainDigestSection_AndInTheOuGpoDigestRule(WatchEventKind kind)
    {
        Assert.True(Tier0Catalog.IsTier0Kind(kind));
        Assert.Equal(DigestSectionKind.DomainPolicy, DigestSections.For(kind));
        var ouGpo = Assert.Single(DefaultRules.Create(), r => r.Id == DefaultRules.OuGpo);
        Assert.Contains(kind, ouGpo.Kinds);
        Assert.True(ouGpo.Delivery.IncludeInDigest);
    }

    [Fact]
    public void ServiceAccountHealth_IsAHealthKind_CoveredByTheLockedHealthRule()
    {
        Assert.True(DigestSections.IsHealth(WatchEventKind.HealthServiceAccount));
        Assert.Equal(DigestSectionKind.Health, DigestSections.For(WatchEventKind.HealthServiceAccount));
        Assert.False(DigestSections.IsHealth(WatchEventKind.WatchdogConfigurationChanged));
        Assert.False(DigestSections.IsHealth(WatchEventKind.Test));
        Assert.Contains(WatchEventKind.HealthServiceAccount, Assert.Single(DefaultRules.Create(), r => r.Id == DefaultRules.Health).Kinds);
        Assert.False(Tier0Catalog.IsTier0Kind(WatchEventKind.HealthServiceAccount));
    }

    [Theory]
    [InlineData(WatchEventKind.GroupPolicySettingsChanged, "ad.gpo.settings_changed")]
    [InlineData(WatchEventKind.GroupPolicyPathRedirected, "ad.gpo.path_redirected")]
    [InlineData(WatchEventKind.GroupPolicyContentOutOfBand, "ad.gpo.content_out_of_band")]
    public void GpoContentKinds_HaveWebhookTypes(WatchEventKind kind, string expected) =>
        Assert.Equal(expected, WebhookEventTypes.For(kind, WatchObjectKind.GroupPolicy));

    [Fact]
    public void ServiceAccountHealth_HasAWebhookType() =>
        Assert.Equal("watchdog.health.service_account", WebhookEventTypes.For(WatchEventKind.HealthServiceAccount, WatchObjectKind.Watchdog));

    [Fact]
    public void HealthRegistry_KeepsTheStageBSections_NullUntilReported()
    {
        var registry = new HealthRegistry();
        var changes = new List<string>();
        registry.Changed += (_, name) => changes.Add(name);

        Assert.Null(registry.ServiceAccount);
        Assert.Null(registry.Collector);
        Assert.Null(registry.GpoContent);
        Assert.Null(registry.Host);

        registry.ReportServiceAccount(new ServiceAccountStatusDto { State = HealthState.Ok, KeytabKvno = 3, DirectoryKvno = 3, CheckedAt = Now });
        registry.ReportCollector(new CollectorStatusDto
        {
            State = HealthState.Warning,
            DomainControllers = [new CollectorDcStatusDto { Dc = "dc02.contoso.com", State = HealthState.Warning, MedianLag = TimeSpan.FromMinutes(6) }],
        });
        registry.ReportGpoContent(new GpoContentStatusDto { State = HealthState.Ok, Enabled = true, TotalGpos = 7, ReadGpos = 3 });
        registry.ReportHost(new HostStatusDto { CredentialKey = HostCredentialKeys.Host, LdapSsf = 256 });
        registry.ReportServiceAccount(new ServiceAccountStatusDto { State = HealthState.Error, KeytabKvno = 3, DirectoryKvno = 5, CheckedAt = Now });

        Assert.Equal(HealthState.Error, registry.ServiceAccount?.State);
        Assert.Equal(5, registry.ServiceAccount?.DirectoryKvno);
        Assert.Equal("dc02.contoso.com", Assert.Single(registry.Collector!.DomainControllers).Dc);
        Assert.Equal(3, registry.GpoContent?.ReadGpos);
        Assert.Equal(256, registry.Host?.LdapSsf);
        Assert.Equal(["serviceAccount", "collector", "gpoContent", "host", "serviceAccount"], changes);
        Assert.Throws<ArgumentNullException>(() => registry.ReportHost(null!));
    }

    [Fact]
    public void NewTags_AreDistinctCamelCaseValues()
    {
        var tags = new[] { EventTags.BadSuccessor, EventTags.MachineAccountQuota, EventTags.WatchdogAccount, EventTags.GppPassword, EventTags.AuditSettings };

        Assert.Equal(tags.Length, tags.Distinct(StringComparer.Ordinal).Count());
        Assert.All(tags, t => Assert.Matches("^[a-z][A-Za-z]+$", t));
    }
}
