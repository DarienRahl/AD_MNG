using System.Text.Json;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Diagnostics;
using AdMng.Watchdog.Service.Notifications;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// <c>--smoke-test</c> (CI, spec 19.1): a temporary data folder, loopback HTTP on an ephemeral port, the Development
/// scheme with a smoke-test Viewer SID, directory and attribution disabled; runs the start-up checks (migrations),
/// GETs <c>/healthz</c> and <c>/api/v1/status</c>, renders a sample event with the HTML renderer (when registered) and
/// the test messages of every channel type; exit code 0/1. The temporary folder is deleted afterwards.
/// </summary>
public static class SmokeTest
{
    private const string SmokeSid = "S-1-5-21-1000000000-1000000000-1000000000-1001";

    public static async Task<int> RunAsync(string[] args)
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "admng-watchdog-smoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            return await RunCoreAsync(args, dataDirectory).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await Console.Error.WriteLineAsync("Smoke test failed: " + SafeExceptionLog.Describe(ex)).ConfigureAwait(false);
            return WatchdogHost.ExitCodeOf(ex);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                System.IO.Directory.Delete(dataDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task<int> RunCoreAsync(string[] args, string dataDirectory)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Watchdog:DataDirectory"] = dataDirectory,
            ["Watchdog:Authentication"] = nameof(WatchdogAuthenticationMode.Development),
            ["Watchdog:Hosting:Server"] = nameof(WatchdogServer.Kestrel),
            ["Watchdog:Hosting:Urls"] = "http://127.0.0.1:0",
            ["Watchdog:Directory:Disabled"] = "true",
            ["Watchdog:Authorization:ViewerSids:0"] = SmokeSid,
            ["Watchdog:Development:Sids:0"] = SmokeSid,
            ["Watchdog:Development:Account"] = @"SMOKE\test",
        };
        await using var app = WatchdogHost.Build(
            [.. args, "--environment", Environments.Development],
            builder => builder.Configuration.AddInMemoryCollection(settings),
            console: true);
        await app.StartAsync().ConfigureAwait(false);
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
                ?? throw new InvalidOperationException("The server reported no listening address.");
            using var http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };
            var health = await http.GetStringAsync(WatchdogRoutes.Health).ConfigureAwait(false);
            Report(WatchdogRoutes.Health, health == "ok");

            var statusJson = await http.GetStringAsync(WatchdogRoutes.Status).ConfigureAwait(false);
            var status = JsonSerializer.Deserialize<StatusDto>(statusJson, WatchdogJson.Options);
            Report(WatchdogRoutes.Status, status is not null && status.ApiVersion == WatchdogRoutes.ApiVersion);

            var rendered = Render(app.Services);
            return health == "ok" && status is not null && rendered ? 0 : 1;
        }
        finally
        {
            await app.StopAsync().ConfigureAwait(false);
        }
    }

    private static bool Render(IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IWatchdogConfigProvider>().Current.Configuration;
        var ok = true;
        var email = TestMessages.Email(configuration);
        ok &= Report("test e-mail", email.HtmlBody.Length > 0 && email.TextBody.Length > 0);
        ok &= Report("test Teams card", TestMessages.TeamsCard(configuration).Length > 0);
        ok &= Report("test webhook", TestMessages.Webhook(configuration, Guid.CreateVersion7().ToString("N"), DateTimeOffset.UtcNow).Length > 0);
        if (services.GetService<IEventHtmlRenderer>() is { } renderer)
        {
            var html = renderer.Render(new EventDetails { Event = SampleEvent() });
            ok &= Report("HTML event page", html.Contains("<html", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            Console.WriteLine("Smoke test: HTML event page renderer not registered – skipped.");
        }

        return ok;
    }

    private static WatchEvent SampleEvent() => new()
    {
        EventId = Guid.CreateVersion7(),
        Kind = WatchEventKind.UserCreated,
        Severity = Severity.Low,
        Object = new ObjectRef
        {
            Dn = "CN=Zofia Żak,OU=Księgowość,DC=contoso,DC=test",
            Name = "Zofia Żak",
            SamAccountName = "zofia.zak",
            Kind = WatchObjectKind.User,
        },
        DetectedAt = DateTimeOffset.UtcNow,
        Source = DetectionSource.Test,
    };

    private static bool Report(string check, bool ok)
    {
        Console.WriteLine($"Smoke test: {check} {(ok ? "ok" : "FAILED")}");
        return ok;
    }
}
