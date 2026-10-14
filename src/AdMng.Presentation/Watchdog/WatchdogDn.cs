using AdMng.Core.Ldap;

namespace AdMng.Presentation.Watchdog;

/// <summary>
/// DN checks of the Watchdog editors (8.3 <c>invalidDn</c>): a DN parses with <see cref="DistinguishedName.TryParse"/>
/// after the configuration tokens are expanded with placeholder values (<c>{domain}</c>, <c>{domainSid}</c>…).
/// </summary>
public static class WatchdogDn
{
    private const string PlaceholderDomain = "DC=contoso,DC=test";

    /// <summary>True when <paramref name="text"/> is a non-empty DN (tokens allowed).</summary>
    public static bool IsValid(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var expanded = text.Trim().Replace("{domain}", PlaceholderDomain, StringComparison.OrdinalIgnoreCase);
        return DistinguishedName.TryParse(expanded, out var dn) && !dn.IsRoot;
    }

    /// <summary>Lines of a multi-line text box, trimmed, without empty lines.</summary>
    public static IReadOnlyList<string> Lines(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : [.. text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => l.Length > 0)];

    /// <summary>The lines of <paramref name="text"/> that are no valid DN.</summary>
    public static IReadOnlyList<string> InvalidLines(string? text) => [.. Lines(text).Where(l => !IsValid(l))];
}
