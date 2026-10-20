using System.Runtime.Versioning;
using AdMng.Testing;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Configuration;
using AdMng.Watchdog.Service.Attribution;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using AdMng.Watchdog.Testing.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Watchdog.Service.Tests.Collector;

/// <summary>The Linux host's collector parts (stage B design 7.3, 7.8): registration, <c>print-wec-subscription</c> and the
/// configuration migration at start.</summary>
public sealed class CollectorHostTests
{
    private const string Uuid = "4f7c2e1a-9b3d-4c55-8e21-6a0d9c7b3e10";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task OnLinuxWithADirectory_TheTailerIsAJobAndTheEnginesCollector()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The WEF tailer is the Linux service's.");
        var directory = new FakeDirectoryBuilder();
        await using var factory = new WatchdogFactory(new Dictionary<string, string?> { ["Watchdog:Directory:Disabled"] = "false" }, directory: directory.Session);

        var collector = factory.Services.GetRequiredService<ISecurityEventCollector>();

        Assert.IsType<WecFileIngestionJob>(collector);
        Assert.True(factory.Services.GetRequiredService<ISecurityEventLookup>().IsAvailable);
        Assert.True(CollectorMode.IsActive(new AttributionSettings(), collector));
    }

    [Fact]
    public async Task WithoutADirectory_ThereIsNoCollector_AndOpenWecReadsTheDcLogs()
    {
        await using var factory = new WatchdogFactory();

        var collector = factory.Services.GetRequiredService<ISecurityEventCollector>();

        Assert.False(collector.IsAvailable);
        Assert.Equal(AttributionSourceKind.DomainControllers, CollectorMode.Effective(new AttributionSettings(), collector));
    }

    [Fact]
    public void PrintWecSubscription_UsesTheConfiguredUuidAndOutputOfTheCollectorSectionOnly()
    {
        using var directory = new TestDataDirectory();
        var bootstrap = Path.Combine(directory.Path, "appsettings.json");
        // The shipped placeholders elsewhere in the file must not matter (instructions 4.4 run it before editing the file).
        File.WriteAllText(bootstrap, """
            {
              "Watchdog": {
                "Authorization": { "AdminSids": [ "S-1-5-21-0-0-0-0" ] },
                "Directory": { "Servers": [ "IP-LITERAL-NOT-ALLOWED" ] },
                "Collector": { "SubscriptionUuid": "0b5d3a52-6c1e-4f0e-9d7a-1f2e3d4c5b6a", "OutputDirectory": "/srv/openwec/out/" }
              }
            }
            """);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = PrintWecSubscriptionCommand.Run(["--contentRoot", "/usr/lib/admng-watchdog", "--dc", "DC01$@CONTOSO.TEST", "--dc=DC02$@CONTOSO.TEST"],
            output, error, bootstrap);

        Assert.Equal(0, exit);
        var toml = output.ToString();
        Assert.StartsWith("uuid = \"0b5d3a52-6c1e-4f0e-9d7a-1f2e3d4c5b6a\"\nname = \"admng-dc-security\"\n", toml, StringComparison.Ordinal);
        Assert.Contains("targets = [\"DC01$@CONTOSO.TEST\", \"DC02$@CONTOSO.TEST\"]", toml, StringComparison.Ordinal);
        Assert.EndsWith("config = { path = \"/srv/openwec/out/{client}/security.jsonl\" }\n", toml, StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public void PrintWecSubscription_WithoutAFile_PrintsTheShippedUuid()
    {
        using var output = new StringWriter();
        var exit = PrintWecSubscriptionCommand.Run(["--dc", "DC01$@CONTOSO.TEST"], output, TextWriter.Null, "/nonexistent/appsettings.json");

        Assert.Equal(0, exit);
        Assert.StartsWith($"uuid = \"{Uuid}\"", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData]
    [InlineData("--dc", "DC01@CONTOSO.TEST")]
    [InlineData("--dc", "DC01$@CONTOSO.TEST", "--dc", "x\" y")]
    public void PrintWecSubscription_RefusesMissingOrInvalidDcs(params string[] args)
    {
        using var error = new StringWriter();

        Assert.Equal(StartupExitCodes.InvalidSettings, PrintWecSubscriptionCommand.Run(args, TextWriter.Null, error, "/nonexistent/appsettings.json"));
        Assert.Contains("NAME$@REALM", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PrintWecSubscription_RefusesAnUnreadableCollectorSection()
    {
        using var directory = new TestDataDirectory();
        var bootstrap = Path.Combine(directory.Path, "appsettings.json");
        File.WriteAllText(bootstrap, """{ "Watchdog": { "Collector": { "SubscriptionUuid": "nie-uuid" } } }""");
        using var error = new StringWriter();

        Assert.Equal(StartupExitCodes.InvalidSettings, PrintWecSubscriptionCommand.Run(["--dc", "DC01$@CONTOSO.TEST"], TextWriter.Null, error, bootstrap));
        Assert.Contains("Watchdog:Collector", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PrintWecSubscription_QueryOnly_IsTheQueryListForGetWinEvent()
    {
        using var output = new StringWriter();

        Assert.Equal(0, PrintWecSubscriptionCommand.Run(["--query-only"], output, TextWriter.Null, "/nonexistent/appsettings.json"));
        Assert.StartsWith("<QueryList>", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(HostCommandKind.PrintWecSubscription, HostCommand.Parse(["print-wec-subscription", "--dc", "DC01$@X"]).Kind);
        Assert.Equal(["--dc", "DC01$@X"], HostCommand.Parse(["print-wec-subscription", "--dc", "DC01$@X"]).HostArgs);
    }

    [Fact]
    public async Task AVersion1Configuration_IsMigratedOnceAtStart_WithAnAuditEntry()
    {
        var store = new InMemoryWatchdogStore(TimeProvider.System);
        var v1 = new WatchdogConfiguration { SchemaVersion = 1, Attribution = new AttributionSettings { Source = AttributionSourceKind.DomainControllers } };
        await store.SaveAsync(v1, 0, new PrincipalRef { Account = "CONTOSO\\adm.łukasz" }, "zapis", [], Ct);

        Assert.True(await StartupChecks.MigrateConfigurationForLinuxAsync(store, Ct));
        Assert.False(await StartupChecks.MigrateConfigurationForLinuxAsync(store, Ct));

        var stored = (await store.GetAsync(Ct))!;
        Assert.Equal(2, stored.Version);
        Assert.Equal(WatchdogConfiguration.CurrentSchemaVersion, stored.Configuration.SchemaVersion);
        Assert.Equal(AttributionSourceKind.OpenWec, stored.Configuration.Attribution.Source);
        var audit = (await store.GetAuditAsync(null, 10, Ct)).Items;
        var migration = Assert.Single(audit, a => a.Version == 2);
        Assert.Equal(ConfigurationMigration.LinuxSummary, migration.Summary);
        Assert.Equal(ConfigurationMigration.MigrationAccount, migration.ByAccount);
    }
}
