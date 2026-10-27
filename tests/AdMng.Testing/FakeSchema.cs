using AdMng.Core.Schema;

namespace AdMng.Testing;

/// <summary>
/// A compact but realistic Active Directory schema: the common attributes of users, computers, groups, contacts and
/// OUs with their real syntaxes, single-valued flags, ranges, link IDs and system-only / constructed flags.
/// </summary>
public static partial class FakeSchema
{
    public const string DefaultSchemaNamingContext = "CN=Schema,CN=Configuration,DC=contoso,DC=test";

    public static SchemaCatalog Create(string schemaNamingContext = DefaultSchemaNamingContext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaNamingContext);
        var attributes = CreateAttributes();
        attributes.AddRange(CreateWatchdogAttributes());
        attributes.AddRange(CreateGroupPolicyAttributes());
        var classes = CreateClasses(schemaNamingContext);
        ExtendForWatchdog(classes, schemaNamingContext);
        ExtendForGroupPolicy(classes, schemaNamingContext);
        return new SchemaCatalog(attributes, classes, CreateDisplayNames());
    }

    private static List<AttributeDefinition> CreateAttributes() =>
    [
        // Naming and identity
        Def("objectClass", AttributeSyntax.ObjectIdentifier, single: false),
        Def("objectCategory", AttributeSyntax.DistinguishedName),
        Def("cn", AttributeSyntax.UnicodeString, lower: 1, upper: 64),
        Def("name", AttributeSyntax.UnicodeString, lower: 1, upper: 255, systemOnly: true),
        Def("distinguishedName", AttributeSyntax.DistinguishedName, systemOnly: true),
        Def("ou", AttributeSyntax.UnicodeString, lower: 1, upper: 64),
        Def("dc", AttributeSyntax.UnicodeString, lower: 1, upper: 255),
        Def("objectGUID", AttributeSyntax.OctetString, lower: 16, upper: 16, systemOnly: true),
        Def("objectSid", AttributeSyntax.Sid, lower: 0, upper: 28, systemOnly: true),
        Def("sIDHistory", AttributeSyntax.Sid, single: false, systemOnly: true),
        Def("sAMAccountName", AttributeSyntax.UnicodeString, lower: 0, upper: 256),
        Def("sAMAccountType", AttributeSyntax.Integer, systemOnly: true),
        Def("userPrincipalName", AttributeSyntax.UnicodeString, lower: 0, upper: 1024),
        Def("displayName", AttributeSyntax.UnicodeString, lower: 0, upper: 256),
        Def("description", AttributeSyntax.UnicodeString, single: false, lower: 0, upper: 1024),
        Def("whenCreated", AttributeSyntax.GeneralizedTime, systemOnly: true),
        Def("whenChanged", AttributeSyntax.GeneralizedTime, systemOnly: true),
        Def("uSNCreated", AttributeSyntax.LargeInteger, systemOnly: true),
        Def("uSNChanged", AttributeSyntax.LargeInteger, systemOnly: true),
        Def("instanceType", AttributeSyntax.Integer, systemOnly: true),
        Def("systemFlags", AttributeSyntax.Integer, systemOnly: true),
        Def("isCriticalSystemObject", AttributeSyntax.Boolean, systemOnly: true),
        Def("showInAdvancedViewOnly", AttributeSyntax.Boolean),
        Def("adminCount", AttributeSyntax.Integer),
        Def("nTSecurityDescriptor", AttributeSyntax.SecurityDescriptor, lower: 0, upper: 132096),
        Def("isDeleted", AttributeSyntax.Boolean, systemOnly: true),
        Def("lastKnownParent", AttributeSyntax.DistinguishedName, systemOnly: true),
        Def("msDS-LastKnownRDN", AttributeSyntax.UnicodeString, lower: 1, upper: 255, systemOnly: true),
        Def("gPLink", AttributeSyntax.UnicodeString),

        // Person / organization
        Def("givenName", AttributeSyntax.UnicodeString, lower: 1, upper: 64),
        Def("initials", AttributeSyntax.UnicodeString, lower: 1, upper: 6),
        Def("sn", AttributeSyntax.UnicodeString, lower: 1, upper: 64),
        Def("title", AttributeSyntax.UnicodeString, lower: 1, upper: 128),
        Def("department", AttributeSyntax.UnicodeString, lower: 1, upper: 64),
        Def("company", AttributeSyntax.UnicodeString, lower: 1, upper: 64),
        Def("physicalDeliveryOfficeName", AttributeSyntax.UnicodeString, lower: 1, upper: 128),
        Def("telephoneNumber", AttributeSyntax.UnicodeString, lower: 1, upper: 64),
        Def("otherTelephone", AttributeSyntax.UnicodeString, single: false, lower: 1, upper: 64),
        Def("mobile", AttributeSyntax.UnicodeString, lower: 1, upper: 64),
        Def("homePhone", AttributeSyntax.UnicodeString, lower: 1, upper: 64),
        Def("facsimileTelephoneNumber", AttributeSyntax.UnicodeString, lower: 1, upper: 64),
        Def("mail", AttributeSyntax.UnicodeString, lower: 0, upper: 256),
        Def("wWWHomePage", AttributeSyntax.UnicodeString, lower: 1, upper: 2048),
        Def("streetAddress", AttributeSyntax.UnicodeString, lower: 1, upper: 1024),
        Def("postOfficeBox", AttributeSyntax.UnicodeString, single: false, lower: 1, upper: 40),
        Def("l", AttributeSyntax.UnicodeString, lower: 1, upper: 128),
        Def("st", AttributeSyntax.UnicodeString, lower: 1, upper: 128),
        Def("postalCode", AttributeSyntax.UnicodeString, lower: 1, upper: 40),
        Def("c", AttributeSyntax.UnicodeString, lower: 1, upper: 3),
        Def("co", AttributeSyntax.UnicodeString, lower: 1, upper: 128),
        Def("countryCode", AttributeSyntax.Integer, lower: 0, upper: 65535),
        Def("info", AttributeSyntax.UnicodeString, lower: 1, upper: 1024),
        Def("employeeID", AttributeSyntax.UnicodeString, lower: 0, upper: 16),
        Def("employeeNumber", AttributeSyntax.UnicodeString, lower: 1, upper: 512),
        Def("manager", AttributeSyntax.DistinguishedName, linkId: 42),
        Def("directReports", AttributeSyntax.DistinguishedName, single: false, linkId: 43, systemOnly: true),
        Def("thumbnailPhoto", AttributeSyntax.OctetString, lower: 0, upper: 102400),
        Def("proxyAddresses", AttributeSyntax.UnicodeString, single: false, lower: 1, upper: 1123),
        Def("msExchHideFromAddressLists", AttributeSyntax.Boolean),

        // Account
        Def("userAccountControl", AttributeSyntax.Integer),
        Def("accountExpires", AttributeSyntax.LargeInteger),
        Def("pwdLastSet", AttributeSyntax.LargeInteger),
        Def("lockoutTime", AttributeSyntax.LargeInteger),
        Def("badPwdCount", AttributeSyntax.Integer, systemOnly: true),
        Def("lastLogon", AttributeSyntax.LargeInteger, systemOnly: true),
        Def("lastLogonTimestamp", AttributeSyntax.LargeInteger, systemOnly: true),
        Def("logonCount", AttributeSyntax.Integer, systemOnly: true),
        Def("unicodePwd", AttributeSyntax.OctetString),
        Def("homeDirectory", AttributeSyntax.UnicodeString, lower: 0, upper: 260),
        Def("homeDrive", AttributeSyntax.UnicodeString, lower: 0, upper: 3),
        Def("scriptPath", AttributeSyntax.UnicodeString, lower: 0, upper: 260),
        Def("profilePath", AttributeSyntax.UnicodeString),
        Def("primaryGroupID", AttributeSyntax.Integer),
        Def("servicePrincipalName", AttributeSyntax.UnicodeString, single: false),
        Def("msDS-SupportedEncryptionTypes", AttributeSyntax.Integer),
        Def("userWorkstations", AttributeSyntax.UnicodeString, lower: 0, upper: 1024),

        // Groups
        Def("member", AttributeSyntax.DistinguishedName, single: false, linkId: 2),
        Def("memberOf", AttributeSyntax.DistinguishedName, single: false, linkId: 3, systemOnly: true),
        Def("groupType", AttributeSyntax.Integer),
        Def("managedBy", AttributeSyntax.DistinguishedName, linkId: 72),

        // Computer
        Def("dNSHostName", AttributeSyntax.UnicodeString, lower: 0, upper: 2048),
        Def("operatingSystem", AttributeSyntax.UnicodeString, lower: 0, upper: 256),
        Def("operatingSystemVersion", AttributeSyntax.UnicodeString, lower: 0, upper: 64),
        Def("location", AttributeSyntax.UnicodeString, lower: 0, upper: 1024),

        // Constructed
        Def("canonicalName", AttributeSyntax.UnicodeString, constructed: true),
        Def("allowedAttributesEffective", AttributeSyntax.ObjectIdentifier, single: false, constructed: true),
        Def("msDS-User-Account-Control-Computed", AttributeSyntax.Integer, constructed: true),
        Def("primaryGroupToken", AttributeSyntax.Integer, constructed: true),
    ];

    private static List<ClassDefinition> CreateClasses(string schemaNc) =>
    [
        new()
        {
            LdapName = "top",
            Category = ClassCategory.Abstract,
            MustContain = ["objectClass"],
            MayContain =
            [
                "cn", "name", "distinguishedName", "objectGUID", "objectCategory", "description", "displayName",
                "whenCreated", "whenChanged", "uSNCreated", "uSNChanged", "instanceType", "systemFlags",
                "isCriticalSystemObject", "showInAdvancedViewOnly", "adminCount", "nTSecurityDescriptor", "isDeleted",
                "lastKnownParent", "msDS-LastKnownRDN", "memberOf", "directReports", "canonicalName",
                "allowedAttributesEffective",
            ],
        },
        new()
        {
            LdapName = "person",
            SubClassOf = "top",
            Category = ClassCategory.Structural,
            MustContain = ["cn"],
            MayContain = ["sn", "telephoneNumber"],
            DefaultObjectCategory = $"CN=Person,{schemaNc}",
        },
        new()
        {
            LdapName = "organizationalPerson",
            SubClassOf = "person",
            Category = ClassCategory.Structural,
            MayContain =
            [
                "givenName", "initials", "title", "department", "company", "physicalDeliveryOfficeName", "otherTelephone",
                "mobile", "homePhone", "facsimileTelephoneNumber", "wWWHomePage", "streetAddress", "postOfficeBox", "l",
                "st", "postalCode", "c", "co", "countryCode", "manager", "thumbnailPhoto", "employeeNumber",
            ],
            DefaultObjectCategory = $"CN=Person,{schemaNc}",
        },
        new()
        {
            LdapName = "user",
            SubClassOf = "organizationalPerson",
            Category = ClassCategory.Structural,
            AuxiliaryClasses = ["securityPrincipal", "mailRecipient"],
            MayContain =
            [
                "userAccountControl", "accountExpires", "pwdLastSet", "lockoutTime", "badPwdCount", "lastLogon",
                "lastLogonTimestamp", "logonCount", "userPrincipalName", "unicodePwd", "homeDirectory", "homeDrive",
                "scriptPath", "profilePath", "primaryGroupID", "employeeID", "servicePrincipalName",
                "msDS-SupportedEncryptionTypes", "msDS-User-Account-Control-Computed", "userWorkstations",
            ],
            PossibleSuperiors = ["organizationalUnit", "container", "domainDNS"],
            DefaultObjectCategory = $"CN=Person,{schemaNc}",
        },
        new()
        {
            LdapName = "inetOrgPerson",
            SubClassOf = "user",
            Category = ClassCategory.Structural,
            PossibleSuperiors = ["organizationalUnit", "container", "domainDNS"],
            DefaultObjectCategory = $"CN=Person,{schemaNc}",
        },
        new()
        {
            LdapName = "computer",
            SubClassOf = "user",
            Category = ClassCategory.Structural,
            MayContain = ["dNSHostName", "operatingSystem", "operatingSystemVersion", "location", "managedBy"],
            PossibleSuperiors = ["organizationalUnit", "container", "domainDNS"],
            DefaultObjectCategory = $"CN=Computer,{schemaNc}",
        },
        new()
        {
            LdapName = "group",
            SubClassOf = "top",
            Category = ClassCategory.Structural,
            AuxiliaryClasses = ["securityPrincipal", "mailRecipient"],
            MustContain = ["groupType"],
            MayContain = ["member", "managedBy", "primaryGroupToken"],
            PossibleSuperiors = ["organizationalUnit", "container", "domainDNS", "builtinDomain"],
            DefaultObjectCategory = $"CN=Group,{schemaNc}",
        },
        new()
        {
            LdapName = "contact",
            SubClassOf = "organizationalPerson",
            Category = ClassCategory.Structural,
            AuxiliaryClasses = ["mailRecipient"],
            PossibleSuperiors = ["organizationalUnit", "container", "domainDNS"],
            DefaultObjectCategory = $"CN=Person,{schemaNc}",
        },
        new()
        {
            LdapName = "securityPrincipal",
            Category = ClassCategory.Auxiliary,
            MustContain = ["objectSid", "sAMAccountName"],
            MayContain = ["sAMAccountType", "sIDHistory"],
        },
        new()
        {
            LdapName = "mailRecipient",
            Category = ClassCategory.Auxiliary,
            MustContain = ["cn"],
            MayContain = ["mail", "proxyAddresses", "msExchHideFromAddressLists", "info", "telephoneNumber"],
        },
        new()
        {
            LdapName = "organizationalUnit",
            SubClassOf = "top",
            Category = ClassCategory.Structural,
            MustContain = ["ou"],
            MayContain = ["l", "st", "streetAddress", "postalCode", "c", "co", "countryCode", "gPLink", "managedBy"],
            PossibleSuperiors = ["organizationalUnit", "domainDNS"],
            DefaultObjectCategory = $"CN=Organizational-Unit,{schemaNc}",
        },
        new()
        {
            LdapName = "container",
            SubClassOf = "top",
            Category = ClassCategory.Structural,
            MustContain = ["cn"],
            PossibleSuperiors = ["container", "domainDNS", "organizationalUnit"],
            DefaultObjectCategory = $"CN=Container,{schemaNc}",
        },
        new()
        {
            LdapName = "domain",
            SubClassOf = "top",
            Category = ClassCategory.Abstract,
            MustContain = ["dc"],
        },
        new()
        {
            LdapName = "domainDNS",
            SubClassOf = "domain",
            Category = ClassCategory.Structural,
            MayContain = ["gPLink"],
            DefaultObjectCategory = $"CN=Domain-DNS,{schemaNc}",
        },
        new()
        {
            LdapName = "builtinDomain",
            SubClassOf = "top",
            Category = ClassCategory.Structural,
            MustContain = ["cn"],
            DefaultObjectCategory = $"CN=Builtin-Domain,{schemaNc}",
        },
        new()
        {
            LdapName = "lostAndFound",
            SubClassOf = "top",
            Category = ClassCategory.Structural,
            MustContain = ["cn"],
            DefaultObjectCategory = $"CN=Lost-And-Found,{schemaNc}",
        },
    ];

    private static Dictionary<string, IReadOnlyDictionary<string, string>> CreateDisplayNames() => new()
    {
        ["en"] = new Dictionary<string, string>
        {
            ["givenName"] = "First Name", ["sn"] = "Last Name", ["displayName"] = "Display Name", ["department"] = "Department",
            ["title"] = "Job Title", ["company"] = "Company", ["l"] = "City", ["mail"] = "E-Mail Address",
            ["telephoneNumber"] = "Telephone Number", ["description"] = "Description", ["sAMAccountName"] = "Logon Name (pre-Windows 2000)",
        },
        ["pl"] = new Dictionary<string, string>
        {
            ["givenName"] = "Imię", ["sn"] = "Nazwisko", ["displayName"] = "Nazwa wyświetlana", ["department"] = "Dział",
            ["title"] = "Stanowisko", ["company"] = "Firma", ["l"] = "Miejscowość", ["mail"] = "Adres e-mail",
            ["telephoneNumber"] = "Numer telefonu", ["description"] = "Opis", ["sAMAccountName"] = "Nazwa logowania (starsza niż Windows 2000)",
        },
    };

    private static AttributeDefinition Def(
        string name,
        AttributeSyntax syntax,
        bool single = true,
        long? lower = null,
        long? upper = null,
        bool systemOnly = false,
        bool constructed = false,
        int? linkId = null) => new()
        {
            LdapName = name,
            Cn = name,
            Syntax = syntax,
            IsSingleValued = single,
            RangeLower = lower,
            RangeUpper = upper,
            IsSystemOnly = systemOnly,
            IsConstructed = constructed,
            LinkId = linkId,
            IsIndexed = name is "sAMAccountName" or "cn" or "name" or "objectGUID" or "objectSid" or "mail" or "userPrincipalName"
                or "displayName" or "givenName" or "sn" or "objectClass" or "objectCategory",
            SchemaIdGuid = DeterministicGuid(name),
        };

    private static Guid DeterministicGuid(string name)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(name));
        return new Guid(bytes);
    }
}
