using MailKit.Net.Smtp;
using MimeKit;

namespace AdMng.Watchdog.Service.Notifications;

/// <summary>
/// MailKit's <see cref="SmtpClient"/> with the watchdog's envelope handling (spec 15.4): a rejected recipient does not
/// abort the message – the others still receive it – and the replies of MAIL FROM / RCPT TO are recorded for the test
/// send stages. No protocol logger is ever attached (it would log DATA = directory values).
/// </summary>
internal sealed class WatchdogSmtpClient : SmtpClient
{
    /// <summary>Status and reply text of every rejected recipient of the current message (the reply is never logged).</summary>
    public List<(int Status, string Reply)> RejectedRecipients { get; } = [];

    public int AcceptedRecipients { get; private set; }

    /// <summary>The MAIL FROM reply (status), when accepted.</summary>
    public int? SenderStatus { get; private set; }

    /// <summary>Resets the per-message counters before a send.</summary>
    public void ResetEnvelope()
    {
        RejectedRecipients.Clear();
        AcceptedRecipients = 0;
        SenderStatus = null;
    }

    protected override void OnSenderAccepted(MimeMessage message, MailboxAddress mailbox, SmtpResponse response)
    {
        SenderStatus = (int)response.StatusCode;
        base.OnSenderAccepted(message, mailbox, response);
    }

    protected override void OnRecipientAccepted(MimeMessage message, MailboxAddress mailbox, SmtpResponse response)
    {
        AcceptedRecipients++;
        base.OnRecipientAccepted(message, mailbox, response);
    }

    protected override void OnRecipientNotAccepted(MimeMessage message, MailboxAddress mailbox, SmtpResponse response) =>
        RejectedRecipients.Add(((int)response.StatusCode, response.Response ?? string.Empty));   // keep going with the others
}
