namespace AdMng.Watchdog.Service.Hosting;

/// <summary>Request limits of the API (spec 17.1).</summary>
public static class ApiLimits
{
    /// <summary>Largest accepted request body (1 MB).</summary>
    public const long MaxRequestBodyBytes = 1024 * 1024;

    /// <summary>Largest body of <c>POST /api/v1/user-messages</c> only (6 MiB, U1c): 2 MiB of inline images ≈ 2.7 MiB of
    /// base64, 400 000 HTML characters JSON-escaped, the text body and headroom. Every other route keeps
    /// <see cref="MaxRequestBodyBytes"/>.</summary>
    public const long MaxUserMessageBodyBytes = 6L * 1024 * 1024;

    /// <summary>Largest page of events / outbox rows / audit entries.</summary>
    public const int MaxPageSize = 500;

    /// <summary>Longest long-poll wait in seconds.</summary>
    public const int MaxWaitSeconds = 30;

    /// <summary>Concurrent requests per caller.</summary>
    public const int ConcurrentRequestsPerCaller = 4;

    /// <summary>Messages to users validated at once, by all callers together (the HTML check is CPU work; the wait for the
    /// SMTP attempt is not limited by it).</summary>
    public const int ConcurrentUserMessageValidations = 2;

    /// <summary>Longest acknowledge comment.</summary>
    public const int MaxCommentLength = 500;

    /// <summary>Test e-mail send timeout.</summary>
    public static readonly TimeSpan EmailTestTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Test channel send timeout.</summary>
    public static readonly TimeSpan ChannelTestTimeout = TimeSpan.FromSeconds(45);

    /// <summary>How long <c>POST /api/v1/user-messages</c> waits for the first SMTP attempt before it answers "queued"
    /// (the client waits <see cref="UserMessageWait"/> + 25 s).</summary>
    public static readonly TimeSpan UserMessageWait = TimeSpan.FromSeconds(20);
}
