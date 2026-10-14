using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Configuration;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Testing.Certificates;
using AdMng.Watchdog.Testing.Smtp;
using MimeKit;

namespace AdMng.Watchdog.Service.Tests.Notifications;

/// <summary>The MailKit transport against the in-process <see cref="SmtpSink"/> (spec 22.2 transports).</summary>
public sealed class SmtpEmailTransportTests
{
    private const string Password = "Hasło-Wąż-2026!";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PlainRelay_DeliversAMultipartMessage_WithWatchdogHeaders()
    {
        await using var sink = new SmtpSink();
        await sink.StartAsync(Ct);
        await using var fixture = DeliveryFixture.Create();
        fixture.Configure(Configuration(sink.Port, SmtpSecurity.None));

        var result = await fixture.Smtp.SendAsync(Message() with { RuleId = "new-accounts", Severity = Severity.Low }, Ct);

        Assert.Equal(DeliveryOutcome.Delivered, result.Outcome);
        var received = Assert.Single(sink.Messages);
        Assert.False(received.Secure);
        Assert.Equal(["jan.kowalski@contoso.test", "lucja.nowak@contoso.test"], received.Recipients.Order());
        var mime = Parse(received);
        Assert.Equal("[AD Watchdog][NISKI] Nowe konto: Zofia Żak (zofia.zak)", mime.Subject);
        Assert.Contains("=?utf-8?", received.Text, StringComparison.OrdinalIgnoreCase);   // RFC 2047 encoded Polish subject
        Assert.Equal("0192f3a45b6c7d8e9f00112233445566@watchdog.contoso.test", mime.MessageId);
        Assert.Equal("auto-generated", mime.Headers["Auto-Submitted"]);
        Assert.Equal("All", mime.Headers["X-Auto-Response-Suppress"]);
        Assert.Equal("Low", mime.Headers["X-AdMng-Severity"]);
        Assert.Equal("new-accounts", mime.Headers["X-AdMng-Rule"]);
        Assert.Equal("0192f3a4-5b6c-7d8e-9f00-112233445566", mime.Headers["X-AdMng-Event-Id"]);
        Assert.Equal("Księgowość", mime.TextBody?.Trim());
        Assert.Contains("<b>Księgowość</b>", mime.HtmlBody, StringComparison.Ordinal);
        Assert.Equal(HealthState.Ok, fixture.Health.Channel("email")?.State);
    }

    [Fact]
    public async Task FollowUp_CarriesInReplyToAndReferences()
    {
        await using var sink = new SmtpSink();
        await sink.StartAsync(Ct);
        await using var fixture = DeliveryFixture.Create();
        fixture.Configure(Configuration(sink.Port, SmtpSecurity.None));
        var followUp = Message() with { Kind = DeliveryKind.FollowUp, Email = Message().Email! with { InReplyTo = "0192f3a45b6c7d8e9f00000000000001" } };

        Assert.Equal(DeliveryOutcome.Delivered, (await fixture.Smtp.SendAsync(followUp, Ct)).Outcome);

        var mime = Parse(Assert.Single(sink.Messages));
        Assert.Equal("0192f3a45b6c7d8e9f00000000000001@watchdog.contoso.test", mime.InReplyTo);
        Assert.Equal("0192f3a45b6c7d8e9f00000000000001@watchdog.contoso.test", Assert.Single(mime.References));
    }

    [Fact]
    public async Task StartTls_WithAnUntrustedCertificate_BreaksTheChannel_UnderSystemValidation()
    {
        await using var sink = new SmtpSink(new SmtpSinkOptions { Security = SmtpSinkSecurity.StartTls });
        await sink.StartAsync(Ct);
        await using var fixture = DeliveryFixture.Create();
        fixture.Configure(Configuration(sink.Port, SmtpSecurity.StartTls));

        var result = await fixture.Smtp.SendAsync(Message(), Ct);

        Assert.Equal(DeliveryOutcome.ChannelBroken, result.Outcome);
        Assert.Equal("tls", result.ErrorCode);
        Assert.Equal(DeliveryHints.CertificateUntrusted, result.Detail);
        Assert.Empty(sink.Messages);
        Assert.Equal(HealthState.Error, fixture.Health.Channel("email")?.State);
    }

