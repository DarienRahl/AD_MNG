using System.Security.Authentication;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Service.Notifications;

namespace AdMng.Watchdog.Service.Tests.Notifications;

/// <summary>MIME building and the delivery TLS settings (spec 14.3, 15.6).</summary>
public sealed class MimeMessageFactoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DuplicateAndBlankRecipients_AreRemoved_CaseInsensitively()
    {
        var message = SmtpEmailTransportTests.Message() with { Recipients = ["jan.kowalski@contoso.test", "JAN.KOWALSKI@contoso.test", " ", "lucja.nowak@contoso.test"] };

        var mime = Assert.Single(MimeMessageFactory.Create(message, Smtp(), "watchdog.contoso.test", Now));

        Assert.Equal(2, mime.To.Count);
    }

    [Fact]
    public void ChunkMessageIds_AreSuffixed()
    {
        var message = SmtpEmailTransportTests.Message() with { Recipients = ["a@contoso.test", "b@contoso.test", "c@contoso.test"] };

        var parts = MimeMessageFactory.Create(message, Smtp() with { MaxRecipientsPerMessage = 1 }, "watchdog.contoso.test", Now);

        Assert.Equal(
            ["0192f3a45b6c7d8e9f00112233445566@watchdog.contoso.test", "0192f3a45b6c7d8e9f00112233445566.2@watchdog.contoso.test", "0192f3a45b6c7d8e9f00112233445566.3@watchdog.contoso.test"],
            parts.Select(p => p.MessageId));
    }

    [Fact]
    public void ReplyTo_AndDate_AreSet()
    {
        var mime = Assert.Single(MimeMessageFactory.Create(SmtpEmailTransportTests.Message(), Smtp() with { ReplyTo = "pomoc@contoso.test" }, "watchdog.contoso.test", Now));

        Assert.Equal("pomoc@contoso.test", mime.ReplyTo.Mailboxes.Single().Address);
        Assert.Equal(Now, mime.Date);
        Assert.Equal("AD Watchdog", mime.From.Mailboxes.Single().Name);
    }

    [Theory]
    [InlineData("nie-adres")]
    [InlineData("a@contoso.test\r\nBcc: evil@evil.example")]
    [InlineData("")]
    public void InvalidSender_IsAFormatError(string from)
    {
        Assert.Throws<FormatException>(() => MimeMessageFactory.Create(SmtpEmailTransportTests.Message(), Smtp() with { From = from }, "watchdog.contoso.test", Now));
    }

    [Fact]
    public async Task InvalidSender_IsAPermanentFailure_WithoutAConnection()
    {
        await using var fixture = DeliveryFixture.Create();
        var configuration = SmtpEmailTransportTests.Configuration(Testing.Net.FreeTcpPort.Next(), SmtpSecurity.None);
        fixture.Configure(configuration with { Smtp = configuration.Smtp with { From = "nie-adres" } });

        var result = await fixture.Smtp.SendAsync(SmtpEmailTransportTests.Message(), TestContext.Current.CancellationToken);

        Assert.Equal(DeliveryOutcome.Permanent, result.Outcome);
        Assert.Equal("smtp.invalidAddress", result.ErrorCode);
    }

    [Fact]
    public void LocalDomain_FallsBackToTheMachineName()
    {
        Assert.Equal("watchdog.contoso.test", MimeMessageFactory.LocalDomain(Smtp()));
        Assert.False(string.IsNullOrWhiteSpace(MimeMessageFactory.LocalDomain(Smtp() with { LocalDomain = null })));
    }

    [Fact]
    public void HeaderSafe_RemovesControlCharacters()
    {
        Assert.Equal("a  Bcc: x", MimeMessageFactory.HeaderSafe("a\r\nBcc: x"));
        Assert.Equal(string.Empty, MimeMessageFactory.HeaderSafe(null));
    }

    [Fact]
    public void DeliveryConnections_AllowOnlyTls12And13()
    {
        Assert.Equal(SslProtocols.Tls12 | SslProtocols.Tls13, DeliveryTls.Protocols);
        using var handler = DeliveryHttpClient.CreateHandler();
        Assert.Equal(DeliveryTls.Protocols, handler.SslOptions.EnabledSslProtocols);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
    }

    [Fact]
    public void LoggedUrls_HaveNoQuery()
    {
        Assert.Equal(
            "https://contoso.environment.api.powerplatform.com/powerautomate/automations/direct/workflows/abc",
            DeliveryHttpClient.ForLog(new Uri("https://contoso.environment.api.powerplatform.com/powerautomate/automations/direct/workflows/abc?sig=tajne#x")));
    }

    private static SmtpSettings Smtp() => new()
    {
        Enabled = true,
        Host = "smtp.contoso.test",
        From = "AD Watchdog <watchdog@contoso.test>",
        LocalDomain = "watchdog.contoso.test",
    };
}
