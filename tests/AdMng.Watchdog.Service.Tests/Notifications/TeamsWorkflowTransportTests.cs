using System.Text;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Configuration;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Service.Notifications;
using AdMng.Watchdog.Testing.Certificates;
using AdMng.Watchdog.Testing.Http;

namespace AdMng.Watchdog.Service.Tests.Notifications;

/// <summary>Teams over Power Automate Workflows against the <see cref="HttpSink"/> (spec 15.3, 15.7).</summary>
public sealed class TeamsWorkflowTransportTests : IAsyncLifetime
{
    private const string ChannelId = "teams-pomoc";
    private const string WorkflowPath = "powerautomate/automations/direct/workflows/0a1b2c3d4e5f/triggers/manual/paths/invoke";
    private const string SasQuery = "?api-version=1&sp=%2Ftriggers%2Fmanual%2Frun&sv=1.0&sig=SEKRET-podpis-ŻÓŁW";
    private const string PowerPlatformUrl = "https://contoso.environment.api.powerplatform.com/" + WorkflowPath + "?api-version=1";
    private const string TenantId = "6f1d3c2a-8b4e-4f5a-9c7d-2e1f0a3b4c5d";
    private const string ClientId = "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d";

    private readonly HttpSink _sink = new(TestCertificates.Localhost);
    private readonly DeliveryFixture _fixture = DeliveryFixture.Create();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string SinkUrl => new Uri(_sink.BaseAddress, WorkflowPath).ToString() + SasQuery;

    public async ValueTask InitializeAsync() => await _sink.StartAsync(Ct);

    public async ValueTask DisposeAsync()
    {
        await _fixture.DisposeAsync();
        await _sink.DisposeAsync();
    }

