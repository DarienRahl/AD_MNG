using System.Net.Http.Headers;
using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Watchdog.Service.Notifications;

/// <summary>One POST of a Teams/webhook delivery (the URL is a secret – <see cref="ToString"/> shows scheme, host and path only).</summary>
public sealed record HttpChannelRequest
{
    public required DeliveryChannelType ChannelType { get; init; }

    public required string ChannelId { get; init; }

    public required Uri Url { get; init; }

    /// <summary>The exact bytes to send (a webhook signature covers them).</summary>
    public required byte[] Body { get; init; }

    public string ContentType { get; init; } = "application/json; charset=utf-8";

    /// <summary>Per-attempt timeout on the service's <see cref="TimeProvider"/>.</summary>
    public required TimeSpan Timeout { get; init; }

    /// <summary>Adds the per-attempt headers (signatures, Authorization).</summary>
    public Action<HttpRequestHeaders>? Headers { get; init; }

    public override string ToString() => $"HttpChannelRequest({ChannelType}, {ChannelId}, {DeliveryHttpClient.ForLog(Url)})";
}
