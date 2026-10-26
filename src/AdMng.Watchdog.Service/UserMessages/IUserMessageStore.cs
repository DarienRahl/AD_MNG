using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Service.UserMessages;

/// <summary>The audit row of one message to a user (README U1b) – who, when, how many recipients and hashes; never an
/// address, a subject or a body.</summary>
public sealed record UserMessageAuditRecord
{
    /// <summary>The message id (idempotency key, Message-ID local part).</summary>
    public required Guid MessageId { get; init; }
    public required DateTimeOffset SubmittedAt { get; init; }
    public string? BySid { get; init; }
    public required string ByAccount { get; init; }
    public required int RecipientCount { get; init; }
    /// <summary>SHA-256 (hex) of the sorted lower-case To and CC addresses – lets an auditor check "did this address get a
    /// message" without the service keeping the address.</summary>
    public required string RecipientsHash { get; init; }
    /// <summary>SHA-256 (hex) of the request: the same id with other content is an idempotency conflict.</summary>
    public required string ContentHash { get; init; }
    public long? OutboxId { get; init; }
    public override string ToString() => $"UserMessageAuditRecord({MessageId:N}, {RecipientCount} recipients)";
}

/// <summary>What <see cref="IUserMessageStore.SubmitAsync"/> did.</summary>
public enum UserMessageSubmitOutcome
{
    /// <summary>The audit row and the outbox message were stored.</summary>
    Created,

    /// <summary>The id was submitted before by the same caller with the same content – nothing stored.</summary>
    Duplicate,

    /// <summary>The id was submitted before by another caller or with other content – nothing stored.</summary>
    Conflict,

    /// <summary><c>userMessages.maxMessagesPerHour</c> reached – nothing stored.</summary>
    HourlyLimit,

    /// <summary><c>userMessages.maxMessagesPerDay</c> reached – nothing stored.</summary>
    DailyLimit,
}

/// <summary>Result of <see cref="IUserMessageStore.SubmitAsync"/>: the outbox row (new or of the earlier submission) or
/// the time until the limit frees a slot.</summary>
public sealed record UserMessageSubmission(UserMessageSubmitOutcome Outcome, long? OutboxId = null, TimeSpan? RetryAfter = null);

/// <summary>Audit, idempotency and limits of messages to users, atomically with the outbox (one write transaction).</summary>
public interface IUserMessageStore
{
    /// <summary>In one transaction: an earlier row with the id → Duplicate/Conflict; the sliding-window limits (submitted
    /// in the last hour / day, all callers) → HourlyLimit/DailyLimit; otherwise the outbox message and the audit row are
    /// inserted (Created).</summary>
    Task<UserMessageSubmission> SubmitAsync(UserMessageAuditRecord record, OutboxMessage message, int maxPerHour, int maxPerDay, CancellationToken cancellationToken);

    /// <summary>Messages submitted in the last hour and the last 24 hours before <paramref name="now"/>.</summary>
    Task<(int LastHour, int LastDay)> CountAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
