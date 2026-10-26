using System.Globalization;
using AdMng.Core.ActiveDirectory;

namespace AdMng.Core.Comparison;

/// <summary>
/// Matches groups (and other principals) across two domains, whose DNs and SIDs differ (design note
/// docs/dev/multi-window.md 8.7): a builtin group by its SID, a well-known domain group by its RID (so <i>Domain Users</i>
/// on an English DC equals <i>Użytkownicy domeny</i> on a Polish one), any other principal by sAMAccountName, else by name.
/// </summary>
public static class GroupKey
{
    /// <summary>The builtin domain's SID prefix (Administrators, Users, Account Operators …).</summary>
    public const string BuiltinSidPrefix = "S-1-5-32-";

    /// <summary>Domain SIDs whose RID is below this value are well-known (Domain Admins 512, Domain Users 513, …).</summary>
    public const uint FirstOrdinaryRid = 1000;

    private const string DomainSidPrefix = "S-1-5-21-";

    /// <summary>
    /// The key of a group: <c>sid:S-1-5-32-544</c>, <c>rid:513</c>, <c>sam:GG_IT</c> (upper-case invariant) or
    /// <c>name:…</c>; empty when nothing identifies it.
    /// </summary>
    public static string For(string? sid, string? samAccountName, string? name)
    {
        if (!string.IsNullOrEmpty(sid))
        {
            if (sid.StartsWith(BuiltinSidPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return "sid:" + sid.ToUpperInvariant();
            }

            if (sid.StartsWith(DomainSidPrefix, StringComparison.OrdinalIgnoreCase) && AdConvert.GetRid(sid) is { } rid && rid < FirstOrdinaryRid
                && sid.Count(c => c == '-') == 7)
            {
                return "rid:" + rid.ToString(CultureInfo.InvariantCulture);
            }
        }

        if (!string.IsNullOrWhiteSpace(samAccountName))
        {
            return "sam:" + samAccountName.Trim().ToUpperInvariant();
        }

        return string.IsNullOrWhiteSpace(name) ? string.Empty : "name:" + name.Trim().ToUpperInvariant();
    }

    /// <summary>
    /// The key of a member of a group (the "Członkowie" tab): like <see cref="For"/> for users, computers and groups (only a
    /// well-known domain RID or a builtin SID wins over sAMAccountName), else <c>mail:…</c>, else <c>name:…</c> (contacts).
    /// </summary>
    public static string ForPrincipal(string? sid, string? samAccountName, string? mail, string? name)
    {
        var key = For(sid, samAccountName, null);
        if (key.Length > 0)
        {
            return key;
        }

        if (!string.IsNullOrWhiteSpace(mail))
        {
            return "mail:" + mail.Trim().ToUpperInvariant();
        }

        return string.IsNullOrWhiteSpace(name) ? string.Empty : "name:" + name.Trim().ToUpperInvariant();
    }
}
