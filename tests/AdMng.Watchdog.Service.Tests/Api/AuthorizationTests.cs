using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Service.Tests.Infrastructure;

namespace AdMng.Watchdog.Service.Tests.Api;

/// <summary>Authentication, roles by group SID, deny list, CSRF and API-version rules (spec 17.1, 17.3).</summary>
public sealed class AuthorizationTests : IAsyncLifetime
{
    private readonly WatchdogFactory _factory = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Anonymous_Gets401_EvenForUnknownRoutes()
    {
        using var client = _factory.Client(account: null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(WatchdogRoutes.Me, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/nie-ma", Ct)).StatusCode);
    }

    [Fact]
    public async Task UnknownGroup_Gets403()
    {
        using var client = _factory.Client(@"CONTOSO\gość", TestRoles.OtherGroup);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(WatchdogRoutes.Status, Ct)).StatusCode);
    }

    [Fact]
    public async Task Viewer_ReadsButCannotChange()
    {
        using var client = _factory.Viewer();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(WatchdogRoutes.Status, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(WatchdogRoutes.Config, Ct)).StatusCode);
        using var put = await client.PutAsync(WatchdogRoutes.Config, ApiJson.Content(new ConfigEnvelope { Version = 0, Configuration = new WatchdogConfiguration() }), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(WatchdogRoutes.Outbox, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(WatchdogRoutes.ConfigAudit, Ct)).StatusCode);
    }

    [Fact]
    public async Task Admin_CanChange()
    {
        using var client = _factory.Admin();

        using var put = await client.PutAsync(WatchdogRoutes.Config, ApiJson.Content(new ConfigEnvelope { Version = 0, Configuration = new WatchdogConfiguration() }), Ct);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
    }

    [Fact]
    public async Task PrimaryGroupSid_GrantsTheRole()
    {
        using var client = _factory.Client(@"CONTOSO\ewa.wiśniewska");
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.PrimaryGroupHeader, TestRoles.AdminGroup);

        var me = await ApiJson.ReadAsync<MeDto>(await client.GetAsync(WatchdogRoutes.Me, Ct));

        Assert.Equal([WatchdogRole.Viewer, WatchdogRole.Admin], me.Roles);
        Assert.Equal(DetailLevel.Full, me.DetailLevel);
    }

    [Fact]
    public async Task DeniedSid_OverridesAdmin()
    {
        using var client = _factory.Client(@"CONTOSO\były.admin", TestRoles.AdminGroup);
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.UserSidHeader, TestRoles.DeniedUser);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(WatchdogRoutes.Me, Ct)).StatusCode);
    }

    [Fact]
    public async Task Me_DescribesTheCaller()
    {
        using var client = _factory.Viewer();

        var me = await ApiJson.ReadAsync<MeDto>(await client.GetAsync(WatchdogRoutes.Me, Ct));

        Assert.Equal(TestRoles.ViewerAccount, me.Account);
        Assert.Equal("Kerberos", me.AuthenticationType);
        Assert.Equal([WatchdogRole.Viewer], me.Roles);
        Assert.Equal(DetailLevel.Standard, me.DetailLevel);
    }

    [Fact]
    public async Task StateChanges_RequireTheClientHeader()
    {
        using var client = _factory.Admin();
        client.DefaultRequestHeaders.Remove(WatchdogRoutes.ClientHeader);

        using var response = await client.PostAsync(WatchdogRoutes.DigestSend, null, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiProblem.ClientHeaderMissing, (await ApiJson.ProblemAsync(response)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task FormPosts_AreRejected()
    {
        using var client = _factory.Admin();
        using var form = new StringContent("ids=1", Encoding.UTF8);
        form.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");

        using var response = await client.PostAsync(WatchdogRoutes.EventsAcknowledge, form, Ct);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task AnotherApiVersion_IsRejected_AndEveryResponseCarriesTheVersion()
    {
        using var client = _factory.Viewer();
        using var ok = await client.GetAsync(WatchdogRoutes.Me, Ct);
        Assert.Equal("1", Assert.Single(ok.Headers.GetValues(WatchdogRoutes.ApiVersionHeader)));

        client.DefaultRequestHeaders.Add(WatchdogRoutes.ApiVersionHeader, "2");
        using var rejected = await client.GetAsync(WatchdogRoutes.Me, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("1", Assert.Single(rejected.Headers.GetValues(WatchdogRoutes.ApiVersionHeader)));
    }

    [Fact]
    public async Task Catalog_ListsEveryEventKind()
    {
        using var client = _factory.Viewer();

        var catalog = await ApiJson.ReadAsync<IReadOnlyList<Contracts.Events.EventKindInfo>>(await client.GetAsync(WatchdogRoutes.Catalog, Ct));

        Assert.Equal(Contracts.Events.EventKindCatalog.All.Count, catalog.Count);
    }
}
