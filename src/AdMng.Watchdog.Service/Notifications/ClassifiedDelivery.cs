using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Service.Notifications;

/// <summary>A delivery outcome plus the operator hints (<c>DeliveryHints</c> codes) that explain it; the transports return
/// <see cref="Result"/>, the delivery tester shows the hints.</summary>
public sealed record ClassifiedDelivery(DeliveryResult Result, IReadOnlyList<string> Hints)
{
    public static ClassifiedDelivery Delivered(int? statusCode = null) =>
        new(statusCode is null ? DeliveryResult.Delivered : DeliveryResult.Delivered with { StatusCode = statusCode }, []);

    /// <summary>A failure with an optional hint (also written to <see cref="DeliveryResult.Detail"/>, which never carries
    /// message content or directory values).</summary>
    public static ClassifiedDelivery Failure(DeliveryOutcome outcome, string errorCode, string? hint = null, int? statusCode = null, TimeSpan? retryAfter = null) =>
        new(
            new DeliveryResult { Outcome = outcome, ErrorCode = errorCode, Detail = hint, StatusCode = statusCode, RetryAfter = retryAfter },
            hint is null ? [] : [hint]);
}
