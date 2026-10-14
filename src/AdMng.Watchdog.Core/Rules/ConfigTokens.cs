using System.Globalization;
using System.Text.RegularExpressions;
using AdMng.Core.Ldap;

namespace AdMng.Watchdog.Core.Rules;

/// <summary>
/// Values of the configuration tokens (section 8.1): <c>{domain}</c> = default naming context, <c>{domainSid}</c> =
/// domain SID, <c>{rootDomainSid}</c> = forest root domain SID. Expansion happens at runtime only – the stored document
/// keeps the tokens. An unknown value never expands to something that could match by accident: a DN with an unknown
/// <c>{domain}</c> expands to null, a SID with an unknown domain SID becomes a relative-id pattern matched within any
/// domain SID (<see cref="SidPattern.AnyDomain"/>), as specified for an unreadable root domain SID.
/// </summary>
public sealed partial record ConfigTokens
{
    /// <summary>The default naming context token.</summary>
    public const string DomainToken = "{domain}";

    /// <summary>The domain SID token.</summary>
    public const string DomainSidToken = "{domainSid}";

    /// <summary>The forest root domain SID token.</summary>
    public const string RootDomainSidToken = "{rootDomainSid}";

    /// <summary>Default naming context (<c>DC=contoso,DC=com</c>); null when not known yet.</summary>
    public string? DomainDn { get; init; }

    /// <summary>Domain SID (<c>S-1-5-21-…</c>); null when not known.</summary>
    public string? DomainSid { get; init; }

    /// <summary>Forest root domain SID; null when not readable.</summary>
    public string? RootDomainSid { get; init; }

    /// <summary>No value known (before the first directory session).</summary>
    public static ConfigTokens Unknown { get; } = new();

    /// <summary>Placeholder values used by the validator (8.3 <c>invalidDn</c> / <c>invalidSid</c>).</summary>
    public static ConfigTokens Placeholder { get; } = new()
    {
        DomainDn = "DC=example,DC=test",
        DomainSid = "S-1-5-21-1000000001-1000000002-1000000003",
        RootDomainSid = "S-1-5-21-1000000001-1000000002-1000000003",
    };

    /// <summary>True when <paramref name="value"/> contains one of the three tokens (case-insensitive).</summary>
    public static bool ContainsToken(string? value) =>
        value is not null
        && (value.Contains(DomainToken, StringComparison.OrdinalIgnoreCase)
            || value.Contains(DomainSidToken, StringComparison.OrdinalIgnoreCase)
            || value.Contains(RootDomainSidToken, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <paramref name="value"/> with <c>{domain}</c> replaced (trimmed); null when the value is empty or contains
    /// <c>{domain}</c> while <see cref="DomainDn"/> is unknown. SID tokens inside a DN are not expanded.
    /// </summary>
    public string? ExpandDn(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (!trimmed.Contains(DomainToken, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return DomainDn is null ? null : trimmed.Replace(DomainToken, DomainDn, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The SID pattern of a configured SID: exact when the value is literal or its token is known, a relative-id
    /// pattern (<c>{domainSid}-512</c> with an unknown domain SID → RID 512 of any domain), null when the value is
    /// empty or not a SID (<c>^S-1-\d+(-\d+)+$</c>) after expansion.
    /// </summary>
    public SidPattern? ExpandSid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        foreach (var (token, known) in new[] { (DomainSidToken, DomainSid), (RootDomainSidToken, RootDomainSid) })
        {
            if (!trimmed.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rest = trimmed[token.Length..];
            if (known is not null)
            {
                trimmed = known + rest;
                break;
            }

            return rest.Length > 1 && rest[0] == '-'
                && uint.TryParse(rest.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var rid)
                ? SidPattern.AnyDomain(rid)
                : null;
        }

        return SidSyntax().IsMatch(trimmed) ? SidPattern.Exact(trimmed) : null;
    }

    /// <summary>These values with <see cref="DomainDn"/> taken from the domain components of <paramref name="objectDn"/>
    /// when unknown (one watchdog instance watches one domain, so every watched object lives under it).</summary>
    public ConfigTokens WithDomainFallback(string? objectDn)
    {
        if (DomainDn is not null || !DistinguishedName.TryParse(objectDn, out var dn))
        {
            return this;
        }

        var domain = dn.DomainComponent.ToString();
        return domain.Length == 0 ? this : this with { DomainDn = domain };
    }

    /// <summary><c>^S-1-\d+(-\d+)+$</c> (section 8.3 <c>invalidSid</c>).</summary>
    [GeneratedRegex(@"^S-1-\d+(-\d+)+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    internal static partial Regex SidSyntax();
}
