using System.Net;
using AdMng.Core.Messaging;
using AdMng.Presentation.Settings;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Contracts.Api;
using WatchdogMessageRequest = AdMng.Watchdog.Contracts.Api.UserMessageRequest;

namespace AdMng.Presentation.UserMessages;

/// <summary>
/// The SMTP password of the direct mode for the application session: in memory only, per host and user name, never in
/// the settings, a log or <see cref="ToString"/> (CLAUDE.md rule 2). Cleared with <see cref="Clear"/> or when the
/// application exits. Thread-safe.
/// </summary>
public sealed class SmtpCredentialCache
{
    private readonly Lock _gate = new();
    private (string Host, string User, NetworkCredential Credential)? _entry;

    /// <summary>The cached credential of <paramref name="host"/> + <paramref name="userName"/>, or null.</summary>
    public NetworkCredential? Find(string? host, string? userName)
    {
        lock (_gate)
        {
            return _entry is { } entry
                && string.Equals(entry.Host, host?.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.User, userName?.Trim(), StringComparison.OrdinalIgnoreCase)
                ? entry.Credential
                : null;
        }
    }

    /// <summary>Keeps the password for the session (replaces any other one).</summary>
    public NetworkCredential Remember(string host, string userName, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentNullException.ThrowIfNull(password);
        var credential = new NetworkCredential(userName.Trim(), password);
        lock (_gate)
        {
            _entry = (host.Trim(), userName.Trim(), credential);
        }

        return credential;
    }

    /// <summary>Forgets the password (e.g. after the server rejected it).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entry = null;
        }
    }

    public override string ToString() => nameof(SmtpCredentialCache);
}

/// <summary>
/// Sending through the AdMng Watchdog service (README U1b, design note section 10): the status from
/// <c>GET /api/v1/user-messages/status</c> (allowed for Watchdog Admins and Viewers), each message as one
/// <c>POST /api/v1/user-messages</c> – the service re-validates it, chooses the envelope sender, queues it in its outbox
/// (retries, delivery log) and answers the first SMTP attempt. A repeated id is idempotent, so the dispatcher's retries
/// after a timeout or a lost connection never send a message twice. Mapping of the answers:
/// <list type="bullet">
/// <item>Delivered → Accepted (with the rejected CC addresses); Queued → Accepted with code <c>watchdog.queued</c> (the
/// service delivers it later – never sent again); Rejected → Rejected; Failed → AuthenticationFailed / TransportFailed
/// (the run stops).</item>
/// <item>409 switched off → TransportFailed <c>watchdog.disabled</c>; 400 with <c>senderNotAllowed</c> → TransportFailed
/// <c>watchdog.senderNotAllowed</c>; other 400 → Rejected <c>watchdog.invalid</c>; 409 idempotency conflict → Rejected
/// <c>watchdog.conflict</c>; 429 → TransportFailed <c>watchdog.limit</c>; 401/403 → AuthenticationFailed
/// <c>watchdog.forbidden</c>; 404 / other API version → TransportFailed <c>watchdog.notSupported</c>; certificate →
/// TransportFailed <c>tls</c>; unreachable, timeout, 5xx → Transient (retried with the same id).</item>
/// <item>U1c: the status carries the service's capabilities (<see cref="UserMessageTransportCapabilities"/>: inline images and
/// their limits, the HTML length, the room left for images); inline images are sent only to a service that reports
/// <c>inlineImages</c>. A message with images to a service without it, or a 400 <c>invalidJson</c> / <c>imagesDisabled</c>
/// for a message with images (downgraded between status and send) → TransportFailed <c>watchdog.imagesNotSupported</c>
/// (never retried without images); 429 <c>imageStorageFull</c> → TransportFailed <c>watchdog.imageStorage</c>.</item>
/// </list>
/// </summary>
public sealed class WatchdogUserMessageTransport(IWatchdogClient? client = null) : IUserMessageTransport
{
    public UserMessageTransportKind Kind => UserMessageTransportKind.Watchdog;

    public string Description => client?.BaseAddress is { } address
        ? $"AdMng Watchdog ({address.GetLeftPart(UriPartial.Authority)})"
        : "AdMng Watchdog";

    /// <summary>A Watchdog service address is configured.</summary>
    public bool IsConfigured => client?.IsConfigured == true;

