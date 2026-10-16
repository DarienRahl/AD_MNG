namespace AdMng.Core.ActiveDirectory;

/// <summary>LDAP display names of attributes used by the application.</summary>
public static class Attr
{
    // Identity / naming
    public const string DistinguishedName = "distinguishedName";
    public const string Cn = "cn";
    public const string Name = "name";
    public const string Ou = "ou";
    public const string Dc = "dc";
    public const string ObjectClass = "objectClass";
    public const string ObjectCategory = "objectCategory";
    public const string ObjectGuid = "objectGUID";
    public const string ObjectSid = "objectSid";
    public const string SidHistory = "sIDHistory";
    public const string SamAccountName = "sAMAccountName";
    public const string SamAccountType = "sAMAccountType";
    public const string UserPrincipalName = "userPrincipalName";
    public const string CanonicalName = "canonicalName";
    public const string DisplayName = "displayName";
    public const string Description = "description";
    public const string WhenCreated = "whenCreated";
    public const string WhenChanged = "whenChanged";
    public const string UsnCreated = "uSNCreated";
    public const string UsnChanged = "uSNChanged";
    public const string NtSecurityDescriptor = "nTSecurityDescriptor";
    public const string ShowInAdvancedViewOnly = "showInAdvancedViewOnly";
    public const string IsCriticalSystemObject = "isCriticalSystemObject";
    public const string SystemFlags = "systemFlags";
    public const string AdminCount = "adminCount";
    public const string IsDeleted = "isDeleted";
    public const string IsRecycled = "isRecycled";
    public const string LastKnownParent = "lastKnownParent";
    public const string MsDsLastKnownRdn = "msDS-LastKnownRDN";

    /// <summary>Days a deleted object stays restorable (Recycle Bin), on CN=Directory Service,CN=Windows NT,CN=Services in Configuration.</summary>
    public const string MsDsDeletedObjectLifetime = "msDS-deletedObjectLifetime";

    /// <summary>Days a tombstone (or recycled object) is kept before garbage collection removes it.</summary>
    public const string TombstoneLifetime = "tombstoneLifetime";
    public const string GpLink = "gPLink";
    public const string ManagedBy = "managedBy";

    // Person / organization (ADUC General, Address, Telephones, Organization tabs)
    public const string GivenName = "givenName";
    public const string Initials = "initials";
    public const string Sn = "sn";
    public const string Office = "physicalDeliveryOfficeName";
    public const string TelephoneNumber = "telephoneNumber";
    public const string OtherTelephone = "otherTelephone";
    public const string Mail = "mail";
    public const string WwwHomePage = "wWWHomePage";
    public const string Url = "url";
    public const string StreetAddress = "streetAddress";
    public const string PostOfficeBox = "postOfficeBox";
    public const string City = "l";
    public const string State = "st";
    public const string PostalCode = "postalCode";
    public const string Country = "c";
    public const string CountryName = "co";
    public const string CountryCode = "countryCode";
    public const string HomePhone = "homePhone";
    public const string OtherHomePhone = "otherHomePhone";
    public const string Pager = "pager";
    public const string OtherPager = "otherPager";
    public const string Mobile = "mobile";
    public const string OtherMobile = "otherMobile";
    public const string FacsimileTelephoneNumber = "facsimileTelephoneNumber";
    public const string OtherFacsimileTelephoneNumber = "otherFacsimileTelephoneNumber";
    public const string IpPhone = "ipPhone";
    public const string OtherIpPhone = "otherIpPhone";
    public const string Info = "info";
    public const string Title = "title";
    public const string Department = "department";
    public const string Company = "company";
    public const string Manager = "manager";
    public const string DirectReports = "directReports";
    public const string EmployeeId = "employeeID";
    public const string EmployeeNumber = "employeeNumber";
    public const string EmployeeType = "employeeType";
    public const string ThumbnailPhoto = "thumbnailPhoto";

