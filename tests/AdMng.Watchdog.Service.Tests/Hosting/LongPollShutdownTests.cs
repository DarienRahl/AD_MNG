using System.Net;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Service.Auth;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Storage;
using AdMng.Watchdog.Service.Tests.Fakes;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Watchdog.Service.Tests.Hosting;

/// <summary>
/// A service stop (sc stop, an update by the install script) must not wait for open GUI long-polls: Kestrel drains
/// in-flight requests on a graceful stop without cancelling them, so each open tab held the stop for up to 30 s of the
/// 30 s shutdown budget. Stopping ends the wait at once and the request answers with the current page.
/// </summary>
public sealed class LongPollShutdownTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Stop_EndsAPendingLongPoll_AtOnce()
    {
        using var directory = new TestDataDirectory();
        var clock = new TestClock(TestStore.Start);   // never advanced: only the stop can end the 30 s wait
        var settings = new Dictionary<string, string?>
        {
            ["Watchdog:DataDirectory"] = directory.Path,
            ["Watchdog:Authentication"] = "Test",
            ["Watchdog:Hosting:Urls"] = "http://127.0.0.1:0",
            ["Watchdog:Directory:Disabled"] = "true",
            ["Watchdog:Authorization:ViewerSids:0"] = TestRoles.ViewerGroup,
        };
        await using var app = WatchdogHost.Build(["--environment", "Development"], builder =>
        {
            builder.Configuration.AddInMemoryCollection(settings);
            builder.Services.AddSingleton<TimeProvider>(clock);
            builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            builder.Services.AddAuthentication().AddScheme<TestAuthenticationOptions, TestAuthenticationHandler>(AuthenticationSetup.TestScheme, null);
        });
        await app.StartAsync(Ct);
        var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
        var first = await app.Services.GetRequiredService<SqliteWatchdogStore>().AppendAsync(TestData.Event(WatchEventKind.UserCreated, TestData.User("Zofia Żak")), Ct);
        using var client = new HttpClient { BaseAddress = address };
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.AccountHeader, Uri.EscapeDataString(TestRoles.ViewerAccount));
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.SidsHeader, TestRoles.ViewerGroup);
        var waiting = clock.WaitForTimerAsync(TimeSpan.FromSeconds(30));
        var polling = client.GetAsync($"{WatchdogRoutes.Events}?after={first}&wait=30", Ct);
        await waiting.WaitAsync(Ct);

        await app.StopAsync(Ct).WaitAsync(TimeSpan.FromSeconds(15), Ct);

        using var response = await polling.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await ApiJson.ReadAsync<EventPage>(response);
        Assert.Empty(page.Items);
        Assert.Equal(first, page.LatestId);
    }
}
