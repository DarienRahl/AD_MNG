using System.Globalization;
using AdMng.Core.Csv;

namespace AdMng.Core.Messaging;

/// <summary>Status of one account in the report of a run (stable English tokens in the CSV).</summary>
public enum UserMessageReportStatus
{
    Accepted,
    Failed,
    NotSent,

    /// <summary>Excluded by the operator in the preview.</summary>
    Excluded,

    /// <summary>The condition was not met.</summary>
    Skipped,

    /// <summary>Validation error – no message could be sent.</summary>
    Invalid,
}

/// <summary>One account of the report: its planned message and, when attempted, the send result.</summary>
public sealed record UserMessageReportEntry(PlannedUserMessage Message, UserMessageSendResult? Result, bool Excluded = false)
{
    public UserMessageReportStatus Status => Result?.Status switch
    {
        UserMessageSendStatus.Accepted => UserMessageReportStatus.Accepted,
        UserMessageSendStatus.Failed => UserMessageReportStatus.Failed,
        UserMessageSendStatus.NotSent => UserMessageReportStatus.NotSent,
        _ => Excluded ? UserMessageReportStatus.Excluded
            : Message.Status == PlannedMessageStatus.Skipped ? UserMessageReportStatus.Skipped
            : Message.Status == PlannedMessageStatus.Error ? UserMessageReportStatus.Invalid
            : UserMessageReportStatus.NotSent,
    };

    /// <summary>Failed or not sent although it could have been sent – what "retry failed" sends again.</summary>
    public bool CanRetry => Status is UserMessageReportStatus.Failed or UserMessageReportStatus.NotSent && Message.CanSend;
}

/// <summary>
/// Writes the report of a run as CSV (only when the operator exports it): Status, Name, Login, DN, To, CC, Code, SMTP
/// status, Hint, Attempts, Time (UTC, ISO 8601). Never the subject or the body. Formula injection protection is on.
/// </summary>
public sealed class UserMessageReportExporter
{
    private static readonly string[] Columns = ["Status", "Name", "Login", "DN", "To", "CC", "Code", "SMTP status", "Hint", "Attempts", "Time (UTC)"];

    private readonly ICsvCodec _csv;

    public UserMessageReportExporter(ICsvCodec csv)
    {
        _csv = csv ?? throw new ArgumentNullException(nameof(csv));
    }

    public void Write(TextWriter writer, IEnumerable<UserMessageReportEntry> entries, char delimiter = ';')
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(entries);
        _csv.Write(writer, Columns, entries.Select(Row), delimiter, protectFormulas: true);
    }

    private static IReadOnlyList<string> Row(UserMessageReportEntry entry)
    {
        var message = entry.Message;
        var delivery = entry.Result?.Delivery;
        return
        [
            entry.Status.ToString(),
            message.Name,
            message.Login ?? string.Empty,
            message.DistinguishedName,
            string.Join("; ", message.Message?.To.Select(a => a.Address) ?? []),
            string.Join("; ", message.Message?.Cc.Select(a => a.Address) ?? []),
            delivery?.Code ?? (entry.Result?.NotSentReason is { } reason and not UserMessageNotSentReason.None ? reason.ToString() : string.Empty),
            delivery?.SmtpStatus?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            delivery?.Hint ?? string.Empty,
            entry.Result?.Attempts.ToString(CultureInfo.InvariantCulture) ?? "0",
            entry.Result?.CompletedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? string.Empty,
        ];
    }
}
