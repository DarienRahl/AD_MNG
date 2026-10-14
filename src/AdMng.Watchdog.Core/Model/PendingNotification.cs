using System.Security.Cryptography;
using System.Text;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Model;

/// <summary>Where a notification goes: one channel and, for e-mail, one recipient set with its detail level.</summary>
public sealed record NotificationTarget
{
    public required DeliveryChannelType ChannelType { get; init; }
    public required string ChannelId { get; init; }                // "email" for SMTP
    public IReadOnlyList<string> Recipients { get; init; } = [];   // e-mail addresses (sorted, distinct)
    public DetailLevel DetailLevel { get; init; }

    /// <summary>channelId + "|" + hash(recipients): 32 lower-case hex chars of the SHA-256 of the recipients
    /// (lower-cased, distinct, ordinally sorted, joined with LF) – stable, independent of the input order, and free of
    /// addresses.</summary>
    public string Key
    {
        get
        {
            var recipients = Recipients
                .Select(r => r.Trim().ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', recipients)));
            return ChannelId + "|" + Convert.ToHexStringLower(hash.AsSpan(0, 16));
        }
    }
}

/// <summary>An event × rule × target waiting to be rendered (holds, quiet hours, aggregation).</summary>
public sealed record PendingNotification
{
    public long Id { get; init; }
    public required long EventId { get; init; }
    public required string RuleId { get; init; }
    public required NotificationTarget Target { get; init; }
    public required Severity Severity { get; init; }               // rule override or event severity
    public required string BucketKey { get; init; }                // AggregationPolicy
    public required DateTimeOffset DueAt { get; init; }            // earliest flush time of its bucket
    public DateTimeOffset? HoldForAttributionUntil { get; init; }
    public bool IsFollowUp { get; init; }
    public string? ReplyToMessageId { get; init; }                 // follow-ups: original Message-ID
    public DateTimeOffset CreatedAt { get; init; }
}
