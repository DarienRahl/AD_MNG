using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Diagnostics;
using AdMng.Watchdog.Core.Model;
using Microsoft.Extensions.Logging;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>
/// <see cref="IWatchdogJob"/> "outbox" (section 15.1): leases due messages (20, lease 5 min – a crash lets the lease
/// expire and the message comes back), sends them through the transport of their channel type – sequentially per
/// channel, up to 4 channels in parallel – and records the result: Delivered → Sent (payload dropped); Deferred (rate
/// limit) → retry without counting an attempt; Transient → <see cref="RetryPolicy"/> or Dead when exhausted; Permanent →
/// Dead (message-specific); ChannelBroken / AuthenticationFailed → retry in 1 h until the message is 24 h old, then Dead
/// (these retries do not count as attempts, so a broken period does not use up the transient retry budget).
/// Channel health goes to <see cref="ChannelHealthTracker"/>; a channel entering Error stores one
/// <c>HealthDeliveryFailing</c> event. Messages of a channel that was disabled or removed (or e-mail with SMTP disabled)
/// are given up (Dead, <see cref="WithdrawnCodes"/>) without touching the channel health. A heartbeat
/// (<see cref="DeliveryKind.Health"/>) is never sent or retried once the next heartbeat of its channel is due – it is
/// given up as <see cref="HeartbeatSupersededCode"/>. Transport exceptions count as transient and are logged via
/// <see cref="SafeExceptionLog"/>.
/// </summary>
public sealed partial class OutboxSenderJob : IWatchdogJob
{
    /// <summary>Messages leased per iteration.</summary>
    public const int BatchSize = 20;

    /// <summary>Channels sent to in parallel.</summary>
    public const int MaxParallelChannels = 4;

    /// <summary>Lease of a message being sent.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    /// <summary>Retry delay of a broken channel or failed authentication.</summary>
    public static readonly TimeSpan BrokenChannelRetry = TimeSpan.FromHours(1);

    /// <summary>Age after which a message of a broken channel is given up.</summary>
    public static readonly TimeSpan BrokenChannelGiveUp = TimeSpan.FromHours(24);

    /// <summary>Delay of a rate-limited message without Retry-After.</summary>
    public static readonly TimeSpan DefaultDeferral = TimeSpan.FromSeconds(5);

    /// <summary>Error code of a heartbeat given up because the next heartbeat of its channel is due.</summary>
    public const string HeartbeatSupersededCode = "heartbeat.superseded";

    private const string ChannelDisabledCode = "channel.disabled";
    private const string ChannelUnknownCode = "channel.unknown";
    private const string SmtpDisabledCode = "smtp.disabled";

