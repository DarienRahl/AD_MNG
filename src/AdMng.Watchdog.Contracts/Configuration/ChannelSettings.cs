namespace AdMng.Watchdog.Contracts.Configuration;

public enum ChannelType { Teams, Webhook }
public enum DetailLevel { Minimal, Standard, Full }
public enum TeamsAuthMode { Sas, EntraClientCredentials }

public sealed record RateLimitSettings
{
    public int PerSecond { get; init; } = 1;
    public int PerMinute { get; init; } = 20;
    public int PerHour { get; init; } = 100;
}

/// <summary>Microsoft Teams through a Power Automate Workflows trigger (Adaptive Card 1.4).</summary>
public sealed record TeamsChannelOptions
{
    public TeamsAuthMode AuthMode { get; init; } = TeamsAuthMode.Sas;
    public EntraCloud Cloud { get; init; } = EntraCloud.Public;
    public string? TenantId { get; init; }
    public string? ClientId { get; init; }
    public string? CertificateThumbprint { get; init; }
    public SecretField ClientSecret { get; init; } = new();
    /// <summary>Only the documented Power Automate scopes of the cloud are accepted (8.3 invalidScope); Public:
    /// "https://service.flow.microsoft.com//.default" (default) or "https://service.flow.microsoft.com/.default".</summary>
    public string Scope { get; init; } = "https://service.flow.microsoft.com//.default";
    public string CardVersion { get; init; } = "1.4";
}

/// <summary>Generic webhook (Standard Webhooks v1 HMAC signatures).</summary>
public sealed record WebhookChannelOptions
{
    public SecretField SigningSecret { get; init; } = new();          // "whsec_" + base64(32 random bytes)
    public SecretField PreviousSigningSecret { get; init; } = new();  // rotation: both signatures sent until cleared
    public bool SendGitHubSignature { get; init; }                    // X-Hub-Signature-256
    public IReadOnlyList<string> AllowedHosts { get; init; } = [];    // empty = any https host
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public bool IncludeDigest { get; init; } = true;                  // watchdog.digest events
    /// <summary>watchdog.heartbeat every interval (≥ 5 min) so a SIEM can alert when it stops; null = off.</summary>
    public TimeSpan? HeartbeatInterval { get; init; }
}

/// <summary>A Teams or webhook channel (e-mail is configured in <see cref="SmtpSettings"/>, channel id "email").</summary>
public sealed record ChannelSettings
{
    public required string Id { get; init; }                 // [a-z0-9-]{1,40}, unique; "email" reserved
    public required ChannelType Type { get; init; }
    public required string Name { get; init; }
    public bool Enabled { get; init; } = true;
    public SecretField Url { get; init; } = new();           // Hint = scheme://host/first-path-segment/…
    public DetailLevel DetailLevel { get; init; } = DetailLevel.Standard;   // Teams Standard, webhook Minimal (defaults factory)
    public TeamsChannelOptions? Teams { get; init; }
    public WebhookChannelOptions? Webhook { get; init; }
    public RateLimitSettings RateLimit { get; init; } = new();
}
