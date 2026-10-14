using System.Net;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Service.Tests.Infrastructure;

namespace AdMng.Watchdog.Service.Tests.Api;

/// <summary>The delivery log and manual retry (spec 17.2).</summary>
public sealed class OutboxApiTests : IAsyncLifetime
{
    private readonly WatchdogFactory _factory = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task List_FiltersByStatusAndChannel()
    {
        using var admin = _factory.Admin();
        var dead = await _factory.Store.EnqueueAsync(TestData.Message(channelId: "siem"), Ct);
        await _factory.Store.MarkDeadAsync(dead, "http.404", null, Ct);
        await _factory.Store.EnqueueAsync(TestData.Message(channelId: "teams-pomoc", type: DeliveryChannelType.Teams), Ct);

        var all = await ApiJson.ReadAsync<OutboxPage>(await admin.GetAsync(WatchdogRoutes.Outbox, Ct));
        var deadOnly = await ApiJson.ReadAsync<OutboxPage>(await admin.GetAsync(WatchdogRoutes.Outbox + "?" + new OutboxQuery { Status = OutboxStatus.Dead }.ToQueryString(), Ct));
        var teams = await ApiJson.ReadAsync<OutboxPage>(await admin.GetAsync(WatchdogRoutes.Outbox + "?" + new OutboxQuery { ChannelId = "teams-pomoc" }.ToQueryString(), Ct));

        Assert.Equal(2, all.Items.Count);
        Assert.Equal(dead, Assert.Single(deadOnly.Items).Id);
        Assert.Equal(DeliveryChannelType.Teams, Assert.Single(teams.Items).ChannelType);
    }

    [Fact]
    public async Task Retry_RequeuesDead_Conflicts_OtherwiseNotFound()
    {
        using var admin = _factory.Admin();
        var dead = await _factory.Store.EnqueueAsync(TestData.Message(), Ct);
        await _factory.Store.MarkDeadAsync(dead, "http.404", null, Ct);
        var pending = await _factory.Store.EnqueueAsync(TestData.Message(), Ct);

        using var retried = await admin.PostAsync(WatchdogRoutes.OutboxRetryPath(dead), null, Ct);
        using var conflict = await admin.PostAsync(WatchdogRoutes.OutboxRetryPath(pending), null, Ct);
        using var missing = await admin.PostAsync(WatchdogRoutes.OutboxRetryPath(424242), null, Ct);

        Assert.Equal(HttpStatusCode.NoContent, retried.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var page = await _factory.Store.QueryAsync(new OutboxQuery { Status = OutboxStatus.Pending }, Ct);
        Assert.Contains(page.Items, i => i.Id == dead);
    }

    [Theory]
    [InlineData("status=Gone")]
    [InlineData("limit=0")]
    [InlineData("before=x")]
    public async Task InvalidQueries_Are400(string query)
    {
        using var admin = _factory.Admin();

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(WatchdogRoutes.Outbox + "?" + query, Ct)).StatusCode);
    }
}
