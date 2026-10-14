namespace AdMng.Watchdog.Core.Model;

/// <summary>Deferred = not attempted (rate limiter); the outbox does not count an attempt.</summary>
public enum DeliveryOutcome { Delivered, Deferred, Transient, Permanent, ChannelBroken, AuthenticationFailed }

/// <summary>Result of one transport send.</summary>
public sealed record DeliveryResult
{
    public required DeliveryOutcome Outcome { get; init; }
    public string? ErrorCode { get; init; }         // "smtp.451", "http.429", "socket", "timeout", "auth" …
    public string? Detail { get; init; }            // no message content, no directory values
    public TimeSpan? RetryAfter { get; init; }
    public int? StatusCode { get; init; }
    public static DeliveryResult Delivered { get; } = new() { Outcome = DeliveryOutcome.Delivered };
}
