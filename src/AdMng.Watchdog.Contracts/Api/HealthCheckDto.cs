namespace AdMng.Watchdog.Contracts.Api;

public enum HealthState { Ok, Warning, Error, Unknown, Disabled }

/// <summary>One health check of the service.</summary>
public sealed record HealthCheckDto
{
    /// <summary>Stable code, e.g. "directory.connection", "permission.uSNChanged", "permission.deletedObjects",
    /// "eventlog.dc01.contoso.com", "smtp", "storage.free" – the GUI localizes "Watchdog.Health.{Code-prefix}".</summary>
    public required string Code { get; init; }
    public required HealthState State { get; init; }
    public string? Detail { get; init; }       // English diagnostic text without directory values
    public IReadOnlyList<string> Args { get; init; } = [];   // numbers/host names for localized messages
    public DateTimeOffset CheckedAt { get; init; }
}
