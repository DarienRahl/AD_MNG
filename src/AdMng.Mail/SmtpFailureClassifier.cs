using System.Globalization;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using AdMng.Core.Messaging;
using AdMng.Watchdog.Contracts.Api;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MailKitAuthenticationException = MailKit.Security.AuthenticationException;

namespace AdMng.Mail;

/// <summary>
/// MailKit failures → <see cref="UserMessageDelivery"/> with the codes and hint keys of the Watchdog's SMTP channel
/// (<c>SmtpErrorClassifier</c> of the service; the Polish hint texts of the channel tests are reused by the app). The
/// server's reply text is read only for the enhanced status code and Exchange Online markers – never returned or logged.
/// </summary>
public static partial class SmtpFailureClassifier
{
    /// <summary>Classifies an exception of a connect, authentication or send.</summary>
    /// <param name="exception">The failure.</param>
    /// <param name="certificateHint">The hint of the certificate check, if it rejected the server.</param>
    public static UserMessageDelivery Classify(Exception exception, string? certificateHint = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            TimeoutException => new UserMessageDelivery(UserMessageDeliveryOutcome.Transient, "timeout"),
            SmtpCommandException command => ClassifyReply((int)command.StatusCode, command.Message),
            SslHandshakeException => new UserMessageDelivery(UserMessageDeliveryOutcome.TransportFailed, "tls", Hint: certificateHint ?? DeliveryHints.CertificateUntrusted),
            System.Security.Authentication.AuthenticationException => new UserMessageDelivery(UserMessageDeliveryOutcome.TransportFailed, "tls", Hint: certificateHint ?? DeliveryHints.CertificateUntrusted),
            MailKitAuthenticationException { InnerException: SmtpCommandException rejected } => ClassifyAuthentication((int)rejected.StatusCode, rejected.Message),
            MailKitAuthenticationException => new UserMessageDelivery(UserMessageDeliveryOutcome.AuthenticationFailed, "auth"),
            ServiceNotAuthenticatedException => new UserMessageDelivery(UserMessageDeliveryOutcome.AuthenticationFailed, "auth"),
            NotSupportedException => new UserMessageDelivery(UserMessageDeliveryOutcome.TransportFailed, "smtp.notSupported", Hint: DeliveryHints.StartTlsMissing),
            SocketException socket => new UserMessageDelivery(UserMessageDeliveryOutcome.Transient, "socket." + socket.SocketErrorCode.ToString().ToLowerInvariant()),
            ProtocolException or IOException or ServiceNotConnectedException => new UserMessageDelivery(UserMessageDeliveryOutcome.Transient, "io"),
            FormatException => new UserMessageDelivery(UserMessageDeliveryOutcome.Rejected, "address"),
            _ => new UserMessageDelivery(UserMessageDeliveryOutcome.Transient, "smtp.error"),
        };
    }

    /// <summary>Classifies an SMTP reply (also a per-recipient rejection).</summary>
    public static UserMessageDelivery ClassifyReply(int status, string? reply)
    {
        var enhanced = EnhancedStatus(reply);
        var code = enhanced is null
            ? string.Create(CultureInfo.InvariantCulture, $"smtp.{status}")
            : string.Create(CultureInfo.InvariantCulture, $"smtp.{status}.{enhanced}");
        if (status is >= 400 and < 500)
        {
            return new UserMessageDelivery(UserMessageDeliveryOutcome.Transient, code, status);
        }

        if (enhanced == "5.7.139")
        {
            var hint = reply?.Contains("SmtpClientAuthentication", StringComparison.OrdinalIgnoreCase) == true
                ? DeliveryHints.SmtpAuthDisabledForMailbox
                : DeliveryHints.BasicAuthDisabled;
            return new UserMessageDelivery(UserMessageDeliveryOutcome.AuthenticationFailed, code, status, hint);
        }

        if (enhanced == "5.7.30")
        {
            return new UserMessageDelivery(UserMessageDeliveryOutcome.AuthenticationFailed, code, status, DeliveryHints.BasicAuthDisabled);
        }

        if (enhanced == "5.7.68")
        {
            return new UserMessageDelivery(UserMessageDeliveryOutcome.TransportFailed, code, status, DeliveryHints.DirectSendRejected);
        }

        // The message (with its inline images, U1c) is larger than the server accepts: a permanent rejection of this message.
        // 552 alone says so only without an enhanced code – 552 5.2.2 is a full mailbox.
        if (DeliveryHints.IsMessageTooLarge(status, enhanced))
        {
            return new UserMessageDelivery(UserMessageDeliveryOutcome.Rejected, code, status, DeliveryHints.MessageTooLarge);
        }

        return status switch
        {
            530 or 534 or 535 or 538 => new UserMessageDelivery(UserMessageDeliveryOutcome.AuthenticationFailed, code, status),
            _ => new UserMessageDelivery(UserMessageDeliveryOutcome.Rejected, code, status),
        };
    }

    /// <summary>A rejected AUTH: temporary failures stay transient, every permanent one is an authentication failure.</summary>
    public static UserMessageDelivery ClassifyAuthentication(int status, string? reply)
    {
        var classified = ClassifyReply(status, reply);
        return classified.Outcome == UserMessageDeliveryOutcome.Rejected
            ? classified with { Outcome = UserMessageDeliveryOutcome.AuthenticationFailed }
            : classified;
    }

    /// <summary>The RFC 3463 enhanced status code at the start of a reply ("5.1.1"), or null.</summary>
    public static string? EnhancedStatus(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return null;
        }

        var match = EnhancedPattern().Match(reply.TrimStart());
        return match.Success ? match.Value : null;
    }

    [GeneratedRegex(@"^[245]\.\d{1,3}\.\d{1,3}(?=\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex EnhancedPattern();
}
