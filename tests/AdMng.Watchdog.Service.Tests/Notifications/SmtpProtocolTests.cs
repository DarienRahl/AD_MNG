using System.Text;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Configuration;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Testing.Certificates;
using AdMng.Watchdog.Testing.Smtp;

namespace AdMng.Watchdog.Service.Tests.Notifications;

/// <summary>SMTP replies and protocol details against the <see cref="ScriptedSmtpServer"/> (spec 15.4, 15.6).</summary>
public sealed class SmtpProtocolTests
{
    private const string TenantId = "6f1d3c2a-8b4e-4f5a-9c7d-2e1f0a3b4c5d";
    private const string ClientId = "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("MAIL", "421 4.3.2 Service not available", "smtp.421.4.3.2")]
    [InlineData("MAIL", "451 4.3.0 Try again later", "smtp.451.4.3.0")]
    [InlineData(ScriptedSmtpServer.EndOfData, "451 4.4.1 Delivery temporarily suspended", "smtp.451.4.4.1")]
    [InlineData("RCPT", "452 4.5.3 Too many recipients", "smtp.452.4.5.3")]
    public async Task TemporaryReplies_AreTransient(string verb, string reply, string errorCode)
    {
        await using var server = await StartAsync(verb, reply);
        await using var fixture = DeliveryFixture.Create();
        fixture.Configure(SmtpEmailTransportTests.Configuration(server.Port, SmtpSecurity.None));

        var result = await fixture.Smtp.SendAsync(SmtpEmailTransportTests.Message(), Ct);

        Assert.Equal(DeliveryOutcome.Transient, result.Outcome);
        Assert.Equal(errorCode, result.ErrorCode);
        Assert.Empty(server.Messages);
    }

    [Theory]
    [InlineData(ScriptedSmtpServer.EndOfData, "552 5.3.4 Message size exceeds fixed limit", "smtp.552.5.3.4")]
    [InlineData("RCPT", "550 5.1.1 User unknown", "smtp.550.5.1.1")]
    public async Task PermanentReplies_ArePermanent(string verb, string reply, string errorCode)
    {
        await using var server = await StartAsync(verb, reply);
        await using var fixture = DeliveryFixture.Create();
        fixture.Configure(SmtpEmailTransportTests.Configuration(server.Port, SmtpSecurity.None));

        var result = await fixture.Smtp.SendAsync(SmtpEmailTransportTests.Message(), Ct);

        Assert.Equal(DeliveryOutcome.Permanent, result.Outcome);
        Assert.Equal(errorCode, result.ErrorCode);
    }

    [Fact]
    public async Task DirectSendRejection_BreaksTheChannel_WithItsHint()
    {
        await using var server = await StartAsync("MAIL", "550 5.7.68 TenantInboundAttribution; Direct Send not allowed for this organization");
        await using var fixture = DeliveryFixture.Create();
        fixture.Configure(SmtpEmailTransportTests.Configuration(server.Port, SmtpSecurity.None));

        var result = await fixture.Smtp.SendAsync(SmtpEmailTransportTests.Message(), Ct);

        Assert.Equal(DeliveryOutcome.ChannelBroken, result.Outcome);
        Assert.Equal("smtp.550.5.7.68", result.ErrorCode);
        Assert.Equal(DeliveryHints.DirectSendRejected, result.Detail);
    }

    [Theory]
    [InlineData("535 5.7.139 Authentication unsuccessful, basic authentication is disabled.", DeliveryHints.BasicAuthDisabled)]
    [InlineData("535 5.7.139 Authentication unsuccessful, SmtpClientAuthentication is disabled for the Mailbox.", DeliveryHints.SmtpAuthDisabledForMailbox)]
    public async Task ExchangeOnlineAuthRejections_CarryTheirHint(string reply, string hint)
    {
        await using var server = await StartAsync("AUTH", reply, tls: true);
        await using var fixture = DeliveryFixture.Create();
        var configuration = WithBasicAuth(server.Port);
        fixture.Configure(configuration);
        await fixture.StoreSecretAsync(SecretNames.SmtpPassword, "Hasło-Wąż-2026!", SecretBindings.ForSmtpPassword(configuration.Smtp));

        var result = await fixture.Smtp.SendAsync(SmtpEmailTransportTests.Message(), Ct);

        Assert.Equal(DeliveryOutcome.AuthenticationFailed, result.Outcome);
        Assert.Equal("smtp.535.5.7.139", result.ErrorCode);
        Assert.Equal(hint, result.Detail);
        Assert.Null(fixture.Health.Channel("email"));   // channel health is the outbox sender's (spec 15.1, 15.8), not the transport's
    }

