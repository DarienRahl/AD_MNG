using System.Text.Json;
using System.Text.Json.Serialization;
using AdMng.Watchdog.Keytab.Native;

namespace AdMng.Watchdog.Keytab;

/// <summary>Values of <see cref="KeytabStatus.Result"/> (the service reads them as <c>KeytabStatusResults</c>).</summary>
internal static class KeytabResults
{
    public const string Ok = "ok";
    public const string OtpActive = "otpActive";
    public const string ChangeFailed = "changeFailed";
    public const string KeytabWriteFailed = "keytabWriteFailed";
    public const string ReplicationPending = "replicationPending";
    public const string VerifyFailed = "verifyFailed";
}

/// <summary>
/// <c>status.json</c> of the keytab tool (design 4.4, root:admng-watchdog 0640): principal, kvno and times – no secret, no
/// directory value. Written atomically (new file + rename).
/// </summary>
internal sealed record KeytabStatus
{
    public const string FileName = "status.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string? Principal { get; init; }

    public uint? Kvno { get; init; }

    public DateTimeOffset? LastChange { get; init; }

    public DateTimeOffset? LastAttempt { get; init; }

    public string Result { get; init; } = KeytabResults.Ok;

    public string? ErrorCode { get; init; }

    public DateTimeOffset? NextDue { get; init; }

    /// <summary>The status in <paramref name="stateDirectory"/>, null when missing or unreadable.</summary>
    public static KeytabStatus? Read(string stateDirectory)
    {
        var path = Path.Combine(stateDirectory, FileName);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<KeytabStatus>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string Serialize(KeytabStatus status) => JsonSerializer.Serialize(status, Json);

    /// <summary>Writes the status (mode 0640, group admng-watchdog when it exists and the tool runs as root).</summary>
    public void Write(string stateDirectory)
    {
        var path = Path.Combine(stateDirectory, FileName);
        var temp = path + ".new";
        File.Delete(temp);
        using (var stream = new FileStream(temp, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
        }))
        {
            stream.Write(System.Text.Encoding.UTF8.GetBytes(Serialize(this) + "\n"));
            stream.Flush(flushToDisk: true);
        }

        File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        Posix.ChownRootGroup(temp, "admng-watchdog");
        File.Move(temp, path, overwrite: true);
    }
}