    [Fact]
    public async Task StartTls_WithThePinnedCertificate_IsDeliveredOverTls()
    {
        await using var sink = new SmtpSink(new SmtpSinkOptions { Security = SmtpSinkSecurity.StartTls });
        await sink.StartAsync(Ct);
        await using var fixture = DeliveryFixture.Create();
        fixture.Configure(Configuration(sink.Port, SmtpSecurity.StartTls, pinned: true));

        Assert.Equal(DeliveryOutcome.Delivered, (await fixture.Smtp.SendAsync(Message(), Ct)).Outcome);

        Assert.True(Assert.Single(sink.Messages).Secure);
    }

    [Fact]
    public async Task ImplicitTls_IsDelivered()
    {
        await using var sink = new SmtpSink(new SmtpSinkOptions { Security = SmtpSinkSecurity.ImplicitTls });
        await sink.StartAsync(Ct);
        await using var fixture = DeliveryFixture.Create();
        fixture.Configure(Configuration(sink.Port, SmtpSecurity.SslOnConnect, pinned: true));

        Assert.Equal(DeliveryOutcome.Delivered, (await fixture.Smtp.SendAsync(Message(), Ct)).Outcome);

        Assert.True(Assert.Single(sink.Messages).Secure);
    }

    [Fact]
    public async Task BasicAuth_HappensOnlyAfterTls_WithTheBoundPassword()
    {
        await using var sink = new SmtpSink(new SmtpSinkOptions
        {
            Security = SmtpSinkSecurity.StartTls,
            RequireAuthentication = true,
            Accounts = new Dictionary<string, string> { ["watchdog@contoso.test"] = Password },
        });
        await sink.StartAsync(Ct);
        await using var fixture = DeliveryFixture.Create();
        var configuration = Configuration(sink.Port, SmtpSecurity.StartTls, pinned: true, basic: true);
        fixture.Configure(configuration);
        await fixture.StoreSecretAsync(SecretNames.SmtpPassword, Password, SecretBindings.ForSmtpPassword(configuration.Smtp));

        Assert.Equal(DeliveryOutcome.Delivered, (await fixture.Smtp.SendAsync(Message(), Ct)).Outcome);

        var received = Assert.Single(sink.Messages);
        Assert.True(received.Secure);
        Assert.Equal("watchdog@contoso.test", received.AuthenticatedUser);
    }

    [Fact]
    public async Task ChangedHost_WithoutANewPassword_SendsNothing_AndTheSinkSeesNoAuth()
    {
        await using var sink = new SmtpSink(new SmtpSinkOptions
        {
            Security = SmtpSinkSecurity.StartTls,
            RequireAuthentication = true,
            Accounts = new Dictionary<string, string> { ["watchdog@contoso.test"] = Password },
        });
        await sink.StartAsync(Ct);
        await using var fixture = DeliveryFixture.Create();
        var configuration = Configuration(sink.Port, SmtpSecurity.StartTls, pinned: true, basic: true);
        var previousHost = configuration with { Smtp = configuration.Smtp with { Host = "mail.contoso.test" } };
        await fixture.StoreSecretAsync(SecretNames.SmtpPassword, Password, SecretBindings.ForSmtpPassword(previousHost.Smtp));
        fixture.Configure(configuration);

        var result = await fixture.Smtp.SendAsync(Message(), Ct);

        Assert.Equal(DeliveryOutcome.AuthenticationFailed, result.Outcome);
        Assert.Equal("secret.rebindRequired", result.ErrorCode);
        Assert.Equal(DeliveryHints.SecretRebindRequired, result.Detail);
        Assert.Empty(sink.AuthenticationAttempts);
        Assert.Empty(sink.Messages);
    }

    [Fact]
    public async Task OpportunisticTls_WithoutTlsOnTheServer_NeverSendsThePassword()
    {
        await using var sink = new SmtpSink();   // no STARTTLS offered
        await sink.StartAsync(Ct);
        await using var fixture = DeliveryFixture.Create();
        var configuration = Configuration(sink.Port, SmtpSecurity.StartTlsWhenAvailable, basic: true);
        fixture.Configure(configuration);
        await fixture.StoreSecretAsync(SecretNames.SmtpPassword, Password, SecretBindings.ForSmtpPassword(configuration.Smtp));

        var result = await fixture.Smtp.SendAsync(Message(), Ct);

        Assert.Equal(DeliveryOutcome.ChannelBroken, result.Outcome);
        Assert.Equal("smtp.insecureCredentials", result.ErrorCode);
        Assert.Empty(sink.AuthenticationAttempts);
        Assert.Empty(sink.Messages);
    }