    public async Task<UserMessageTransportStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (client is null || !IsConfigured)
        {
            return new UserMessageTransportStatus(false, UserMessageTransportStatus.NotConfigured);
        }

        try
        {
            var status = await client.GetUserMessagingStatusAsync(cancellationToken).ConfigureAwait(false);
            return new UserMessageTransportStatus(status.Available, status.Available ? null : UserMessageTransportStatus.Disabled) { Capabilities = CapabilitiesOf(status) };
        }
        catch (WatchdogClientException ex) when (ex.Kind == WatchdogClientErrorKind.Cancelled && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (WatchdogClientException ex)
        {
            return new UserMessageTransportStatus(false, ex.Kind switch
            {
                WatchdogClientErrorKind.NotConfigured => UserMessageTransportStatus.NotConfigured,
                WatchdogClientErrorKind.NotFound or WatchdogClientErrorKind.ApiVersionMismatch => UserMessageTransportStatus.NotSupported,
                WatchdogClientErrorKind.Unauthorized or WatchdogClientErrorKind.Forbidden => UserMessageTransportStatus.Forbidden,
                _ => UserMessageTransportStatus.Unreachable,
            });
        }
    }

    public async Task<IUserMessageConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        if (client is null || !IsConfigured)
        {
            throw new UserMessageTransportException(new UserMessageDelivery(UserMessageDeliveryOutcome.TransportFailed, "watchdog.notConfigured"));
        }

        UserMessagingStatusDto status;
        try
        {
            status = await client.GetUserMessagingStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (WatchdogClientException ex) when (ex.Kind == WatchdogClientErrorKind.Cancelled && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (WatchdogClientException ex)
        {
            throw new UserMessageTransportException(Classify(ex), ex);
        }

        return status.Available
            ? new Connection(client, CapabilitiesOf(status))
            : throw new UserMessageTransportException(new UserMessageDelivery(UserMessageDeliveryOutcome.TransportFailed, "watchdog.disabled"));
    }

    /// <summary>What a service with this status accepts (U1c); an older service (no capabilities) takes no images and HTML up
    /// to 100 000 characters.</summary>
    public static UserMessageTransportCapabilities CapabilitiesOf(UserMessagingStatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var htmlLength = status.MaxHtmlLength > 0 ? status.MaxHtmlLength : UserMessageTransportCapabilities.Legacy.MaxHtmlLength;
        if (!(status.Capabilities ?? []).Contains(UserMessagingCapabilities.InlineImages, StringComparer.Ordinal))
        {
            return UserMessageTransportCapabilities.Legacy with { MaxHtmlLength = htmlLength };
        }

        return new UserMessageTransportCapabilities
        {
            InlineImages = status.MaxInlineImages > 0,
            MaxInlineImages = status.MaxInlineImages,
            MaxInlineImageBytes = status.MaxInlineImageBytes,
            MaxInlineImagesTotalBytes = status.MaxInlineImagesTotalBytes,
            MaxHtmlLength = htmlLength,
            ImageStorageAvailableBytes = status.MaxStoredImageBytes > 0 ? Math.Max(0, status.MaxStoredImageBytes - status.StoredImageBytes) : null,
        };
    }

    /// <summary>Code of a message with images the service cannot take (an older version, or images switched off).</summary>
    public const string ImagesNotSupportedCode = "watchdog.imagesNotSupported";

    /// <summary>Code of a message refused because the images waiting in the service's outbox reached its cap.</summary>
    public const string ImageStorageCode = "watchdog.imageStorage";

    /// <summary>The request of a rendered message (nothing is changed: the service checks it again); the inline images go
    /// with it only when the service takes images (<paramref name="withImages"/>).</summary>
    public static WatchdogMessageRequest ToRequest(OutgoingUserMessage message, bool withImages = true)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new WatchdogMessageRequest
        {
            Id = message.Id,
            From = Address(message.From),
            To = [.. message.To.Select(Address)],
            Cc = [.. message.Cc.Select(Address)],
            ReplyTo = message.ReplyTo is { } replyTo ? Address(replyTo) : null,
            Subject = message.Subject,
            TextBody = message.TextBody,
            HtmlBody = message.HtmlBody,
            Language = MessageLanguages.Code(message.Language),
            InlineImages = withImages
                ? [.. message.InlineImages.Select(i => new UserMessageImageDto { ContentId = i.ContentId, MediaType = i.MediaType, Data = i.Data, FileName = i.FileName })]
                : [],
        };
    }

