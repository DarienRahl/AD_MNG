using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// The LDAP filters of the detection searches (section 10.5), built with <see cref="LdapFilter"/> only. Computers,
/// managed service accounts and inetOrgPerson are <c>objectClass=user</c>; out-of-scope filtering happens client-side.
/// </summary>
public static class DetectionFilters
{
    /// <summary><c>(|(objectClass=organizationalUnit)(objectClass=container)(objectClass=domainDNS))</c>.</summary>
    public static string ContainerClasses { get; } = LdapFilter.Or(
        LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.OrganizationalUnit),
        LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.Container),
        LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.DomainDns));

    /// <summary>Every watched class of the default naming context.</summary>
    public static string ScopeClasses { get; } = LdapFilter.Or(
        LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.User),
        LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.Group),
        LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.Contact),
        LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.OrganizationalUnit),
        LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.Container),
        LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.DomainDns),
        LdapFilter.Equal(Attr.ObjectClass, WatchdogObjectClasses.GroupPolicyContainer),
        LdapFilter.Equal(Attr.ObjectClass, WatchdogObjectClasses.TrustedDomain),
        LdapFilter.Equal(Attr.ObjectClass, WatchdogObjectClasses.PasswordSettings),
        LdapFilter.Equal(Attr.ObjectClass, WatchdogObjectClasses.RidManager),
        LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.InfrastructureUpdate));

    /// <summary>
    /// Objects of pass 2: not a container class – except group policy containers, which derive from
    /// <c>container</c> but are leaf objects of their own kind.
    /// </summary>
    public static string NotContainer { get; } = LdapFilter.Or(
        LdapFilter.Not(ContainerClasses),
        LdapFilter.Equal(Attr.ObjectClass, WatchdogObjectClasses.GroupPolicyContainer));

    /// <summary>Non-container objects of the scope classes (pass 2 of a full scan).</summary>
    public static string NonContainerScope { get; } = LdapFilter.And(ScopeClasses, NotContainer);

    /// <summary>Containers changed since <paramref name="searchFrom"/> (pass 1 of an incremental cycle).</summary>
    public static string ChangedContainers(long searchFrom) =>
        LdapFilter.And(LdapFilter.GreaterOrEqual(Attr.UsnChanged, searchFrom), ContainerClasses);

    /// <summary>Non-container objects changed since <paramref name="searchFrom"/> (pass 2 of an incremental cycle).</summary>
    public static string ChangedObjects(long searchFrom) =>
        LdapFilter.And(LdapFilter.GreaterOrEqual(Attr.UsnChanged, searchFrom), ScopeClasses, NotContainer);

    /// <summary>Users (people incl. inetOrgPerson, without contacts): <c>(&amp;(objectCategory=person)(objectClass=user))</c>.</summary>
    public static string Users { get; } = LdapFilter.And(
        LdapFilter.Equal(Attr.ObjectCategory, WatchdogObjectClasses.Person),
        LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.User));

    /// <summary>Enabled accounts: <c>(!(userAccountControl:1.2.840.113556.1.4.803:=2))</c>.</summary>
    public static string Enabled { get; } = LdapFilter.Not(LdapFilter.BitAnd(Attr.UserAccountControl, 2));
}
