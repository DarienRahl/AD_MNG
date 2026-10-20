using System.Security.Claims;
using System.Text.Json;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Service.Auth;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Tests.Fakes;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Tests.Hosting;

/// <summary>
/// The bootstrap file is watched: <c>Authorization</c> (emergency revocation through <c>DeniedSids</c>), the notification
/// allow-list and the subnet lists apply without a restart, while settings the host consumed when it was built
/// (listeners, server, Kerberos enforcement, certificate, data folder, directory, authentication) stay pinned.
/// </summary>
public sealed class BootstrapReloadTests
{
    private const string ŁukaszSid = "S-1-5-21-3623811015-3361044348-30300820-1104";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DeniedSids_EditedInTheBootstrapFile_RevokeAccessWithoutARestart()
    {
        using var directory = new TestDataDirectory();
        var file = Path.Combine(directory.Path, "config", "appsettings.json");
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, Bootstrap(denied: [], urls: "https://admng-wd.contoso.test:8443", requireKerberos: true), Ct);

        using var configuration = new ConfigurationManager();
        ConfigurationSetup.AddSources(configuration, [], isManagedService: false, file, reloadBootstrapFile: true);
        var startup = configuration.GetSection(ServiceOptions.SectionName).Get<ServiceOptions>()!;
        var services = new ServiceCollection();
        ServiceRegistration.AddServiceOptions(services, configuration, startup);
        await using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<ServiceOptions>>();
        var handler = new SidRoleHandler(monitor);
        Assert.True(await IsAdminAsync(handler));

        var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = monitor.OnChange((options, _) =>
        {
            if (options.Authorization.DeniedSids.Contains(ŁukaszSid, StringComparer.OrdinalIgnoreCase))
            {
                reloaded.TrySetResult();
            }
        });