    [Fact]
    public async Task OneRejectedRecipient_DoesNotStopTheOthers()
    {
        await using var server = await StartAsync("RCPT TO:<lucja.nowak@contoso.test>", "550 5.1.1 User unknown");
        await using var fixture = DeliveryFixture.Create();
        fixture.Configure(SmtpEmailTransportTests.Configuration(server.Port, SmtpSecurity.None));

        var result = await fixture.Smtp.SendAsync(SmtpEmailTransportTests.Message(), Ct);

        Assert.Equal(DeliveryOutcome.Delivered, result.Outcome);
        Assert.Single(server.Messages);
    }

    [Fact]
    public async Task XOAuth2_SendsTheTokenForTheConfiguredMailbox_OverTls()
    {
        await using var server = await StartAsync(verb: null, reply: null, tls: true);
        await using var fixture = DeliveryFixture.Create(new ServiceOptions { Notifications = new NotificationBootstrapOptions { OAuthSmtpHosts = ["127.0.0.1"] } });
        var configuration = WithOAuth(server.Port);
        fixture.Configure(configuration);
        await fixture.StoreSecretAsync(SecretNames.SmtpOAuthClientSecret, "sekret-klienta-źż", SecretBindings.ForSmtpOAuthClientSecret(configuration.Smtp));

        var result = await fixture.Smtp.SendAsync(SmtpEmailTransportTests.Message(), Ct);

        Assert.Equal(DeliveryOutcome.Delivered, result.Outcome);
        var request = Assert.Single(fixture.Tokens.Requests);
        Assert.Equal("https://outlook.office365.com/.default", request.Scope);
        Assert.Equal(TenantId, request.TenantId);
        Assert.Equal("sekret-klienta-źż", request.ClientSecret?.Reveal());
        var auth = Assert.Single(server.Commands, c => c.StartsWith("AUTH XOAUTH2 ", StringComparison.Ordinal));
        var payload = Encoding.UTF8.GetString(Convert.FromBase64String(auth["AUTH XOAUTH2 ".Length..]));
        Assert.Equal("user=watchdog@contoso.test\u0001auth=Bearer eyJ0eXAiOiJKV1QiLCJhbGciOiJSUzI1NiJ9.e30.c2lnbmF0dXJl\u0001\u0001", payload);
    }

    [Fact]
    public async Task XOAuth2_ToAHostOutsideTheBootstrapList_RequestsNoToken_AndOpensNoConnection()
    {
        await using var server = await StartAsync(verb: null, reply: null, tls: true);
        await using var fixture = DeliveryFixture.Create();   // default list: smtp.office365.com only
        var configuration = WithOAuth(server.Port);
        fixture.Configure(configuration);
        await fixture.StoreSecretAsync(SecretNames.SmtpOAuthClientSecret, "sekret-klienta-źż", SecretBindings.ForSmtpOAuthClientSecret(configuration.Smtp));

        var result = await fixture.Smtp.SendAsync(SmtpEmailTransportTests.Message(), Ct);

        Assert.Equal(DeliveryOutcome.AuthenticationFailed, result.Outcome);
        Assert.Equal("oauthHostNotAllowed", result.ErrorCode);
        Assert.Empty(fixture.Tokens.Requests);
        Assert.Empty(server.Commands);
    }

