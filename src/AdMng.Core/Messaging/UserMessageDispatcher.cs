namespace AdMng.Core.Messaging;

/// <summary>
/// Rate limit of messages (in memory, shared by the runs of one application session): messages are spaced by
/// <c>60 s / messagesPerMinute</c> and the recipients of the last 24 hours are counted against a daily budget (defaults:
/// the Exchange Online limits, 30 messages per minute and 10 000 recipients per day per mailbox). Thread-safe.
/// </summary>
public sealed class MessageRateLimiter
{
    public const int DefaultMessagesPerMinute = 30;
    public const int DefaultRecipientsPerDay = 10_000;

    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Queue<(DateTimeOffset At, int Recipients)> _sent = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _next = DateTimeOffset.MinValue;
    private int _sentToday;

    /// <param name="time">Clock.</param>
    /// <param name="delay">Waits (default: <c>Task.Delay</c> on <paramref name="time"/>); tests advance a manual clock.</param>
    public MessageRateLimiter(TimeProvider time, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _delay = delay ?? ((d, ct) => Task.Delay(d, time, ct));
    }

    /// <summary>Messages per minute (1–600).</summary>
    public int MessagesPerMinute { get; set; } = DefaultMessagesPerMinute;

    /// <summary>Recipients per rolling 24 hours (1–1 000 000).</summary>
    public int RecipientsPerDay { get; set; } = DefaultRecipientsPerDay;

    /// <summary>Recipients counted in the last 24 hours.</summary>
    public int RecipientsInLastDay
    {
        get
        {
            Prune(_time.GetUtcNow());
            return _sentToday;
        }
    }

    /// <summary>
    /// Waits for the next slot and books <paramref name="recipients"/>. Returns false (without waiting) when the daily budget
    /// would be exceeded.
    /// </summary>
    public async Task<bool> WaitAsync(int recipients, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            Prune(now);
            if (_sentToday + recipients > Math.Clamp(RecipientsPerDay, 1, 1_000_000))
            {
                return false;
            }

            if (_next > now)
            {
                await _delay(_next - now, cancellationToken).ConfigureAwait(false);
                now = _time.GetUtcNow();
            }

            _next = now + TimeSpan.FromMinutes(1) / Math.Clamp(MessagesPerMinute, 1, 600);
            _sent.Enqueue((now, recipients));
            _sentToday += recipients;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Prune(DateTimeOffset now)
    {
        lock (_sent)
        {
            while (_sent.Count > 0 && now - _sent.Peek().At >= Day)
            {
                _sentToday -= _sent.Dequeue().Recipients;
            }
        }
    }
}

/// <summary>Final status of one message of a run.</summary>
public enum UserMessageSendStatus
{
    /// <summary>Accepted by the server.</summary>
    Accepted,

    /// <summary>Rejected (permanently, or still failing after the retries).</summary>
    Failed,

    /// <summary>Not attempted: cancelled, stopped by a transport failure or the daily limit.</summary>
    NotSent,
}

/// <summary>Why a message was not sent.</summary>
public enum UserMessageNotSentReason
{
    None,
    Cancelled,

    /// <summary>The transport failed for the whole run (authentication, TLS, configuration).</summary>
    TransportStopped,

    DailyLimit,
}

/// <summary>The result of one message of a run.</summary>
public sealed record UserMessageSendResult(
    PlannedUserMessage Message,
    UserMessageSendStatus Status,
    UserMessageDelivery? Delivery,
    int Attempts,
    DateTimeOffset? CompletedAt,
    UserMessageNotSentReason NotSentReason = UserMessageNotSentReason.None)
{
    public override string ToString() => $"{nameof(UserMessageSendResult)} #{Message.Index} {Status}";
}

/// <summary>Progress of a run (reported after every message and while waiting for a retry).</summary>
public sealed record UserMessageProgress(int Completed, int Total, int Accepted, int Failed, bool IsWaitingForRetry);

/// <summary>Retry policy of a run.</summary>
public sealed record UserMessageDispatchOptions
{
    /// <summary>Attempts per message for transient failures (1 = no retry).</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Wait before the 2nd, 3rd … attempt (the last value repeats).</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } = [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];
}

/// <summary>
/// Sends planned messages through a transport (README U1, pipeline step 4): one at a time over one connection, rate
/// limited, transient failures retried on a new connection, authentication / TLS failures stop the run, cancellation
/// leaves the rest "not sent". Never throws for a failed message; the result has one entry per message, in order.
/// </summary>
public sealed class UserMessageDispatcher
{
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public UserMessageDispatcher(TimeProvider time, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _delay = delay ?? ((d, ct) => Task.Delay(d, time, ct));
    }