    // Account (ADUC Account / Profile tabs)
    public const string UserAccountControl = "userAccountControl";
    public const string UserAccountControlComputed = "msDS-User-Account-Control-Computed";
    public const string AccountExpires = "accountExpires";
    public const string PwdLastSet = "pwdLastSet";
    public const string PasswordExpiryComputed = "msDS-UserPasswordExpiryTimeComputed";
    public const string LockoutTime = "lockoutTime";
    public const string BadPwdCount = "badPwdCount";
    public const string BadPasswordTime = "badPasswordTime";
    public const string LastLogon = "lastLogon";
    public const string LastLogonTimestamp = "lastLogonTimestamp";
    public const string LogonCount = "logonCount";
    public const string LogonHours = "logonHours";
    public const string UserWorkstations = "userWorkstations";
    public const string UnicodePwd = "unicodePwd";
    public const string ProfilePath = "profilePath";
    public const string ScriptPath = "scriptPath";
    public const string HomeDirectory = "homeDirectory";
    public const string HomeDrive = "homeDrive";
    public const string PrimaryGroupId = "primaryGroupID";
    public const string MemberOf = "memberOf";
    public const string Member = "member";
    public const string GroupType = "groupType";
    public const string TokenGroups = "tokenGroups";
    public const string ServicePrincipalName = "servicePrincipalName";
    public const string SupportedEncryptionTypes = "msDS-SupportedEncryptionTypes";

    // Computer
    public const string DnsHostName = "dNSHostName";
    public const string OperatingSystem = "operatingSystem";
    public const string OperatingSystemVersion = "operatingSystemVersion";
    public const string OperatingSystemServicePack = "operatingSystemServicePack";
    public const string Location = "location";
    public const string AllowedToDelegateTo = "msDS-AllowedToDelegateTo";

    // Exchange / mail (base schema + Exchange schema extension when present)
    public const string ProxyAddresses = "proxyAddresses";
    public const string MailNickname = "mailNickname";
    public const string TargetAddress = "targetAddress";
    public const string HideFromAddressLists = "msExchHideFromAddressLists";
    public const string LegacyExchangeDn = "legacyExchangeDN";
    public const string ShowInAddressBook = "showInAddressBook";

    // Permission-aware UI (constructed attributes)
    public const string AllowedAttributesEffective = "allowedAttributesEffective";
    public const string AllowedChildClassesEffective = "allowedChildClassesEffective";
    public const string SdRightsEffective = "sDRightsEffective";
    public const string MsDsPrincipalName = "msDS-PrincipalName";

    // RootDSE
    public const string DefaultNamingContext = "defaultNamingContext";
    public const string ConfigurationNamingContext = "configurationNamingContext";
    public const string SchemaNamingContext = "schemaNamingContext";
    public const string RootDomainNamingContext = "rootDomainNamingContext";
    public const string NamingContexts = "namingContexts";
    public const string DnsHostNameRootDse = "dnsHostName";
    public const string ServerName = "serverName";
    public const string SupportedControl = "supportedControl";
    public const string SupportedCapabilities = "supportedCapabilities";
    public const string DomainFunctionality = "domainFunctionality";
    public const string ForestFunctionality = "forestFunctionality";
    public const string DomainControllerFunctionality = "domainControllerFunctionality";
    public const string IsGlobalCatalogReady = "isGlobalCatalogReady";
    public const string CurrentTime = "currentTime";

    /// <summary>
    /// Attributes holding FILETIME (100ns since 1601) large integers - shown as dates in the UI.
    /// </summary>
    public static readonly IReadOnlySet<string> FileTimeAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        AccountExpires, PwdLastSet, LockoutTime, BadPasswordTime, LastLogon, LastLogonTimestamp,
        PasswordExpiryComputed, "lastLogoff", "ms-Mcs-AdmPwdExpirationTime", "msLAPS-PasswordExpirationTime",
    };
}

/// <summary>LDAP display names of object classes.</summary>
public static class ObjectClasses
{
    public const string Top = "top";
    public const string User = "user";
    public const string InetOrgPerson = "inetOrgPerson";
    public const string Computer = "computer";
    public const string Group = "group";
    public const string Contact = "contact";
    public const string OrganizationalUnit = "organizationalUnit";
    public const string Container = "container";
    public const string BuiltinDomain = "builtinDomain";
    public const string DomainDns = "domainDNS";
    public const string LostAndFound = "lostAndFound";
    public const string ForeignSecurityPrincipal = "foreignSecurityPrincipal";
    public const string ManagedServiceAccount = "msDS-ManagedServiceAccount";
    public const string GroupManagedServiceAccount = "msDS-GroupManagedServiceAccount";
    public const string DelegatedManagedServiceAccount = "msDS-DelegatedManagedServiceAccount";
    public const string PrintQueue = "printQueue";
    public const string Volume = "volume";
    public const string InfrastructureUpdate = "infrastructureUpdate";
    public const string TpmInformationObjectsContainer = "msTPM-InformationObjectsContainer";
    public const string BitLockerRecoveryInformation = "msFVE-RecoveryInformation";
}

