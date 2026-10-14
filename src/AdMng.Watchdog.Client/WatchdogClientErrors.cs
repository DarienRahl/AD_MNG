using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Watchdog.Client;

/// <summary>
/// HTTP results and transport failures → <see cref="WatchdogClientException"/> (spec 5.5): 401 Unauthorized, 403
/// Forbidden, 404 NotFound, 412 VersionConflict, 400/409/413/415 Validation (with the problem's issues), 504 Timeout,
/// other statuses ServerError; a response without or with another <c>X-AdMng-Watchdog-Api</c> version
/// ApiVersionMismatch; socket failures Unreachable, TLS failures CertificateInvalid, an elapsed request timeout Timeout,
/// the caller's cancellation Cancelled. Messages are constant texts plus the server's problem title – never a body.
/// </summary>
public static class WatchdogClientErrors
{
    /// <summary>The error of an unsuccessful (or foreign) response, or null when the response is a valid success.</summary>
    public static async Task<WatchdogClientException?> FromResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        var status = (int)response.StatusCode;
        var version = response.Headers.TryGetValues(WatchdogRoutes.ApiVersionHeader, out var values) ? values.FirstOrDefault() : null;
        var expected = WatchdogRoutes.ApiVersion.ToString(CultureInfo.InvariantCulture);
        if ((version is not null && version != expected) || (version is null && response.IsSuccessStatusCode))
        {
            return new WatchdogClientException(WatchdogClientErrorKind.ApiVersionMismatch, "The server does not speak watchdog API version 1.") { StatusCode = status };
        }

        if (response.IsSuccessStatusCode)
        {
            return null;
        }

        var (title, issues) = await ReadProblemAsync(response, cancellationToken).ConfigureAwait(false);
        var kind = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => WatchdogClientErrorKind.Unauthorized,
            HttpStatusCode.Forbidden => WatchdogClientErrorKind.Forbidden,
            HttpStatusCode.NotFound => WatchdogClientErrorKind.NotFound,
            HttpStatusCode.PreconditionFailed => WatchdogClientErrorKind.VersionConflict,
            HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnsupportedMediaType
                => WatchdogClientErrorKind.Validation,
            HttpStatusCode.GatewayTimeout => WatchdogClientErrorKind.Timeout,
            _ => WatchdogClientErrorKind.ServerError,
        };
        var message = string.IsNullOrWhiteSpace(title)
            ? string.Create(CultureInfo.InvariantCulture, $"The watchdog service answered HTTP {status}.")
            : string.Create(CultureInfo.InvariantCulture, $"The watchdog service answered HTTP {status}: {title}");
        return new WatchdogClientException(kind, message) { StatusCode = status, Issues = issues };
    }

    /// <summary>The error of a failed request.</summary>
    public static WatchdogClientException FromException(Exception exception, bool callerCancelled)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is OperationCanceledException)
        {
            return callerCancelled
                ? new WatchdogClientException(WatchdogClientErrorKind.Cancelled, "The request was cancelled.", exception)
                : new WatchdogClientException(WatchdogClientErrorKind.Timeout, "The watchdog service did not answer in time.", exception);
        }

        if (Find<AuthenticationException>(exception) is not null)
        {
            return new WatchdogClientException(WatchdogClientErrorKind.CertificateInvalid, "The certificate of the watchdog service is not trusted.", exception);
        }

        if (Find<SocketException>(exception) is not null || exception is HttpRequestException)
        {
            return new WatchdogClientException(WatchdogClientErrorKind.Unreachable, "The watchdog service cannot be reached.", exception);
        }

        return new WatchdogClientException(WatchdogClientErrorKind.Unreachable, "The connection to the watchdog service failed.", exception);
    }

    private static async Task<(string? Title, IReadOnlyList<ConfigIssue> Issues)> ReadProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentType?.MediaType is not ("application/problem+json" or "application/json"))
        {
            return (null, []);
        }

        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(text);
            var title = document.RootElement.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == JsonValueKind.String
                ? titleElement.GetString()
                : null;
            var issues = document.RootElement.TryGetProperty("issues", out _)
                ? JsonSerializer.Deserialize<ConfigValidationResult>(text, WatchdogJson.Options)?.Issues ?? []
                : [];
            return (title, issues);
        }
        catch (JsonException)
        {
            return (null, []);
        }
    }

    private static T? Find<T>(Exception exception)
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
