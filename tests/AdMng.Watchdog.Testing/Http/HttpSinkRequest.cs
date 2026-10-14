using System.Text;

namespace AdMng.Watchdog.Testing.Http;

/// <summary>One request received by <see cref="HttpSink"/>.</summary>
public sealed record HttpSinkRequest
{
    public required string Method { get; init; }
    /// <summary>Path and query exactly as received (tests assert that secrets in the query are never logged).</summary>
    public required string PathAndQuery { get; init; }
    /// <summary>Request headers (multiple values joined with ", "), case-insensitive.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>Exact body bytes (signatures are verified over them).</summary>
    public byte[] Body { get; init; } = [];
    public required DateTimeOffset ReceivedAt { get; init; }

    public string BodyText => Encoding.UTF8.GetString(Body);
}
