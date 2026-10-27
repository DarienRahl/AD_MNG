using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Notifications;
using AdMng.Watchdog.Service.Api;
using AdMng.Watchdog.Service.Auth;
using AdMng.Watchdog.Service.Hosting;

namespace AdMng.Watchdog.Service.UserMessages;

/// <summary>
/// Messages to users submitted by AD Manager (README U1b, <c>docs/dev/u1-user-messages.md</c> section 10): availability
/// (<c>userMessages.enabled</c> + a complete SMTP channel), the service's own validation
/// (<see cref="UserMessageRequestValidator"/>), idempotency by message id, the sliding hourly/daily limits and the audit
/// row – then the message goes to the outbox (kind <see cref="DeliveryKind.UserMessage"/>) and the request waits up to
/// <see cref="ApiLimits.UserMessageWait"/> for the first SMTP attempt:
/// <list type="bullet">
/// <item>accepted → <see cref="UserMessageState.Delivered"/> (200, with the rejected CC addresses);</item>
/// <item>5xx → <see cref="UserMessageState.Rejected"/> (200; Dead in the delivery log, payload per <c>outboxDeadDays</c>);</item>
/// <item>4xx / timeout / connection lost, a rate-limit deferral or no attempt in time → <see cref="UserMessageState.Queued"/>
/// (202; the outbox retries per its policy and the delivery log shows the outcome);</item>
/// <item>authentication / TLS / configuration failure of the channel → <see cref="UserMessageState.Failed"/> (200; the
/// message is given up at once – the run in AD Manager stops).</item>
/// </list>
/// A repeated id (same caller, same content) answers the state of the first submission (waiting again while it is still
/// queued); another caller or other content is 409 <c>idempotencyConflict</c>. The diagnostic log gets one line per
/// message with the caller and the recipient count – never an address, a hash, the subject or the body.
/// </summary>
public sealed partial class UserMessageService
{
    /// <summary>Value of the <c>X-AdMng-Message</c> header (as the direct SMTP transport of AD Manager writes it).</summary>
    public const string MessageHeaderValue = "user-notification";

    private readonly IWatchdogConfigProvider _configuration;
    private readonly IUserMessageStore _store;
    private readonly IOutboxStore _outbox;
    private readonly UserMessageDeliveryNotifier _notifier;
    private readonly IWatchdogSignals _signals;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<UserMessageService> _logger;
    private readonly SemaphoreSlim _validation = new(ApiLimits.ConcurrentUserMessageValidations, ApiLimits.ConcurrentUserMessageValidations);

