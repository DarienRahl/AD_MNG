namespace AdMng.Watchdog.Contracts.Api;

public enum DeliveryChannelType { Email, Teams, Webhook }
public enum DeliveryKind { Alert, Digest, FollowUp, Test, Health }
public enum OutboxStatus { Pending, Sending, Sent, Retrying, Dead, Cancelled }

/// <summary>Filter and paging of <c>GET /api/v1/outbox</c> (delivery log).</summary>
public sealed record OutboxQuery
{
    public OutboxStatus? Status { get; init; }
    public string? ChannelId { get; init; }
    public long? Before { get; init; }
    public int Limit { get; init; } = 100;

    /// <summary>Keys <c>status</c> (enum name), <c>channelId</c>, <c>before</c>, <c>limit</c> (omitted when 100);
    /// only set values are written, every value is URI-escaped.</summary>
    public string ToQueryString()
    {
        var query = new QueryStringWriter();
        if (Status is { } status)
        {
            query.Add("status", status.ToString());
        }

        query.Add("channelId", ChannelId);
        query.Add("before", Before);
        if (Limit != 100)
        {
            query.Add("limit", Limit);
        }

        return query.ToString();
    }
}

/// <summary>One row of the delivery log.</summary>
public sealed record OutboxItemDto
{
    public required long Id { get; init; }
    public required string MessageId { get; init; }
    public required DeliveryChannelType ChannelType { get; init; }
    public required string ChannelId { get; init; }
    public string? ChannelName { get; init; }
    public required DeliveryKind Kind { get; init; }
    public required OutboxStatus Status { get; init; }
    public int Attempts { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? NextAttemptAt { get; init; }
    public DateTimeOffset? SentAt { get; init; }
    public string? LastErrorCode { get; init; }
    public string? LastError { get; init; }      // never contains message content or directory values
    public string? Subject { get; init; }        // e-mail subject / card title (directory data; shown only in the GUI)
    public int EventCount { get; init; }
    public int RecipientCount { get; init; }
}

public sealed record OutboxPage
{
    public IReadOnlyList<OutboxItemDto> Items { get; init; } = [];
    public bool HasMore { get; init; }
}
