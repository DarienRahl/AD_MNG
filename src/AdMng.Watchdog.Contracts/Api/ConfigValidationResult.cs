namespace AdMng.Watchdog.Contracts.Api;

public enum IssueSeverity { Error, Warning }

/// <summary>One validation finding of a configuration (section 8.3).</summary>
public sealed record ConfigIssue
{
    public required string Path { get; init; }       // JSON path, e.g. "rules[3].delivery.channelIds[0]"
    public required string Code { get; init; }       // stable code (section 8.3) – GUI localizes "Watchdog.Validation.{Code}"
    public required IssueSeverity Severity { get; init; }
    public required string Message { get; init; }    // English fallback
    public IReadOnlyList<string> Args { get; init; } = [];
}

public sealed record ConfigValidationResult
{
    public IReadOnlyList<ConfigIssue> Issues { get; init; } = [];
    public bool IsValid => Issues.All(i => i.Severity != IssueSeverity.Error);
}
