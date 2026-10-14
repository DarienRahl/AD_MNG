using System.Net;
using AdMng.Watchdog.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AdMng.Watchdog.Service.Tests.Hosting;

public sealed class HealthEndpointTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Healthz_AnswersOk()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(WatchdogRoutes.Health, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync(ct));
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
    }
}
