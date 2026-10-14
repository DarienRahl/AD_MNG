using System.Globalization;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Model;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Identity.Client;
using MailKitAuthenticationException = MailKit.Security.AuthenticationException;

namespace AdMng.Watchdog.Service.Notifications;

/// <summary>
/// MailKit / MSAL failures → <see cref="DeliveryResult"/> + hints (spec 15.4). MailKit types stay inside this class, the
/// transport, <see cref="MimeMessageFactory"/> and <see cref="SmtpCertificateValidation"/>. The server's reply text is
/// read only to extract the enhanced status code (e.g. 5.7.139) and Exchange Online markers – it is never logged or
/// returned (it may contain addresses). Error codes: "smtp.&lt;status&gt;", "auth", "tls", "timeout", "socket", "io".
/// </summary>
public static partial class SmtpErrorClassifier
{
    /// <summary>Classifies an exception of a send or test send.</summary>
    /// <param name="exception">The failure.</param>
    /// <param name="timedOut">The send timeout elapsed (not the caller's cancellation).</param>
    /// <param name="certificateHint">The hint recorded by the certificate validation callback, if it rejected the server.</param>
    public static ClassifiedDelivery Classify(Exception exception, bool timedOut = false, string? certificateHint = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (timedOut || exception is TimeoutException)
        {
            return ClassifiedDelivery.Failure(DeliveryOutcome.Transient, "timeout");
        }

        return exception switch
        {
            SmtpCommandException command => ClassifyCommand((int)command.StatusCode, command.Message),
            SslHandshakeException => ClassifiedDelivery.Failure(DeliveryOutcome.ChannelBroken, "tls", certificateHint ?? DeliveryHints.CertificateUntrusted),
            MailKitAuthenticationException { InnerException: SmtpCommandException rejected } => ClassifyAuthentication((int)rejected.StatusCode, rejected.Message),
            MailKitAuthenticationException => ClassifiedDelivery.Failure(DeliveryOutcome.AuthenticationFailed, "auth"),
            ServiceNotAuthenticatedException => ClassifiedDelivery.Failure(DeliveryOutcome.AuthenticationFailed, "auth"),
            MsalServiceException msal => ClassifiedDelivery.Failure(
                DeliveryOutcome.AuthenticationFailed,
                string.Create(CultureInfo.InvariantCulture, $"auth.token.{msal.StatusCode}")),
            MsalClientException => ClassifiedDelivery.Failure(DeliveryOutcome.AuthenticationFailed, "auth.token"),
            NotSupportedException => ClassifiedDelivery.Failure(DeliveryOutcome.ChannelBroken, "smtp.notSupported"),
            SocketException socket => ClassifiedDelivery.Failure(DeliveryOutcome.Transient, "socket." + socket.SocketErrorCode.ToString().ToLowerInvariant()),
            ProtocolException or IOException or ServiceNotConnectedException => ClassifiedDelivery.Failure(DeliveryOutcome.Transient, "io"),
            _ => ClassifiedDelivery.Failure(DeliveryOutcome.Transient, "smtp.error"),
        };
    }

    /// <summary>Classifies an SMTP reply (also used for per-recipient rejections).</summary>
    /// <param name="status">Basic status code (421, 535 …).</param>
    /// <param name="reply">The reply text (read for the enhanced code and Exchange markers only).</param>
    public static ClassifiedDelivery ClassifyCommand(int status, string? reply)
    {
        var enhanced = EnhancedStatus(reply);
        var code = enhanced is null
            ? string.Create(CultureInfo.InvariantCulture, $"smtp.{status}")
            : string.Create(CultureInfo.InvariantCulture, $"smtp.{status}.{enhanced}");
        if (status is >= 400 and < 500)
        {
            return ClassifiedDelivery.Failure(DeliveryOutcome.Transient, code, statusCode: status);
        }

        if (enhanced == "5.7.139")
        {
            var hint = reply?.Contains("SmtpClientAuthentication", StringComparison.OrdinalIgnoreCase) == true
                ? DeliveryHints.SmtpAuthDisabledForMailbox
                : DeliveryHints.BasicAuthDisabled;
            return ClassifiedDelivery.Failure(DeliveryOutcome.AuthenticationFailed, code, hint, status);
        }

        if (enhanced == "5.7.30")
        {
            return ClassifiedDelivery.Failure(DeliveryOutcome.AuthenticationFailed, code, DeliveryHints.BasicAuthDisabled, status);
        }

        if (enhanced == "5.7.68")
        {
            return ClassifiedDelivery.Failure(DeliveryOutcome.ChannelBroken, code, DeliveryHints.DirectSendRejected, status);
        }

        return status switch
        {
            530 or 534 or 535 or 538 => ClassifiedDelivery.Failure(DeliveryOutcome.AuthenticationFailed, code, statusCode: status),
            _ => ClassifiedDelivery.Failure(DeliveryOutcome.Permanent, code, statusCode: status),
        };
    }

    /// <summary>Classifies the reply that rejected AUTH: temporary failures stay transient, every permanent one (also
    /// the ones <see cref="ClassifyCommand"/> would call permanent, e.g. 504) is an authentication failure.</summary>
    public static ClassifiedDelivery ClassifyAuthentication(int status, string? reply)
    {
        var classified = ClassifyCommand(status, reply);
        return classified.Result.Outcome == DeliveryOutcome.Permanent
            ? classified with { Result = classified.Result with { Outcome = DeliveryOutcome.AuthenticationFailed } }
            : classified;
    }

    /// <summary>The SMTP reply behind a MailKit failure (directly or wrapped by an authentication failure), or null.</summary>
    public static SmtpCommandException? CommandOf(Exception exception) => exception switch
    {
        SmtpCommandException command => command,
        MailKitAuthenticationException { InnerException: SmtpCommandException command } => command,
        _ => null,
    };

    /// <summary>The RFC 3463 enhanced status code at the start of a reply ("5.7.139"), or null.</summary>
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
