using System.Net;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Service.Tests.Fakes;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using AdMng.Watchdog.Testing.Certificates;
using AdMng.Watchdog.Testing.Http;
using AdMng.Watchdog.Testing.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdMng.Watchdog.Service.Tests.Client;

/// <summary><see cref="WatchdogHttpClient"/> against the real server over https (spec 22.2).</summary>
public sealed class WatchdogHttpClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryMethod_TalksToTheService()
    {
        await using var server = WatchdogClientServer.Start(TestRoles.AdminAccount, TestRoles.AdminGroup);
        using var client = server.Client();
        var id = await server.Factory.Store.AppendAsync(TestData.Event(WatchEventKind.UserCreated), Ct);
        var dead = await server.Factory.Store.EnqueueAsync(TestData.Message(), Ct);
        await server.Factory.Store.MarkDeadAsync(dead, "http.404", null, Ct);

        var me = await client.GetMeAsync(Ct);
        var status = await client.GetStatusAsync(Ct);
        var catalog = await client.GetCatalogAsync(Ct);
        var page = await client.GetEventsAsync(new EventQuery { Kinds = [WatchEventKind.UserCreated] }, Ct);
        var details = await client.GetEventAsync(id, Ct);
        await client.AcknowledgeAsync(id, new AcknowledgeRequest { Comment = "Sprawdzone – Łukasz" }, Ct);
        await client.AcknowledgeManyAsync(new BulkAcknowledgeRequest { Ids = [id] }, Ct);
        var defaults = await client.GetDefaultConfigAsync(Ct);
        var saved = await client.SaveConfigAsync(new ConfigEnvelope { Version = 0, Configuration = defaults with { General = defaults.General with { InstanceName = "Centrala – Łódź" } } }, Ct);
        var config = await client.GetConfigAsync(Ct);
        var validation = await client.ValidateConfigAsync(config.Configuration, Ct);
        var audit = await client.GetConfigAuditAsync(null, 10, Ct);
        var test = await client.SendTestEmailAsync(new TestEmailRequest { Recipients = ["jan.kowalski@contoso.test"] }, Ct);
        var outbox = await client.GetOutboxAsync(new OutboxQuery { Status = OutboxStatus.Dead }, Ct);
        await client.RetryOutboxAsync(dead, Ct);

        Assert.Equal(TestRoles.AdminAccount, me.Account);
        Assert.Equal("Centrala – Łódź", (await client.GetStatusAsync(Ct)).InstanceName);
        Assert.Equal(1, status.ApiVersion);
        Assert.Equal(EventKindCatalog.All.Count, catalog.Count);
        Assert.Equal(id, Assert.Single(page.Items).Id);
        Assert.Equal(id, details.Event.Id);
        Assert.Equal(1, saved.Version);
        Assert.Equal("Centrala – Łódź", config.Configuration.General.InstanceName);
        Assert.True(validation.IsValid);
        Assert.Single(audit.Items);
        Assert.False(test.Success);
        Assert.Equal(dead, Assert.Single(outbox.Items).Id);
        Assert.Equal(TestRoles.AdminAccount, (await server.Factory.Store.GetAsync(id, Ct))!.AcknowledgedBy);
    }

    [Fact]
    public async Task ServerErrors_AreClassified()
    {
        // The directory is down while the digest preview runs: 503 ApiProblem.Unavailable (spec 5.3).
        await using var server = WatchdogClientServer.Start(
            services => services.AddSingleton<IDigestService>(new DirectoryDownDigest()), TestRoles.AdminAccount, TestRoles.AdminGroup);
        using var client = server.Client();
        await client.SaveConfigAsync(new ConfigEnvelope { Version = 0, Configuration = TestData.Configuration() }, Ct);
        var channel = new ChannelSettings { Id = "siem", Type = ChannelType.Webhook, Name = "SIEM" };

        var conflict = await Assert.ThrowsAsync<WatchdogClientException>(() =>
            client.SaveConfigAsync(new ConfigEnvelope { Version = 0, Configuration = TestData.Configuration() }, Ct));
        var invalid = await Assert.ThrowsAsync<WatchdogClientException>(() =>
            client.SaveConfigAsync(new ConfigEnvelope { Version = 1, Configuration = TestData.Configuration() with { Channels = [channel, channel] } }, Ct));
        var missing = await Assert.ThrowsAsync<WatchdogClientException>(() => client.GetEventAsync(424242, Ct));
        var unavailable = await Assert.ThrowsAsync<WatchdogClientException>(() => client.PreviewDigestAsync(Ct));
        var noChannel = await Assert.ThrowsAsync<WatchdogClientException>(() => client.SendTestChannelAsync("brak", new TestChannelRequest(), Ct));

        Assert.Equal(WatchdogClientErrorKind.VersionConflict, conflict.Kind);
        Assert.Equal(412, conflict.StatusCode);
        Assert.Equal(WatchdogClientErrorKind.Validation, invalid.Kind);
        Assert.NotEmpty(invalid.Issues);
        Assert.Equal(WatchdogClientErrorKind.NotFound, missing.Kind);
        Assert.Equal(WatchdogClientErrorKind.ServerError, unavailable.Kind);
        Assert.Equal(503, unavailable.StatusCode);
        Assert.Equal(WatchdogClientErrorKind.NotFound, noChannel.Kind);
    }

    [Fact]
    public async Task Anonymous_IsUnauthorized_AndAViewerForbiddenToSave()
    {
        await using var anonymous = WatchdogClientServer.Start(account: null);
        await using var viewer = WatchdogClientServer.Start(TestRoles.ViewerAccount, TestRoles.ViewerGroup);
        using var anonymousClient = anonymous.Client();
        using var viewerClient = viewer.Client();

        var unauthorized = await Assert.ThrowsAsync<WatchdogClientException>(() => anonymousClient.GetMeAsync(Ct));
        var forbidden = await Assert.ThrowsAsync<WatchdogClientException>(() =>
            viewerClient.SaveConfigAsync(new ConfigEnvelope { Version = 0, Configuration = TestData.Configuration() }, Ct));

        Assert.Equal(WatchdogClientErrorKind.Unauthorized, unauthorized.Kind);
        Assert.Equal(WatchdogClientErrorKind.Forbidden, forbidden.Kind);
    }

    [Fact]
    public async Task UntrustedCertificate_WithoutAPin_IsCertificateInvalid()
    {
        await using var server = WatchdogClientServer.Start(TestRoles.AdminAccount, TestRoles.AdminGroup);
        using var client = server.Client(pinned: false);

        var error = await Assert.ThrowsAsync<WatchdogClientException>(() => client.GetMeAsync(Ct));

        Assert.Equal(WatchdogClientErrorKind.CertificateInvalid, error.Kind);
    }

    [Fact]
    public async Task StoppedServer_IsUnreachable()
    {
        var server = WatchdogClientServer.Start(TestRoles.AdminAccount, TestRoles.AdminGroup);
        using var client = server.Client();
        await client.GetMeAsync(Ct);
        await server.DisposeAsync();

        var error = await Assert.ThrowsAsync<WatchdogClientException>(() => client.GetMeAsync(Ct));

        Assert.Equal(WatchdogClientErrorKind.Unreachable, error.Kind);
    }

    [Fact]
    public async Task NothingListening_IsUnreachable()
    {
        using var client = new WatchdogHttpClient(TimeProvider.System, NullLogger<WatchdogHttpClient>.Instance);
        client.Configure(new WatchdogClientOptions { BaseAddress = new Uri($"https://127.0.0.1:{FreeTcpPort.Next()}/") });

        var error = await Assert.ThrowsAsync<WatchdogClientException>(() => client.GetStatusAsync(Ct));

        Assert.Equal(WatchdogClientErrorKind.Unreachable, error.Kind);
    }

    [Fact]
    public async Task LongPoll_ReturnsAfterTheServerWait_AndTheClientTimeoutCoversIt()
    {
        await using var server = WatchdogClientServer.Start(TestRoles.ViewerAccount, TestRoles.ViewerGroup);
        using var client = server.Client();
        var id = await server.Factory.Store.AppendAsync(TestData.Event(WatchEventKind.UserCreated), Ct);
        var waiting = server.Factory.Clock.WaitForTimerAsync(TimeSpan.FromSeconds(25));

        var polling = client.GetEventsAsync(new EventQuery { After = id, WaitSeconds = 25 }, Ct);
        await waiting.WaitAsync(Ct);
        server.Factory.Clock.Advance(TimeSpan.FromSeconds(25));
        var page = await polling;

        Assert.Empty(page.Items);
        Assert.Equal(id, page.LatestId);
    }

    [Fact]
    public async Task ClientTimeout_IsTimeout_AndCallerCancellation_Cancelled()
    {
        await using var server = WatchdogClientServer.Start(TestRoles.ViewerAccount, TestRoles.ViewerGroup);
        var clientClock = new TestClock(TestStore.Start);
        using var client = server.Client(clientClock, timeout: TimeSpan.FromSeconds(5));
        var id = await server.Factory.Store.AppendAsync(TestData.Event(WatchEventKind.UserCreated), Ct);

        var clientTimer = clientClock.WaitForTimerAsync(TimeSpan.FromSeconds(45));
        var polling = client.GetEventsAsync(new EventQuery { After = id, WaitSeconds = 30 }, Ct);
        await clientTimer.WaitAsync(Ct);
        clientClock.Advance(TimeSpan.FromSeconds(45));
        var timeout = await Assert.ThrowsAsync<WatchdogClientException>(() => polling);

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var cancelled = client.GetEventsAsync(new EventQuery { After = id, WaitSeconds = 30 }, cancel.Token);
        await cancel.CancelAsync();
        var cancellation = await Assert.ThrowsAsync<WatchdogClientException>(() => cancelled);

        Assert.Equal(WatchdogClientErrorKind.Timeout, timeout.Kind);
        Assert.Equal(WatchdogClientErrorKind.Cancelled, cancellation.Kind);
    }

    [Fact]
    public async Task Requests_CarryTheApiAndClientHeaders_AndNoPreemptiveCredentials()
    {
        await using var sink = new HttpSink(TestCertificates.Localhost);
        sink.Script(_ => HttpSinkResponse.WithHeader(200, "X-AdMng-Watchdog-Api", "2"));
        await sink.StartAsync(Ct);
        using var client = new WatchdogHttpClient(TimeProvider.System, NullLogger<WatchdogHttpClient>.Instance);
        client.Configure(new WatchdogClientOptions
        {
            BaseAddress = sink.BaseAddress,
            PinnedCertificateSha256 = TestCertificates.Sha256(TestCertificates.Localhost),
            Credential = new NetworkCredential("jan.kowalski", "Hasło-1!", "CONTOSO"),
        });

        var mismatch = await Assert.ThrowsAsync<WatchdogClientException>(() => client.SendDigestNowAsync(Ct));

        Assert.Equal(WatchdogClientErrorKind.ApiVersionMismatch, mismatch.Kind);
        var request = Assert.Single(sink.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/api/v1/digest/send", request.PathAndQuery);
        Assert.Equal("1", request.Headers["X-AdMng-Watchdog-Api"]);
        Assert.Equal("1", request.Headers["X-AdMng-Client"]);
        Assert.False(request.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task AResponseWithoutTheApiHeader_IsNotTheWatchdog()
    {
        await using var sink = new HttpSink(TestCertificates.Localhost);
        sink.Script(_ => new HttpSinkResponse { StatusCode = 200, Body = "{\"account\":\"x\"}", ContentType = "application/json" });
        await sink.StartAsync(Ct);
        using var client = new WatchdogHttpClient(TimeProvider.System, NullLogger<WatchdogHttpClient>.Instance);
        client.Configure(new WatchdogClientOptions { BaseAddress = sink.BaseAddress, PinnedCertificateSha256 = TestCertificates.Sha256(TestCertificates.Localhost) });

        var error = await Assert.ThrowsAsync<WatchdogClientException>(() => client.GetMeAsync(Ct));

        Assert.Equal(WatchdogClientErrorKind.ApiVersionMismatch, error.Kind);
    }

    [Theory]
    [InlineData("http://admng-wd.contoso.com:8443/", true)]
    [InlineData("http://127.0.0.1:5089/", false)]
    [InlineData("https://admng-wd.contoso.com:8443/?x=1", false)]
    [InlineData("https://user@admng-wd.contoso.com:8443/", false)]
    [InlineData("ftp://admng-wd.contoso.com/", true)]
    public void NonHttpsAddresses_AreRejected(string address, bool allowLoopback)
    {
        using var client = new WatchdogHttpClient(TimeProvider.System, NullLogger<WatchdogHttpClient>.Instance);

        Assert.Throws<ArgumentException>(() => client.Configure(new WatchdogClientOptions { BaseAddress = new Uri(address), AllowInsecureLoopback = allowLoopback }));
        Assert.False(client.IsConfigured);
    }

    [Fact]
    public void LoopbackHttp_IsAllowedOnlyOnRequest_AndConfigureNullDisconnects()
    {
        using var client = new WatchdogHttpClient(TimeProvider.System, NullLogger<WatchdogHttpClient>.Instance);

        client.Configure(new WatchdogClientOptions { BaseAddress = new Uri("http://127.0.0.1:5089"), AllowInsecureLoopback = true });
        Assert.True(client.IsConfigured);
        Assert.Equal(new Uri("http://127.0.0.1:5089"), client.BaseAddress);

        client.Configure(null);
        Assert.False(client.IsConfigured);
        Assert.Null(client.BaseAddress);
    }

    [Fact]
    public async Task NotConfigured_Throws()
    {
        using var client = new WatchdogHttpClient(TimeProvider.System, NullLogger<WatchdogHttpClient>.Instance);

        var error = await Assert.ThrowsAsync<WatchdogClientException>(() => client.GetStatusAsync(Ct));

        Assert.Equal(WatchdogClientErrorKind.NotConfigured, error.Kind);
    }

    [Fact]
    public void Credentials_AreOfferedOnlyToTheConfiguredOrigin()
    {
        var credential = new NetworkCredential("jan.kowalski", "Hasło-1!", "CONTOSO");
        using var handler = WatchdogHttpHandlerFactory.Create(new WatchdogClientOptions
        {
            BaseAddress = new Uri("https://admng-wd.contoso.com:8443/watchdog/"),
            Credential = credential,
        });

        Assert.Same(credential, handler.Credentials!.GetCredential(new Uri("https://admng-wd.contoso.com:8443/api/v1/me"), "Negotiate"));
        Assert.Null(handler.Credentials.GetCredential(new Uri("https://attacker.example/"), "Negotiate"));
        Assert.Null(handler.Credentials.GetCredential(new Uri("https://admng-wd.contoso.com:9443/"), "Negotiate"));
        Assert.Null(handler.Credentials.GetCredential(new Uri("http://admng-wd.contoso.com:8443/"), "Negotiate"));
        Assert.Null(handler.Credentials.GetCredential(new Uri("https://admng-wd.contoso.com:8443/"), "NTLM"));
        Assert.False(handler.PreAuthenticate);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
    }

    [Fact]
    public void AMalformedPin_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => WatchdogHttpHandlerFactory.Create(new WatchdogClientOptions
        {
            BaseAddress = new Uri("https://admng-wd.contoso.com:8443/"),
            PinnedCertificateSha256 = "ABCD",
        }));
    }

    private sealed class DirectoryDownDigest : IDigestService
    {
        public DateTimeOffset? NextScheduledAt => null;

        public Task<DigestPreviewDto> PreviewAsync(CancellationToken cancellationToken) =>
            throw new AdMng.Core.Directory.DirectoryException(AdMng.Core.Directory.DirectoryErrorKind.ServerUnavailable, "dc01.contoso.test is not reachable");

        public Task SendNowAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
