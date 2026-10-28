using AdMng.Core.Correspondence.Outlook;
using AdMng.Core.Messaging;

namespace AdMng.Outlook;

/// <summary>
/// Messages to users (U1) through classic Outlook (design note docs/dev/u5-outlook-correspondence.md 5):
/// <see cref="GetStatusAsync"/> maps <see cref="IOutlookClient.Availability"/> (Installed → available with
/// <see cref="UserMessageTransportCapabilities.Outlook"/>); <see cref="ConnectAsync"/> opens a session (a failure throws
/// <see cref="UserMessageTransportException"/> with <see cref="UserMessageDeliveryOutcome.TransportFailed"/>); a connection
/// maps each <see cref="OutgoingUserMessage"/> to an <see cref="OutlookMessage"/> (To/Cc, ReplyTo, subject, HTML, inline
/// images, the account, "w imieniu" = From when configured, internet headers <c>Auto-Submitted: auto-generated</c>,
/// <c>X-Auto-Response-Suppress: All</c>, <c>X-AdMng-Message: user-notification</c>) and classifies the result: sent →
/// Accepted <see cref="SentCode"/>; Busy/NotResponding → Transient; <see cref="OutlookFailure.StopsRun"/> kinds →
/// TransportFailed; else Rejected with the failure code. Disposing the connection ends the session.
/// </summary>
public sealed class OutlookUserMessageTransport : IUserMessageTransport
{
    /// <summary>The delivery code of a message Outlook accepted.</summary>
    public const string SentCode = "outlook.sent";

    /// <param name="client">The Outlook client of the application.</param>
    /// <param name="account">The SMTP address of the profile account to send from; null: the profile's default account.</param>
    /// <param name="onBehalfOfSender">Put the message's From address into "w imieniu" (SentOnBehalfOfName).</param>
    public OutlookUserMessageTransport(IOutlookClient client, string? account = null, bool onBehalfOfSender = false)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        Account = string.IsNullOrWhiteSpace(account) ? null : account.Trim();
        OnBehalfOfSender = onBehalfOfSender;
    }

    public IOutlookClient Client { get; }

    /// <summary>The SMTP address of the profile account, or null (the default account).</summary>
    public string? Account { get; }

    public bool OnBehalfOfSender { get; }

    public UserMessageTransportKind Kind => UserMessageTransportKind.Outlook;

    /// <summary>"Outlook (account)" or "Outlook (default account)".</summary>
    public string Description => $"Outlook ({Account ?? "default account"})";

    public Task<UserMessageTransportStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        throw new NotImplementedException("U5 T3 outlook");

    public Task<IUserMessageConnection> ConnectAsync(CancellationToken cancellationToken) =>
        throw new NotImplementedException("U5 T3 outlook");

    public override string ToString() => nameof(OutlookUserMessageTransport);
}