    /// <summary>Codes of a channel the configuration no longer delivers to (disabled or removed channel, SMTP disabled –
    /// the transports answer with the same codes): its messages are given up without a channel health failure.</summary>
    public static readonly IReadOnlySet<string> WithdrawnCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        ChannelDisabledCode, ChannelUnknownCode, SmtpDisabledCode,
    };

    private static readonly WatchdogSignal[] Signals = [WatchdogSignal.OutboxEnqueued];

    private readonly IOutboxStore _outbox;
    private readonly Dictionary<DeliveryChannelType, INotificationTransport> _transports;
    private readonly RetryPolicy _retry;
    private readonly ChannelHealthTracker _health;
    private readonly DeliveryFailureReporter _failures;
    private readonly IWatchdogConfigProvider _configProvider;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OutboxSenderJob> _logger;

    public OutboxSenderJob(IOutboxStore outbox, IEnumerable<INotificationTransport> transports, RetryPolicy retry, ChannelHealthTracker health,
        DeliveryFailureReporter failures, IWatchdogConfigProvider configProvider, TimeProvider timeProvider, ILogger<OutboxSenderJob> logger)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(transports);
        ArgumentNullException.ThrowIfNull(retry);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(failures);
        ArgumentNullException.ThrowIfNull(configProvider);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _outbox = outbox;
        _transports = [];
        foreach (var transport in transports)
        {
            _transports[transport.ChannelType] = transport;     // the last registration wins (decorators, tests)
        }

        _retry = retry;
        _health = health;
        _failures = failures;
        _configProvider = configProvider;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    public string Name => "outbox";

    /// <inheritdoc/>
    public TimeSpan DefaultInterval { get; } = TimeSpan.FromSeconds(5);

    /// <inheritdoc/>
    public IReadOnlyList<WatchdogSignal> WakeSignals => Signals;

    /// <inheritdoc/>
    public async Task<JobResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var now = _timeProvider.GetUtcNow();
            _health.Refresh(now);       // an old Error decays even when nothing is sent
            var messages = await _outbox.LeaseDueAsync(now, BatchSize, Lease, cancellationToken).ConfigureAwait(false);
            if (messages.Count == 0)
            {
                return new JobResult(null);
            }

            var channels = messages.GroupBy(m => (m.ChannelType, Channel: m.ChannelId.ToLowerInvariant())).Select(g => g.OrderBy(m => m.Id).ToList()).ToList();
            var options = new ParallelOptions { MaxDegreeOfParallelism = MaxParallelChannels, CancellationToken = cancellationToken };
            await Parallel.ForEachAsync(channels, options, async (channel, ct) =>
            {
                foreach (var message in channel)
                {
                    await SendAsync(message, ct).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

            var pending = await _outbox.CountPendingByChannelAsync(cancellationToken).ConfigureAwait(false);
            _health.UpdatePending(pending, ChannelTypeOf);
            return new JobResult(messages.Count >= BatchSize ? TimeSpan.Zero : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SafeExceptionLog.LogError(_logger, ex, "outbox delivery");
            return new JobResult(DefaultInterval, Failed: true, FailureCode: SafeExceptionLog.ErrorCode(ex));
        }
    }

    private async Task SendAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var configuration = _configProvider.Current.Configuration;
        if (WithdrawnCode(message, configuration) is { } withdrawn)
        {
            await GiveUpAsync(message, withdrawn, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (IsSuperseded(message, configuration, _timeProvider.GetUtcNow()))
        {
            await GiveUpAsync(message, HeartbeatSupersededCode, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var result = await TrySendAsync(message, cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var errorCode = result.ErrorCode ?? result.Outcome.ToString();
        if (result.Outcome == DeliveryOutcome.ChannelBroken && WithdrawnCodes.Contains(errorCode))
        {
            await GiveUpAsync(message, errorCode, result.Detail, cancellationToken).ConfigureAwait(false);     // disabled while sending
            return;
        }

        switch (result.Outcome)
        {
            case DeliveryOutcome.Delivered:
                await _outbox.MarkSentAsync(message.Id, now, cancellationToken).ConfigureAwait(false);
                _health.Success(message.ChannelId, message.ChannelType, now);
                break;

            case DeliveryOutcome.Deferred:
                var deferral = result.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero ? retryAfter : DefaultDeferral;
                await RetryAsync(message, configuration, now + deferral, errorCode, result.Detail, countAttempt: false, cancellationToken).ConfigureAwait(false);
                break;

            case DeliveryOutcome.Transient:
                if (_retry.Next(message.ChannelType, message.Attempts + 1, result.RetryAfter) is { } delay)
                {
                    await RetryAsync(message, configuration, now + delay, errorCode, result.Detail, countAttempt: true, cancellationToken).ConfigureAwait(false);
                    await FailureAsync(message, HealthState.Ok, errorCode, now, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await DeadAsync(message, errorCode, result.Detail, HealthState.Error, now, cancellationToken).ConfigureAwait(false);
                }

                break;

            case DeliveryOutcome.Permanent:
                await DeadAsync(message, errorCode, result.Detail, HealthState.Warning, now, cancellationToken).ConfigureAwait(false);
                break;

            default:    // ChannelBroken, AuthenticationFailed
                if (now - message.CreatedAt >= BrokenChannelGiveUp)
                {
                    await DeadAsync(message, errorCode, result.Detail, HealthState.Error, now, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // Not counted as an attempt: the transient schedule must still be complete when the channel is back.
                    await RetryAsync(message, configuration, now + BrokenChannelRetry, errorCode, result.Detail, countAttempt: false, cancellationToken)
                        .ConfigureAwait(false);
                    await FailureAsync(message, HealthState.Error, errorCode, now, cancellationToken).ConfigureAwait(false);
                }

                break;
        }
    }

    /// <summary>Schedules the next attempt – unless the message is a heartbeat whose successor is due by then.</summary>
    private Task RetryAsync(OutboxMessage message, WatchdogConfiguration configuration, DateTimeOffset next, string errorCode, string? detail,
        bool countAttempt, CancellationToken cancellationToken) =>
        IsSuperseded(message, configuration, next)
            ? GiveUpAsync(message, HeartbeatSupersededCode, errorCode, cancellationToken)
            : _outbox.MarkRetryAsync(message.Id, next, errorCode, detail, countAttempt, cancellationToken);

    /// <summary>Dead without a channel health failure (withdrawn channel, superseded heartbeat); the payload is kept for
    /// a manual retry until <c>OutboxDeadDays</c>.</summary>
    private async Task GiveUpAsync(OutboxMessage message, string code, string? detail, CancellationToken cancellationToken)
    {
        await _outbox.MarkDeadAsync(message.Id, code, detail, cancellationToken).ConfigureAwait(false);
        LogGivenUp(message.Id, message.ChannelId, code);
    }

    /// <summary>The withdrawn code when the configuration no longer delivers to the message's channel, else null.</summary>
    private static string? WithdrawnCode(OutboxMessage message, WatchdogConfiguration configuration)
    {
        if (message.ChannelType == DeliveryChannelType.Email)
        {
            return configuration.Smtp?.Enabled == true ? null : SmtpDisabledCode;
        }

        var channel = FindChannel(configuration, message.ChannelId);
        return channel is null || ChannelTypeOf(channel) != message.ChannelType ? ChannelUnknownCode : channel.Enabled ? null : ChannelDisabledCode;
    }

    /// <summary>True for a heartbeat whose channel's next heartbeat is due at <paramref name="at"/> (its interval – at least
    /// <see cref="HeartbeatJob.MinimumInterval"/> – after it was created): a SIEM needs the latest one, not a backlog.</summary>
    private static bool IsSuperseded(OutboxMessage message, WatchdogConfiguration configuration, DateTimeOffset at)
    {
        if (message.Kind != DeliveryKind.Health)
        {
            return false;
        }

        var interval = FindChannel(configuration, message.ChannelId)?.Webhook?.HeartbeatInterval ?? TimeSpan.Zero;
        return at >= message.CreatedAt + (interval > HeartbeatJob.MinimumInterval ? interval : HeartbeatJob.MinimumInterval);
    }

    private static ChannelSettings? FindChannel(WatchdogConfiguration configuration, string channelId) =>
        (configuration.Channels ?? []).FirstOrDefault(c => c is not null && string.Equals(c.Id, channelId, StringComparison.OrdinalIgnoreCase));

    private static DeliveryChannelType ChannelTypeOf(ChannelSettings channel) =>
        channel.Type == ChannelType.Teams ? DeliveryChannelType.Teams : DeliveryChannelType.Webhook;

    private async Task<DeliveryResult> TrySendAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        if (!_transports.TryGetValue(message.ChannelType, out var transport))
        {
            return new DeliveryResult { Outcome = DeliveryOutcome.ChannelBroken, ErrorCode = "transport.missing" };
        }

        if (message.Email is null && message.Http is null)
        {
            return new DeliveryResult { Outcome = DeliveryOutcome.Permanent, ErrorCode = "payload.missing" };
        }

        try
        {
            return await transport.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SafeExceptionLog.LogWarning(_logger, ex, "notification transport");
            return Transient(SafeExceptionLog.ErrorCode(ex));
        }
    }

    private async Task DeadAsync(OutboxMessage message, string errorCode, string? detail, HealthState level, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await _outbox.MarkDeadAsync(message.Id, errorCode, detail, cancellationToken).ConfigureAwait(false);
        LogDead(message.Id, message.ChannelId, errorCode);
        await FailureAsync(message, level, errorCode, now, cancellationToken).ConfigureAwait(false);
    }

    private async Task FailureAsync(OutboxMessage message, HealthState level, string errorCode, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_health.Failure(message.ChannelId, message.ChannelType, level, errorCode, now))
        {
            LogChannelFailing(message.ChannelId, errorCode);
            await _failures.ReportAsync(message.ChannelId, message.ChannelType, errorCode, now, cancellationToken).ConfigureAwait(false);
        }
    }

    private DeliveryChannelType? ChannelTypeOf(string channelId)
    {
        if (string.Equals(channelId, NotificationTargetResolver.EmailChannelId, StringComparison.OrdinalIgnoreCase))
        {
            return DeliveryChannelType.Email;
        }

        return FindChannel(_configProvider.Current.Configuration, channelId) is { } channel ? ChannelTypeOf(channel) : null;
    }

    private static DeliveryResult Transient(string errorCode) => new() { Outcome = DeliveryOutcome.Transient, ErrorCode = errorCode };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {Id} on channel {ChannelId} gave up ({ErrorCode}).")]
    private partial void LogDead(long id, string channelId, string errorCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox message {Id} on channel {ChannelId} is no longer sent ({Code}).")]
    private partial void LogGivenUp(long id, string channelId, string code);

    [LoggerMessage(EventId = WatchdogEventIds.DeliveryFailing, Level = LogLevel.Error, Message = "Channel {ChannelId} cannot deliver notifications ({ErrorCode}).")]
    private partial void LogChannelFailing(string channelId, string errorCode);
}