    [Fact]
    public async Task Recipients_AreSplitByMaxRecipientsPerMessage()
    {
        await using var sink = new SmtpSink();
        await sink.StartAsync(Ct);
        await using var fixture = DeliveryFixture.Create();
        var configuration = Configuration(sink.Port, SmtpSecurity.None);
        fixture.Configure(configuration with { Smtp = configuration.Smtp with { MaxRecipientsPerMessage = 2 } });
        var recipients = Enumerable.Range(1, 5).Select(i => $"pomoc{i}@contoso.test").ToList();

        Assert.Equal(DeliveryOutcome.Delivered, (await fixture.Smtp.SendAsync(Message() with { Recipients = recipients }, Ct)).Outcome);

        Assert.Equal([2, 2, 1], sink.Messages.Select(m => m.Recipients.Count));
        Assert.Equal(3, sink.Messages.Select(m => Parse(m).MessageId).Distinct().Count());
    }

    [Fact]
    public async Task RateLimit_DefersWithoutAnAttempt_UntilTheBucketRefills()
    {
        await using var sink = new SmtpSink();
        await sink.StartAsync(Ct);
        await using var fixture = DeliveryFixture.Create();
        var configuration = Configuration(sink.Port, SmtpSecurity.None);
        fixture.Configure(configuration with { Smtp = configuration.Smtp with { MaxMessagesPerMinute = 1 } });

        Assert.Equal(DeliveryOutcome.Delivered, (await fixture.Smtp.SendAsync(Message(), Ct)).Outcome);
        var deferred = await fixture.Smtp.SendAsync(Message(), Ct);
        Assert.Equal(DeliveryOutcome.Deferred, deferred.Outcome);
        Assert.True(deferred.RetryAfter > TimeSpan.Zero);

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(DeliveryOutcome.Delivered, (await fixture.Smtp.SendAsync(Message(), Ct)).Outcome);
        Assert.Equal(2, sink.Messages.Count);
    }

    [Fact]
    public async Task DisabledSmtp_BreaksTheChannel()
    {
        await using var fixture = DeliveryFixture.Create();
        fixture.Configure(new WatchdogConfiguration());

        var result = await fixture.Smtp.SendAsync(Message(), Ct);

        Assert.Equal(DeliveryOutcome.ChannelBroken, result.Outcome);
        Assert.Equal("smtp.disabled", result.ErrorCode);
    }

    internal static WatchdogConfiguration Configuration(int port, SmtpSecurity security, bool pinned = false, bool basic = false) => new()
    {
        General = new GeneralSettings { InstanceName = "Watchdog – Centrala" },
        Smtp = new SmtpSettings
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = port,
            Security = security,
            Authentication = basic ? SmtpAuthentication.Basic : SmtpAuthentication.None,
            UserName = basic ? "watchdog@contoso.test" : null,
            Password = new SecretField { IsSet = basic },
            From = "AD Watchdog <watchdog@contoso.test>",
            LocalDomain = "watchdog.contoso.test",
            CertificateValidation = pinned ? CertificateValidationMode.PinnedSha256 : CertificateValidationMode.System,
            PinnedSha256 = pinned ? TestCertificates.Sha256(TestCertificates.Localhost) : null,
            CheckRevocation = false,
            Timeout = TimeSpan.FromSeconds(30),
        },
    };

    internal static OutboxMessage Message() => new()
    {
        MessageId = "0192f3a45b6c7d8e9f00112233445566",
        ChannelType = DeliveryChannelType.Email,
        ChannelId = "email",
        Kind = DeliveryKind.Alert,
        Recipients = ["jan.kowalski@contoso.test", "lucja.nowak@contoso.test"],
        Email = new RenderedEmail
        {
            Subject = "[AD Watchdog][NISKI] Nowe konto: Zofia Żak (zofia.zak)",
            HtmlBody = "<p><b>Księgowość</b></p>",
            TextBody = "Księgowość",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-AdMng-Event-Id"] = "0192f3a4-5b6c-7d8e-9f00-112233445566",
                ["Bcc"] = "never@evil.example",
                ["X-Injected"] = "a\r\nBcc: evil@evil.example",
            },
        },
        CreatedAt = DateTimeOffset.UnixEpoch,
        NextAttemptAt = DateTimeOffset.UnixEpoch,
    };

    internal static MimeMessage Parse(SmtpSinkMessage message)
    {
        using var stream = new MemoryStream(message.Data);
        return MimeMessage.Load(stream);
    }
}
