using AdMng.Core.Ldap;

namespace AdMng.Presentation.Connections;

/// <summary>
/// Pinned and recent containers are shared by every connection window (one settings file). A window shows only those of
/// its own domain - under its default naming context - and leaves the others in the settings.
/// </summary>
public static class NamingContextFilter
{
    /// <summary>
    /// True when <paramref name="distinguishedName"/> is <paramref name="namingContext"/> or below it. Without a naming
    /// context (not connected) everything is shown; a DN that does not parse is not.
    /// </summary>
    public static bool Contains(string? namingContext, string? distinguishedName)
    {
        if (string.IsNullOrWhiteSpace(namingContext) || !DistinguishedName.TryParse(namingContext, out var root))
        {
            return true;
        }

        return DistinguishedName.TryParse(distinguishedName, out var dn) && (dn.Equals(root) || dn.IsDescendantOf(root));
    }
}
