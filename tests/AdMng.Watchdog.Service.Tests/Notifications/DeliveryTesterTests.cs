using System.Text;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Configuration;
using AdMng.Watchdog.Core.Formatting.Webhooks;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Testing.Certificates;
using AdMng.Watchdog.Testing.Http;
using AdMng.Watchdog.Testing.Smtp;

namespace AdMng.Watchdog.Service.Tests.Notifications;

/// <summary>The test buttons: same code path, stage by stage, no outbox, no rate limit, no health (spec 15.8, 17.2).</summary>
public sealed class DeliveryTesterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EmailTest_ReportsEveryStage_AndUsesTheLanguageOfTheConfiguration()
    {
        await using var sink = new SmtpSink(new SmtpSinkOptions { Security = SmtpSinkSecurity.StartTls });
        await sink.StartAsync(Ct);
        await using var fixture = DeliveryFixture.Create();
        var configuration = SmtpEmailTransportTests.Configuration(sink.Port, SmtpSecurity.StartTls, pinned: true);
        fixture.Configure(configuration with { General = configuration.General with { Language = "pl" } });

        var result = await fixture.Tester.TestEmailAsync(new TestEmailRequest { Recipients = ["jan.kowalski@contoso.test"] }, Ct);

        Assert.True(result.Success);
        Assert.Equal(
            [DeliveryStage.Resolve, DeliveryStage.Connect, DeliveryStage.Tls, DeliveryStage.Sender, DeliveryStage.Recipients, DeliveryStage.Data],
            result.Stages.Select(s => s.Stage));
        Assert.All(result.Stages, s => Assert.True(s.Success));
        Assert.StartsWith("TLS 1.", result.Stages.Single(s => s.Stage == DeliveryStage.Tls).Detail, StringComparison.Ordinal);
        var mime = SmtpEmailTransportTests.Parse(Assert.Single(sink.Messages));
        Assert.Contains("Wiadomość testowa", mime.Subject, StringComparison.Ordinal);
        Assert.Null(fixture.Health.Channel("email"));                    // tests never change channel health
        Assert.Empty(await fixture.Store.Store.Outbox.CountPendingByChannelAsync(Ct));
    }

    [Fact]
    public async Task EmailTest_WithUnsavedSettings_UsesTheirNewPassword()
    {
        await using var sink = new SmtpSink(new SmtpSinkOptions
        {
            Security = SmtpSinkSecurity.StartTls,
            RequireAuthentication = true,
            Accounts = new Dictionary<string, string> { ["watchdog@contoso.test"] = "Nowe-Hasło-1!" },
        });
        await sink.StartAsync(Ct);
        await using var fixture = DeliveryFixture.Create();
        fixture.Configure(new WatchdogConfiguration());
        var unsaved = SmtpEmailTransportTests.Configuration(sink.Port, SmtpSecurity.StartTls, pinned: true, basic: true).Smtp;

        var result = await fixture.Tester.TestEmailAsync(
            new TestEmailRequest
            {
                Recipients = ["jan.kowalski@contoso.test"],
                Settings = unsaved with { Password = new SecretField { NewValue = "Nowe-Hasło-1!" } },
            },
            Ct);

        Assert.True(result.Success);
        Assert.Contains(result.Stages, s => s.Stage == DeliveryStage.Authenticate && s.Success);
        Assert.Equal("watchdog@contoso.test", Assert.Single(sink.Messages).AuthenticatedUser);
    }

    [Fact]
    public async Task EmailTest_ExchangeBasicAuthRejection_FailsAtAuthenticate_WithTheHint()
    {
        await using var server = new ScriptedSmtpServer(new ScriptedSmtpOptions { StartTlsCertificate = TestCertificates.Localhost })
        {
            Script = line => line.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase)
                ? "535 5.7.139 Authentication unsuccessful, basic authentication is disabled."
                : null,
        };
        await server.StartAsync();
        await using var fixture = DeliveryFixture.Create();
        var configuration = SmtpEmailTransportTests.Configuration(server.Port, SmtpSecurity.StartTls, pinned: true, basic: true);
        fixture.Configure(configuration);
        await fixture.StoreSecretAsync(SecretNames.SmtpPassword, "Hasło-Wąż-2026!", SecretBindings.ForSmtpPassword(configuration.Smtp));

        var result = await fixture.Tester.TestEmailAsync(new TestEmailRequest { Recipients = ["jan.kowalski@contoso.test"] }, Ct);

        Assert.False(result.Success);
        var failed = Assert.Single(result.Stages, s => !s.Success);
        Assert.Equal(DeliveryStage.Authenticate, failed.Stage);
        Assert.Equal(535, failed.StatusCode);
        Assert.Equal("5.7.139", failed.EnhancedStatus);
        Assert.Contains(DeliveryHints.BasicAuthDisabled, result.Hints);
    }

    [Fact]
    public async Task EmailTest_UntrustedCertificate_FailsAtTls()
    {
        await using var sink = new SmtpSink(new SmtpSinkOptions { Security = SmtpSinkSecurity.StartTls });
        await sink.StartAsync(Ct);
        await using var fixture = DeliveryFixture.Create();
        fixture.Configure(SmtpEmailTransportTests.Configuration(sink.Port, SmtpSecurity.StartTls));

        var result = await fixture.Tester.TestEmailAsync(new TestEmailRequest { Recipients = ["jan.kowalski@contoso.test"] }, Ct);

        Assert.False(result.Success);
        Assert.Equal(DeliveryStage.Tls, Assert.Single(result.Stages, s => !s.Success).Stage);
        Assert.Contains(DeliveryHints.CertificateUntrusted, result.Hints);
    }

    [Fact]
    public async Task TeamsTest_AlwaysCarriesTheAcceptedNotDeliveredHint()
    {
        await using var sink = new HttpSink(TestCertificates.Localhost);
        await sink.StartAsync(Ct);
        await using var fixture = DeliveryFixture.Create();
        var url = new Uri(sink.BaseAddress, "powerautomate/automations/direct/workflows/abc/triggers/manual/paths/invoke?sig=x").ToString();
        var channel = new ChannelSettings { Id = "teams-pomoc", Type = ChannelType.Teams, Name = "Teams", Url = new SecretField { IsSet = true } };
        fixture.Configure(new WatchdogConfiguration { General = new GeneralSettings { Language = "pl" }, Channels = [channel] });
        await fixture.StoreSecretAsync(SecretNames.ChannelUrl(channel.Id), url, SecretBindings.ForChannelUrl(channel));

        var result = await fixture.Tester.TestChannelAsync(channel.Id, new TestChannelRequest(), Ct);

        Assert.True(result.Success);
        Assert.Equal([DeliveryStage.HttpRequest, DeliveryStage.HttpResponse], result.Stages.Select(s => s.Stage));
        Assert.Equal("HTTP 202", result.Stages[1].Detail);
        Assert.Contains(DeliveryHints.AcceptedNotDelivered, result.Hints);
        Assert.Contains("Wiadomość testowa", Assert.Single(sink.Requests).BodyText, StringComparison.Ordinal);
        Assert.Null(fixture.Health.Channel(channel.Id));

        // A test is not counted against the channel's rate limit: a real delivery right after still goes out.
        Assert.Equal(DeliveryOutcome.Delivered, (await fixture.Teams.SendAsync(TeamsWorkflowTransportTests.Message(), Ct)).Outcome);
    }

    [Fact]
    public async Task WebhookTest_SendsASignedTestEvent()
    {
        await using var sink = new HttpSink(TestCertificates.Localhost);
        await sink.StartAsync(Ct);
        await using var fixture = DeliveryFixture.Create();
        var url = new Uri(sink.BaseAddress, "hooks").ToString();
        var secret = StandardWebhookSigner.GenerateSecret();
        var channel = new ChannelSettings { Id = "siem", Type = ChannelType.Webhook, Name = "SIEM", Url = new SecretField { NewValue = url } };
        channel = channel with { Webhook = new WebhookChannelOptions { SigningSecret = new SecretField { NewValue = secret } } };
        fixture.Configure(new WatchdogConfiguration());

        var result = await fixture.Tester.TestChannelAsync("siem", new TestChannelRequest { Settings = channel }, Ct);

        Assert.True(result.Success);
        var request = Assert.Single(sink.Requests);
        Assert.Contains("watchdog.test", request.BodyText, StringComparison.Ordinal);
        var timestamp = long.Parse(request.Headers["webhook-timestamp"], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(StandardWebhookSigner.Verify(request.Headers["webhook-signature"], new SecretValue(secret), request.Headers["webhook-id"], timestamp, request.Body));
        Assert.DoesNotContain(DeliveryHints.AcceptedNotDelivered, result.Hints);
    }

    [Fact]
    public async Task UnknownChannel_Throws()
    {
        await using var fixture = DeliveryFixture.Create();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Tester.TestChannelAsync("brak", new TestChannelRequest(), Ct));
    }

    [Fact]
    public async Task ChannelTest_WithAStoredUrlBoundElsewhere_FailsBeforeSending()
    {
        await using var fixture = DeliveryFixture.Create();
        var channel = new ChannelSettings { Id = "siem", Type = ChannelType.Webhook, Name = "SIEM", Url = new SecretField { IsSet = true } };
        fixture.Configure(new WatchdogConfiguration { Channels = [channel] });
        await fixture.StoreSecretAsync(SecretNames.ChannelUrl("siem"), "https://siem.contoso.test/hooks", "channel|siem|teams");

        var result = await fixture.Tester.TestChannelAsync("siem", new TestChannelRequest(), Ct);

        Assert.False(result.Success);
        Assert.Contains(DeliveryHints.SecretRebindRequired, result.Hints);
        Assert.Equal(0, fixture.HttpClients.Created);
    }

    [Fact]
    public async Task TestMessages_AreBuiltInBothLanguages()
    {
        // The test sends use the Core notification formatters and texts (NotificationStrings, pl + en).
        var polish = Core.Formatting.EmailAlertFormatter.Test(Core.Formatting.NotificationContext.Create(
            new WatchdogConfiguration { General = new GeneralSettings { Language = "pl", InstanceName = "Centrala" } }, DetailLevel.Full), Infrastructure.TestStore.Start);
        var english = Core.Formatting.EmailAlertFormatter.Test(Core.Formatting.NotificationContext.Create(
            new WatchdogConfiguration { General = new GeneralSettings { Language = "en", InstanceName = "Centrala" } }, DetailLevel.Full), Infrastructure.TestStore.Start);

        Assert.Contains("Wiadomość testowa", polish.Subject, StringComparison.Ordinal);
        Assert.Contains("Test message", english.Subject, StringComparison.Ordinal);
        Assert.Contains("Centrala", polish.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", polish.HtmlBody, StringComparison.OrdinalIgnoreCase);
        Assert.True(Encoding.UTF8.GetByteCount(polish.TextBody) > 0);
    }
}
