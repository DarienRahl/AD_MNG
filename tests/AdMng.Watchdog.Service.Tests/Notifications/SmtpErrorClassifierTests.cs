using System.Net.Sockets;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Service.Notifications;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace AdMng.Watchdog.Service.Tests.Notifications;

/// <summary>The SMTP classification table of spec 15.4.</summary>
public sealed class SmtpErrorClassifierTests
{
    [Theory]
    [InlineData(421, "4.3.2 Service not available", DeliveryOutcome.Transient, null)]
    [InlineData(450, "4.2.1 Mailbox busy", DeliveryOutcome.Transient, null)]
    [InlineData(451, "4.7.500 Server busy", DeliveryOutcome.Transient, null)]
    [InlineData(452, "4.5.3 Too many recipients", DeliveryOutcome.Transient, null)]
    [InlineData(454, "4.7.0 Temporary authentication failure", DeliveryOutcome.Transient, null)]
    [InlineData(530, "5.7.57 Client not authenticated to send mail", DeliveryOutcome.AuthenticationFailed, null)]
    [InlineData(535, "5.7.3 Authentication unsuccessful", DeliveryOutcome.AuthenticationFailed, null)]
    [InlineData(535, "5.7.139 Authentication unsuccessful, basic authentication is disabled.", DeliveryOutcome.AuthenticationFailed, DeliveryHints.BasicAuthDisabled)]
    [InlineData(535, "5.7.139 Authentication unsuccessful, SmtpClientAuthentication is disabled for the Mailbox.", DeliveryOutcome.AuthenticationFailed, DeliveryHints.SmtpAuthDisabledForMailbox)]
    [InlineData(550, "5.7.30 Basic authentication is not supported for Client Submission.", DeliveryOutcome.AuthenticationFailed, DeliveryHints.BasicAuthDisabled)]
    [InlineData(550, "5.7.68 TenantInboundAttribution; Direct Send not allowed", DeliveryOutcome.ChannelBroken, DeliveryHints.DirectSendRejected)]
    [InlineData(550, "5.1.1 User unknown", DeliveryOutcome.Permanent, null)]
    [InlineData(552, "5.3.4 Message size exceeds fixed limit", DeliveryOutcome.Permanent, DeliveryHints.MessageTooLarge)]
    [InlineData(554, "Transaction failed", DeliveryOutcome.Permanent, null)]
    public void Replies(int status, string reply, DeliveryOutcome expected, string? hint)
    {
        var result = SmtpErrorClassifier.ClassifyCommand(status, reply);

        Assert.Equal(expected, result.Result.Outcome);
        Assert.Equal(hint, result.Result.Detail);
        Assert.Equal(status, result.Result.StatusCode);
        Assert.StartsWith($"smtp.{status}", result.Result.ErrorCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorCodes_CarryTheEnhancedStatus_ButNeverTheReplyText()
    {
        var result = SmtpErrorClassifier.ClassifyCommand(550, "5.1.1 <zofia.zak@contoso.test>: Recipient address rejected");

        Assert.Equal("smtp.550.5.1.1", result.Result.ErrorCode);
        Assert.DoesNotContain("zofia", result.Result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("5.7.139 Authentication unsuccessful", "5.7.139")]
    [InlineData("  4.3.2 busy", "4.3.2")]
    [InlineData("2.0.0 OK", "2.0.0")]
    [InlineData("OK queued as 5.7.1", null)]
    [InlineData("5.7.1234 too long", null)]
    [InlineData(null, null)]
    public void EnhancedStatus(string? reply, string? expected)
    {
        Assert.Equal(expected, SmtpErrorClassifier.EnhancedStatus(reply));
    }

    [Fact]
    public void Exceptions()
    {
        Assert.Equal(("timeout", DeliveryOutcome.Transient), Pair(SmtpErrorClassifier.Classify(new TimeoutException())));
        Assert.Equal(("timeout", DeliveryOutcome.Transient), Pair(SmtpErrorClassifier.Classify(new IOException(), timedOut: true)));
        Assert.Equal(("tls", DeliveryOutcome.ChannelBroken), Pair(SmtpErrorClassifier.Classify(new SslHandshakeException("handshake"))));
        Assert.Equal(("auth", DeliveryOutcome.AuthenticationFailed), Pair(SmtpErrorClassifier.Classify(new AuthenticationException())));
        Assert.Equal(("auth", DeliveryOutcome.AuthenticationFailed), Pair(SmtpErrorClassifier.Classify(new MailKit.ServiceNotAuthenticatedException())));
        Assert.Equal(("socket.timedout", DeliveryOutcome.Transient), Pair(SmtpErrorClassifier.Classify(new SocketException((int)SocketError.TimedOut))));
        Assert.Equal(("io", DeliveryOutcome.Transient), Pair(SmtpErrorClassifier.Classify(new IOException())));
        Assert.Equal(("io", DeliveryOutcome.Transient), Pair(SmtpErrorClassifier.Classify(new MailKit.ServiceNotConnectedException())));
        Assert.Equal(("smtp.notSupported", DeliveryOutcome.ChannelBroken), Pair(SmtpErrorClassifier.Classify(new NotSupportedException())));
    }

    [Fact]
    public void TlsFailures_KeepTheValidationHint()
    {
        var result = SmtpErrorClassifier.Classify(new SslHandshakeException("handshake"), certificateHint: DeliveryHints.RevocationUnavailable);

        Assert.Equal(DeliveryHints.RevocationUnavailable, result.Result.Detail);
    }

    [Fact]
    public void RejectedAuthentication_IsClassifiedByItsReply()
    {
        var reply = new SmtpCommandException(SmtpErrorCode.UnexpectedStatusCode, SmtpStatusCode.AuthenticationInvalidCredentials, "5.7.139 Authentication unsuccessful, basic authentication is disabled.");
        var wrapped = new AuthenticationException("535: 5.7.139 Authentication unsuccessful", reply);

        var result = SmtpErrorClassifier.Classify(wrapped);

        Assert.Equal(DeliveryOutcome.AuthenticationFailed, result.Result.Outcome);
        Assert.Equal("smtp.535.5.7.139", result.Result.ErrorCode);
        Assert.Equal(DeliveryHints.BasicAuthDisabled, result.Result.Detail);
        Assert.Same(reply, SmtpErrorClassifier.CommandOf(wrapped));
    }

    [Fact]
    public void PermanentAuthReplies_AreAuthenticationFailures()
    {
        Assert.Equal(DeliveryOutcome.AuthenticationFailed, SmtpErrorClassifier.ClassifyAuthentication(504, "5.5.4 Unrecognized authentication type").Result.Outcome);
        Assert.Equal(DeliveryOutcome.Transient, SmtpErrorClassifier.ClassifyAuthentication(454, "4.7.0 Try later").Result.Outcome);
    }

    private static (string?, DeliveryOutcome) Pair(ClassifiedDelivery classified) => (classified.Result.ErrorCode, classified.Result.Outcome);
}