    public UserMessageService(
        IWatchdogConfigProvider configuration,
        IUserMessageStore store,
        IOutboxStore outbox,
        UserMessageDeliveryNotifier notifier,
        IWatchdogSignals signals,
        TimeProvider timeProvider,
        ILogger<UserMessageService> logger)
    {
        _configuration = configuration;
        _store = store;
        _outbox = outbox;
        _notifier = notifier;
        _signals = signals;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>How long a request waits for the first attempt (tests shorten it).</summary>
    public TimeSpan Wait { get; set; } = ApiLimits.UserMessageWait;

    /// <summary><c>GET /api/v1/user-messages/status</c>.</summary>
    public async Task<UserMessagingStatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        var configuration = _configuration.Current.Configuration;
        var settings = configuration.UserMessages ?? new UserMessagingSettings();
        var smtp = configuration.Smtp ?? new SmtpSettings();
        var (hour, day) = await _store.CountAsync(_timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return new UserMessagingStatusDto
        {
            Enabled = settings.Enabled,
            SmtpConfigured = IsSmtpConfigured(smtp),
            Channel = string.IsNullOrWhiteSpace(smtp.Host) ? null : string.Create(CultureInfo.InvariantCulture, $"{smtp.Host.Trim()}:{smtp.Port}"),
            Sender = UserMessageRequestValidator.AddressOf(smtp.From),
            AllowedSenders = [.. (settings.AllowedSenders ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim())],
            MaxMessagesPerHour = settings.MaxMessagesPerHour,
            MaxMessagesPerDay = settings.MaxMessagesPerDay,
            SubmittedLastHour = hour,
            SubmittedLastDay = day,
            Capabilities = settings.AllowInlineImages ? [UserMessagingCapabilities.InlineImages] : [],
            MaxInlineImages = settings.AllowInlineImages ? UserMessageLimits.MaxInlineImages : 0,
            MaxInlineImageBytes = settings.AllowInlineImages ? UserMessageLimits.MaxInlineImageBytes : 0,
            MaxInlineImagesTotalBytes = settings.AllowInlineImages ? UserMessageLimits.MaxInlineImagesTotalBytes : 0,
            MaxHtmlLength = UserMessageLimits.MaxHtmlLength,
            StoredImageBytes = await _store.StoredImageBytesAsync(cancellationToken).ConfigureAwait(false),
            MaxStoredImageBytes = MaxStoredImageBytes(settings),
        };
    }

    /// <summary><c>userMessages.maxStoredImageMb</c> in bytes.</summary>
    public static long MaxStoredImageBytes(UserMessagingSettings settings) =>
        Math.Clamp((settings ?? new UserMessagingSettings()).MaxStoredImageMb, 16, 4096) * 1024L * 1024L;

    /// <summary><c>POST /api/v1/user-messages</c> (the body already read).</summary>
    public async Task<IResult> SubmitAsync(UserMessageRequest request, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(user);
        var configuration = _configuration.Current.Configuration;
        var settings = configuration.UserMessages ?? new UserMessagingSettings();
        var smtp = configuration.Smtp ?? new SmtpSettings();
        if (!settings.Enabled)
        {
            return Unavailable(UserMessageIssueCodes.Disabled, "Messages to users are switched off in the service (userMessages.enabled).");
        }

        if (!IsSmtpConfigured(smtp))
        {
            return Unavailable(UserMessageIssueCodes.SmtpNotConfigured, "The e-mail channel (SMTP) of the service is disabled or incomplete.");
        }

        IReadOnlyList<ConfigIssue> issues;
        UserMessageContent? content;
        await _validation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            issues = UserMessageRequestValidator.Validate(request, smtp, settings, out content);
        }
        finally
        {
            _validation.Release();
        }

        if (issues.Count > 0 || content is null)
        {
            return ApiResults.Invalid(issues, "The message is not valid.");
        }

        var now = _timeProvider.GetUtcNow();
        var envelope = UserMessageRequestValidator.Envelope(request, smtp);
        var messageId = request.Id.ToString("N");
        var record = new UserMessageAuditRecord
        {
            MessageId = request.Id,
            SubmittedAt = now,
            BySid = CallerIdentity.Sid(user),
            ByAccount = CallerIdentity.Account(user),
            RecipientCount = envelope.RecipientCount,
            RecipientsHash = RecipientsHash(envelope),
            ContentHash = ContentHash(request),
        };
        var message = new OutboxMessage
        {
            MessageId = messageId,
            ChannelType = DeliveryChannelType.Email,
            ChannelId = NotificationTargetResolver.EmailChannelId,
            Kind = DeliveryKind.UserMessage,
            Email = new RenderedEmail
            {
                Subject = request.Subject.Trim(),
                TextBody = request.TextBody,
                // U1c: the shared sanitizer's document and the checked, stripped images – only in the payload.
                HtmlBody = content.HtmlBody ?? string.Empty,
                UserMessage = envelope,
                InlineImages = [.. content.Images.Select(i => new RenderedInlineImage { ContentId = i.ContentId, MediaType = i.MediaType, FileName = i.FileName, Data = i.Data })],
            },
            CreatedAt = now,
            NextAttemptAt = now,
        };

        using var registration = _notifier.Register(messageId);
        var submission = await _store.SubmitAsync(record, message, settings.MaxMessagesPerHour, settings.MaxMessagesPerDay, MaxStoredImageBytes(settings), cancellationToken)
            .ConfigureAwait(false);
        switch (submission.Outcome)
        {
            case UserMessageSubmitOutcome.Conflict:
                Log.Conflict(_logger, messageId, record.ByAccount);
                return ApiResults.Problem(StatusCodes.Status409Conflict, ApiProblem.IdempotencyConflict, "The message id was used before with other content.",
                    [Issue("id", UserMessageIssueCodes.IdempotencyConflict, "The message id was used before with other content.")]);
            case UserMessageSubmitOutcome.HourlyLimit or UserMessageSubmitOutcome.DailyLimit:
                var hourly = submission.Outcome == UserMessageSubmitOutcome.HourlyLimit;
                Log.Limited(_logger, messageId, record.ByAccount, hourly ? "hour" : "day");
                return new LimitedResult(
                    submission.RetryAfter ?? TimeSpan.FromMinutes(1),
                    ApiResults.Problem(StatusCodes.Status429TooManyRequests, ApiProblem.RateLimited, "The limit of messages to users is reached.",
                        [Issue(hourly ? "userMessages.maxMessagesPerHour" : "userMessages.maxMessagesPerDay",
                            hourly ? UserMessageIssueCodes.HourlyLimit : UserMessageIssueCodes.DailyLimit, "The limit of messages to users is reached.")]));
            case UserMessageSubmitOutcome.ImageStorageFull:
                Log.ImageStorageFull(_logger, messageId, record.ByAccount);
                return new LimitedResult(
                    submission.RetryAfter ?? TimeSpan.FromHours(1),
                    ApiResults.Problem(StatusCodes.Status429TooManyRequests, ApiProblem.RateLimited, "The images waiting in the outbox reached the service's cap.",
                        [Issue("userMessages.maxStoredImageMb", UserMessageIssueCodes.ImageStorageFull, "The images waiting in the outbox reached the service's cap.")]));
            case UserMessageSubmitOutcome.Created:
                Log.Submitted(_logger, messageId, record.ByAccount, record.RecipientCount, content.Images.Count, submission.OutboxId ?? 0);
                _signals.Raise(WatchdogSignal.OutboxEnqueued);
                break;
            default:
                Log.Repeated(_logger, messageId, record.ByAccount);
                if (await StateAsync(request.Id, submission.OutboxId, cancellationToken).ConfigureAwait(false) is { State: not UserMessageState.Queued } final)
                {
                    return Answer(final with { Duplicate = true });
                }

                break;
        }

        var attempt = await registration.WaitAsync(Wait, _timeProvider, cancellationToken).ConfigureAwait(false);
        var result = attempt is not null
            ? FromAttempt(request.Id, attempt) with { OutboxId = submission.OutboxId }
            : await StateAsync(request.Id, submission.OutboxId, cancellationToken).ConfigureAwait(false);
        return Answer(result with { Duplicate = submission.Outcome == UserMessageSubmitOutcome.Duplicate });
    }

