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
/// <c>POST /api/v1/user-messages</c> request waiting for it (README U1b). In memory only; an attempt nobody waits for is
/// dropped. Rate-limit deferrals are not attempts and are not reported.
/// </summary>
public sealed class UserMessageDeliveryNotifier
{
    private readonly ConcurrentDictionary<string, Waiter> _waiters = new(StringComparer.Ordinal);

    /// <summary>Starts waiting for <paramref name="messageId"/> (the outbox MessageId) – before the message is enqueued, so
    /// an attempt that completes at once is not missed. Dispose the handle when done.</summary>
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
        return registration;
    }

    /// <summary>Reports an attempt to everybody waiting for <paramref name="messageId"/>.</summary>
    public void Report(string messageId, UserMessageAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempt.Outcome == DeliveryOutcome.Deferred || !_waiters.TryRemove(messageId, out var waiter))
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
}
