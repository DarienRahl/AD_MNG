using System.Collections.Concurrent;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Service.UserMessages;

/// <summary>The classified outcome of one SMTP attempt of a message to a user (no reply text; the rejected CC addresses
/// stay in memory only until the waiting request has answered).</summary>
public sealed record UserMessageAttempt(DeliveryOutcome Outcome, string Code, int? SmtpStatus = null, string? Hint = null)
{
    public IReadOnlyList<string> RejectedCopies { get; init; } = [];

    public override string ToString() => $"UserMessageAttempt({Outcome}, {Code})";
}

/// <summary>
/// Hands the outcome of a message's SMTP attempt from the outbox job (<c>SmtpEmailTransport</c>) to the
/// <c>POST /api/v1/user-messages</c> request waiting for it (README U1b). In memory only. Rate-limit deferrals are not
/// attempts and are not reported.
/// <para>The transport reports an attempt before the outbox job records its outcome in the outbox row, so a repeated
/// request arriving in between reads the row as still queued: the last attempt of each message (codes only, no rejected
/// addresses) is therefore kept for <see cref="RecentAttemptLifetime"/> and answers a registration made after it at
/// once.</para>
/// </summary>
public sealed class UserMessageDeliveryNotifier
{
    /// <summary>How long the last attempt of a message answers registrations made after it was reported.</summary>
    public static readonly TimeSpan RecentAttemptLifetime = TimeSpan.FromMinutes(5);

    private const int MaxRecentAttempts = 1024;

    private readonly ConcurrentDictionary<string, Waiter> _waiters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RecentAttempt> _recent = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public UserMessageDeliveryNotifier(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <summary>Starts waiting for <paramref name="messageId"/> (the outbox MessageId) – before the message is enqueued, so
    /// an attempt that completes at once is not missed; an attempt reported within <see cref="RecentAttemptLifetime"/>
    /// before completes the handle at once. Dispose the handle when done.</summary>
    public Registration Register(string messageId)
    {
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        var registration = new Registration(this, messageId);
        _waiters.AddOrUpdate(
            messageId,
            _ => new Waiter([registration]),
            (_, existing) =>
            {
                lock (existing.Registrations)
                {
                    existing.Registrations.Add(registration);
                }

                return existing;
            });

        // After joining the waiters: an attempt reported before is in _recent (Report writes it before taking the waiters).
        if (_recent.TryGetValue(messageId, out var recent) && _timeProvider.GetUtcNow() - recent.ReportedAt < RecentAttemptLifetime)
        {
            registration.Completion.TrySetResult(recent.Attempt);
        }

        return registration;
    }

    /// <summary>Reports an attempt to everybody waiting for <paramref name="messageId"/>.</summary>
    public void Report(string messageId, UserMessageAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempt.Outcome == DeliveryOutcome.Deferred)
        {
            return;
        }

        Remember(messageId, attempt);
        if (!_waiters.TryRemove(messageId, out var waiter))
        {
            return;
        }

        lock (waiter.Registrations)
        {
            foreach (var registration in waiter.Registrations)
            {
                registration.Completion.TrySetResult(attempt);
            }
        }
    }

    private void Remember(string messageId, UserMessageAttempt attempt)
    {
        var now = _timeProvider.GetUtcNow();
        _recent[messageId] = new RecentAttempt(attempt with { RejectedCopies = [] }, now);
        foreach (var (id, recent) in _recent)
        {
            if (now - recent.ReportedAt >= RecentAttemptLifetime)
            {
                _recent.TryRemove(new KeyValuePair<string, RecentAttempt>(id, recent));
            }
        }

        if (_recent.Count > MaxRecentAttempts)
        {
            foreach (var oldest in _recent.OrderBy(r => r.Value.ReportedAt).Take(_recent.Count - MaxRecentAttempts).ToList())
            {
                _recent.TryRemove(oldest);
            }
        }
    }

    private void Remove(Registration registration)
    {
        if (!_waiters.TryGetValue(registration.MessageId, out var waiter))
        {
            return;
        }

        lock (waiter.Registrations)
        {
            waiter.Registrations.Remove(registration);
            if (waiter.Registrations.Count == 0)
            {
                _waiters.TryRemove(new KeyValuePair<string, Waiter>(registration.MessageId, waiter));
            }
        }
    }

    /// <summary>One waiting request.</summary>
    public sealed class Registration : IDisposable
    {
        private readonly UserMessageDeliveryNotifier _owner;

        internal Registration(UserMessageDeliveryNotifier owner, string messageId)
        {
            _owner = owner;
            MessageId = messageId;
        }

        public string MessageId { get; }

        internal TaskCompletionSource<UserMessageAttempt> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The attempt, or null when <paramref name="timeout"/> passes first.</summary>
        public async Task<UserMessageAttempt?> WaitAsync(TimeSpan timeout, TimeProvider timeProvider, CancellationToken cancellationToken)
        {
            try
            {
                return await Completion.Task.WaitAsync(timeout, timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return null;
            }
        }

        public void Dispose() => _owner.Remove(this);
    }

    private sealed record Waiter(List<Registration> Registrations);

    private sealed record RecentAttempt(UserMessageAttempt Attempt, DateTimeOffset ReportedAt);
}