    [Fact]
    public async Task TokenEndpointOutage_IsTransient()
    {
        await using var server = await StartAsync(verb: null, reply: null, tls: true);
        await using var fixture = DeliveryFixture.Create(new ServiceOptions { Notifications = new NotificationBootstrapOptions { OAuthSmtpHosts = ["127.0.0.1"] } });
        var configuration = WithOAuth(server.Port);
        fixture.Configure(configuration);
        await fixture.StoreSecretAsync(SecretNames.SmtpOAuthClientSecret, "sekret-klienta-źż", SecretBindings.ForSmtpOAuthClientSecret(configuration.Smtp));
        fixture.Tokens.Failure = new HttpRequestException("login.microsoftonline.com unreachable");

        var result = await fixture.Smtp.SendAsync(SmtpEmailTransportTests.Message(), Ct);

        Assert.Equal(DeliveryOutcome.Transient, result.Outcome);
        Assert.Equal("auth.token.network", result.ErrorCode);
        Assert.Empty(server.Commands);
    }

    [Fact]
    public async Task WithoutEightBitMime_TheMessageIsSevenBitClean()
    {
        await using var server = new ScriptedSmtpServer(new ScriptedSmtpOptions { Extensions = ["ENHANCEDSTATUSCODES"] });
        await server.StartAsync();
        await using var fixture = DeliveryFixture.Create();
        fixture.Configure(SmtpEmailTransportTests.Configuration(server.Port, SmtpSecurity.None));

        Assert.Equal(DeliveryOutcome.Delivered, (await fixture.Smtp.SendAsync(SmtpEmailTransportTests.Message(), Ct)).Outcome);

        var data = Assert.Single(server.Messages);
        Assert.All(data, b => Assert.True(b < 0x80));
        Assert.DoesNotContain(server.Commands, c => c.Contains("BODY=8BITMIME", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HeaderInjection_FromTheRenderedHeaders_IsDropped()
    {
        await using var server = await StartAsync(verb: null, reply: null);
        await using var fixture = DeliveryFixture.Create();
        fixture.Configure(SmtpEmailTransportTests.Configuration(server.Port, SmtpSecurity.None));

        Assert.Equal(DeliveryOutcome.Delivered, (await fixture.Smtp.SendAsync(SmtpEmailTransportTests.Message(), Ct)).Outcome);

        Assert.Equal(2, server.Commands.Count(c => c.StartsWith("RCPT", StringComparison.OrdinalIgnoreCase)));
        var text = Encoding.Latin1.GetString(Assert.Single(server.Messages));
        Assert.DoesNotContain("\r\nBcc:", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("never@evil.example", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("X-Injected: a  Bcc: evil@evil.example\r\n", text, StringComparison.Ordinal);   // one harmless header line
    }

    [Fact]
    public async Task NothingListening_IsTransient()
    {
        await using var fixture = DeliveryFixture.Create();
        fixture.Configure(SmtpEmailTransportTests.Configuration(Testing.Net.FreeTcpPort.Next(), SmtpSecurity.None));

        var result = await fixture.Smtp.SendAsync(SmtpEmailTransportTests.Message(), Ct);

        Assert.Equal(DeliveryOutcome.Transient, result.Outcome);
        Assert.StartsWith("socket", result.ErrorCode, StringComparison.Ordinal);
    }

    private static async Task<ScriptedSmtpServer> StartAsync(string? verb, string? reply, bool tls = false)
    {
        var server = new ScriptedSmtpServer(new ScriptedSmtpOptions { StartTlsCertificate = tls ? TestCertificates.Localhost : null })
        {
            Script = line => verb is not null && line.StartsWith(verb, StringComparison.OrdinalIgnoreCase) ? reply : null,
        };
        await server.StartAsync();
        return server;
    }

    private static WatchdogConfiguration WithBasicAuth(int port) =>
        SmtpEmailTransportTests.Configuration(port, SmtpSecurity.StartTls, pinned: true, basic: true);

    private static WatchdogConfiguration WithOAuth(int port)
    {
        var configuration = SmtpEmailTransportTests.Configuration(port, SmtpSecurity.StartTls, pinned: true);
        return configuration with
        {
            Smtp = configuration.Smtp with
            {
                Authentication = SmtpAuthentication.OAuth2ClientCredentials,
                UserName = "watchdog@contoso.test",
                OAuth = new SmtpOAuthSettings { TenantId = TenantId, ClientId = ClientId, ClientSecret = new SecretField { IsSet = true } },
            },
        };
    }
}
