namespace AdMng.Watchdog.Contracts.Api;

/// <summary>ProblemDetails "type" values (RFC 9457) used by the API; the client maps them to WatchdogClientErrorKind.</summary>
public static class ApiProblem
{
    public const string Validation = "https://admng/problems/validation";          // 400, extensions["issues"] = ConfigIssue[]
    public const string VersionConflict = "https://admng/problems/version-conflict"; // 412
    public const string ClientHeaderMissing = "https://admng/problems/client-header"; // 400
    public const string NotFound = "https://admng/problems/not-found";             // 404
    public const string Unavailable = "https://admng/problems/unavailable";         // 503 (store/directory down)

    /// <summary>409: messages to users are switched off (<c>userMessages.enabled</c>) or the SMTP channel is not
    /// configured; <c>extensions.issues[0].code</c> says which (<see cref="UserMessageIssueCodes"/>).</summary>
    public const string UserMessagesUnavailable = "https://admng/problems/user-messages-unavailable";

    /// <summary>429 with <c>Retry-After</c>: the hourly or daily limit of messages to users is reached.</summary>
    public const string RateLimited = "https://admng/problems/rate-limited";

    /// <summary>409: an idempotency key (message id) was used before with other content or by another caller.</summary>
    public const string IdempotencyConflict = "https://admng/problems/idempotency-conflict";

    /// <summary>503: the caller's roles cannot be resolved – LDAP unavailable and no usable cached entry (Linux role
    /// resolution, stage B design 6.2 step 5). Never a role is granted instead.</summary>
    public const string RoleLookupUnavailable = "https://admng/problems/role-lookup-unavailable";
}
