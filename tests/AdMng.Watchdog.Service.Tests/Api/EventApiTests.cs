using System.Net;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Service.Tests.Infrastructure;

namespace AdMng.Watchdog.Service.Tests.Api;

/// <summary>The event feed, long-poll, details, acknowledgement and viewer detail levels (spec 17.2).</summary>
public sealed class EventApiTests : IAsyncLifetime
{
    private readonly WatchdogFactory _factory = new(new Dictionary<string, string?> { ["Watchdog:Authorization:ViewerDetailLevel"] = "Standard" });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Paging_DescendingWithBefore_AscendingWithAfter()
    {
        using var client = _factory.Viewer();
        var ids = await AppendAsync(5);

        var newest = await GetPageAsync(client, "limit=2");
        Assert.Equal([ids[4], ids[3]], newest.Items.Select(e => e.Id));
        Assert.True(newest.HasMore);
        Assert.Equal(ids[4], newest.LatestId);

        var older = await GetPageAsync(client, $"limit=2&before={ids[3]}");
        Assert.Equal([ids[2], ids[1]], older.Items.Select(e => e.Id));

        var live = await GetPageAsync(client, $"after={ids[1]}");
        Assert.Equal([ids[2], ids[3], ids[4]], live.Items.Select(e => e.Id));
    }

