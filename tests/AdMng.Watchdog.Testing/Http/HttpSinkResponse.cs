namespace AdMng.Watchdog.Testing.Http;

/// <summary>The scripted answer of <see cref="HttpSink"/> to one request.</summary>
public sealed record HttpSinkResponse
{
    public int StatusCode { get; init; } = 202;
    /// <summary>Response headers, e.g. Retry-After.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public string? Body { get; init; }
    public string ContentType { get; init; } = "text/plain; charset=utf-8";

    /// <summary>202 Accepted without a body (what Power Automate answers).</summary>
    public static HttpSinkResponse Accepted { get; } = new();

    public static HttpSinkResponse Status(int statusCode, string? body = null) => new() { StatusCode = statusCode, Body = body };
}
