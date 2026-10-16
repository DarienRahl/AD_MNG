using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Service.Notifications;

/// <summary>
/// HTTP responses and failures of the Teams (Power Automate Workflows) and generic webhook channels →
/// <see cref="DeliveryResult"/> (spec 15.3). Redirects are never followed; <c>Retry-After</c> is honoured for transient
/// statuses. Error codes are "http.&lt;status&gt;", "timeout", "socket", "tls"; hints are <see cref="DeliveryHints"/> codes.
/// </summary>
public static class HttpDeliveryClassifier
{
    /// <summary>Error code of a flow that is switched off (Teams).</summary>
    public const string WorkflowNotEnabledMarker = "WorkflowTriggerIsNotEnabled";

    private static readonly string[] AuthMismatchMarkers = ["Shared Access scheme", "OAuth authorization scheme"];

    /// <summary>Classifies one response.</summary>
    /// <param name="channel">Teams or Webhook.</param>
    /// <param name="status">HTTP status code.</param>
    /// <param name="headers">Response headers (Retry-After, error markers).</param>
    /// <param name="bodyPrefix">The first characters of the response body (error markers only – never logged).</param>
    /// <param name="now">Time the response arrived (Retry-After dates).</param>
    public static ClassifiedDelivery Classify(DeliveryChannelType channel, int status, HttpResponseHeaders? headers, string? bodyPrefix, DateTimeOffset now)
    {
        var teams = channel == DeliveryChannelType.Teams;
        var code = string.Create(CultureInfo.InvariantCulture, $"http.{status}");
        var body = bodyPrefix ?? string.Empty;
        if (teams && (body.Contains(WorkflowNotEnabledMarker, StringComparison.OrdinalIgnoreCase) || HeadersContain(headers, WorkflowNotEnabledMarker)))
        {
            return ClassifiedDelivery.Failure(DeliveryOutcome.ChannelBroken, code, statusCode: status);
        }

        if (status is >= 200 and < 300)
        {
            // Power Automate answers 202: accepted by the flow, not proof that the card was posted.
            return teams && status == 202
                ? new ClassifiedDelivery(DeliveryResult.Delivered with { StatusCode = status }, [DeliveryHints.AcceptedNotDelivered])
                : ClassifiedDelivery.Delivered(status);
        }

        if (status is 408 or 429 || status >= 500)
        {
            return ClassifiedDelivery.Failure(DeliveryOutcome.Transient, code, statusCode: status, retryAfter: RetryAfter(headers, now));
        }

        return status switch
        {
            >= 300 and < 400 => ClassifiedDelivery.Failure(teams ? DeliveryOutcome.ChannelBroken : DeliveryOutcome.Permanent, code, statusCode: status),
            401 or 403 when teams => ClassifiedDelivery.Failure(
                DeliveryOutcome.ChannelBroken,
                code,
                AuthMismatchMarkers.Any(m => body.Contains(m, StringComparison.OrdinalIgnoreCase)) ? DeliveryHints.TeamsAuthMismatch : null,
                status),
            401 or 403 => ClassifiedDelivery.Failure(DeliveryOutcome.AuthenticationFailed, code, statusCode: status),
            404 => ClassifiedDelivery.Failure(DeliveryOutcome.ChannelBroken, code, statusCode: status),
            410 => ClassifiedDelivery.Failure(DeliveryOutcome.ChannelBroken, code, teams ? null : DeliveryHints.WebhookGone, status),
            413 => ClassifiedDelivery.Failure(DeliveryOutcome.Permanent, code, statusCode: status),
            _ => ClassifiedDelivery.Failure(teams ? DeliveryOutcome.ChannelBroken : DeliveryOutcome.Permanent, code, statusCode: status),
        };
    }

    /// <summary>Classifies a failed request: TLS validation failures break the channel, everything else on the wire is
    /// transient.</summary>
    /// <param name="exception">The failure.</param>
    /// <param name="timedOut">The per-attempt timeout elapsed (not the caller's cancellation).</param>
    /// <param name="certificateHint">The hint recorded by the certificate validation callback, if it rejected the server.</param>
    public static ClassifiedDelivery Classify(Exception exception, bool timedOut, string? certificateHint = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (timedOut)
        {
            return ClassifiedDelivery.Failure(DeliveryOutcome.Transient, "timeout");
        }

        if (certificateHint is not null || FindInner<AuthenticationException>(exception) is not null)
        {
            return ClassifiedDelivery.Failure(DeliveryOutcome.ChannelBroken, "tls", certificateHint ?? DeliveryHints.CertificateUntrusted);
        }

        if (FindInner<SocketException>(exception) is { } socket)
        {
            return ClassifiedDelivery.Failure(DeliveryOutcome.Transient, "socket." + socket.SocketErrorCode.ToString().ToLowerInvariant());
        }

        return exception is HttpRequestException http
            ? ClassifiedDelivery.Failure(DeliveryOutcome.Transient, "http." + http.HttpRequestError.ToString().ToLowerInvariant())
            : ClassifiedDelivery.Failure(DeliveryOutcome.Transient, "io");
    }

    /// <summary>The Retry-After delay (delta seconds or HTTP date), or null.</summary>
    public static TimeSpan? RetryAfter(HttpResponseHeaders? headers, DateTimeOffset now)
    {
        if (headers?.RetryAfter is not { } retryAfter)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        return retryAfter.Date is { } date ? (date > now ? date - now : TimeSpan.Zero) : null;
    }

    private static bool HeadersContain(HttpResponseHeaders? headers, string marker) =>
        headers is not null && headers.Any(h => h.Value.Any(v => v.Contains(marker, StringComparison.OrdinalIgnoreCase)));

    private static T? FindInner<T>(Exception exception)
        where T : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }
}