    /// <summary>SHA-256 (hex, lower case) of the sorted lower-case To and CC addresses, one per line.</summary>
    public static string RecipientsHash(UserMessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var addresses = envelope.To.Concat(envelope.Cc).Select(m => m.Address.Trim().ToLowerInvariant()).Order(StringComparer.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', addresses))));
    }

    /// <summary>The state a waiting request answers from an attempt of the outbox job.</summary>
    public static UserMessageResult FromAttempt(Guid id, UserMessageAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        return attempt.Outcome switch
        {
            DeliveryOutcome.Delivered => new UserMessageResult
            {
                Id = id, State = UserMessageState.Delivered, Code = "ok", SmtpStatus = attempt.SmtpStatus ?? 250, RejectedCopies = attempt.RejectedCopies,
            },
            DeliveryOutcome.Permanent => new UserMessageResult { Id = id, State = UserMessageState.Rejected, Code = attempt.Code, SmtpStatus = attempt.SmtpStatus, Hint = attempt.Hint },
            DeliveryOutcome.ChannelBroken or DeliveryOutcome.AuthenticationFailed =>
                new UserMessageResult { Id = id, State = UserMessageState.Failed, Code = attempt.Code, SmtpStatus = attempt.SmtpStatus, Hint = attempt.Hint },
            _ => new UserMessageResult { Id = id, State = UserMessageState.Queued, Code = attempt.Code, SmtpStatus = attempt.SmtpStatus, Hint = attempt.Hint },
        };
    }

    /// <summary>The state of the outbox row (no attempt reported in time, or a repeated id): Sent → Delivered, Dead →
    /// Rejected (5xx code) or Failed, anything else (or a purged row) → Queued.</summary>
    private async Task<UserMessageResult> StateAsync(Guid id, long? outboxId, CancellationToken cancellationToken)
    {
        OutboxItemDto? item = null;
        if (outboxId is { } outbox)
        {
            var page = await _outbox.QueryAsync(new OutboxQuery { Before = outbox + 1, Limit = 1 }, cancellationToken).ConfigureAwait(false);
            item = page.Items.FirstOrDefault(i => i.Id == outbox);
        }

        var code = item?.LastErrorCode;
        return item?.Status switch
        {
            OutboxStatus.Sent => new UserMessageResult { Id = id, State = UserMessageState.Delivered, Code = "ok", OutboxId = outboxId },
            OutboxStatus.Dead or OutboxStatus.Cancelled => new UserMessageResult
            {
                Id = id,
                State = SmtpStatusOf(code) is >= 500 ? UserMessageState.Rejected : UserMessageState.Failed,
                Code = code ?? "dead",
                SmtpStatus = SmtpStatusOf(code),
                OutboxId = outboxId,
            },
            _ => new UserMessageResult { Id = id, State = UserMessageState.Queued, Code = code ?? "queued", SmtpStatus = SmtpStatusOf(code), OutboxId = outboxId },
        };
    }

    /// <summary>"smtp.550.5.1.1" / "partial.smtp.451" → 550 / 451.</summary>
    public static int? SmtpStatusOf(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return null;
        }

        var parts = code.Split('.');
        var index = Array.IndexOf(parts, "smtp");
        return index >= 0 && index + 1 < parts.Length && int.TryParse(parts[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var status)
               && status is >= 200 and < 600
            ? status
            : null;
    }

    private static string ContentHash(UserMessageRequest request) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, WatchdogJson.Options)));

    private static bool IsSmtpConfigured(SmtpSettings smtp) =>
        smtp.Enabled && !string.IsNullOrWhiteSpace(smtp.Host) && UserMessageRequestValidator.AddressOf(smtp.From) is not null;

    private static IResult Answer(UserMessageResult result) =>
        ApiResults.Json(result, result.State == UserMessageState.Queued ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);

    private static IResult Unavailable(string code, string title) =>
        ApiResults.Problem(StatusCodes.Status409Conflict, ApiProblem.UserMessagesUnavailable, title, [Issue("userMessages.enabled", code, title)]);

    private static ConfigIssue Issue(string path, string code, string message) =>
        new() { Path = path, Code = code, Severity = IssueSeverity.Error, Message = message };

    /// <summary>A problem response with <c>Retry-After</c> (whole seconds, at least 1).</summary>
    private sealed class LimitedResult(TimeSpan retryAfter, IResult problem) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            httpContext.Response.Headers.RetryAfter = Math.Max(1, (long)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return problem.ExecuteAsync(httpContext);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 2610, Level = LogLevel.Information,
            Message = "Message to a user {MessageId} submitted by {Account}: {RecipientCount} recipient(s), {ImageCount} inline image(s), outbox {OutboxId}.")]
        public static partial void Submitted(ILogger logger, string messageId, string account, int recipientCount, int imageCount, long outboxId);

        [LoggerMessage(EventId = 2611, Level = LogLevel.Information, Message = "Message to a user {MessageId} submitted again by {Account}.")]
        public static partial void Repeated(ILogger logger, string messageId, string account);

        [LoggerMessage(EventId = 2612, Level = LogLevel.Warning, Message = "Message to a user {MessageId} from {Account} refused: the id was used with other content.")]
        public static partial void Conflict(ILogger logger, string messageId, string account);

        [LoggerMessage(EventId = 2613, Level = LogLevel.Warning, Message = "Message to a user {MessageId} from {Account} refused: the {Window} limit is reached.")]
        public static partial void Limited(ILogger logger, string messageId, string account, string window);

        [LoggerMessage(EventId = 2614, Level = LogLevel.Warning,
            Message = "Message to a user {MessageId} from {Account} refused: the images waiting in the outbox reached userMessages.maxStoredImageMb.")]
        public static partial void ImageStorageFull(ILogger logger, string messageId, string account);
    }
}
