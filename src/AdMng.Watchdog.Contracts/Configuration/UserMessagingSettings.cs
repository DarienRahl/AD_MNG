namespace AdMng.Watchdog.Contracts.Configuration;

/// <summary>
/// Messages to users submitted by AD Manager through <c>POST /api/v1/user-messages</c> (README U1b): sent over the SMTP
/// channel (<see cref="SmtpSettings"/>) through the outbox. Off by default. The limits count submitted messages of all
/// callers in a sliding window (the last 60 minutes / 24 hours).
/// </summary>
public sealed record UserMessagingSettings
{
    public bool Enabled { get; init; }
    public int MaxMessagesPerHour { get; init; } = 300;
    public int MaxMessagesPerDay { get; init; } = 2000;
    /// <summary>Header From / Reply-To addresses accepted besides <c>smtp.from</c> and <c>smtp.replyTo</c>: whole addresses
    /// (<c>helpdesk@contoso.com</c>) or a domain as <c>@contoso.com</c>. The envelope sender is always <c>smtp.from</c>.</summary>
    public IReadOnlyList<string> AllowedSenders { get; init; } = [];
}
