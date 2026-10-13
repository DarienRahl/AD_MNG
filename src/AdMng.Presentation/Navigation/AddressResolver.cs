using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;

namespace AdMng.Presentation.Navigation;

/// <summary>
/// Resolves the address bar text - a DN (<c>OU=IT,OU=Firma,DC=contoso,DC=test</c>) or a canonical path
/// (<c>contoso.test/Firma/IT</c>, the format of ADUC's Object tab and Explorer-like breadcrumbs) - to a DN.
/// </summary>
public static class AddressResolver
{
    /// <summary>Canonical display path of a DN ("contoso.test/Firma/IT"; the domain itself as "contoso.test").</summary>
    public static string ToDisplayPath(string distinguishedName) =>
        DistinguishedName.TryParse(distinguishedName, out var dn) && !dn.IsRoot ? dn.ToCanonicalName().TrimEnd('/') : distinguishedName;

    /// <summary>True when <paramref name="text"/> is a syntactically valid DN (contains '=' and parses).</summary>
    public static bool IsDistinguishedName(string text) =>
        text.Contains('=', StringComparison.Ordinal) && DistinguishedName.TryParse(text.Trim(), out var dn) && !dn.IsRoot;

    /// <summary>
    /// Returns the DN for the address or null when a path segment does not exist. DNs are returned unchanged
    /// (the caller verifies existence with a base read). Canonical paths are resolved level by level with
    /// one-level searches on <c>name</c> (each a cheap indexed query).
    /// </summary>
    public static async Task<string?> ResolveAsync(IDirectorySession session, string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(text);
        var address = text.Trim();
        if (address.Length == 0)
        {
            return null;
        }

        if (IsDistinguishedName(address))
        {
            return address;
        }

        var segments = SplitPath(address);
        var domain = session.Info.DomainDnsName;
        if (segments.Count > 0 && string.Equals(segments[0], domain, StringComparison.OrdinalIgnoreCase))
        {
            segments.RemoveAt(0);
        }

        var current = session.Info.DefaultNamingContext;
        foreach (var segment in segments)
        {
            var search = new DirectorySearch
            {
                BaseDn = current,
                Scope = SearchScope.OneLevel,
                Filter = LdapFilter.Equal(Attr.Name, segment),
                Attributes = [Attr.ObjectClass],

                // No size limit: two children may share the name (OU=Sales and CN=Sales) and a size limit of 1 would
                // end the search with "size limit exceeded" instead of a result. The first match wins.
                PageSize = 10,
            };
            string? next = null;
            await foreach (var obj in session.SearchAsync(search, cancellationToken).ConfigureAwait(false))
            {
                next = obj.DistinguishedName;
                break;
            }

            if (next is null)
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    /// <summary>Splits a canonical path on '/' honouring the <c>\/</c> escape used for slashes inside names.</summary>
    public static List<string> SplitPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var parts = new List<string>();
        var sb = new StringBuilder();
        for (var i = 0; i < path.Length; i++)
        {
            var c = path[i];
            if (c == '\\' && i + 1 < path.Length && path[i + 1] == '/')
            {
                sb.Append('/');
                i++;
            }
            else if (c == '/')
            {
                AddPart();
            }
            else
            {
                sb.Append(c);
            }
        }

        AddPart();
        return parts;

        void AddPart()
        {
            var part = sb.ToString().Trim();
            if (part.Length > 0)
            {
                parts.Add(part);
            }

            sb.Clear();
        }
    }
}
