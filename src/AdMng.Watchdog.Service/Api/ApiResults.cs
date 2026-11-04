using System.Text.Json;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Service.Hosting;

namespace AdMng.Watchdog.Service.Api;

/// <summary>
/// Responses and request bodies of the API: contract types are always (de)serialized through
/// <see cref="WatchdogJson.Options"/>; errors are RFC 9457 problem details with a <see cref="ApiProblem"/> type and – for
/// validation – <c>extensions.issues</c> (<see cref="ConfigIssue"/>[]). No exception text ever reaches a response.
/// </summary>
public static class ApiResults
{
    /// <summary>A JSON response of a contract type.</summary>
    public static IResult Json<T>(T value, int statusCode = StatusCodes.Status200OK) =>
        Results.Json(value, WatchdogJson.Options, contentType: "application/json; charset=utf-8", statusCode: statusCode);

    /// <summary>A problem response.</summary>
    public static IResult Problem(int statusCode, string type, string title, IReadOnlyList<ConfigIssue>? issues = null)
    {
        Dictionary<string, object?>? extensions = null;
        if (issues is { Count: > 0 })
        {
            extensions = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["issues"] = JsonSerializer.SerializeToElement(new ConfigValidationResult { Issues = issues }, WatchdogJson.Options).GetProperty("issues"),
            };
        }

        return Results.Problem(statusCode: statusCode, type: type, title: title, extensions: extensions);
    }

    /// <summary>400 with validation issues.</summary>
    public static IResult Invalid(IReadOnlyList<ConfigIssue> issues, string title = "The request is not valid.") =>
        Problem(StatusCodes.Status400BadRequest, ApiProblem.Validation, title, issues);

    /// <summary>400 with one issue.</summary>
    public static IResult Invalid(string path, string code, string message) =>
        Invalid([new ConfigIssue { Path = path, Code = code, Severity = IssueSeverity.Error, Message = message }], message);

    /// <summary>404.</summary>
    public static IResult NotFound(string title = "Not found.") => Problem(StatusCodes.Status404NotFound, ApiProblem.NotFound, title);

    /// <summary>Reads a JSON body of a contract type (≤ <see cref="ApiLimits.MaxRequestBodyBytes"/>). Returns the value or the
    /// error response: 415 for another content type, 413 for a larger body, 400 for malformed JSON or an empty body.</summary>
    public static Task<(T? Value, IResult? Error)> ReadJsonAsync<T>(HttpRequest request, bool allowEmpty, CancellationToken cancellationToken)
        where T : class =>
        ReadJsonAsync<T>(request, allowEmpty, ApiLimits.MaxRequestBodyBytes, cancellationToken);

    /// <summary>Reads a JSON body of a contract type of at most <paramref name="maxBytes"/> (one route raises the limit,
    /// U1c: <see cref="ApiLimits.MaxUserMessageBodyBytes"/>).</summary>
    public static async Task<(T? Value, IResult? Error)> ReadJsonAsync<T>(HttpRequest request, bool allowEmpty, long maxBytes, CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(request);
        var hasContentType = !string.IsNullOrEmpty(request.ContentType);
        if (hasContentType && !request.HasJsonContentType())
        {
            return (null, Problem(StatusCodes.Status415UnsupportedMediaType, ApiProblem.Validation, "Only application/json request bodies are accepted."));
        }

        if (request.ContentLength > maxBytes)
        {
            return (null, Problem(StatusCodes.Status413PayloadTooLarge, ApiProblem.Validation, "The request body is too large."));
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return (null, Problem(StatusCodes.Status413PayloadTooLarge, ApiProblem.Validation, "The request body is too large."));
            }

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0)
        {
            return allowEmpty ? (null, null) : (null, Invalid("$", "required", "A JSON body is required."));
        }

        if (!hasContentType)
        {
            return (null, Problem(StatusCodes.Status415UnsupportedMediaType, ApiProblem.Validation, "Only application/json request bodies are accepted."));
        }

        try
        {
            buffer.Position = 0;
            var value = await JsonSerializer.DeserializeAsync<T>(buffer, WatchdogJson.Options, cancellationToken).ConfigureAwait(false);
            return value is null && !allowEmpty ? (null, Invalid("$", "required", "A JSON body is required.")) : (value, null);
        }
        catch (JsonException ex)
        {
            // The exception text may quote the input – only the JSON path and position are used.
            var path = string.IsNullOrEmpty(ex.Path) ? "$" : ex.Path;
            return (null, Invalid(path, "invalidJson", "The request body is not valid JSON for this endpoint."));
        }
    }
}
