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
/// Dead (message-specific); ChannelBroken / AuthenticationFailed → retry in 1 h until the message is 24 h old, then Dead.
/// Channel health goes to <see cref="ChannelHealthTracker"/>; a channel entering Error stores one
/// <c>HealthDeliveryFailing</c> event. Transport exceptions count as transient and are logged via
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
            var messages = await _outbox.LeaseDueAsync(_timeProvider.GetUtcNow(), BatchSize, Lease, cancellationToken).ConfigureAwait(false);
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
        var result = await TrySendAsync(message, cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var errorCode = result.ErrorCode ?? result.Outcome.ToString();
        switch (result.Outcome)
        {
            case DeliveryOutcome.Delivered:
                await _outbox.MarkSentAsync(message.Id, now, cancellationToken).ConfigureAwait(false);
                _health.Success(message.ChannelId, message.ChannelType, now);
                break;

            case DeliveryOutcome.Deferred:
                var deferral = result.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero ? retryAfter : DefaultDeferral;
                await _outbox.MarkRetryAsync(message.Id, now + deferral, errorCode, result.Detail, countAttempt: false, cancellationToken).ConfigureAwait(false);
                break;

            case DeliveryOutcome.Transient:
                if (_retry.Next(message.ChannelType, message.Attempts + 1, result.RetryAfter) is { } delay)
                {
                    await _outbox.MarkRetryAsync(message.Id, now + delay, errorCode, result.Detail, countAttempt: true, cancellationToken).ConfigureAwait(false);
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
                    await _outbox.MarkRetryAsync(message.Id, now + BrokenChannelRetry, errorCode, result.Detail, countAttempt: true, cancellationToken)
                        .ConfigureAwait(false);
                    await FailureAsync(message, HealthState.Error, errorCode, now, cancellationToken).ConfigureAwait(false);
                }

                break;
        }
    }

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

        var channel = (_configProvider.Current.Configuration.Channels ?? [])
            .FirstOrDefault(c => c is not null && string.Equals(c.Id, channelId, StringComparison.OrdinalIgnoreCase));
        return channel is null ? null : channel.Type == ChannelType.Teams ? DeliveryChannelType.Teams : DeliveryChannelType.Webhook;
    }

    private static DeliveryResult Transient(string errorCode) => new() { Outcome = DeliveryOutcome.Transient, ErrorCode = errorCode };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {Id} on channel {ChannelId} gave up ({ErrorCode}).")]
    private partial void LogDead(long id, string channelId, string errorCode);

    [LoggerMessage(EventId = WatchdogEventIds.DeliveryFailing, Level = LogLevel.Error, Message = "Channel {ChannelId} cannot deliver notifications ({ErrorCode}).")]
    private partial void LogChannelFailing(string channelId, string errorCode);
}