    public async Task<IReadOnlyList<UserMessageSendResult>> SendAsync(
        IUserMessageTransport transport,
        IReadOnlyList<PlannedUserMessage> messages,
        MessageRateLimiter limiter,
        UserMessageDispatchOptions? options,
        IProgress<UserMessageProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(limiter);
        options ??= new UserMessageDispatchOptions();
        var results = new List<UserMessageSendResult>(messages.Count);
        var accepted = 0;
        var failed = 0;
        IUserMessageConnection? connection = null;
        var stop = UserMessageNotSentReason.None;
        try
        {
            foreach (var planned in messages)
            {
                if (stop == UserMessageNotSentReason.None && cancellationToken.IsCancellationRequested)
                {
                    stop = UserMessageNotSentReason.Cancelled;
                }

                if (stop != UserMessageNotSentReason.None || planned.Message is not { } message)
                {
                    results.Add(new UserMessageSendResult(planned, UserMessageSendStatus.NotSent, null, 0, null,
                        stop == UserMessageNotSentReason.None ? UserMessageNotSentReason.Cancelled : stop));
                    continue;
                }

                var attempts = 0;
                UserMessageDelivery? delivery = null;
                while (true)
                {
                    bool booked;
                    try
                    {
                        booked = await limiter.WaitAsync(message.RecipientCount, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        stop = UserMessageNotSentReason.Cancelled;
                        break;
                    }

                    if (!booked)
                    {
                        stop = UserMessageNotSentReason.DailyLimit;
                        break;
                    }

                    attempts++;
                    try
                    {
                        connection ??= await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
                        delivery = await connection.SendAsync(message, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        stop = UserMessageNotSentReason.Cancelled;
                        delivery = null;
                        break;
                    }
                    catch (UserMessageTransportException ex)
                    {
                        delivery = ex.Delivery;
                    }
                    catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
                    {
                        delivery = new UserMessageDelivery(UserMessageDeliveryOutcome.Transient, ex is TimeoutException ? "timeout" : "io");
                    }

                    if (delivery.Outcome != UserMessageDeliveryOutcome.Accepted)
                    {
                        // A connection that failed is not reused; the next attempt / message connects again.
                        connection = await CloseAsync(connection).ConfigureAwait(false);
                    }

                    if (delivery.Outcome != UserMessageDeliveryOutcome.Transient || attempts >= Math.Max(1, options.MaxAttempts))
                    {
                        break;
                    }

                    progress?.Report(new UserMessageProgress(results.Count, messages.Count, accepted, failed, IsWaitingForRetry: true));
                    try
                    {
                        await _delay(RetryDelay(options, attempts), cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;  // the last failure is the result of this message; the rest is cancelled
                    }
                }

                if (delivery is null)
                {
                    results.Add(new UserMessageSendResult(planned, UserMessageSendStatus.NotSent, null, attempts, null, stop));
                }
                else
                {
                    var status = delivery.IsAccepted ? UserMessageSendStatus.Accepted : UserMessageSendStatus.Failed;
                    accepted += delivery.IsAccepted ? 1 : 0;
                    failed += delivery.IsAccepted ? 0 : 1;
                    results.Add(new UserMessageSendResult(planned, status, delivery, attempts, _time.GetUtcNow()));
                    if (delivery.StopsRun)
                    {
                        stop = UserMessageNotSentReason.TransportStopped;
                    }
                }

                progress?.Report(new UserMessageProgress(results.Count, messages.Count, accepted, failed, IsWaitingForRetry: false));
            }
        }
        finally
        {
            await CloseAsync(connection).ConfigureAwait(false);
        }

        return results;
    }

    private static TimeSpan RetryDelay(UserMessageDispatchOptions options, int attempt) =>
        options.RetryDelays is { Count: > 0 } delays ? delays[Math.Min(attempt - 1, delays.Count - 1)] : TimeSpan.Zero;

    private static async Task<IUserMessageConnection?> CloseAsync(IUserMessageConnection? connection)
    {
        if (connection is not null)
        {
            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                // A broken connection cannot be closed politely.
            }
        }

        return null;
    }
}
