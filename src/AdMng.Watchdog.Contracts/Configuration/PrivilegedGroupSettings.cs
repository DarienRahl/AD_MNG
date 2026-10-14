namespace AdMng.Watchdog.Contracts.Configuration;

/// <summary>Roots of privileged access (PrivilegeEvaluator, section 10.8).</summary>
public sealed record PrivilegedGroupSettings
{
    /// <summary>Tier "Critical". Tokens {domainSid} / {rootDomainSid} are expanded at runtime.</summary>
    public IReadOnlyList<string> CriticalSids { get; init; } =
        ["S-1-5-32-544", "{domainSid}-512", "{domainSid}-516", "{rootDomainSid}-518", "{rootDomainSid}-519",
         "{domainSid}-526", "{rootDomainSid}-527"];
    public IReadOnlyList<string> HighSids { get; init; } =
        ["S-1-5-32-548", "S-1-5-32-549", "S-1-5-32-550", "S-1-5-32-551", "S-1-5-32-552",
         "{domainSid}-520", "{domainSid}-517", "{domainSid}-521"];
    /// <summary>Groups without a well-known RID, matched by sAMAccountName (case-insensitive).</summary>
    public IReadOnlyList<string> HighSamAccountNames { get; init; } = ["DnsAdmins"];
    public IReadOnlyList<string> AdditionalCriticalDns { get; init; } = [];
    public IReadOnlyList<string> AdditionalHighDns { get; init; } = [];
    /// <summary>Removal from this group raises High (Protected Users).</summary>
    public IReadOnlyList<string> ProtectRemovalSids { get; init; } = ["{domainSid}-525"];
    /// <summary>Ordinary groups whose membership changes notify immediately (GroupFilter.WatchList).</summary>
    public IReadOnlyList<string> WatchListDns { get; init; } = [];
}
