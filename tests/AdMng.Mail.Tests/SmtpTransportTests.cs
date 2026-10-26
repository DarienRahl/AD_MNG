using System.Net;
using AdMng.Core.Messaging;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Testing.Certificates;
using AdMng.Watchdog.Testing.Smtp;
using MimeKit;
using static AdMng.Mail.Tests.MailTestData;

namespace AdMng.Mail.Tests;

/// <summary>
/// The direct SMTP transport against the in-process SMTP servers of AdMng.Watchdog.Testing: TLS modes, certificate pin,
/// authentication, rejections and retries through the dispatcher.
/// </summary>
public class SmtpTransportTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static UserMessageDispatcher Dispatcher() => new(TimeProvider.System, (_, _) => Task.CompletedTask);

    private static MessageRateLimiter Limiter() => new(TimeProvider.System, (_, _) => Task.CompletedTask) { MessagesPerMinute = 600 };

    private static PlannedUserMessage Planned(OutgoingUserMessage message, int index = 0) => new()
    {
        Index = index,
        DistinguishedName = "CN=Łukasz Wiśniewski,DC=contoso,DC=test",
        Name = "Łukasz Wiśniewski",
        Status = PlannedMessageStatus.Ready,
        Message = message,
    };

    [Fact]
    public async Task StartTls_PinnedCertificate_AndUserNameWithPassword_DeliverThePolishMessage()
    {
        await using var sink = new SmtpSink(new SmtpSinkOptions
        {
            Security = SmtpSinkSecurity.StartTls,
            RequireAuthentication = true,
            Accounts = new Dictionary<string, string> { ["helpdesk"] = "Tajne-Hasło-1" },
        });
        await sink.StartAsync(Token);
        var transport = new SmtpUserMessageTransport(Options(sink.Port, auth: SmtpAuthentication.Basic), new NetworkCredential("helpdesk", "Tajne-Hasło-1"));

        var test = await transport.TestConnectionAsync(Token);
        var results = await Dispatcher().SendAsync(transport, [Planned(Message(cc: "pomoc@contoso.test"))], Limiter(), null, null, Token);

        Assert.True(test.Success, test.Failure?.Code);
        Assert.True(test.IsEncrypted);
        Assert.Equal(TestCertificates.Sha256(TestCertificates.Localhost), test.CertificateSha256);
        Assert.Contains("PLAIN", test.AuthenticationMechanisms);
        Assert.Equal(UserMessageSendStatus.Accepted, results.Single().Status);
        await sink.WaitForMessagesAsync(1, Token);
        var received = sink.Messages.Single();
        Assert.True(received.Secure);
        Assert.Equal("helpdesk", received.AuthenticatedUser);
        Assert.Equal(["lukasz.wisniewski@contoso.test", "pomoc@contoso.test"], received.Recipients.Order(StringComparer.Ordinal));
        var parsed = MimeMessage.Load(new MemoryStream(received.Data), Token);
        Assert.Equal(PolishSubject, parsed.Subject);
        Assert.Contains("ZAŻÓŁĆ GĘŚLĄ JAŹŃ", parsed.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Tajne-Hasło-1", transport.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartTlsRequired_FailsWhenTheServerDoesNotOfferIt()
    {
        await using var sink = new SmtpSink(new SmtpSinkOptions { Security = SmtpSinkSecurity.Plain });
        await sink.StartAsync(Token);

        var test = await new SmtpUserMessageTransport(Options(sink.Port), null).TestConnectionAsync(Token);

        Assert.False(test.Success);
        Assert.Equal(SmtpTestStage.Tls, test.Stage);
        Assert.Equal(UserMessageDeliveryOutcome.TransportFailed, test.Failure!.Outcome);
        Assert.Equal(DeliveryHints.StartTlsMissing, test.Failure.Hint);
        Assert.Empty(sink.Messages);
    }

    [Fact]
    public async Task SystemTrust_RejectsTheSelfSignedCertificate_AndAWrongPinToo()
    {
        await using var sink = new SmtpSink(new SmtpSinkOptions { Security = SmtpSinkSecurity.StartTls });
        await sink.StartAsync(Token);

        var system = await new SmtpUserMessageTransport(Options(sink.Port) with { Certificate = SmtpCertificateCheck.SystemTrust }, null).TestConnectionAsync(Token);
        var wrongPin = await new SmtpUserMessageTransport(Options(sink.Port) with { PinnedSha256 = new string('A', 64) }, null).TestConnectionAsync(Token);
        var noPin = await new SmtpUserMessageTransport(Options(sink.Port) with { PinnedSha256 = "abc" }, null).TestConnectionAsync(Token);

        Assert.Equal((SmtpTestStage.Tls, "tls", DeliveryHints.CertificateUntrusted), (system.Stage, system.Failure!.Code, system.Failure.Hint));
        Assert.Equal(TestCertificates.Sha256(TestCertificates.Localhost), system.CertificateSha256);
        Assert.Equal("tls", wrongPin.Failure!.Code);
        Assert.Equal("config.pin", noPin.Failure!.Code);
    }

    [Fact]
    public async Task ImplicitTls_Works()
    {
        await using var sink = new SmtpSink(new SmtpSinkOptions { Security = SmtpSinkSecurity.ImplicitTls });
        await sink.StartAsync(Token);
        var transport = new SmtpUserMessageTransport(Options(sink.Port, SmtpTlsMode.ImplicitTls), null);

        var results = await Dispatcher().SendAsync(transport, [Planned(Message())], Limiter(), null, null, Token);

        Assert.Equal(UserMessageSendStatus.Accepted, results.Single().Status);
        await sink.WaitForMessagesAsync(1, Token);
        Assert.True(sink.Messages.Single().Secure);
    }

    [Fact]
    public async Task Credentials_AreNeverSentWithoutEncryption()
    {
        await using var server = new ScriptedSmtpServer();
        await server.StartAsync();
        var transport = new SmtpUserMessageTransport(Options(server.Port, SmtpTlsMode.None, SmtpAuthentication.Basic), new NetworkCredential("helpdesk", "Tajne-Hasło-1"));

        var test = await transport.TestConnectionAsync(Token);

        Assert.Equal(("auth.unencrypted", DeliveryHints.StartTlsMissing), (test.Failure!.Code, test.Failure.Hint));
        Assert.DoesNotContain(server.Commands, c => c.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RecipientRejections_AreClassified_TransientOnesAreRetried()
    {
        await using var server = new ScriptedSmtpServer(new ScriptedSmtpOptions { StartTlsCertificate = TestCertificates.Localhost });
        var busyAttempts = 0;
        server.Script = line => line switch
        {
            _ when line.Contains("nieznany@", StringComparison.OrdinalIgnoreCase) && line.StartsWith("RCPT", StringComparison.Ordinal)
                => "550 5.1.1 User unknown",
            _ when line.Contains("zajety@", StringComparison.OrdinalIgnoreCase) && line.StartsWith("RCPT", StringComparison.Ordinal)
                => ++busyAttempts == 1 ? "451 4.3.0 Try again later" : null,
            _ => null,
        };
        await server.StartAsync();
        var transport = new SmtpUserMessageTransport(Options(server.Port), null);

        var results = await Dispatcher().SendAsync(
            transport,
            [Planned(Message(to: "nieznany@contoso.test"), 0), Planned(Message(to: "zajety@contoso.test"), 1), Planned(Message(cc: "nieznany@contoso.test"), 2)],
            Limiter(),
            new UserMessageDispatchOptions { RetryDelays = [TimeSpan.Zero] },
            null,
            Token);

        Assert.Equal((UserMessageSendStatus.Failed, "smtp.550.5.1.1", 1), (results[0].Status, results[0].Delivery!.Code, results[0].Attempts));
        Assert.Equal((UserMessageSendStatus.Accepted, 2), (results[1].Status, results[1].Attempts));
        Assert.Equal(UserMessageSendStatus.Accepted, results[2].Status);
        Assert.Equal(["nieznany@contoso.test"], results[2].Delivery!.RejectedCopies);
        Assert.Equal(2, server.Messages.Count);
    }

    [Fact]
    public async Task ExchangeOnlineAuthReply_StopsTheRun_WithTheHint()
    {
        await using var server = new ScriptedSmtpServer(new ScriptedSmtpOptions { StartTlsCertificate = TestCertificates.Localhost });
        server.Script = line => line.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase)
            ? "535 5.7.139 Authentication unsuccessful, SmtpClientAuthentication is disabled for the Mailbox."
            : null;
        await server.StartAsync();
        var transport = new SmtpUserMessageTransport(Options(server.Port, auth: SmtpAuthentication.Basic), new NetworkCredential("helpdesk@contoso.test", "x"));

        var results = await Dispatcher().SendAsync(transport, [Planned(Message(), 0), Planned(Message(), 1)], Limiter(), null, null, Token);

        Assert.Equal(UserMessageDeliveryOutcome.AuthenticationFailed, results[0].Delivery!.Outcome);
        Assert.Equal(DeliveryHints.SmtpAuthDisabledForMailbox, results[0].Delivery!.Hint);
        Assert.Equal(UserMessageNotSentReason.TransportStopped, results[1].NotSentReason);
        Assert.Empty(server.Messages);
    }

    [Fact]
    public async Task WindowsAccount_NeedsAMechanismTheServerOffers()
    {
        await using var server = new ScriptedSmtpServer(new ScriptedSmtpOptions { StartTlsCertificate = TestCertificates.Localhost });
        await server.StartAsync();

        var test = await new SmtpUserMessageTransport(Options(server.Port, auth: SmtpAuthentication.CurrentWindowsAccount), null).TestConnectionAsync(Token);

        Assert.Equal((SmtpTestStage.Authenticate, "auth.mechanismNotOffered"), (test.Stage, test.Failure!.Code));
    }

    [Fact]
    public void Classifier_UsesTheWatchdogCodesAndHints()
    {
        Assert.Equal(UserMessageDeliveryOutcome.Transient, SmtpFailureClassifier.ClassifyReply(421, "4.4.2 Timeout").Outcome);
        Assert.Equal("smtp.550.5.1.1", SmtpFailureClassifier.ClassifyReply(550, "5.1.1 User unknown").Code);
        Assert.Equal(DeliveryHints.BasicAuthDisabled, SmtpFailureClassifier.ClassifyReply(535, "5.7.139 Authentication unsuccessful, basic authentication is disabled.").Hint);
        Assert.Equal(DeliveryHints.DirectSendRejected, SmtpFailureClassifier.ClassifyReply(550, "5.7.68 TenantInboundAttribution").Hint);
        Assert.Equal(UserMessageDeliveryOutcome.AuthenticationFailed, SmtpFailureClassifier.ClassifyAuthentication(504, "5.5.4 Unrecognized").Outcome);
        Assert.Equal("timeout", SmtpFailureClassifier.Classify(new TimeoutException()).Code);
    }
}
