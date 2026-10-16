using AdMng.Core.ActiveDirectory;

namespace AdMng.Core.Model;

/// <summary>Application level classification of directory objects (drives icons, columns, property pages).</summary>
public enum ObjectKind
{
    Unknown,
    Domain,
    OrganizationalUnit,
    Container,
    BuiltinDomain,
    User,
    InetOrgPerson,
    Computer,
    Group,
    Contact,
    ForeignSecurityPrincipal,
    ManagedServiceAccount,
    GroupManagedServiceAccount,
    PrintQueue,
    SharedFolder,
    LostAndFound,
    BitLockerRecovery,
}

/// <summary>Maps objectClass values to <see cref="ObjectKind"/>.</summary>
public static class ObjectKinds
{
    /// <summary>
    /// Resolves the most specific kind from the objectClass hierarchy as returned by AD
    /// (e.g. <c>top, person, organizationalPerson, user, computer</c>).
    /// </summary>
    public static ObjectKind FromObjectClasses(IEnumerable<string>? objectClasses)
    {
        if (objectClasses is null)
        {
            return ObjectKind.Unknown;
        }

        var set = new HashSet<string>(objectClasses, StringComparer.OrdinalIgnoreCase);
        // Order matters: most derived classes first.
        if (set.Contains(ObjectClasses.GroupManagedServiceAccount) || set.Contains(ObjectClasses.DelegatedManagedServiceAccount))
        {
            return ObjectKind.GroupManagedServiceAccount;
        }

        if (set.Contains(ObjectClasses.ManagedServiceAccount))
        {
            return ObjectKind.ManagedServiceAccount;
        }

        if (set.Contains(ObjectClasses.Computer))
        {
            return ObjectKind.Computer;
        }

        if (set.Contains(ObjectClasses.InetOrgPerson))
        {
            return ObjectKind.InetOrgPerson;
        }

        if (set.Contains(ObjectClasses.User))
        {
            return ObjectKind.User;
        }

        if (set.Contains(ObjectClasses.Group))
        {
            return ObjectKind.Group;
        }

        if (set.Contains(ObjectClasses.Contact))
        {
            return ObjectKind.Contact;
        }

        if (set.Contains(ObjectClasses.OrganizationalUnit))
        {
            return ObjectKind.OrganizationalUnit;
        }

        if (set.Contains(ObjectClasses.DomainDns))
        {
            return ObjectKind.Domain;
        }

        if (set.Contains(ObjectClasses.BuiltinDomain))
        {
            return ObjectKind.BuiltinDomain;
        }

        if (set.Contains(ObjectClasses.LostAndFound))
        {
            return ObjectKind.LostAndFound;
        }

        if (set.Contains(ObjectClasses.ForeignSecurityPrincipal))
        {
            return ObjectKind.ForeignSecurityPrincipal;
        }

        if (set.Contains(ObjectClasses.PrintQueue))
        {
            return ObjectKind.PrintQueue;
        }

        if (set.Contains(ObjectClasses.Volume))
        {
            return ObjectKind.SharedFolder;
        }

        if (set.Contains(ObjectClasses.BitLockerRecoveryInformation))
        {
            return ObjectKind.BitLockerRecovery;
        }

        if (set.Contains(ObjectClasses.Container) || set.Contains(ObjectClasses.TpmInformationObjectsContainer))
        {
            return ObjectKind.Container;
        }

        return ObjectKind.Unknown;
    }

    /// <summary>True when objects of this kind are shown as nodes in the navigation tree.</summary>
    public static bool IsContainer(this ObjectKind kind) => kind is ObjectKind.Domain or ObjectKind.OrganizationalUnit
        or ObjectKind.Container or ObjectKind.BuiltinDomain or ObjectKind.LostAndFound;

    /// <summary>True for security principals that have a sAMAccountName and can be group members.</summary>
    public static bool IsSecurityPrincipal(this ObjectKind kind) => kind is ObjectKind.User or ObjectKind.InetOrgPerson
        or ObjectKind.Computer or ObjectKind.Group or ObjectKind.ManagedServiceAccount
        or ObjectKind.GroupManagedServiceAccount or ObjectKind.ForeignSecurityPrincipal;

    /// <summary>True for account objects with userAccountControl (enable/disable, unlock, reset password).</summary>
    public static bool IsAccount(this ObjectKind kind) => kind is ObjectKind.User or ObjectKind.InetOrgPerson
        or ObjectKind.Computer or ObjectKind.ManagedServiceAccount or ObjectKind.GroupManagedServiceAccount;

    /// <summary>
    /// The longest sAMAccountName the domain controller accepts for objects of this kind: 256 (the schema's rangeUpper)
    /// for groups, the SAM limit of 20 for user and computer accounts.
    /// </summary>
    public static int MaxSamAccountNameLength(this ObjectKind kind) => kind == ObjectKind.Group ? 256 : 20;

    /// <summary>
    /// LDAP filter selecting objects of the given kind using indexed attributes where possible
    /// (objectCategory / sAMAccountType) - important for performance on large directories.
    /// </summary>
    public static string ToLdapFilter(this ObjectKind kind) => kind switch
    {
        ObjectKind.User => "(sAMAccountType=805306368)",
        ObjectKind.InetOrgPerson => "(objectClass=inetOrgPerson)",
        ObjectKind.Computer => "(sAMAccountType=805306369)",
        ObjectKind.Group => "(objectCategory=group)",
        ObjectKind.Contact => "(&(objectCategory=person)(objectClass=contact))",
        ObjectKind.OrganizationalUnit => "(objectCategory=organizationalUnit)",
        ObjectKind.Container => "(objectCategory=container)",
        ObjectKind.BuiltinDomain => "(objectCategory=builtinDomain)",
        ObjectKind.Domain => "(objectCategory=domainDNS)",
        ObjectKind.ForeignSecurityPrincipal => "(objectCategory=foreignSecurityPrincipal)",
        ObjectKind.ManagedServiceAccount => "(objectCategory=msDS-ManagedServiceAccount)",
        ObjectKind.GroupManagedServiceAccount => "(objectCategory=msDS-GroupManagedServiceAccount)",
        ObjectKind.PrintQueue => "(objectCategory=printQueue)",
        ObjectKind.SharedFolder => "(objectCategory=volume)",
        ObjectKind.LostAndFound => "(objectClass=lostAndFound)",
        ObjectKind.BitLockerRecovery => "(objectClass=msFVE-RecoveryInformation)",
        _ => "(objectClass=*)",
    };

    /// <summary>Filter matching every container-like class shown in the navigation tree.</summary>
    public const string ContainerFilter =
        "(|(objectClass=organizationalUnit)(objectClass=container)(objectClass=builtinDomain)" +
        "(objectClass=lostAndFound)(objectClass=domainDNS)(objectClass=msTPM-InformationObjectsContainer))";
}
