using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Configuration;
using AdMng.Watchdog.Core.Rules;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Watchdog.Core.Tests.Rules;

/// <summary>Validation of the collector settings (stage B design 7.7–7.9, B4): host sources, logon-record retention and
/// the silence thresholds; and the configuration migration for Linux (7.8).</summary>
public sealed class CollectorValidationTests
{
    private static readonly ConfigValidationContext Linux = new() { SupportedAttributionSources = [AttributionSourceKind.OpenWec, AttributionSourceKind.None] };

    private static WatchdogConfiguration With(AttributionSettings? attribution = null, RetentionSettings? retention = null)
    {
        var defaults = new DefaultConfigurationFactory().Create();
        return defaults with { Attribution = attribution ?? defaults.Attribution, Retention = retention ?? defaults.Retention };
    }

    private static IReadOnlyList<ConfigIssue> Issues(WatchdogConfiguration configuration, ConfigValidationContext? context = null) =>
        new ConfigurationValidator(new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero)))
            .Validate(configuration, context ?? Linux).Issues;

    [Theory]
    [InlineData(AttributionSourceKind.OpenWec, false)]
    [InlineData(AttributionSourceKind.None, false)]
    [InlineData(AttributionSourceKind.DomainControllers, true)]
    [InlineData(AttributionSourceKind.ForwardedEvents, true)]
    public void OnLinux_OnlyOpenWecAndNoneAreSources(AttributionSourceKind source, bool refused)
    {
        var issues = Issues(With(new AttributionSettings { Source = source, Collector = "wec01.contoso.test" }));

        Assert.Equal(refused, issues.Any(i => i.Code == ConfigValidationCodes.SourceUnsupportedOnHost && i.Path == "attribution.source"));
        Assert.DoesNotContain(Issues(With(new AttributionSettings { Source = source, Collector = "wec01.contoso.test" }), new ConfigValidationContext()),
            i => i.Code == ConfigValidationCodes.SourceUnsupportedOnHost);   // the Windows service keeps every source
    }

    [Theory]
    [InlineData(11, ConfigValidationCodes.Range)]
    [InlineData(721, ConfigValidationCodes.Range)]
    [InlineData(12, ConfigValidationCodes.LogonRetentionTooShort)]   // 12 h lookback + 30 min last retry = 13 h
    [InlineData(13, null)]
    [InlineData(48, null)]
    [InlineData(720, null)]
    public void LogonRecordsHours_IsInRangeAndCoversTheLongestLookup(int hours, string? code)
    {
        var issues = Issues(With(retention: new RetentionSettings { LogonRecordsHours = hours }))
            .Where(i => i.Path == "retention.logonRecordsHours").ToList();

        Assert.Equal(code, issues.SingleOrDefault()?.Code);
    }

    [Fact]
    public void ALongerLookback_NeedsLongerLogonRetention()
    {
        var attribution = new AttributionSettings { LogonLookback = TimeSpan.FromHours(72) };

        var issue = Assert.Single(Issues(With(attribution)), i => i.Path == "retention.logonRecordsHours");

        Assert.Equal(ConfigValidationCodes.LogonRetentionTooShort, issue.Code);
        Assert.Equal(["73"], issue.Args);
    }

    [Fact]
    public void SilenceThresholds_AreRangedAndOrdered()
    {
        Assert.Contains(Issues(With(new AttributionSettings { CollectorSilenceWarning = TimeSpan.FromMinutes(1) })),
            i => i.Path == "attribution.collectorSilenceWarning" && i.Code == ConfigValidationCodes.Range);
        Assert.Contains(Issues(With(new AttributionSettings { CollectorSilenceWarning = TimeSpan.FromMinutes(30), CollectorSilenceError = TimeSpan.FromMinutes(20) })),
            i => i.Path == "attribution.collectorSilenceError" && i.Code == ConfigValidationCodes.Range);
        Assert.DoesNotContain(Issues(With()), i => i.Path.StartsWith("attribution.collectorSilence", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(AttributionSourceKind.DomainControllers, AttributionSourceKind.OpenWec)]
    [InlineData(AttributionSourceKind.ForwardedEvents, AttributionSourceKind.OpenWec)]
    [InlineData(AttributionSourceKind.None, AttributionSourceKind.None)]
    [InlineData(AttributionSourceKind.OpenWec, AttributionSourceKind.OpenWec)]
    public void AVersion1Document_IsMigratedOnceForLinux(AttributionSourceKind stored, AttributionSourceKind migrated)
    {
        var v1 = With(new AttributionSettings { Source = stored, Collector = "wec01.contoso.test" }) with { SchemaVersion = 1 };

        var result = ConfigurationMigration.ForLinux(v1)!;

        Assert.Equal(WatchdogConfiguration.CurrentSchemaVersion, result.SchemaVersion);
        Assert.Equal(migrated, result.Attribution.Source);
        Assert.Equal("wec01.contoso.test", result.Attribution.Collector);
        Assert.Null(ConfigurationMigration.ForLinux(result));
        Assert.DoesNotContain(Issues(result), i => i.Code == ConfigValidationCodes.SourceUnsupportedOnHost);
    }

    [Fact]
    public void AStoredEventRetentionBelowTheFloor_IsRaisedToIt_AndThenValid()
    {
        // Every document saved by 0.2.0 carries its default of 365 days.
        var stored = With(retention: new RetentionSettings { EventsDays = 365, SecurityEventsDays = 14 }) with { SchemaVersion = 1 };
        var floor = Linux with { MinEventsDays = 1095 };
        Assert.Contains(Issues(stored, floor), i => i.Code == ConfigValidationCodes.RetentionBelowFloor);

        var raised = ConfigurationMigration.RaiseEventsDaysToFloor(stored, 1095)!;

        Assert.Equal(1095, raised.Retention.EventsDays);
        Assert.Equal(14, raised.Retention.SecurityEventsDays);
        Assert.DoesNotContain(Issues(raised, floor), i => i.Code == ConfigValidationCodes.RetentionBelowFloor);
        Assert.Null(ConfigurationMigration.RaiseEventsDaysToFloor(raised, 1095));
        Assert.Null(ConfigurationMigration.RaiseEventsDaysToFloor(stored, 0));       // no floor
        Assert.Null(ConfigurationMigration.RaiseEventsDaysToFloor(stored, 365));
        Assert.Null(ConfigurationMigration.RaiseEventsDaysToFloor(With(retention: new RetentionSettings { EventsDays = 2000 }), 1095));
        Assert.Equal(3650, ConfigurationMigration.RaiseEventsDaysToFloor(stored, 5000)!.Retention.EventsDays);   // the validator's maximum
    }
}

