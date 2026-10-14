namespace AdMng.Watchdog.Testing.Http;

/// <summary>The scripted answer of <see cref="HttpSink"/> to one request.</summary>
public sealed record HttpSinkResponse
{
    public int StatusCode { get; init; } = 202;
    /// <summary>Response headers, e.g. Retry-After.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public string? Body { get; init; }
    public string ContentType { get; init; } = "text/plain; charset=utf-8";

    /// <summary>The sink answers only after this task completes (or the client gives up) – timeout tests without
    /// sleeping: release it after advancing the fake clock.</summary>
    public Task? HoldUntil { get; init; }

    /// <summary>202 Accepted without a body (what Power Automate answers).</summary>
    public static HttpSinkResponse Accepted { get; } = new();

    public static HttpSinkResponse Status(int statusCode, string? body = null) => new() { StatusCode = statusCode, Body = body };

    /// <summary>A response with one header (e.g. <c>Retry-After</c>, <c>Location</c>).</summary>
    public static HttpSinkResponse WithHeader(int statusCode, string name, string value) => new()
    {
        StatusCode = statusCode,
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [name] = value },
    };
}