    [Fact]
    public async Task Filters_ByTextOnWorkstation_KindAndAttributionStatus()
    {
        using var client = _factory.Viewer();
        await _factory.Store.AppendAsync(TestData.Event(WatchEventKind.UserCreated, attribution: TestData.Resolved()), Ct);
        await _factory.Store.AppendAsync(TestData.Event(WatchEventKind.AccountDisabled, TestData.User("Marek Wójcik")), Ct);

        var byWorkstation = await GetPageAsync(client, "text=" + Uri.EscapeDataString("ws-łódź"));
        var byKind = await GetPageAsync(client, "kind=AccountDisabled");
        var byStatus = await GetPageAsync(client, "attributionStatus=Resolved");

        Assert.Equal(WatchEventKind.UserCreated, Assert.Single(byWorkstation.Items).Kind);
        Assert.Equal("Marek Wójcik", Assert.Single(byKind.Items).Object.Name);
        Assert.Equal(WatchEventKind.UserCreated, Assert.Single(byStatus.Items).Kind);
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=501")]
    [InlineData("wait=5")]
    [InlineData("after=1&wait=31")]
    [InlineData("kind=NieMa")]
    [InlineData("objectGuid=Zofia")]
    public async Task InvalidQueries_Are400(string query)
    {
        using var client = _factory.Viewer();

        using var response = await client.GetAsync($"{WatchdogRoutes.Events}?{query}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("Zofia", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LongPoll_ReturnsWhenAnEventIsAppended()
    {
        using var client = _factory.Viewer();
        var ids = await AppendAsync(1);

        var waiting = _factory.Clock.WaitForTimerAsync(TimeSpan.FromSeconds(30));
        var polling = client.GetAsync($"{WatchdogRoutes.Events}?after={ids[0]}&wait=30", Ct);
        await waiting.WaitAsync(Ct);
        Assert.False(polling.IsCompleted);
        var appended = await _factory.Store.AppendAsync(TestData.Event(WatchEventKind.AccountEnabled), Ct);

        using var response = await polling;
        var page = await ApiJson.ReadAsync<EventPage>(response);
        Assert.Equal(appended, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task LongPoll_ReturnsEmptyAfterTheWait()
    {
        using var client = _factory.Viewer();
        var ids = await AppendAsync(1);

        var waiting = _factory.Clock.WaitForTimerAsync(TimeSpan.FromSeconds(10));
        var polling = client.GetAsync($"{WatchdogRoutes.Events}?after={ids[0]}&wait=10", Ct);
        await waiting.WaitAsync(Ct);
        _factory.Clock.Advance(TimeSpan.FromSeconds(10));

        var page = await ApiJson.ReadAsync<EventPage>(await polling);
        Assert.Empty(page.Items);
        Assert.Equal(ids[0], page.LatestId);
    }

    [Fact]
    public async Task Details_IncludeTheChangeSetAndDeliveries_AndTheAccessIsLogged()
    {
        using var client = _factory.Viewer();
        var changeSet = Guid.CreateVersion7();
        var created = await _factory.Store.AppendAsync(TestData.Event(WatchEventKind.UserCreated) with { ChangeSetId = changeSet }, Ct);
        var enabled = await _factory.Store.AppendAsync(TestData.Event(WatchEventKind.AccountEnabled) with { ChangeSetId = changeSet }, Ct);
        await _factory.Store.EnqueueAsync(TestData.Message(eventIds: [created]), Ct);

        var details = await ApiJson.ReadAsync<EventDetails>(await client.GetAsync(WatchdogRoutes.EventByIdPath(created), Ct));

        Assert.Equal(created, details.Event.Id);
        Assert.Equal(enabled, Assert.Single(details.RelatedEvents).Id);
        Assert.Equal("siem", Assert.Single(details.Deliveries).ChannelId);
        var access = Assert.Single(_factory.Log.Entries, e => e.EventId.Id == 2600);
        Assert.Contains(created.ToString(System.Globalization.CultureInfo.InvariantCulture), access.Message, StringComparison.Ordinal);
        Assert.Contains(TestRoles.ViewerAccount, access.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Zofia", access.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingEvent_Is404()
    {
        using var client = _factory.Viewer();

        using var response = await client.GetAsync(WatchdogRoutes.EventByIdPath(424242), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiProblem.NotFound, (await ApiJson.ProblemAsync(response)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Acknowledge_IsIdempotent_AndStoresTheCaller()
    {
        using var client = _factory.Viewer();
        var ids = await AppendAsync(1);

        using var first = await client.PostAsync(WatchdogRoutes.EventAcknowledgePath(ids[0]), ApiJson.Content(new AcknowledgeRequest { Comment = "Sprawdzone – zgłoszenie HR" }), Ct);
        _factory.Clock.Advance(TimeSpan.FromMinutes(5));
        using var second = await client.PostAsync(WatchdogRoutes.EventAcknowledgePath(ids[0]), null, Ct);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        var stored = await _factory.Store.GetAsync(ids[0], Ct);
        Assert.Equal(TestRoles.ViewerAccount, stored!.AcknowledgedBy);
        Assert.Equal("Sprawdzone – zgłoszenie HR", stored.AcknowledgeComment);
        Assert.Equal(TestStore.Start, stored.AcknowledgedAt);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(WatchdogRoutes.EventAcknowledgePath(424242), null, Ct)).StatusCode);
    }

    [Fact]
    public async Task BulkAcknowledge_AcknowledgesAll_AndChecksTheRange()
    {
        using var client = _factory.Viewer();
        var ids = await AppendAsync(3);

        using var ok = await client.PostAsync(WatchdogRoutes.EventsAcknowledge, ApiJson.Content(new BulkAcknowledgeRequest { Ids = ids }), Ct);
        using var empty = await client.PostAsync(WatchdogRoutes.EventsAcknowledge, ApiJson.Content(new BulkAcknowledgeRequest()), Ct);
        using var tooLong = await client.PostAsync(
            WatchdogRoutes.EventsAcknowledge, ApiJson.Content(new BulkAcknowledgeRequest { Ids = ids, Comment = new string('ż', 501) }), Ct);

        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        var page = await _factory.Store.QueryAsync(new EventQuery { UnacknowledgedOnly = true }, Ct);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task Viewers_GetTheViewerDetailLevel_AdminsEverything()
    {
        var change = new AttributeChange { Attribute = "telephoneNumber", OldValues = ["+48 22 555 01 01"], NewValues = ["+48 601 234 567"] };
        var department = new AttributeChange { Attribute = "department", OldValues = ["Kadry"], NewValues = ["Księgowość"] };
        var id = await _factory.Store.AppendAsync(TestData.Event(WatchEventKind.AttributesChanged, changes: [change, department]), Ct);
        using var viewer = _factory.Viewer();
        using var admin = _factory.Admin();

        var asViewer = await ApiJson.ReadAsync<EventDetails>(await viewer.GetAsync(WatchdogRoutes.EventByIdPath(id), Ct));
        var asAdmin = await ApiJson.ReadAsync<EventDetails>(await admin.GetAsync(WatchdogRoutes.EventByIdPath(id), Ct));

        var phone = asViewer.Event.Changes.Single(c => c.Attribute == "telephoneNumber");
        Assert.True(phone.Redacted);
        Assert.Empty(phone.NewValues);
        Assert.Equal(["Księgowość"], asViewer.Event.Changes.Single(c => c.Attribute == "department").NewValues);
        Assert.Equal(["+48 601 234 567"], asAdmin.Event.Changes.Single(c => c.Attribute == "telephoneNumber").NewValues);
    }

    [Fact]
    public async Task MinimalDetailLevel_HidesValuesAndSources()
    {
        await using var factory = new WatchdogFactory(new Dictionary<string, string?> { ["Watchdog:Authorization:ViewerDetailLevel"] = "Minimal" });
        var change = new AttributeChange { Attribute = "department", OldValues = ["Kadry"], NewValues = ["Księgowość"] };
        await factory.Store.AppendAsync(TestData.Event(WatchEventKind.AttributesChanged, attribution: TestData.Resolved(), changes: [change]), Ct);
        using var viewer = factory.Viewer();

        var page = await ApiJson.ReadAsync<EventPage>(await viewer.GetAsync(WatchdogRoutes.Events, Ct));

        var item = Assert.Single(page.Items);
        Assert.Empty(item.Changes[0].NewValues);
        Assert.Null(item.Attribution.Workstation);
        Assert.Null(item.Attribution.IpAddress);
        Assert.Equal(@"CONTOSO\adm.łukasz", item.Attribution.Actor?.Account);
    }

    private async Task<long[]> AppendAsync(int count)
    {
        var ids = new long[count];
        for (var i = 0; i < count; i++)
        {
            ids[i] = await _factory.Store.AppendAsync(TestData.Event(WatchEventKind.UserCreated, TestData.User($"Użytkownik {i}")), Ct);
        }

        return ids;
    }

    private static async Task<EventPage> GetPageAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync($"{WatchdogRoutes.Events}?{query}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiJson.ReadAsync<EventPage>(response);
    }
}
