namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// Runtime expansion of the configuration tokens <c>{domain}</c>, <c>{domainSid}</c> and <c>{rootDomainSid}</c>
/// (section 8.1) for the detection settings. Minimal detection-side counterpart of the rules area's ConfigTokens
/// (owned by W-rules); values that still contain a token after expansion (unknown SID) are dropped by the callers.
/// </summary>
internal static class DetectionTokens
{
    public const string Domain = "{domain}";
    public const string DomainSid = "{domainSid}";
    public const string RootDomainSid = "{rootDomainSid}";

    /// <summary>Expands the tokens; a SID token without a known value stays in place (caller drops the value).</summary>
    public static string Expand(string value, string domainDn, string? domainSid, string? rootDomainSid)
    {
        ArgumentNullException.ThrowIfNull(value);
        var result = value.Replace(Domain, domainDn, StringComparison.OrdinalIgnoreCase);
        if (domainSid is not null)
        {
            result = result.Replace(DomainSid, domainSid, StringComparison.OrdinalIgnoreCase);
        }

        var root = rootDomainSid ?? domainSid;
        if (root is not null)
        {
            result = result.Replace(RootDomainSid, root, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    /// <summary>True when an unexpanded token is left.</summary>
    public static bool HasToken(string value) => value.Contains('{', StringComparison.Ordinal);
}
