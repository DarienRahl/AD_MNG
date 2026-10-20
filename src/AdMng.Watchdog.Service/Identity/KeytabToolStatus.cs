using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdMng.Watchdog.Service.Identity;

/// <summary>
/// <c>status.json</c> of <c>admng-watchdog-keytab</c> (design 4.4) as the service reads it (root:admng-watchdog 0640, no
/// secrets): principal, kvno, last change and attempt, result (<c>KeytabStatusResults</c>), krb5 error name, next due.
/// </summary>
public sealed record KeytabToolStatus
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public string? Principal { get; init; }

    public int? Kvno { get; init; }

    public DateTimeOffset? LastChange { get; init; }

    public DateTimeOffset? LastAttempt { get; init; }

    public string? Result { get; init; }

    public string? ErrorCode { get; init; }

    public DateTimeOffset? NextDue { get; init; }

    /// <summary>The status file, null when it does not exist.</summary>
    /// <exception cref="IOException">Unreadable.</exception>
    /// <exception cref="JsonException">Malformed.</exception>
    public static KeytabToolStatus? Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return File.Exists(path) ? JsonSerializer.Deserialize<KeytabToolStatus>(File.ReadAllText(path), Json) : null;
    }
}