    [Fact]
    public async Task Accepted_IsDelivered_WithoutAnAuthorizationHeader()
    {
        var channel = await ConfigureAsync(SinkUrl);
        var message = Message();

        var result = await _fixture.Teams.SendAsync(message, Ct);

        Assert.Equal(DeliveryOutcome.Delivered, result.Outcome);
        Assert.Equal(202, result.StatusCode);
        var request = Assert.Single(_sink.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal(message.Http!.Body, request.Body);
        Assert.StartsWith("application/json", request.Headers["Content-Type"], StringComparison.Ordinal);
        Assert.False(request.Headers.ContainsKey("Authorization"));
        Assert.StartsWith("AdMng-Watchdog/", request.Headers["User-Agent"], StringComparison.Ordinal);
        Assert.EndsWith(SasQuery[^20..], Uri.UnescapeDataString(request.PathAndQuery), StringComparison.Ordinal);
        Assert.Equal(HealthState.Ok, _fixture.Health.Channel(channel.Id)?.State);
    }

    [Theory]
    [InlineData(400, DeliveryOutcome.ChannelBroken)]
    [InlineData(404, DeliveryOutcome.ChannelBroken)]
    [InlineData(408, DeliveryOutcome.Transient)]
    [InlineData(413, DeliveryOutcome.Permanent)]
    [InlineData(500, DeliveryOutcome.Transient)]
    [InlineData(502, DeliveryOutcome.Transient)]
    [InlineData(503, DeliveryOutcome.Transient)]
    public async Task StatusCodes_AreClassified(int status, DeliveryOutcome expected)
    {
        await ConfigureAsync(SinkUrl);
        _sink.Script(_ => HttpSinkResponse.Status(status));

        var result = await _fixture.Teams.SendAsync(Message(), Ct);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal($"http.{status}", result.ErrorCode);
        Assert.Equal(status, result.StatusCode);
    }

    [Fact]
    public async Task SasOAuthMismatch_IsReportedWithItsHint()
    {
        await ConfigureAsync(SinkUrl);
        _sink.Script(_ => HttpSinkResponse.Status(401,
            "{\"error\":{\"code\":\"MisMatchingOAuthClaims\",\"message\":\"The request must be authenticated only by Shared Access scheme.\"}}"));

        var result = await _fixture.Teams.SendAsync(Message(), Ct);

        Assert.Equal(DeliveryOutcome.ChannelBroken, result.Outcome);
        Assert.Equal(DeliveryHints.TeamsAuthMismatch, result.Detail);
        Assert.Equal(HealthState.Error, _fixture.Health.Channel(ChannelId)?.State);
    }

    [Fact]
    public async Task DisabledFlow_BreaksTheChannel()
    {
        await ConfigureAsync(SinkUrl);
        _sink.Script(_ => HttpSinkResponse.Status(400, "{\"error\":{\"code\":\"WorkflowTriggerIsNotEnabled\",\"message\":\"…\"}}"));

        var result = await _fixture.Teams.SendAsync(Message(), Ct);

        Assert.Equal(DeliveryOutcome.ChannelBroken, result.Outcome);
    }

    [Fact]
    public async Task Throttling_HonoursRetryAfter()
    {
        await ConfigureAsync(SinkUrl);
        _sink.Script(_ => HttpSinkResponse.WithHeader(429, "Retry-After", "120"));

        var result = await _fixture.Teams.SendAsync(Message(), Ct);

        Assert.Equal(DeliveryOutcome.Transient, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(120), result.RetryAfter);
    }

    [Fact]
    public async Task Redirects_AreNotFollowed()
    {
        await ConfigureAsync(SinkUrl);
        _sink.Script(request => request.PathAndQuery.StartsWith("/elsewhere", StringComparison.Ordinal)
            ? HttpSinkResponse.Accepted
            : HttpSinkResponse.WithHeader(307, "Location", new Uri(_sink.BaseAddress, "elsewhere").ToString()));

        var result = await _fixture.Teams.SendAsync(Message(), Ct);

        Assert.Equal(DeliveryOutcome.ChannelBroken, result.Outcome);
        Assert.Equal("http.307", result.ErrorCode);
        Assert.Single(_sink.Requests);
    }

    [Fact]
    public async Task AnUnansweredRequest_TimesOutOnTheClock()
    {
        await ConfigureAsync(SinkUrl);
        var release = new TaskCompletionSource();
        _sink.Script(_ => HttpSinkResponse.Accepted with { HoldUntil = release.Task });

        var sending = _fixture.Teams.SendAsync(Message(), Ct);
        await _sink.WaitForRequestsAsync(1, Ct);
        _fixture.Clock.Advance(TeamsWorkflowTransport.Timeout);
        var result = await sending;
        release.SetResult();

        Assert.Equal(DeliveryOutcome.Transient, result.Outcome);
        Assert.Equal("timeout", result.ErrorCode);
    }

    [Fact]
    public async Task TheSasSignature_NeverReachesTheLog()
    {
        await ConfigureAsync(SinkUrl);
        _sink.Script(_ => HttpSinkResponse.Status(500));

        await _fixture.Teams.SendAsync(Message(), Ct);

        var log = _fixture.Log.Text;
        Assert.Contains("/powerautomate/automations/direct/workflows/", log, StringComparison.Ordinal);
        Assert.DoesNotContain("sig=", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SEKRET", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api-version", log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RetiredConnectorUrl_IsRefusedWithoutARequest()
    {
        await ConfigureAsync("https://contoso.webhook.office.com/webhookb2/0a1b2c3d@6f1d3c2a/IncomingWebhook/abc/def");

        var result = await _fixture.Teams.SendAsync(Message(), Ct);

        Assert.Equal(DeliveryOutcome.ChannelBroken, result.Outcome);
        Assert.Equal("retiredTeamsUrl", result.ErrorCode);
        Assert.Equal(DeliveryHints.RetiredTeamsUrl, result.Detail);
        Assert.Empty(_sink.Requests);
    }

    [Fact]
    public async Task UrlStoredForAnotherChannel_IsNotUsed()
    {
        await ConfigureAsync(SinkUrl, urlBinding: "channel|teams-inny|teams");

        var result = await _fixture.Teams.SendAsync(Message(), Ct);

        Assert.Equal(DeliveryOutcome.ChannelBroken, result.Outcome);
        Assert.Equal(DestinationGuard.RebindRequired, result.ErrorCode);
        Assert.Empty(_sink.Requests);
    }

    [Fact]
    public async Task EntraClientCredentials_SendTheBearerToAPowerPlatformHost()
    {
        _fixture.HttpClients.ConnectToPort = _sink.BaseAddress.Port;
        var channel = await ConfigureAsync(PowerPlatformUrl, entra: true);
        await _fixture.StoreSecretAsync(
            SecretNames.ChannelClientSecret(ChannelId),
            "sekret-aplikacji-ąę",
            SecretBindings.ForTeamsClientSecret(channel, SecretBindings.UrlHost(PowerPlatformUrl)));

        var result = await _fixture.Teams.SendAsync(Message(), Ct);

        Assert.Equal(DeliveryOutcome.Delivered, result.Outcome);
        var request = Assert.Single(_fixture.Tokens.Requests);
        Assert.Equal("https://service.flow.microsoft.com//.default", request.Scope);
        Assert.Equal("sekret-aplikacji-ąę", request.ClientSecret?.Reveal());
        Assert.Equal("Bearer " + _fixture.Tokens.Token, Assert.Single(_sink.Requests).Headers["Authorization"]);
    }

    [Fact]
    public async Task EntraClientCredentials_ToANonPowerPlatformHost_RequestNoToken()
    {
        var channel = await ConfigureAsync(SinkUrl, entra: true);
        await _fixture.StoreSecretAsync(
            SecretNames.ChannelClientSecret(ChannelId),
            "sekret-aplikacji-ąę",
            SecretBindings.ForTeamsClientSecret(channel, SecretBindings.UrlHost(SinkUrl)));

        var result = await _fixture.Teams.SendAsync(Message(), Ct);

        Assert.Equal(DeliveryOutcome.ChannelBroken, result.Outcome);
        Assert.Empty(_fixture.Tokens.Requests);
        Assert.Empty(_sink.Requests);
    }

    [Fact]
    public async Task EntraClientSecret_BoundToAnotherHost_IsRefused_AndNothingIsSent()
    {
        _fixture.HttpClients.ConnectToPort = _sink.BaseAddress.Port;
        var channel = await ConfigureAsync(PowerPlatformUrl, entra: true);
        await _fixture.StoreSecretAsync(
            SecretNames.ChannelClientSecret(ChannelId),
            "sekret-aplikacji-ąę",
            SecretBindings.ForTeamsClientSecret(channel, "attacker.environment.api.powerplatform.com"));

        var result = await _fixture.Teams.SendAsync(Message(), Ct);

        Assert.Equal(DeliveryOutcome.ChannelBroken, result.Outcome);
        Assert.Equal(DestinationGuard.RebindRequired, result.ErrorCode);
        Assert.Equal(DeliveryHints.SecretRebindRequired, result.Detail);
        Assert.Empty(_fixture.Tokens.Requests);
        Assert.Empty(_sink.Requests);
    }

    [Fact]
    public async Task RateLimit_DefersTheSecondPostInTheSameSecond()
    {
        await ConfigureAsync(SinkUrl);

        Assert.Equal(DeliveryOutcome.Delivered, (await _fixture.Teams.SendAsync(Message(), Ct)).Outcome);
        var deferred = await _fixture.Teams.SendAsync(Message(), Ct);

        Assert.Equal(DeliveryOutcome.Deferred, deferred.Outcome);
        Assert.Single(_sink.Requests);
        _fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(DeliveryOutcome.Delivered, (await _fixture.Teams.SendAsync(Message(), Ct)).Outcome);
    }

    [Fact]
    public async Task DisabledChannel_BreaksWithoutARequest()
    {
        var channel = await ConfigureAsync(SinkUrl);
        _fixture.Configure(new WatchdogConfiguration { Channels = [channel with { Enabled = false }] });

        var result = await _fixture.Teams.SendAsync(Message(), Ct);

        Assert.Equal("channel.disabled", result.ErrorCode);
        Assert.Empty(_sink.Requests);
    }

    internal static OutboxMessage Message() => new()
    {
        MessageId = "0192f3a45b6c7d8e9f00112233445566",
        ChannelType = DeliveryChannelType.Teams,
        ChannelId = ChannelId,
        Kind = DeliveryKind.Alert,
        Http = new RenderedHttpMessage
        {
            Body = Encoding.UTF8.GetBytes("{\"type\":\"message\",\"attachments\":[{\"content\":{\"body\":[{\"text\":\"Nowe konto: Zofia Żak\"}]}}]}"),
            Title = "Nowe konto",
        },
        CreatedAt = DateTimeOffset.UnixEpoch,
        NextAttemptAt = DateTimeOffset.UnixEpoch,
    };

    private async Task<ChannelSettings> ConfigureAsync(string url, bool entra = false, string? urlBinding = null)
    {
        var channel = new ChannelSettings
        {
            Id = ChannelId,
            Type = ChannelType.Teams,
            Name = "Teams – Pomoc techniczna",
            Url = new SecretField { IsSet = true },
            Teams = entra
                ? new TeamsChannelOptions
                {
                    AuthMode = TeamsAuthMode.EntraClientCredentials,
                    TenantId = TenantId,
                    ClientId = ClientId,
                    ClientSecret = new SecretField { IsSet = true },
                }
                : new TeamsChannelOptions(),
        };
        _fixture.Configure(new WatchdogConfiguration { Channels = [channel] });
        await _fixture.StoreSecretAsync(SecretNames.ChannelUrl(ChannelId), url, urlBinding ?? SecretBindings.ForChannelUrl(channel));
        return channel;
    }
}
