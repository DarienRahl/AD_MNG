using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Hosting.Fallbacks;
using AdMng.Watchdog.Service.Tests.Fakes;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using Microsoft.Extensions.Logging;

using RoleOptions = AdMng.Watchdog.Service.Hosting.AuthorizationOptions;

namespace AdMng.Watchdog.Service.Tests.Hosting;

/// <summary>Bootstrap validation, data folder, log redaction and the fallback signal hub.</summary>
public sealed class HostingUnitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void BootstrapValidation_FindsTheUsualMistakes()
    {
        var options = new ServiceOptions
        {
            Hosting = new HostingOptions
            {
                Urls = "http://admng-wd.contoso.com:8080;https://+:8443",
                Certificate = new CertificateOptions(),
                MonitoringSubnets = ["10.0.0.0/33"],
            },
            Authorization = new RoleOptions { AdminSids = ["S-1-5-21-1-2-3-512", "Domain Admins"] },
            Authentication = WatchdogAuthenticationMode.Development,
            Directory = new DirectoryBootstrapOptions { Authentication = AdMng.Core.Directory.AuthenticationMode.SimpleBind, AllowInsecureLab = true },
        };

        var codes = ServiceOptionsValidator.Validate(options, "Production").Select(i => i.Code).ToList();

        Assert.Contains("insecureUrl", codes);
        Assert.Contains("required", codes);              // certificate and LabSimpleBindUser
        Assert.Contains("invalidSubnet", codes);
        Assert.Contains("invalidSid", codes);
        Assert.Contains("developmentAuthentication", codes);
        Assert.Contains("labOnly", codes);
    }

    [Fact]
    public void BootstrapValidation_AcceptsTheShippedShape()
    {
        var options = new ServiceOptions
        {
            Hosting = new HostingOptions
            {
                Urls = "https://admng-wd.contoso.com:8443",
                Certificate = new CertificateOptions { Subject = "admng-wd.contoso.com" },
                AllowedRemoteSubnets = ["10.10.0.0/16"],
            },
            Authorization = new RoleOptions { ViewerSids = [TestRoles.ViewerGroup], AdminSids = [TestRoles.AdminGroup] },
        };

        Assert.DoesNotContain(ServiceOptionsValidator.Validate(options, "Production"), i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void DataDirectory_IsCreatedPrivate_WithKeysAndBackups()
    {
        using var root = new TestDataDirectory();
        var directory = new DataDirectory(Path.Combine(root.Path, "dane-łódź"));

        directory.Ensure();

        Assert.True(directory.CreatedNow);
        Assert.True(System.IO.Directory.Exists(directory.KeysPath));
        Assert.True(System.IO.Directory.Exists(directory.BackupPath));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory.Path));
        }

        Assert.Equal(4, directory.ProtectedPaths().Count);
    }

    [Fact]
    public void DataDirectory_ExpandsEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("ADMNG_TEST_DATA_ROOT", Path.GetTempPath());
        try
        {
            var resolved = DataDirectory.Resolve(new ServiceOptions { DataDirectory = Path.Combine("%ADMNG_TEST_DATA_ROOT%", "watchdog") });

            Assert.Equal(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "watchdog")), resolved);
            Assert.DoesNotContain("%", DataDirectory.Resolve(new ServiceOptions()), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ADMNG_TEST_DATA_ROOT", null);
        }
    }

    [Fact]
    public void RedactingProvider_DropsExceptionMessages_ButKeepsTypes()
    {
        var sink = new TestLog();
        using var provider = new RedactingLoggerProvider(sink);
        var logger = provider.CreateLogger("Microsoft.AspNetCore.Server.Kestrel");

        logger.LogError(new FormatException("Wartość 'Zofia Żak' nie jest liczbą"), "Request failed for {Path}", "/api/v1/events");

        var entry = Assert.Single(sink.Entries);
        Assert.Null(entry.Exception);
        Assert.Contains("FormatException", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Zofia", entry.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FallbackSignals_WakeOnlyMatchingSubscriptions_AndTimeOutOnTheClock()
    {
        var clock = new TestClock(TestStore.Start);
        var signals = new FallbackWatchdogSignals(clock);
        using var outbox = signals.Subscribe([WatchdogSignal.OutboxEnqueued]);
        using var config = signals.Subscribe([WatchdogSignal.ConfigChanged]);

        signals.Raise(WatchdogSignal.OutboxEnqueued);

        Assert.True(await outbox.WaitAsync(TimeSpan.FromMinutes(1), Ct));
        var timer = clock.WaitForTimerAsync(TimeSpan.FromMinutes(1));
        var waiting = config.WaitAsync(TimeSpan.FromMinutes(1), Ct);
        await timer.WaitAsync(Ct);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(await waiting);

        signals.Raise(WatchdogSignal.ConfigChanged);
        signals.Raise(WatchdogSignal.ConfigChanged);
        Assert.True(await config.WaitAsync(TimeSpan.Zero, Ct));
        var reset = clock.WaitForTimerAsync(TimeSpan.FromSeconds(1));
        var second = config.WaitAsync(TimeSpan.FromSeconds(1), Ct);
        await reset.WaitAsync(Ct);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(await second);   // the latch was reset by the previous wait
    }

    [Fact]
    public void FallbackHealthRegistry_KeepsTheLastReportPerCode()
    {
        var registry = new FallbackHealthRegistry();

        registry.Report(new HealthCheckDto { Code = "storage.free", State = HealthState.Ok });
        registry.Report(new HealthCheckDto { Code = "storage.free", State = HealthState.Warning });
        registry.ReportChannel(new ChannelHealthDto { ChannelId = "email", ChannelType = DeliveryChannelType.Email, State = HealthState.Ok });

        Assert.Equal(HealthState.Warning, Assert.Single(registry.Checks).State);
        Assert.Single(registry.Channels);
        Assert.Equal(HealthState.Unknown, registry.Directory.State);
    }
}
