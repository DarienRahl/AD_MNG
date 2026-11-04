using System.Net.NetworkInformation;
using AdMng.Core.Messaging;
using MimeKit;

namespace AdMng.Mail;

/// <summary>
/// <see cref="OutgoingUserMessage"/> → MIME: <c>multipart/alternative</c> (text/plain + text/html, UTF-8; MimeKit picks
/// quoted-printable/base64 and encodes the subject and display names per RFC 2047) – with inline images the HTML and its
/// images in <c>multipart/related</c> (<see cref="UserMessageMimeBody"/>, U1c) – <c>Message-ID</c>, <c>Date</c>,
/// <c>Auto-Submitted: auto-generated</c>, <c>X-Auto-Response-Suppress: All</c>, <c>X-AdMng-Message: user-notification</c>.
/// CR/LF and control characters never reach a header. There are no attachments.
/// </summary>
public static class UserMimeMessageBuilder
{
    /// <summary>Value of the <c>X-AdMng-Message</c> header.</summary>
    public const string MessageKind = "user-notification";

    /// <exception cref="FormatException">An address is not a single valid mailbox.</exception>
    public static MimeMessage Build(OutgoingUserMessage message, string localDomain, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(localDomain);
        var mime = new MimeMessage
        {
            Subject = UserMessageRules.HeaderSafe(message.Subject),
            Date = now,
            MessageId = $"{message.Id:N}@{UserMessageRules.HeaderSafe(localDomain)}",
        };
        mime.From.Add(Mailbox(message.From));
        foreach (var to in message.To)
        {
            mime.To.Add(Mailbox(to));
        }

        foreach (var cc in message.Cc)
        {
            mime.Cc.Add(Mailbox(cc));
        }

        if (message.ReplyTo is { } replyTo)
        {
            mime.ReplyTo.Add(Mailbox(replyTo));
        }

        mime.Headers.Add("Auto-Submitted", "auto-generated");
        mime.Headers.Add("X-Auto-Response-Suppress", "All");
        mime.Headers.Add("X-AdMng-Message", MessageKind);
        mime.Body = UserMessageMimeBody.Create(message.TextBody, message.HtmlBody, message.InlineImages);
        return mime;
    }

    /// <summary>A mailbox from a checked address; the display name is header-safe.</summary>
    /// <exception cref="FormatException">Not a single valid address.</exception>
    public static MailboxAddress Mailbox(MessageAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!UserMessageRules.TryParseAddress(address.Address, out var checkedAddress))
        {
            throw new FormatException("An e-mail address is not valid.");
        }

        var name = UserMessageRules.HeaderSafe(address.DisplayName);
        return new MailboxAddress(name.Length == 0 ? null : name, checkedAddress);
    }

    /// <summary>The EHLO / Message-ID domain: the configured one, else the machine's FQDN.</summary>
    public static string LocalDomain(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return UserMessageRules.HeaderSafe(configured);
        }

        var properties = IPGlobalProperties.GetIPGlobalProperties();
        var fqdn = string.IsNullOrEmpty(properties.DomainName) ? properties.HostName : $"{properties.HostName}.{properties.DomainName}";
        return string.IsNullOrWhiteSpace(fqdn) ? "localhost" : fqdn.ToLowerInvariant();
    }
}
