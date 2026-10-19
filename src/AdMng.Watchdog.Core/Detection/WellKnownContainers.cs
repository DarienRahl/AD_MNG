using AdMng.Core.Directory;
using AdMng.Core.Ldap;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// The default users and computers containers from the domain head's <c>wellKnownObjects</c> (section 10.2): values
/// <c>B:32:A9D1CA15768811D1ADED00C04FD8D5CD:&lt;dn&gt;</c> (users) and <c>B:32:AA312825768811D1ADED00C04FD8D5CD:&lt;dn&gt;</c>
/// (computers), the DN possibly in Extended DN form. Follows <c>redirusr</c>/<c>redircmp</c>. Values are parsed by
/// <see cref="WellKnownObjects.TryParseValue"/> (the parser the console uses too).
/// </summary>
public sealed class WellKnownContainers
{
    public const string UsersGuid = WellKnownGuids.Users;
    public const string ComputersGuid = WellKnownGuids.Computers;

    private WellKnownContainers(string domainDn, DistinguishedName? users, DistinguishedName? computers)
    {
        var domain = DistinguishedName.Parse(domainDn);
        LiteralUsers = domain.Child("CN", "Users");
        LiteralComputers = domain.Child("CN", "Computers");
        Users = users ?? LiteralUsers;
        Computers = computers ?? LiteralComputers;
    }

    /// <summary>Resolved default users container.</summary>
    public DistinguishedName Users { get; }

    /// <summary>Resolved default computers container.</summary>
    public DistinguishedName Computers { get; }

    /// <summary><c>CN=Users,{domain}</c>.</summary>
    public DistinguishedName LiteralUsers { get; }

    /// <summary><c>CN=Computers,{domain}</c>.</summary>
    public DistinguishedName LiteralComputers { get; }

    /// <summary>True when the computers container was redirected (redircmp).</summary>
    public bool ComputersRedirected => Computers != LiteralComputers;

    /// <summary>True when the users container was redirected (redirusr).</summary>
    public bool UsersRedirected => Users != LiteralUsers;

    /// <summary>Parses the wellKnownObjects values of the domain head; missing entries fall back to the literal containers.</summary>
    public static WellKnownContainers Parse(IEnumerable<string> wellKnownObjects, string domainDn)
    {
        ArgumentNullException.ThrowIfNull(wellKnownObjects);
        ArgumentNullException.ThrowIfNull(domainDn);
        DistinguishedName? users = null, computers = null;
        foreach (var value in wellKnownObjects)
        {
            if (!WellKnownObjects.TryParseValue(value, out var binary, out var dnText)
                || !DistinguishedName.TryParse(dnText, out var dn))
            {
                continue;
            }

            if (binary.Equals(UsersGuid, StringComparison.OrdinalIgnoreCase))
            {
                users = dn;
            }
            else if (binary.Equals(ComputersGuid, StringComparison.OrdinalIgnoreCase))
            {
                computers = dn;
            }
        }

        return new WellKnownContainers(domainDn, users, computers);
    }

    /// <summary>The parent DN equals the resolved default users or computers container (tag defaultContainer).</summary>
    public bool IsDefaultContainer(string? parentDn) =>
        parentDn is not null && DistinguishedName.TryParse(parentDn, out var parent) && (parent == Users || parent == Computers);

    /// <summary>
    /// The default container is redirected, yet the object was created in the literal <c>CN=Users</c> /
    /// <c>CN=Computers</c> (tag unexpectedContainer).
    /// </summary>
    public bool IsUnexpectedContainer(string? parentDn)
    {
        if (parentDn is null || !DistinguishedName.TryParse(parentDn, out var parent))
        {
            return false;
        }

        return (ComputersRedirected && parent == LiteralComputers) || (UsersRedirected && parent == LiteralUsers);
    }
}
