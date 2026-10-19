using System.Globalization;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
using MimeKit;

namespace AdMng.Watchdog.Service.Notifications;

/// <summary>
/// <see cref="RenderedEmail"/> → MIME (spec 14.3, 15.6): <c>multipart/alternative</c> (text/plain + text/html, UTF-8;
/// MimeKit picks 8bit/base64 per the server's capabilities), <c>Message-ID: &lt;{MessageId}@{LocalDomain}&gt;</c>,
/// <c>Auto-Submitted: auto-generated</c>, <c>X-Auto-Response-Suppress: All</c>, <c>X-AdMng-Severity</c>,
/// <c>X-AdMng-Rule</c>, the formatter's own <c>X-AdMng-*</c> headers, <c>In-Reply-To</c>/<c>References</c> for follow-ups.
/// Recipients are split into messages of at most <see cref="SmtpSettings.MaxRecipientsPerMessage"/>; CR/LF and control
/// characters never reach a header.
/// </summary>
public static partial class MimeMessageFactory
{
    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "From", "To", "Cc", "Bcc", "Reply-To", "Sender", "Subject", "Message-ID", "In-Reply-To", "References", "Date",
        "MIME-Version", "Content-Type", "Content-Transfer-Encoding", "Return-Path", "Received",
    };

    /// <summary>The EHLO / Message-ID domain: <see cref="SmtpSettings.LocalDomain"/> or the machine's FQDN.</summary>
    public static string LocalDomain(SmtpSettings smtp)
    {
        ArgumentNullException.ThrowIfNull(smtp);
        if (!string.IsNullOrWhiteSpace(smtp.LocalDomain))
        {
            return smtp.LocalDomain.Trim();
        }

        var properties = IPGlobalProperties.GetIPGlobalProperties();
        var fqdn = string.IsNullOrEmpty(properties.DomainName) ? properties.HostName : $"{properties.HostName}.{properties.DomainName}";
        return string.IsNullOrWhiteSpace(fqdn) ? "localhost" : fqdn.ToLowerInvariant();
    }

    /// <summary>The MIME messages of one outbox e-mail (one per recipient chunk). The recipients are sorted before they
    /// are chunked, so the same recipient set always gives the same chunks: chunk n has the Message-ID
    /// <c>{MessageId}.{n}</c> (the first one <c>{MessageId}</c>), and chunk n of a follow-up replies to chunk n of its
    /// original (<c>In-Reply-To: {original}.{n}</c>) – the follow-up goes to the original's recipients, so every recipient
    /// gets the reply in the thread of the message it received.</summary>
    /// <exception cref="FormatException">The configured sender address is not valid.</exception>
    public static IReadOnlyList<MimeMessage> Create(OutboxMessage message, SmtpSettings smtp, string localDomain, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(smtp);
        var email = message.Email ?? throw new ArgumentException("The outbox message carries no e-mail.", nameof(message));
        var chunks = message.Recipients
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Chunk(Math.Max(1, smtp.MaxRecipientsPerMessage))
            .ToList();
        var result = new List<MimeMessage>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunkEmail = i > 0 && !string.IsNullOrWhiteSpace(email.InReplyTo) ? email with { InReplyTo = ChunkId(email.InReplyTo, i) } : email;
            result.Add(Create(chunkEmail, smtp, chunks[i], ChunkId(message.MessageId, i), localDomain, message.Severity, message.RuleId, now));
        }

        return result;
    }

    /// <summary>The Message-ID local part of recipient chunk <paramref name="index"/> (0-based) of a message.</summary>
    public static string ChunkId(string messageId, int index) =>
        index == 0 ? messageId : string.Create(CultureInfo.InvariantCulture, $"{messageId}.{index + 1}");

    /// <summary>One MIME message for <paramref name="recipients"/>.</summary>
    public static MimeMessage Create(
        RenderedEmail email,
        SmtpSettings smtp,
        IReadOnlyList<string> recipients,
        string messageId,
        string localDomain,
        Severity severity,
        string? ruleId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(smtp);
        ArgumentNullException.ThrowIfNull(recipients);
        var mime = new MimeMessage
        {
            Subject = HeaderSafe(email.Subject),
            Date = now,
            MessageId = $"{messageId}@{localDomain}",
        };
        mime.From.Add(ParseMailbox(smtp.From ?? throw new FormatException("No sender address is configured.")));
        if (!string.IsNullOrWhiteSpace(smtp.ReplyTo))
        {
            mime.ReplyTo.Add(ParseMailbox(smtp.ReplyTo));
        }

        foreach (var recipient in recipients)
        {
            mime.To.Add(ParseMailbox(recipient));
        }

        if (!string.IsNullOrWhiteSpace(email.InReplyTo))
        {
            var original = $"{HeaderSafe(email.InReplyTo)}@{localDomain}";
            mime.InReplyTo = original;
            mime.References.Add(original);
        }

        mime.Headers.Add("Auto-Submitted", "auto-generated");
        mime.Headers.Add("X-Auto-Response-Suppress", "All");
        mime.Headers.Add("X-AdMng-Severity", severity.ToString());
        if (!string.IsNullOrWhiteSpace(ruleId))
        {
            mime.Headers.Add("X-AdMng-Rule", HeaderSafe(ruleId));
        }

        foreach (var (name, value) in email.Headers)
        {
            if (HeaderNamePattern().IsMatch(name) && !ReservedHeaders.Contains(name) && mime.Headers[name] is null)
            {
                mime.Headers.Add(name, HeaderSafe(value));
            }
        }

        mime.Body = new BodyBuilder { TextBody = email.TextBody, HtmlBody = email.HtmlBody }.ToMessageBody();
        return mime;
    }

    /// <summary>Parses an address ("Name &lt;a@b&gt;" or "a@b"; a local part and a domain are required).</summary>
    /// <exception cref="FormatException">Not a single valid mailbox (the address itself is not part of the message).</exception>
    public static MailboxAddress ParseMailbox(string address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Any(char.IsControl) || !MailboxAddress.TryParse(address, out var mailbox)
            || string.IsNullOrEmpty(mailbox.LocalPart) || string.IsNullOrEmpty(mailbox.Domain))
        {
            throw new FormatException("An e-mail address is not valid.");
        }

        return mailbox;
    }

    /// <summary>Removes CR/LF and other control characters (header injection) and trims.</summary>
    public static string HeaderSafe(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(char.IsControl(c) ? ' ' : c);
        }

        return builder.ToString().Trim();
    }

    [GeneratedRegex("^[A-Za-z0-9-]{1,76}$", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderNamePattern();
}
