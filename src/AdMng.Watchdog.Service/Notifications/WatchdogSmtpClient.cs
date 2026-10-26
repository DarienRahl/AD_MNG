using MailKit.Net.Smtp;
using MimeKit;

namespace AdMng.Watchdog.Service.Notifications;

/// <summary>
/// MailKit's <see cref="SmtpClient"/> with the watchdog's envelope handling (spec 15.4): a rejected recipient does not
/// abort the message – the others still receive it – and the replies of MAIL FROM / RCPT TO are recorded for the test
/// send stages and for the per-recipient delivery progress (<see cref="SmtpRecipientProgress"/>). No protocol logger is
/// ever attached (it would log DATA = directory values).
/// </summary>
internal sealed class WatchdogSmtpClient : SmtpClient
{
    /// <summary>Status, reply text and address of every rejected recipient of the current message (never logged).</summary>
    public List<(int Status, string Reply, string Address)> RejectedRecipients { get; } = [];

    /// <summary>Addresses accepted at RCPT TO for the current message.</summary>
    public List<string> AcceptedAddresses { get; } = [];

    public int AcceptedRecipients => AcceptedAddresses.Count;

    /// <summary>The MAIL FROM reply (status), when accepted.</summary>
    public int? SenderStatus { get; private set; }

    /// <summary>Resets the per-message counters before a send.</summary>
    public void ResetEnvelope()
    {
        RejectedRecipients.Clear();
        AcceptedAddresses.Clear();
        SenderStatus = null;
    }

    protected override void OnSenderAccepted(MimeMessage message, MailboxAddress mailbox, SmtpResponse response)
    {
        SenderStatus = (int)response.StatusCode;
        base.OnSenderAccepted(message, mailbox, response);
    }

    protected override void OnRecipientAccepted(MimeMessage message, MailboxAddress mailbox, SmtpResponse response)
    {
        AcceptedAddresses.Add(mailbox.Address);
        base.OnRecipientAccepted(message, mailbox, response);
    }

    protected override void OnRecipientNotAccepted(MimeMessage message, MailboxAddress mailbox, SmtpResponse response) =>
        RejectedRecipients.Add(((int)response.StatusCode, response.Response ?? string.Empty, mailbox.Address));   // keep going with the others
}