        // An incident: the operator adds the compromised admin's SID – and also (wrongly) edits settings that need a restart.
        await File.WriteAllTextAsync(file, Bootstrap(denied: [ŁukaszSid], urls: "http://+:8080", requireKerberos: false), Ct);
        await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.False(await IsAdminAsync(handler));
        Assert.Equal("https://admng-wd.contoso.test:8443", monitor.CurrentValue.Hosting.Urls);
        Assert.True(monitor.CurrentValue.Hosting.RequireKerberos);
        Assert.Equal(["10.20.30.0/24"], monitor.CurrentValue.Hosting.MonitoringSubnets);
    }

    /// <summary>An edit with a syntax error (here a missing comma) cannot be loaded: the previous settings stay in effect
    /// and warning 2002 tells the administrator that the edit – an emergency DeniedSids entry – did not apply (without
    /// any value of the file); the corrected file applies as usual.</summary>
    [Fact]
    public async Task AMalformedEdit_KeepsThePreviousSettings_AndIsReported()
    {
        using var directory = new TestDataDirectory();
        var file = Path.Combine(directory.Path, "config", "appsettings.json");
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, Bootstrap(denied: [], urls: "https://admng-wd.contoso.test:8443", requireKerberos: true), Ct);

        using var configuration = new ConfigurationManager();
        ConfigurationSetup.AddSources(configuration, [], isManagedService: false, file, reloadBootstrapFile: true);
        var startup = configuration.GetSection(ServiceOptions.SectionName).Get<ServiceOptions>()!;
        var services = new ServiceCollection();
        ServiceRegistration.AddServiceOptions(services, configuration, startup);
        await using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<ServiceOptions>>();
        var handler = new SidRoleHandler(monitor);
        var log = new TestLog();
        ConfigurationSetup.ReportReloadFailures(configuration, file, log.CreateLogger(typeof(ConfigurationSetup).FullName!));
        Assert.True(await IsAdminAsync(handler));

        await File.WriteAllTextAsync(
            file,
            $$"""{ "Watchdog": { "Authorization": { "AdminSids": [ "{{TestRoles.AdminGroup}}" ] "DeniedSids": [ "{{ŁukaszSid}}" ] } } }""",
            Ct);
        await WaitUntilAsync(() => log.Entries.Any(e => e.EventId.Id == ConfigurationSetup.ReloadFailedEventId));

        Assert.True(await IsAdminAsync(handler));
        Assert.Equal(["10.20.30.0/24"], monitor.CurrentValue.Hosting.MonitoringSubnets);
        AssertReloadWarnings(log);
        Assert.DoesNotContain(ŁukaszSid, log.Text, StringComparison.Ordinal);

        var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = monitor.OnChange((options, _) =>
        {
            if (options.Authorization.DeniedSids.Contains(ŁukaszSid, StringComparer.OrdinalIgnoreCase))
            {
                reloaded.TrySetResult();
            }
        });
        await File.WriteAllTextAsync(file, Bootstrap(denied: [ŁukaszSid], urls: "https://admng-wd.contoso.test:8443", requireKerberos: true), Ct);
        await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.False(await IsAdminAsync(handler));
    }

    /// <summary>Valid JSON with a value that cannot be bound (a Polish level name, "tak" for a flag) must not take the
    /// running service down: the options keep the previous settings – every request, the jobs and /healthz read them –
    /// and warning 2002 (no value of the file) tells the administrator; the corrected file applies as usual.</summary>
    [Fact]
    public async Task AnEditWithAValueThatCannotBeBound_KeepsThePreviousSettings_AndIsReported()
    {
        using var directory = new TestDataDirectory();
        var file = Path.Combine(directory.Path, "config", "appsettings.json");
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, Bootstrap(denied: [], urls: "https://admng-wd.contoso.test:8443", requireKerberos: true), Ct);

        using var configuration = new ConfigurationManager();
        ConfigurationSetup.AddSources(configuration, [], isManagedService: false, file, reloadBootstrapFile: true);
        var startup = configuration.GetSection(ServiceOptions.SectionName).Get<ServiceOptions>()!;
        var log = new TestLog();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(log));
        ServiceRegistration.AddServiceOptions(services, configuration, startup);
        await using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<ServiceOptions>>();
        var handler = new SidRoleHandler(monitor);
        Assert.True(await IsAdminAsync(handler));

        await File.WriteAllTextAsync(
            file,
            $$"""{ "Watchdog": { "Authorization": { "AdminSids": [ "{{TestRoles.AdminGroup}}" ], "DeniedSids": [ "{{ŁukaszSid}}" ], "ViewerDetailLevel": "Minimalny" } } }""",
            Ct);
        await WaitUntilAsync(() => log.Entries.Any(e => e.EventId.Id == ConfigurationSetup.ReloadFailedEventId));

        Assert.True(await IsAdminAsync(handler));
        Assert.Equal(["10.20.30.0/24"], monitor.CurrentValue.Hosting.MonitoringSubnets);
        AssertReloadWarnings(log);
        Assert.DoesNotContain("Minimalny", log.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(ŁukaszSid, log.Text, StringComparison.Ordinal);

        var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = monitor.OnChange((options, _) =>
        {
            if (options.Authorization.DeniedSids.Contains(ŁukaszSid, StringComparer.OrdinalIgnoreCase))
            {
                reloaded.TrySetResult();
            }
        });
        await File.WriteAllTextAsync(file, Bootstrap(denied: [ŁukaszSid], urls: "https://admng-wd.contoso.test:8443", requireKerberos: true), Ct);
        await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.False(await IsAdminAsync(handler));
    }

    [Fact]
    public void LiveOptions_KeepTheSettingsTheHostWasBuiltWith()
    {
        var startup = new ServiceOptions
        {
            DataDirectory = @"D:\AdMng\dane",
            Hosting = new HostingOptions { Urls = "https://admng-wd.contoso.test:8443", RequireKerberos = true, Certificate = new CertificateOptions { Subject = "admng-wd.contoso.test" } },
            Directory = new DirectoryBootstrapOptions { Server = "dc01.contoso.test" },
        };
        var live = new ServiceOptions
        {
            DataDirectory = @"E:\inne",
            Authentication = WatchdogAuthenticationMode.Development,
            Hosting = new HostingOptions { Urls = "http://+:8080", RequireKerberos = false, Server = WatchdogServer.HttpSys, AllowedRemoteSubnets = ["10.0.0.0/8"], MonitoringSubnets = ["10.9.9.0/24"] },
            Directory = new DirectoryBootstrapOptions { Server = "dc02.contoso.test" },
            Development = new DevelopmentOptions { Account = @"LAB\łucja" },
            Authorization = new Service.Hosting.AuthorizationOptions { DeniedSids = [ŁukaszSid], ViewerDetailLevel = Contracts.Configuration.DetailLevel.Minimal },
            Notifications = new NotificationBootstrapOptions { OAuthSmtpHosts = ["smtp.office365.us"] },
        };

        var effective = ServiceOptionsFactory.Pin(live, startup);

        Assert.Equal(startup.DataDirectory, effective.DataDirectory);
        Assert.Equal(startup.Authentication, effective.Authentication);
        Assert.Equal(startup.Hosting.Urls, effective.Hosting.Urls);
        Assert.Equal(startup.Hosting.Server, effective.Hosting.Server);
        Assert.True(effective.Hosting.RequireKerberos);
        Assert.Equal("admng-wd.contoso.test", effective.Hosting.Certificate.Subject);
        Assert.Equal("dc01.contoso.test", effective.Directory.Server);
        Assert.Equal(@"LAB\developer", effective.Development.Account);

        Assert.Equal(["10.0.0.0/8"], effective.Hosting.AllowedRemoteSubnets);
        Assert.Equal(["10.9.9.0/24"], effective.Hosting.MonitoringSubnets);
        Assert.Equal([ŁukaszSid], effective.Authorization.DeniedSids);
        Assert.Equal(Contracts.Configuration.DetailLevel.Minimal, effective.Authorization.ViewerDetailLevel);
        Assert.Equal(["smtp.office365.us"], effective.Notifications.OAuthSmtpHosts);
    }

    /// <summary>Warning 2002 – once per reload the watcher saw (one save may fire it twice: truncate, then write), readable
    /// text (no control characters from an escape sequence in the template) naming the file.</summary>
    private static void AssertReloadWarnings(TestLog log)
    {
        var warnings = log.Entries.Where(e => e.EventId.Id == ConfigurationSetup.ReloadFailedEventId).ToList();
        Assert.NotEmpty(warnings);
        Assert.All(warnings, w =>
        {
            Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, w.Level);
            Assert.Contains("appsettings.json", w.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(w.Message, char.IsControl);
        });
    }

    /// <summary>Waits (real time – the file watcher) until <paramref name="condition"/> holds, at most 30 s.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }

    private static async Task<bool> IsAdminAsync(SidRoleHandler handler)
    {
        var identity = new ClaimsIdentity("Kerberos");
        identity.AddClaim(new Claim(ClaimTypes.Name, @"CONTOSO\adm.łukasz"));
        identity.AddClaim(new Claim(ClaimTypes.PrimarySid, ŁukaszSid));
        identity.AddClaim(new Claim(ClaimTypes.GroupSid, TestRoles.AdminGroup));
        var context = new AuthorizationHandlerContext([new SidRoleRequirement(WatchdogRole.Admin)], new ClaimsPrincipal(identity), null);
        await handler.HandleAsync(context);
        return context.HasSucceeded && !context.HasFailed;
    }

    private static string Bootstrap(string[] denied, string urls, bool requireKerberos) => JsonSerializer.Serialize(new
    {
        Watchdog = new
        {
            Hosting = new { Urls = urls, RequireKerberos = requireKerberos, MonitoringSubnets = new[] { "10.20.30.0/24" } },
            Authorization = new { ViewerSids = new[] { TestRoles.ViewerGroup }, AdminSids = new[] { TestRoles.AdminGroup }, DeniedSids = denied },
        },
    });
}