    /// <summary>The delivery of an answer of the service.</summary>
    public static UserMessageDelivery FromResult(UserMessageResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.State switch
        {
            UserMessageState.Delivered => UserMessageDelivery.Accepted(result.SmtpStatus ?? 250) with { RejectedCopies = result.RejectedCopies },
            UserMessageState.Queued => new UserMessageDelivery(UserMessageDeliveryOutcome.Accepted, QueuedCode, result.SmtpStatus, result.Hint),
            UserMessageState.Rejected => new UserMessageDelivery(UserMessageDeliveryOutcome.Rejected, result.Code, result.SmtpStatus, result.Hint),
            _ => new UserMessageDelivery(
                IsAuthentication(result) ? UserMessageDeliveryOutcome.AuthenticationFailed : UserMessageDeliveryOutcome.TransportFailed,
                result.Code,
                result.SmtpStatus,
                result.Hint),
        };
    }

    /// <summary>A failed call of the service, classified (see the class remarks); <paramref name="hadImages"/>: the message
    /// carried inline images (an <c>invalidJson</c> then means a service that does not take them).</summary>
    public static UserMessageDelivery Classify(WatchdogClientException exception, bool hadImages = false)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception.Issues.Any(i => i.Code == UserMessageIssueCodes.ImageStorageFull))
        {
            return new UserMessageDelivery(UserMessageDeliveryOutcome.TransportFailed, ImageStorageCode);
        }

        if (hadImages && exception.Kind == WatchdogClientErrorKind.Validation
            && exception.Issues.Any(i => i.Code is "invalidJson" or UserMessageIssueCodes.ImagesDisabled))
        {
            return new UserMessageDelivery(UserMessageDeliveryOutcome.TransportFailed, ImagesNotSupportedCode);
        }

        if (exception.ProblemType == ApiProblem.UserMessagesUnavailable)
        {
            return new UserMessageDelivery(UserMessageDeliveryOutcome.TransportFailed, "watchdog.disabled");
        }

        if (exception.ProblemType == ApiProblem.RateLimited || exception.StatusCode == 429)
        {
            return new UserMessageDelivery(UserMessageDeliveryOutcome.TransportFailed, "watchdog.limit");
        }

        if (exception.ProblemType == ApiProblem.IdempotencyConflict)
        {
            return new UserMessageDelivery(UserMessageDeliveryOutcome.Rejected, "watchdog.conflict");
        }

        return exception.Kind switch
        {
            WatchdogClientErrorKind.Validation when exception.Issues.Any(i => i.Code == UserMessageIssueCodes.SenderNotAllowed) =>
                new UserMessageDelivery(UserMessageDeliveryOutcome.TransportFailed, "watchdog.senderNotAllowed"),
            WatchdogClientErrorKind.Validation => new UserMessageDelivery(UserMessageDeliveryOutcome.Rejected, "watchdog.invalid"),
            WatchdogClientErrorKind.Unauthorized or WatchdogClientErrorKind.Forbidden =>
                new UserMessageDelivery(UserMessageDeliveryOutcome.AuthenticationFailed, "watchdog.forbidden"),
            WatchdogClientErrorKind.NotFound or WatchdogClientErrorKind.ApiVersionMismatch =>
                new UserMessageDelivery(UserMessageDeliveryOutcome.TransportFailed, "watchdog.notSupported"),
            WatchdogClientErrorKind.NotConfigured => new UserMessageDelivery(UserMessageDeliveryOutcome.TransportFailed, "watchdog.notConfigured"),
            WatchdogClientErrorKind.CertificateInvalid or WatchdogClientErrorKind.CertificateUntrusted or WatchdogClientErrorKind.CertificateChanged =>
                new UserMessageDelivery(UserMessageDeliveryOutcome.TransportFailed, "tls"),
            WatchdogClientErrorKind.Timeout or WatchdogClientErrorKind.Cancelled => new UserMessageDelivery(UserMessageDeliveryOutcome.Transient, "timeout"),
            WatchdogClientErrorKind.Unreachable => new UserMessageDelivery(UserMessageDeliveryOutcome.Transient, "watchdog.unreachable"),
            _ => new UserMessageDelivery(UserMessageDeliveryOutcome.Transient, "watchdog.serverError", exception.StatusCode),
        };
    }

    /// <summary>Code of a message the service queued (accepted, delivered later by the service).</summary>
    public const string QueuedCode = "watchdog.queued";

    private static bool IsAuthentication(UserMessageResult result) =>
        result.Code.StartsWith("auth", StringComparison.Ordinal) || result.Code.StartsWith("secret", StringComparison.Ordinal)
        || result.SmtpStatus is 530 or 534 or 535 or 538;

    private static UserMessageAddressDto Address(MessageAddress address) =>
        new() { Address = address.Address, DisplayName = string.IsNullOrWhiteSpace(address.DisplayName) ? null : address.DisplayName };

    /// <summary>One "connection" = the configured API client; nothing to close.</summary>
    private sealed class Connection(IWatchdogClient client, UserMessageTransportCapabilities capabilities) : IUserMessageConnection
    {
        public async Task<UserMessageDelivery> SendAsync(OutgoingUserMessage message, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(message);
            var hasImages = message.InlineImages.Count > 0;
            if (hasImages && !capabilities.InlineImages)
            {
                // Never sent without its images, and never with a member the service would reject.
                return new UserMessageDelivery(UserMessageDeliveryOutcome.TransportFailed, ImagesNotSupportedCode);
            }

            try
            {
                return FromResult(await client.SendUserMessageAsync(ToRequest(message, hasImages), cancellationToken).ConfigureAwait(false));
            }
            catch (WatchdogClientException ex) when (ex.Kind == WatchdogClientErrorKind.Cancelled && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (WatchdogClientException ex)
            {
                return Classify(ex, hasImages);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>The transport chosen for a send, or why there is none.</summary>
/// <param name="Transport">The transport, null when none can be used.</param>
/// <param name="Problem">Localization key suffix of <c>UserMessages.Transport.Problem.{Problem}</c> when there is none.</param>
/// <param name="NeedsPassword">Direct SMTP with user name + password and no password in the session yet.</param>
public sealed record UserMessageTransportChoiceResult(IUserMessageTransport? Transport, string? Problem = null, bool NeedsPassword = false)
{
    public const string SmtpNotConfigured = "SmtpNotConfigured";
    public const string SmtpUnavailable = "SmtpUnavailable";
    public const string WatchdogNotConfigured = "WatchdogNotConfigured";
    public const string WatchdogNotSupported = "WatchdogNotSupported";
    public const string WatchdogDisabled = "WatchdogDisabled";
    public const string WatchdogForbidden = "WatchdogForbidden";
    public const string WatchdogUnreachable = "WatchdogUnreachable";

    /// <summary>The Outlook transport (U5) cannot be used here: not Windows, or the host has no Outlook client.</summary>
    public const string OutlookNotSupported = "OutlookNotSupported";

    /// <summary>The Outlook transport (U5): classic Outlook is not installed (ProgID "Outlook.Application" missing).</summary>
    public const string OutlookNotInstalled = "OutlookNotInstalled";

    /// <summary>Every problem constant (localization test).</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        SmtpNotConfigured, SmtpUnavailable, WatchdogNotConfigured, WatchdogNotSupported, WatchdogDisabled, WatchdogForbidden, WatchdogUnreachable,
        OutlookNotSupported, OutlookNotInstalled,
    ];

    /// <summary>The problem of an unavailable Watchdog transport status.</summary>
    public static string ForWatchdog(UserMessageTransportStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.Reason switch
        {
            UserMessageTransportStatus.NotConfigured => WatchdogNotConfigured,
            UserMessageTransportStatus.Disabled => WatchdogDisabled,
            UserMessageTransportStatus.Forbidden => WatchdogForbidden,
            UserMessageTransportStatus.Unreachable => WatchdogUnreachable,
            _ => WatchdogNotSupported,
        };
    }
}

/// <summary>
/// Chooses the transport of a send (design note section 7): the setting automatic (default) / direct SMTP / Watchdog /
/// Outlook (U5, docs/dev/u5-outlook-correspondence.md 5: the choice Outlook creates the transport through
/// <see cref="OutlookTransports"/> with the account and "w imieniu" of the settings, when <see cref="Outlook"/> reports
/// classic Outlook installed; Automatic never uses Outlook). Automatic uses the Watchdog when its connection is configured
/// and the service reports the feature available, else direct SMTP from the settings with the session's password.
/// </summary>
public sealed class UserMessageTransportSelector(
    ISettingsStore settings,
    SmtpCredentialCache credentials,
    WatchdogUserMessageTransport watchdog,
    IDirectSmtpTransportFactory? smtp = null,
    Core.Correspondence.Outlook.IOutlookClient? outlook = null,
    IOutlookUserMessageTransportFactory? outlookTransports = null)
{
    public SmtpCredentialCache Credentials { get; } = credentials ?? throw new ArgumentNullException(nameof(credentials));

    /// <summary>Classic Outlook of the host (null: the choice Outlook is not supported here).</summary>
    public Core.Correspondence.Outlook.IOutlookClient? Outlook { get; } = outlook;

    /// <summary>Creates the Outlook transport (AdMng.Outlook, registered by the app; null: the choice Outlook is not
    /// supported here).</summary>
    public IOutlookUserMessageTransportFactory? OutlookTransports { get; } = outlookTransports;

    public async Task<UserMessageTransportChoiceResult> SelectAsync(CancellationToken cancellationToken)
    {
        var options = settings.Current.UserMessages;
        if (options.Transport == UserMessageTransportChoice.Outlook)
        {
            return SelectOutlook(options);
        }

        if (options.Transport != UserMessageTransportChoice.DirectSmtp)
        {
            var status = watchdog.IsConfigured
                ? await watchdog.GetStatusAsync(cancellationToken).ConfigureAwait(true)
                : new UserMessageTransportStatus(false, UserMessageTransportStatus.NotConfigured);
            if (status.IsAvailable)
            {
                return new UserMessageTransportChoiceResult(watchdog);
            }

            if (options.Transport == UserMessageTransportChoice.Watchdog)
            {
                return new UserMessageTransportChoiceResult(null, UserMessageTransportChoiceResult.ForWatchdog(status));
            }
        }

        return Smtp(options.Smtp);
    }

    /// <summary>
    /// The Outlook transport of the settings (account, "w imieniu"): <see cref="UserMessageTransportChoiceResult.OutlookNotSupported"/>
    /// without an Outlook client or factory (a host without Outlook, tests) or off Windows,
    /// <see cref="UserMessageTransportChoiceResult.OutlookNotInstalled"/> without classic Outlook. Outlook is neither started
    /// nor attached to here (that happens when the send connects).
    /// </summary>
    public UserMessageTransportChoiceResult SelectOutlook(UserMessageSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (Outlook is null || OutlookTransports is null)
        {
            return new UserMessageTransportChoiceResult(null, UserMessageTransportChoiceResult.OutlookNotSupported);
        }

        return Outlook.Availability switch
        {
            Core.Correspondence.Outlook.OutlookAvailability.Installed => new UserMessageTransportChoiceResult(
                OutlookTransports.Create(string.IsNullOrWhiteSpace(options.OutlookAccount) ? null : options.OutlookAccount.Trim(), options.OutlookOnBehalfOfSender)),
            Core.Correspondence.Outlook.OutlookAvailability.NotInstalled =>
                new UserMessageTransportChoiceResult(null, UserMessageTransportChoiceResult.OutlookNotInstalled),
            _ => new UserMessageTransportChoiceResult(null, UserMessageTransportChoiceResult.OutlookNotSupported),
        };
    }

    /// <summary>The direct SMTP transport of <paramref name="connection"/> (the current settings or unsaved values).</summary>
    public UserMessageTransportChoiceResult Smtp(SmtpConnectionSettings connection, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (smtp is null)
        {
            return new UserMessageTransportChoiceResult(null, UserMessageTransportChoiceResult.SmtpUnavailable);
        }

        if (!connection.IsConfigured)
        {
            return new UserMessageTransportChoiceResult(null, UserMessageTransportChoiceResult.SmtpNotConfigured);
        }

        var options = connection.ToOptions();
        NetworkCredential? credential = null;
        if (options.Authentication == SmtpAuthentication.Basic)
        {
            credential = !string.IsNullOrEmpty(password) && !string.IsNullOrWhiteSpace(options.UserName)
                ? Credentials.Remember(options.Host, options.UserName, password)
                : Credentials.Find(options.Host, options.UserName);
        }

        return new UserMessageTransportChoiceResult(
            smtp.Create(options, credential),
            NeedsPassword: options.Authentication == SmtpAuthentication.Basic && credential is null);
    }
}
