namespace AdMng.Watchdog.Core.Detection;

/// <summary>Facts about the watched domain read once per session (naming contexts, domain and root domain SID).</summary>
public sealed record DirectoryFacts
{
    public required string DomainDn { get; init; }
    public required string DomainDnsName { get; init; }
    public required string ConfigurationDn { get; init; }
    public required string SchemaDn { get; init; }
    public string? DomainSid { get; init; }
    /// <summary>Forest root domain SID (best effort; null when unreadable – root groups then match by RID).</summary>
    public string? RootDomainSid { get; init; }
}
