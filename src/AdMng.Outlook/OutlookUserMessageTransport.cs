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

    /// <summary>The status reason when classic Outlook is not installed (localization key suffix).</summary>
    public const string NotInstalledReason = "notInstalled";

    /// <summary>The internet headers of every message (as the direct SMTP transport writes them; best effort in Outlook).</summary>
    public static IReadOnlyDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Auto-Submitted"] = "auto-generated",
        ["X-Auto-Response-Suppress"] = "All",
        ["X-AdMng-Message"] = "user-notification",
    }.AsReadOnly();

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

    /// <summary>Installed → available with the Outlook capabilities; not installed → <see cref="NotInstalledReason"/>; not
    /// Windows → <see cref="UserMessageTransportStatus.NotSupported"/>. Outlook is neither started nor attached to.</summary>
    public Task<UserMessageTransportStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Client.Availability switch
        {
            OutlookAvailability.Installed => UserMessageTransportStatus.Available with { Capabilities = UserMessageTransportCapabilities.Outlook },
            OutlookAvailability.NotInstalled => new UserMessageTransportStatus(false, NotInstalledReason),
            _ => new UserMessageTransportStatus(false, UserMessageTransportStatus.NotSupported),
        });
    }

    /// <summary>Connects to Outlook (attach or start).</summary>
    /// <exception cref="UserMessageTransportException">Outlook cannot be used: TransportFailed with the failure code.</exception>
    public async Task<IUserMessageConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        var result = await Client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (result.Session is { } session)
        {
            return new Connection(this, session);
        }

        var code = result.Failure?.Code ?? OutlookErrors.Code(OutlookFailureKind.Unknown);
        throw new UserMessageTransportException(new UserMessageDelivery(UserMessageDeliveryOutcome.TransportFailed, code));
    }

    public override string ToString() => nameof(OutlookUserMessageTransport);

    /// <summary>The Outlook message of one user message (design note 5).</summary>
    internal OutlookMessage ToOutlookMessage(OutgoingUserMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new OutlookMessage
        {
            To = [.. message.To.Select(address => address.Address)],
            Cc = [.. message.Cc.Select(address => address.Address)],
            ReplyTo = message.ReplyTo is { } replyTo ? [replyTo.Address] : [],
            Subject = message.Subject,
            HtmlBody = message.HtmlBody,
            InlineImages = message.InlineImages,
            SendUsingAccount = Account,
            SentOnBehalfOf = OnBehalfOfSender && !string.IsNullOrWhiteSpace(message.From.Address) ? message.From.Address : null,
            InternetHeaders = Headers,
        };
    }

    /// <summary>The delivery of an Outlook item result.</summary>
    internal static UserMessageDelivery ToDelivery(OutlookItemResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Succeeded)
        {
            return new UserMessageDelivery(UserMessageDeliveryOutcome.Accepted, SentCode);
        }

        var failure = result.Failure ?? OutlookErrors.Failure(OutlookFailureKind.Unknown);
        var outcome = failure.IsTransient
            ? UserMessageDeliveryOutcome.Transient
            : failure.StopsRun ? UserMessageDeliveryOutcome.TransportFailed : UserMessageDeliveryOutcome.Rejected;
        return new UserMessageDelivery(outcome, failure.Code);
    }

    private sealed class Connection(OutlookUserMessageTransport owner, IOutlookSession session) : IUserMessageConnection
    {
        public async Task<UserMessageDelivery> SendAsync(OutgoingUserMessage message, CancellationToken cancellationToken)
        {
            var outlook = owner.ToOutlookMessage(message);
            try
            {
                return ToDelivery(await session.SendAsync(outlook, OutlookSendMode.Send, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ObjectDisposedException)
            {
                return ToDelivery(new OutlookItemResult(false, false, OutlookErrors.Failure(OutlookFailureKind.Disconnected, OutlookSteps.Send)));
            }
            catch (ArgumentException)
            {
                // No usable recipient address.
                return ToDelivery(new OutlookItemResult(false, false, OutlookErrors.Failure(OutlookFailureKind.RecipientUnresolved, OutlookSteps.Recipients)));
            }
        }

        public ValueTask DisposeAsync() => session.DisposeAsync();

        public override string ToString() => nameof(Connection);
    }
}

/// <summary>Creates <see cref="OutlookUserMessageTransport"/>s for Presentation (which does not reference AdMng.Outlook).</summary>
public sealed class OutlookUserMessageTransportFactory : IOutlookUserMessageTransportFactory
{
    public OutlookUserMessageTransportFactory(IOutlookClient client)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public IOutlookClient Client { get; }

    public IUserMessageTransport Create(string? account, bool onBehalfOfSender) => new OutlookUserMessageTransport(Client, account, onBehalfOfSender);

    public override string ToString() => nameof(OutlookUserMessageTransportFactory);
}
