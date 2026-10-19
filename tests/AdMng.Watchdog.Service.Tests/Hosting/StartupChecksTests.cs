using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Secrets;
using AdMng.Watchdog.Service.Storage;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdMng.Watchdog.Service.Tests.Hosting;

/// <summary>Start-up checks and their exit codes (spec 19.1).</summary>
public sealed class StartupChecksTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ADatabaseFromANewerVersion_RefusesToStart_WithExitCode3()
    {
        using var directory = new TestDataDirectory();
        await using (var database = new SqliteDatabase(directory.Path))
        {
            using var connection = database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        await using var factory = new WatchdogFactory(new Dictionary<string, string?> { ["Watchdog:DataDirectory"] = directory.Path });

        Assert.NotNull(Record.Exception(() => factory.Services));

        Assert.Equal(StartupExitCodes.Migration, factory.Terminator.ExitCode);
        Assert.Contains(factory.Log.Entries, e => e.EventId.Id == EventLogIds.FatalStartupCheck && e.Message.Contains("exit code 3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DevelopmentAuthentication_WithANetworkListener_RefusesToStart_WithExitCode7()
    {
        await using var factory = new WatchdogFactory(new Dictionary<string, string?>
        {
            ["Watchdog:Authentication"] = "Development",
            ["Watchdog:Hosting:Urls"] = "https://admng-wd.contoso.com:8443",
        });

        Assert.NotNull(Record.Exception(() => factory.Services));

        Assert.Equal(StartupExitCodes.DevelopmentAuthentication, factory.Terminator.ExitCode);
    }

    /// <summary>Plain http is allowed only on loopback listeners – for HTTP.sys as well as for Kestrel.</summary>
    [Theory]
    [InlineData("Kestrel")]
    [InlineData("HttpSys")]
    public async Task PlainHttpOnANetworkListener_RefusesToStart_WithExitCode4(string server)
    {
        await using var factory = new WatchdogFactory(new Dictionary<string, string?>
        {
            ["Watchdog:Hosting:Server"] = server,
            ["Watchdog:Hosting:Urls"] = "http://+:8080/",
        });

        Assert.NotNull(Record.Exception(() => factory.Services));

        Assert.Equal(StartupExitCodes.Certificate, factory.Terminator.ExitCode);
        Assert.Contains(factory.Log.Entries, e => e.EventId.Id == EventLogIds.FatalStartupCheck && e.Message.Contains("loopback", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Production_WithoutProtectedKeys_RefusesToStart_WithExitCode5()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "On Windows the key ring is protected with DPAPI-NG.");
        await using var factory = new WatchdogFactory(environment: "Production");

        Assert.NotNull(Record.Exception(() => factory.Services));

        Assert.Equal(StartupExitCodes.DataDirectory, factory.Terminator.ExitCode);
    }

    [Fact]
    public async Task UnreadableSecrets_DoNotStopTheService_ButAreReported()
    {
        using var directory = new TestDataDirectory();
        var store = SqliteWatchdogStore.Open(directory.Path, new SecretProtector(new EphemeralDataProtectionProvider()), TimeProvider.System, NullLogger<MigrationRunner>.Instance);
        await store.SetAsync(SecretNames.SmtpPassword, new SecretValue("Hasło-Wąż-2026!"), "smtp|x|587|y|basic", Ct);
        await store.DisposeAsync();
        await using var factory = new WatchdogFactory(new Dictionary<string, string?> { ["Watchdog:DataDirectory"] = directory.Path });

        var health = factory.Services.GetRequiredService<IHealthRegistry>();

        var check = Assert.Single(health.Checks, c => c.Code == SecretUnreadableException.HealthCode);
        Assert.Equal(HealthState.Error, check.State);
        Assert.Equal([SecretNames.SmtpPassword], check.Args);
        Assert.DoesNotContain("Hasło", factory.Log.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOutdatedRuntime_IsAHealthWarning()
    {
        await using var factory = new WatchdogFactory();
        var checks = factory.Services.GetServices<IHostedService>().OfType<StartupChecks>().Single();
        var health = factory.Services.GetRequiredService<IHealthRegistry>();

        checks.CheckRuntime(new Version(10, 0, 12));

        Assert.Equal(HealthState.Warning, Assert.Single(health.Checks, c => c.Code == RuntimeVersionCheck.HealthCode).State);
        Assert.Contains(factory.Log.Entries, e => e.EventId.Id == EventLogIds.RuntimeOrCertificate);
    }

    [Theory]
    [InlineData(10, 0, 12, true)]
    [InlineData(10, 0, 13, false)]
    [InlineData(10, 1, 0, false)]
    [InlineData(11, 0, 0, false)]
    [InlineData(9, 0, 99, true)]
    public void RuntimeVersions(int major, int minor, int build, bool outdated)
    {
        Assert.Equal(outdated, RuntimeVersionCheck.IsOutdated(new Version(major, minor, build)));
    }

    [Fact]
    public async Task StartedService_LogsEvent1000()
    {
        await using var factory = new WatchdogFactory();

        _ = factory.Services;

        Assert.Contains(factory.Log.Entries, e => e.EventId.Id == EventLogIds.ServiceStarted);
    }
}
