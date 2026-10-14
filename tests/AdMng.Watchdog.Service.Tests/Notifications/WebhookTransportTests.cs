using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Configuration;
using AdMng.Watchdog.Core.Formatting.Webhooks;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Service.Notifications;
using AdMng.Watchdog.Testing.Certificates;
using AdMng.Watchdog.Testing.Http;

namespace AdMng.Watchdog.Service.Tests.Notifications;

/// <summary>The signed generic webhook against the <see cref="HttpSink"/> (spec 14.5, 15.3).</summary>
public sealed class WebhookTransportTests : IAsyncLifetime
{
    private const string ChannelId = "siem";
    private static readonly string SigningSecret = "whsec_" + Convert.ToBase64String(SHA256.HashData("klucz-podpisu-żółw"u8));
    private static readonly string PreviousSecret = "whsec_" + Convert.ToBase64String(SHA256.HashData("poprzedni-klucz-ćma"u8));

    private readonly HttpSink _sink = new(TestCertificates.Localhost);
    private readonly DeliveryFixture _fixture = DeliveryFixture.Create();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string SinkUrl => new Uri(_sink.BaseAddress, "hooks/watchdog?token=abc").ToString();

    public async ValueTask InitializeAsync() => await _sink.StartAsync(Ct);

    public async ValueTask DisposeAsync()
    {
        await _fixture.DisposeAsync();
        await _sink.DisposeAsync();
    }

    [Fact]
    public async Task Delivery_IsSignedPerStandardWebhooks()
    {
        await ConfigureAsync(SinkUrl);
        var message = Message();

        var result = await _fixture.Webhook.SendAsync(message, Ct);

        Assert.Equal(DeliveryOutcome.Delivered, result.Outcome);
        var request = Assert.Single(_sink.Requests);
        Assert.Equal(message.MessageId, request.Headers["webhook-id"]);
        var timestamp = long.Parse(request.Headers["webhook-timestamp"], CultureInfo.InvariantCulture);
        Assert.Equal(_fixture.Clock.GetUtcNow().ToUnixTimeSeconds(), timestamp);
        Assert.True(StandardWebhookSigner.Verify(request.Headers["webhook-signature"], new SecretValue(SigningSecret), message.MessageId, timestamp, request.Body));
        Assert.Equal(message.Http!.Body, request.Body);
        Assert.False(request.Headers.ContainsKey(StandardWebhookSigner.GitHubSignatureHeader));
        Assert.Null(_fixture.Health.Channel(ChannelId));   // channel health is the outbox sender's (spec 15.1, 15.8), not the transport's
    }

    [Fact]
    public async Task Retries_KeepTheIdButSignANewTimestamp()
    {
        await ConfigureAsync(SinkUrl);
        var calls = 0;
        _sink.Script(_ => Interlocked.Increment(ref calls) == 1 ? HttpSinkResponse.Status(503) : HttpSinkResponse.Status(204));

        Assert.Equal(DeliveryOutcome.Transient, (await _fixture.Webhook.SendAsync(Message(), Ct)).Outcome);
        _fixture.Clock.Advance(TimeSpan.FromSeconds(90));
        Assert.Equal(DeliveryOutcome.Delivered, (await _fixture.Webhook.SendAsync(Message() with { Attempts = 1 }, Ct)).Outcome);

        var first = _sink.Requests[0];
        var second = _sink.Requests[1];
        Assert.Equal(first.Headers["webhook-id"], second.Headers["webhook-id"]);
        Assert.Equal(
            long.Parse(first.Headers["webhook-timestamp"], CultureInfo.InvariantCulture) + 90,
            long.Parse(second.Headers["webhook-timestamp"], CultureInfo.InvariantCulture));
        Assert.NotEqual(first.Headers["webhook-signature"], second.Headers["webhook-signature"]);
    }

