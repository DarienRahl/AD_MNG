using AdMng.Core.Ldap;

namespace AdMng.Presentation.Connections;

/// <summary>
/// Pinned and recent containers are shared by every connection window (one settings file). A window shows only those of
/// its own domain - in its default naming context, not in a child domain below it - and leaves the others in the settings.
/// </summary>
public static class NamingContextFilter
{
    /// <summary>
    /// True when <paramref name="distinguishedName"/> is <paramref name="namingContext"/> or in it. A child domain's DNs
    /// (<c>OU=Kadry,DC=emea,DC=contoso,DC=test</c>) are below the parent's naming context too, but belong to another domain:
    /// the RDN directly below a <c>DC=</c> naming context must not be another <c>DC=</c> component. Without a naming
    /// context (not connected) everything is shown; a DN that does not parse is not.
    /// </summary>
    public static bool Contains(string? namingContext, string? distinguishedName)
    {
        if (string.IsNullOrWhiteSpace(namingContext) || !DistinguishedName.TryParse(namingContext, out var root))
        {
            return true;
        }

        if (!DistinguishedName.TryParse(distinguishedName, out var dn) || !(dn.Equals(root) || dn.IsDescendantOf(root)))
        {
            return false;
        }

        var below = dn.Rdns.Count - root.Rdns.Count;
        return below == 0 || root.IsRoot || !IsDomainComponent(root.Rdns[0]) || !IsDomainComponent(dn.Rdns[below - 1]);
    }

    private static bool IsDomainComponent(Rdn rdn) => string.Equals(rdn.Type, "DC", StringComparison.OrdinalIgnoreCase);
}
