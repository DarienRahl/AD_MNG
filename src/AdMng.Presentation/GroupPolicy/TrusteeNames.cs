using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.Ldap;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.GroupPolicy;

/// <summary>
/// Names of the trustees of GPO permissions (owner, security filtering, delegation) as GPMC shows them: well-known
/// principals by their localized name, accounts of the domain as <c>DOMENA\konto</c>, builtin groups as
/// <c>BUILTIN\…</c>, unknown SIDs as the SID. Read on the session's DC, kept in the memory of the document only.
/// </summary>
public static class TrusteeNames
{
    private const int Chunk = 50;

    /// <summary>The localized name of a well-known principal, or null.</summary>
    public static string? WellKnown(ILocalizer localizer, string sid)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return sid?.ToUpperInvariant() switch
        {
            GroupPolicySecurityFilter.EveryoneSid => localizer[L.GroupPolicy.TrusteeEveryone],
            GroupPolicySecurityFilter.AuthenticatedUsersSid => localizer[L.GroupPolicy.TrusteeAuthenticatedUsers],
            GroupPolicySecurityFilter.EnterpriseDomainControllersSid => localizer[L.GroupPolicy.TrusteeEnterpriseDomainControllers],
            "S-1-5-18" => localizer[L.GroupPolicy.TrusteeSystem],
            "S-1-3-0" => localizer[L.GroupPolicy.TrusteeCreatorOwner],
            _ => null,
        };
    }

    /// <summary>
    /// Resolves <paramref name="sids"/> to display names (one OR search per 50 SIDs in the domain partition). Never throws
    /// for unreadable names: a SID that cannot be resolved keeps its SID form.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> ResolveAsync(
        IDirectorySession session,
        IEnumerable<string> sids,
        ILocalizer localizer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sids);
        ArgumentNullException.ThrowIfNull(localizer);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unknown = new List<string>();
        foreach (var sid in sids.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (WellKnown(localizer, sid) is { } name)
            {
                result[sid] = name;
            }
            else if (sid.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
            {
                unknown.Add(sid);
            }
        }

        var netBios = string.IsNullOrWhiteSpace(session.Info.DomainNetBiosName) ? session.Info.DomainDnsName : session.Info.DomainNetBiosName;
        foreach (var chunk in unknown.Chunk(Chunk))
        {
            try
            {
                await foreach (var obj in session.SearchAsync(new DirectorySearch
                {
                    BaseDn = session.Info.DefaultNamingContext,
                    Scope = SearchScope.Subtree,
                    Filter = LdapFilter.Or(chunk.Select(sid => LdapFilter.Equal(Attr.ObjectSid, sid))),
                    Attributes = [Attr.ObjectSid, Attr.Name, Attr.SamAccountName],
                    PageSize = Chunk,
                }, cancellationToken).ConfigureAwait(false))
                {
                    if (obj.ObjectSid is not { } sid)
                    {
                        continue;
                    }

                    var account = obj.GetString(Attr.SamAccountName) ?? obj.Name;
                    var prefix = sid.StartsWith("S-1-5-32-", StringComparison.OrdinalIgnoreCase) ? "BUILTIN" : netBios;
                    result[sid] = string.IsNullOrEmpty(prefix) ? account : prefix + "\\" + account;
                }
            }
            catch (DirectoryException)
            {
                // Names are a convenience: the SIDs stay.
            }
        }

        return result;
    }

    /// <summary>The name of <paramref name="sid"/> from <paramref name="names"/>, else the SID itself.</summary>
    public static string NameOf(IReadOnlyDictionary<string, string> names, string? sid)
    {
        ArgumentNullException.ThrowIfNull(names);
        return sid is null ? "–" : names.TryGetValue(sid, out var name) ? name : sid;
    }
}
