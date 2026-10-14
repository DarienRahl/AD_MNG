using System.Globalization;
using Microsoft.Identity.Client;

namespace AdMng.Watchdog.Service.Notifications;

/// <summary>An access token, or why there is none: the guard refused (no token request was made then) or the token
/// endpoint failed (<see cref="IsTransient"/> for network errors, throttling and 5xx – the delivery is retried; other
/// failures break the channel as an authentication failure).</summary>
public sealed record AccessTokenResult
{
    public string? Token { get; init; }

    public string? FailureCode { get; init; }

    public string? Hint { get; init; }

    /// <summary>The token endpoint was unreachable, throttled or failed with 5xx.</summary>
    public bool IsTransient { get; init; }

    public bool IsAvailable => Token is not null;

    public static AccessTokenResult Refused(string failureCode, string? hint = null) => new() { FailureCode = failureCode, Hint = hint };

    /// <summary>Classifies a failed token request (the exception text is never kept – it may contain tenant details).</summary>
    public static AccessTokenResult Failed(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            MsalServiceException service when service.StatusCode is 0 or 408 or 429 or >= 500 => new()
            {
                FailureCode = string.Create(CultureInfo.InvariantCulture, $"auth.token.{service.StatusCode}"),
                IsTransient = true,
            },
            MsalServiceException service => new() { FailureCode = string.Create(CultureInfo.InvariantCulture, $"auth.token.{service.StatusCode}") },
            HttpRequestException or TimeoutException => new() { FailureCode = "auth.token.network", IsTransient = true },
            _ => new() { FailureCode = "auth.token" },   // MsalClientException, missing certificate or private key …
        };
    }

    public override string ToString() => IsAvailable ? "AccessTokenResult(token)" : $"AccessTokenResult({FailureCode})";
}
