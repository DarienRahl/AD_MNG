namespace AdMng.Watchdog.Contracts.Api;

/// <summary>ProblemDetails "type" values (RFC 9457) used by the API; the client maps them to WatchdogClientErrorKind.</summary>
public static class ApiProblem
{
    public const string Validation = "https://admng/problems/validation";          // 400, extensions["issues"] = ConfigIssue[]
    public const string VersionConflict = "https://admng/problems/version-conflict"; // 412
    public const string ClientHeaderMissing = "https://admng/problems/client-header"; // 400
    public const string NotFound = "https://admng/problems/not-found";             // 404
    public const string Unavailable = "https://admng/problems/unavailable";         // 503 (store/directory down)
}
