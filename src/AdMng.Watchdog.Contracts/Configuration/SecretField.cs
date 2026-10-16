namespace AdMng.Watchdog.Contracts.Configuration;

/// <summary>A write-only secret in the configuration document. GET returns IsSet/Hint only.</summary>
public sealed record SecretField
{
    public bool IsSet { get; init; }
    public string? Hint { get; init; }
    /// <summary>PUT only: new value. Never returned, never stored in the configuration document.</summary>
    public string? NewValue { get; init; }
    /// <summary>PUT only: remove the stored secret.</summary>
    public bool Clear { get; init; }
    public override string ToString() => IsSet ? "SecretField(set)" : "SecretField(empty)";
}