/// <summary>LDAP control OIDs (Microsoft extensions, [MS-ADTS] 3.1.1.3.4.1).</summary>
public static class LdapControls
{
    public const string PagedResults = "1.2.840.113556.1.4.319";
    public const string ServerSort = "1.2.840.113556.1.4.473";
    public const string VirtualListView = "2.16.840.1.113730.3.4.9";
    public const string ShowDeleted = "1.2.840.113556.1.4.417";
    public const string ShowRecycled = "1.2.840.113556.1.4.2064";
    public const string ShowDeactivatedLink = "1.2.840.113556.1.4.2065";
    public const string TreeDelete = "1.2.840.113556.1.4.805";
    public const string SdFlags = "1.2.840.113556.1.4.801";
    public const string Notification = "1.2.840.113556.1.4.528";
    public const string DirSync = "1.2.840.113556.1.4.841";
    public const string AttributeScopedQuery = "1.2.840.113556.1.4.1504";
    public const string PermissiveModify = "1.2.840.113556.1.4.1413";
    public const string LazyCommit = "1.2.840.113556.1.4.619";
    public const string DomainScope = "1.2.840.113556.1.4.1339";
    public const string ExtendedDn = "1.2.840.113556.1.4.529";
    public const string RangeRetrievalNoErr = "1.2.840.113556.1.4.1948";
}

/// <summary>Extended rights and property GUIDs used for permission checks and delegation.</summary>
public static class ExtendedRights
{
    public static readonly Guid ResetPassword = new("00299570-246d-11d0-a768-00aa006e0529");
    public static readonly Guid ChangePassword = new("ab721a53-1e2f-11d0-9819-00aa0040529b");
    public static readonly Guid SendAs = new("ab721a54-1e2f-11d0-9819-00aa0040529b");
    public static readonly Guid ReceiveAs = new("ab721a56-1e2f-11d0-9819-00aa0040529b");
    public static readonly Guid UserAccountRestrictionsPropertySet = new("4c164200-20c0-11d0-a768-00aa006e0529");
    public static readonly Guid PersonalInformationPropertySet = new("77b5b886-944a-11d1-aebd-0000f80367c1");
    public static readonly Guid GeneralInformationPropertySet = new("59ba2f42-79a2-11d0-9020-00c04fc2d3cf");
    public static readonly Guid PublicInformationPropertySet = new("e48d0154-bcf8-11d1-8702-00c04fb96050");
}

/// <summary>sDRightsEffective bits ([MS-ADTS] 3.1.1.4.5.4).</summary>
[Flags]
public enum SecurityDescriptorRights
{
    None = 0,
    Owner = 0x1,
    Group = 0x2,
    Dacl = 0x4,
    Sacl = 0x8,
}

/// <summary>Well-known RIDs and SIDs that deserve special care in the UI (Tier 0 protection).</summary>
public static class WellKnownRids
{
    public const uint Administrator = 500;
    public const uint Guest = 501;
    public const uint Krbtgt = 502;
    public const uint DomainAdmins = 512;
    public const uint DomainUsers = 513;
    public const uint DomainGuests = 514;
    public const uint DomainComputers = 515;
    public const uint DomainControllers = 516;
    public const uint CertPublishers = 517;
    public const uint SchemaAdmins = 518;
    public const uint EnterpriseAdmins = 519;
    public const uint GroupPolicyCreatorOwners = 520;
    public const uint ReadOnlyDomainControllers = 521;
    public const uint ProtectedUsers = 525;
    public const uint KeyAdmins = 526;
    public const uint EnterpriseKeyAdmins = 527;
}
