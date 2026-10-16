using System.Globalization;

namespace AdMng.Watchdog.Core.Rules;

/// <summary>
/// A configured SID after token expansion (<see cref="ConfigTokens.ExpandSid"/>): either an exact SID, or – when the
/// domain SID of a <c>{domainSid}</c> / <c>{rootDomainSid}</c> token is unknown – a relative id matched within any
/// domain SID (<c>S-1-5-21-a-b-c-RID</c>, section 8.1).
/// </summary>
public readonly record struct SidPattern
{
    private const string DomainPrefix = "S-1-5-21-";

    private SidPattern(string? sid, uint? anyDomainRid)
    {
        Sid = sid;
        AnyDomainRid = anyDomainRid;
    }

    /// <summary>The exact SID; null for a relative-id pattern.</summary>
    public string? Sid { get; }

    /// <summary>The relative id matched within any domain SID; null for an exact SID.</summary>
    public uint? AnyDomainRid { get; }

    /// <summary>An exact SID.</summary>
    public static SidPattern Exact(string sid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);
        return new SidPattern(sid.Trim(), null);
    }

    /// <summary>A relative id within any domain SID.</summary>
    public static SidPattern AnyDomain(uint rid) => new(null, rid);

    /// <summary>True when <paramref name="sid"/> is matched by this pattern (case-insensitive).</summary>
    public bool Matches(string? sid)
    {
        if (string.IsNullOrWhiteSpace(sid))
        {
            return false;
        }

        if (Sid is not null)
        {
            return string.Equals(Sid, sid.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        if (AnyDomainRid is not { } rid)
        {
            return false;
        }

        var value = sid.Trim();
        if (!value.StartsWith(DomainPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // S-1-5-21-a-b-c-RID: exactly four numeric sub-authorities after "21".
        var parts = value[DomainPrefix.Length..].Split('-');
        return parts.Length == 4
            && parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit))
            && uint.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var actual)
            && actual == rid;
    }

    /// <inheritdoc/>
    public override string ToString() =>
        Sid ?? string.Create(CultureInfo.InvariantCulture, $"S-1-5-21-*-{AnyDomainRid}");
}