    [Fact]
    public async Task DuringRotation_BothKeysSign()
    {
        var channel = await ConfigureAsync(SinkUrl, previous: true);
        await _fixture.StoreSecretAsync(
            SecretNames.WebhookPreviousSigningSecret(ChannelId),
            PreviousSecret,
            SecretBindings.ForWebhookSigningSecret(channel, SecretBindings.UrlHost(SinkUrl)));

        Assert.Equal(DeliveryOutcome.Delivered, (await _fixture.Webhook.SendAsync(Message(), Ct)).Outcome);

        var request = Assert.Single(_sink.Requests);
        var header = request.Headers["webhook-signature"];
        var timestamp = long.Parse(request.Headers["webhook-timestamp"], CultureInfo.InvariantCulture);
        Assert.Equal(2, header.Split(' ').Length);
        Assert.True(StandardWebhookSigner.Verify(header, new SecretValue(SigningSecret), Message().MessageId, timestamp, request.Body));
        Assert.True(StandardWebhookSigner.Verify(header, new SecretValue(PreviousSecret), Message().MessageId, timestamp, request.Body));
    }

    [Fact]
    public async Task GitHubSignature_IsAddedWhenEnabled()
    {
        await ConfigureAsync(SinkUrl, github: true);

        Assert.Equal(DeliveryOutcome.Delivered, (await _fixture.Webhook.SendAsync(Message(), Ct)).Outcome);

        var request = Assert.Single(_sink.Requests);
        var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(SigningSecret), request.Body));
        Assert.Equal(expected, request.Headers[StandardWebhookSigner.GitHubSignatureHeader]);
    }

    [Theory]
    [InlineData(400, DeliveryOutcome.Permanent, null)]
    [InlineData(401, DeliveryOutcome.AuthenticationFailed, null)]
    [InlineData(404, DeliveryOutcome.ChannelBroken, null)]
    [InlineData(410, DeliveryOutcome.ChannelBroken, DeliveryHints.WebhookGone)]
    [InlineData(429, DeliveryOutcome.Transient, null)]
    [InlineData(500, DeliveryOutcome.Transient, null)]
    public async Task StatusCodes_AreClassified(int status, DeliveryOutcome expected, string? hint)
    {
        await ConfigureAsync(SinkUrl);
        _sink.Script(_ => HttpSinkResponse.Status(status));

        var result = await _fixture.Webhook.SendAsync(Message(), Ct);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(hint, result.Detail);
    }

    [Fact]
    public async Task HostOutsideAllowedHosts_IsRefusedWithoutARequest()
    {
        await ConfigureAsync(SinkUrl, allowedHosts: ["siem.contoso.test"]);

        var result = await _fixture.Webhook.SendAsync(Message(), Ct);

        Assert.Equal(DeliveryOutcome.ChannelBroken, result.Outcome);
        Assert.Equal("hostNotAllowed", result.ErrorCode);
        Assert.Empty(_sink.Requests);
    }

    [Fact]
    public async Task SigningSecretBoundToAnotherHost_IsRefusedWithoutARequest()
    {
        await ConfigureAsync(SinkUrl, signingHost: "siem.contoso.test");

        var result = await _fixture.Webhook.SendAsync(Message(), Ct);

        Assert.Equal(DeliveryOutcome.ChannelBroken, result.Outcome);
        Assert.Equal(DestinationGuard.RebindRequired, result.ErrorCode);
        Assert.Equal(DeliveryHints.SecretRebindRequired, result.Detail);
        Assert.Empty(_sink.Requests);
    }

    [Fact]
    public async Task PlainHttp_IsRefusedOutsideDevelopment()
    {
        await using var plain = new HttpSink();
        await plain.StartAsync(Ct);
        await ConfigureAsync(new Uri(plain.BaseAddress, "hooks").ToString());

        var result = await _fixture.Webhook.SendAsync(Message(), Ct);

        Assert.Equal(DeliveryOutcome.ChannelBroken, result.Outcome);
        Assert.Equal("insecureUrl", result.ErrorCode);
        Assert.Empty(plain.Requests);
    }

    [Fact]
    public async Task PlainHttpToLoopback_IsAllowedInDevelopment()
    {
        await using var plain = new HttpSink();
        await plain.StartAsync(Ct);
        await using var fixture = DeliveryFixture.Create(environment: "Development");
        var url = new Uri(plain.BaseAddress, "hooks").ToString();
        var channel = Channel();
        fixture.Configure(new WatchdogConfiguration { Channels = [channel] });
        await fixture.StoreSecretAsync(SecretNames.ChannelUrl(ChannelId), url, SecretBindings.ForChannelUrl(channel));
        await fixture.StoreSecretAsync(
            SecretNames.WebhookSigningSecret(ChannelId), SigningSecret, SecretBindings.ForWebhookSigningSecret(channel, SecretBindings.UrlHost(url)));

        Assert.Equal(DeliveryOutcome.Delivered, (await fixture.Webhook.SendAsync(Message(), Ct)).Outcome);
        Assert.Single(plain.Requests);
    }

    [Fact]
    public async Task UntrustedServerCertificate_BreaksTheChannel()
    {
        await using var untrusted = new HttpSink(TestCertificates.Create("siem.contoso.test"));
        await untrusted.StartAsync(Ct);
        await ConfigureAsync(new Uri(untrusted.BaseAddress, "hooks").ToString());

        var result = await _fixture.Webhook.SendAsync(Message(), Ct);

        Assert.Equal(DeliveryOutcome.ChannelBroken, result.Outcome);
        Assert.Equal("tls", result.ErrorCode);
        Assert.Equal(DeliveryHints.CertificateUntrusted, result.Detail);
        Assert.Empty(untrusted.Requests);
    }

    [Fact]
    public async Task NothingListening_IsTransient()
    {
        await ConfigureAsync($"https://127.0.0.1:{Testing.Net.FreeTcpPort.Next()}/hooks");

        var result = await _fixture.Webhook.SendAsync(Message(), Ct);

        Assert.Equal(DeliveryOutcome.Transient, result.Outcome);
    }

    [Fact]
    public async Task MalformedSigningSecret_BreaksTheChannel()
    {
        var channel = await ConfigureAsync(SinkUrl);
        await _fixture.StoreSecretAsync(
            SecretNames.WebhookSigningSecret(ChannelId), "nie-whsec", SecretBindings.ForWebhookSigningSecret(channel, SecretBindings.UrlHost(SinkUrl)));

        var result = await _fixture.Webhook.SendAsync(Message(), Ct);

        Assert.Equal("webhook.invalidSecret", result.ErrorCode);
        Assert.Empty(_sink.Requests);
    }

    internal static OutboxMessage Message() => new()
    {
        MessageId = "0192f3a45b6c7d8e9f00112233445566",
        ChannelType = DeliveryChannelType.Webhook,
        ChannelId = ChannelId,
        Kind = DeliveryKind.Alert,
        Http = new RenderedHttpMessage
        {
            Body = Encoding.UTF8.GetBytes("{\"type\":\"watchdog.alert\",\"data\":{\"target\":\"Zofia Żak\"}}"),
            WebhookEventType = "watchdog.alert",
        },
        CreatedAt = DateTimeOffset.UnixEpoch,
        NextAttemptAt = DateTimeOffset.UnixEpoch,
    };

    private static ChannelSettings Channel(bool previous = false, bool github = false, IReadOnlyList<string>? allowedHosts = null) => new()
    {
        Id = ChannelId,
        Type = ChannelType.Webhook,
        Name = "SIEM – Centrum bezpieczeństwa",
        Url = new SecretField { IsSet = true },
        Webhook = new WebhookChannelOptions
        {
            SigningSecret = new SecretField { IsSet = true },
            PreviousSigningSecret = new SecretField { IsSet = previous },
            SendGitHubSignature = github,
            AllowedHosts = allowedHosts ?? [],
        },
    };

    private async Task<ChannelSettings> ConfigureAsync(
        string url,
        bool previous = false,
        bool github = false,
        IReadOnlyList<string>? allowedHosts = null,
        string? signingHost = null)
    {
        var channel = Channel(previous, github, allowedHosts);
        _fixture.Configure(new WatchdogConfiguration { Channels = [channel] });
        await _fixture.StoreSecretAsync(SecretNames.ChannelUrl(ChannelId), url, SecretBindings.ForChannelUrl(channel));
        await _fixture.StoreSecretAsync(
            SecretNames.WebhookSigningSecret(ChannelId),
            SigningSecret,
            SecretBindings.ForWebhookSigningSecret(channel, signingHost ?? SecretBindings.UrlHost(url)));
        return channel;
    }
}
