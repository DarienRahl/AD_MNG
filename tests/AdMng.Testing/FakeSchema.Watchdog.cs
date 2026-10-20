using AdMng.Core.Schema;

namespace AdMng.Testing;

/// <summary>
/// Schema additions for the AdMng Watchdog emulation (watchdog specification 4.3): the attributes the watchdog diffs
/// with their real syntaxes and the classes of the configuration, schema and PKI objects, plus the Windows Server 2025
/// schema of delegated managed service accounts (dMSA, BadSuccessor – stage B design 9.1): the migration links with
/// their back links, the managed password attributes and the possible superiors a dMSA inherits along
/// <c>computer → user → organizationalPerson → person → top</c>. Adding definitions only relaxes the fake (more
/// attributes are known and allowed), so existing tests keep their behaviour.
/// </summary>
public static partial class FakeSchema
{
    /// <summary>Extra attributes allowed per class (merged into the base class definitions).</summary>
    private static readonly Dictionary<string, string[]> WatchdogMayContain = new(StringComparer.OrdinalIgnoreCase)
    {
        ["top"] =
        [
            "msDS-ReplAttributeMetaData", "replPropertyMetaData", "fSMORoleOwner", "wellKnownObjects", "otherWellKnownObjects",
            "isRecycled", "flags",
        ],
        ["user"] =
        [
            "msDS-AllowedToDelegateTo", "msDS-AllowedToActOnBehalfOfOtherIdentity", "msDS-KeyCredentialLink",
            "userCertificate", "altSecurityIdentities", "logonHours", "employeeType", "badPasswordTime",
            "msDS-SupersededManagedAccountLink", "msDS-SupersededServiceAccountState", "msDS-ResultantPSO",
            "msDS-UserPasswordExpiryTimeComputed", "msDS-KeyVersionNumber", "msDS-ManagedAccountPrecededByLinkBL",
            "msDS-SupersededManagedAccountLinkBL",
        ],
        ["computer"] = ["mS-DS-CreatorSID"],
        ["organizationalUnit"] = ["gPOptions"],
        ["domainDNS"] =
        [
            "gPOptions", "lockoutThreshold", "lockoutDuration", "lockOutObservationWindow", "maxPwdAge", "minPwdAge",
            "minPwdLength", "pwdHistoryLength", "pwdProperties", "ms-DS-MachineAccountQuota", "msDS-Behavior-Version",
        ],
    };

    private static List<AttributeDefinition> CreateWatchdogAttributes() =>
    [
        // Accounts: delegation, keys, certificates, logon restrictions
        Def("msDS-AllowedToDelegateTo", AttributeSyntax.UnicodeString, single: false),
        Def("msDS-AllowedToActOnBehalfOfOtherIdentity", AttributeSyntax.SecurityDescriptor),
        Def("msDS-KeyCredentialLink", AttributeSyntax.DnWithBinary, single: false),
        Def("userCertificate", AttributeSyntax.OctetString, single: false),
        Def("altSecurityIdentities", AttributeSyntax.UnicodeString, single: false),
        Def("logonHours", AttributeSyntax.OctetString),
        Def("employeeType", AttributeSyntax.UnicodeString, lower: 1, upper: 256),
        Def("mS-DS-CreatorSID", AttributeSyntax.Sid, systemOnly: true),
        Def("badPasswordTime", AttributeSyntax.LargeInteger, systemOnly: true),
        Def("isRecycled", AttributeSyntax.Boolean, systemOnly: true),

        // Domain and password policy
        Def("gPOptions", AttributeSyntax.Integer),
        Def("lockoutDuration", AttributeSyntax.LargeInteger),
        Def("lockOutObservationWindow", AttributeSyntax.LargeInteger),
        Def("maxPwdAge", AttributeSyntax.LargeInteger),
        Def("minPwdAge", AttributeSyntax.LargeInteger),
        Def("minPwdLength", AttributeSyntax.Integer),
        Def("pwdHistoryLength", AttributeSyntax.Integer),
        Def("pwdProperties", AttributeSyntax.Integer),
        Def("lockoutThreshold", AttributeSyntax.Integer),
        Def("ms-DS-MachineAccountQuota", AttributeSyntax.Integer),
        Def("msDS-Behavior-Version", AttributeSyntax.Integer),

        // Group policy and trusts
        Def("versionNumber", AttributeSyntax.Integer),
        Def("flags", AttributeSyntax.Integer),
        Def("gPCFileSysPath", AttributeSyntax.UnicodeString),
        Def("trustDirection", AttributeSyntax.Integer),
        Def("trustType", AttributeSyntax.Integer),
        Def("trustAttributes", AttributeSyntax.Integer),
        Def("flatName", AttributeSyntax.UnicodeString),
        Def("trustPartner", AttributeSyntax.UnicodeString),

        // Fine-grained password policies
        Def("msDS-PasswordSettingsPrecedence", AttributeSyntax.Integer),
        Def("msDS-MinimumPasswordLength", AttributeSyntax.Integer),
        Def("msDS-PasswordHistoryLength", AttributeSyntax.Integer),
        Def("msDS-PasswordComplexityEnabled", AttributeSyntax.Boolean),
        Def("msDS-PasswordReversibleEncryptionEnabled", AttributeSyntax.Boolean),
        Def("msDS-MinimumPasswordAge", AttributeSyntax.LargeInteger),
        Def("msDS-MaximumPasswordAge", AttributeSyntax.LargeInteger),
        Def("msDS-LockoutThreshold", AttributeSyntax.Integer),
        Def("msDS-LockoutObservationWindow", AttributeSyntax.LargeInteger),
        Def("msDS-LockoutDuration", AttributeSyntax.LargeInteger),
        Def("msDS-PSOAppliesTo", AttributeSyntax.DistinguishedName, single: false, linkId: 2118),
        Def("msDS-ResultantPSO", AttributeSyntax.DistinguishedName, constructed: true),

        // Domain controllers and forest singletons
        Def("invocationId", AttributeSyntax.OctetString, lower: 16, upper: 16, systemOnly: true),
        Def("fSMORoleOwner", AttributeSyntax.DistinguishedName),
        Def("msDS-HasDomainNCs", AttributeSyntax.DistinguishedName, single: false, systemOnly: true),
        Def("wellKnownObjects", AttributeSyntax.DnWithBinary, single: false, systemOnly: true),
        Def("otherWellKnownObjects", AttributeSyntax.DnWithBinary, single: false),
        Def("msDS-EnabledFeature", AttributeSyntax.DistinguishedName, single: false, systemOnly: true),
        Def("dSHeuristics", AttributeSyntax.UnicodeString),
        Def("tombstoneLifetime", AttributeSyntax.Integer),
        Def("msDS-deletedObjectLifetime", AttributeSyntax.Integer),

        // Constructed
        Def("msDS-UserPasswordExpiryTimeComputed", AttributeSyntax.LargeInteger, constructed: true),
        Def("msDS-ReplAttributeMetaData", AttributeSyntax.UnicodeString, single: false, constructed: true),
        Def("replPropertyMetaData", AttributeSyntax.OctetString, systemOnly: true),

        // Managed service accounts (gMSA / dMSA, Windows Server 2025 schema): msDS-DelegatedMSAState and
        // msDS-SupersededServiceAccountState are 0 unknown, 1 migration in progress, 2 migration completed, 3 standalone.
        Def("msDS-GroupMSAMembership", AttributeSyntax.SecurityDescriptor),
        Def("msDS-ManagedAccountPrecededByLink", AttributeSyntax.DistinguishedName, linkId: 2210),
        Def("msDS-ManagedAccountPrecededByLinkBL", AttributeSyntax.DistinguishedName, single: false, linkId: 2211),
        Def("msDS-DelegatedMSAState", AttributeSyntax.Integer),
        Def("msDS-SupersededManagedAccountLink", AttributeSyntax.DistinguishedName, linkId: 2212),
        Def("msDS-SupersededManagedAccountLinkBL", AttributeSyntax.DistinguishedName, single: false, linkId: 2213),
        Def("msDS-SupersededServiceAccountState", AttributeSyntax.Integer),
        Def("msDS-ManagedPasswordInterval", AttributeSyntax.Integer),
        Def("msDS-ManagedPasswordId", AttributeSyntax.OctetString, systemOnly: true),
        Def("msDS-ManagedPasswordPreviousId", AttributeSyntax.OctetString, systemOnly: true),
        Def("msDS-ManagedPassword", AttributeSyntax.OctetString, constructed: true),
        Def("msDS-KeyVersionNumber", AttributeSyntax.Integer, constructed: true),

        // PKI
        Def("msPKI-Certificate-Name-Flag", AttributeSyntax.Integer),
        Def("msPKI-Enrollment-Flag", AttributeSyntax.Integer),
        Def("pKIExtendedKeyUsage", AttributeSyntax.UnicodeString, single: false),
        Def("msPKI-Certificate-Application-Policy", AttributeSyntax.UnicodeString, single: false),
        Def("msPKI-RA-Signature", AttributeSyntax.Integer),
        Def("certificateTemplates", AttributeSyntax.UnicodeString, single: false),
        Def("cACertificate", AttributeSyntax.OctetString, single: false),

        // Schema objects
        Def("lDAPDisplayName", AttributeSyntax.UnicodeString),
        Def("attributeID", AttributeSyntax.ObjectIdentifier),
        Def("msDS-IntId", AttributeSyntax.Integer, systemOnly: true),
        Def("dNSTombstoned", AttributeSyntax.Boolean),
    ];

    /// <summary>The managed password attributes of gMSA and dMSA.</summary>
    private static readonly string[] ManagedPasswordAttributes =
        ["msDS-ManagedPasswordInterval", "msDS-ManagedPasswordId", "msDS-ManagedPasswordPreviousId", "msDS-ManagedPassword"];

    /// <summary>
    /// possSuperiors + systemPossSuperiors of the real schema that the base fake leaves out. A dMSA defines none itself:
    /// its effective parents are inherited – domainDNS, organizationalUnit, container, builtinDomain (user),
    /// organization (organizationalPerson) and lostAndFound (top) – which the BadSuccessor audit of the instructions
    /// computes per forest (design 9.1).
    /// </summary>
    private static readonly Dictionary<string, string[]> WatchdogPossibleSuperiors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["top"] = ["lostAndFound"],
        ["organizationalPerson"] = ["organization", "container", "organizationalUnit"],
        ["user"] = ["builtinDomain"],
    };

    private static void ExtendForWatchdog(List<ClassDefinition> classes, string schemaNc)
    {
        for (var i = 0; i < classes.Count; i++)
        {
            if (WatchdogMayContain.TryGetValue(classes[i].LdapName, out var extra))
            {
                classes[i] = classes[i] with { MayContain = [.. classes[i].MayContain, .. extra] };
            }

            if (WatchdogPossibleSuperiors.TryGetValue(classes[i].LdapName, out var superiors))
            {
                classes[i] = classes[i] with { PossibleSuperiors = [.. classes[i].PossibleSuperiors.Concat(superiors).Distinct(StringComparer.OrdinalIgnoreCase)] };
            }
        }

        classes.AddRange(
        [
            Class("msDS-ManagedServiceAccount", "computer", schemaNc, "ms-DS-Managed-Service-Account", ["msDS-GroupMSAMembership"]),
            Class("msDS-GroupManagedServiceAccount", "computer", schemaNc, "ms-DS-Group-Managed-Service-Account",
                ["msDS-GroupMSAMembership", .. ManagedPasswordAttributes]),
            Class("msDS-DelegatedManagedServiceAccount", "computer", schemaNc, "ms-DS-Delegated-Managed-Service-Account",
                ["msDS-GroupMSAMembership", "msDS-ManagedAccountPrecededByLink", "msDS-DelegatedMSAState", .. ManagedPasswordAttributes]),
            Class("groupPolicyContainer", "container", schemaNc, "Group-Policy-Container", ["versionNumber", "gPCFileSysPath", "flags", "gPOptions"]),
            Class("trustedDomain", "top", schemaNc, "Trusted-Domain", ["trustDirection", "trustType", "trustAttributes", "flatName", "trustPartner"]),
            Class("msDS-PasswordSettings", "top", schemaNc, "ms-DS-Password-Settings",
            [
                "msDS-PasswordSettingsPrecedence", "msDS-MinimumPasswordLength", "msDS-PasswordHistoryLength",
                "msDS-PasswordComplexityEnabled", "msDS-PasswordReversibleEncryptionEnabled", "msDS-MinimumPasswordAge",
                "msDS-MaximumPasswordAge", "msDS-LockoutThreshold", "msDS-LockoutObservationWindow", "msDS-LockoutDuration",
                "msDS-PSOAppliesTo",
            ]),
            Class("msDS-PasswordSettingsContainer", "top", schemaNc, "ms-DS-Password-Settings-Container"),
            Class("rIDManager", "top", schemaNc, "RID-Manager"),
            Class("infrastructureUpdate", "top", schemaNc, "Infrastructure-Update"),
            Class("configuration", "top", schemaNc, "Configuration"),
            Class("dMD", "top", schemaNc, "DMD"),
            Class("sitesContainer", "top", schemaNc, "Sites-Container"),
            Class("site", "top", schemaNc, "Site"),
            Class("serversContainer", "top", schemaNc, "Servers-Container"),
            Class("server", "top", schemaNc, "Server", ["dNSHostName"]),
            Class("nTDSDSA", "top", schemaNc, "NTDS-DSA", ["invocationId", "msDS-HasDomainNCs"]),
            Class("crossRefContainer", "top", schemaNc, "Cross-Ref-Container", ["msDS-EnabledFeature"]),
            Class("nTDSService", "top", schemaNc, "NTDS-Service", ["dSHeuristics", "tombstoneLifetime", "msDS-deletedObjectLifetime"]),
            Class("attributeSchema", "top", schemaNc, "Attribute-Schema", ["lDAPDisplayName", "attributeID", "msDS-IntId"]),
            Class("classSchema", "top", schemaNc, "Class-Schema", ["lDAPDisplayName"]),
            Class("pKICertificateTemplate", "top", schemaNc, "PKI-Certificate-Template",
            [
                "msPKI-Certificate-Name-Flag", "msPKI-Enrollment-Flag", "pKIExtendedKeyUsage",
                "msPKI-Certificate-Application-Policy", "msPKI-RA-Signature",
            ]),
            Class("pKIEnrollmentService", "top", schemaNc, "PKI-Enrollment-Service", ["certificateTemplates", "dNSHostName", "cACertificate"]),
            Class("certificationAuthority", "top", schemaNc, "Certification-Authority", ["cACertificate"]),
            Class("msFVE-RecoveryInformation", "top", schemaNc, "ms-FVE-RecoveryInformation"),
            Class("dnsNode", "top", schemaNc, "Dns-Node", ["dNSTombstoned"]),
        ]);
    }

    private static ClassDefinition Class(string name, string parent, string schemaNc, string categoryCn, IReadOnlyList<string>? mayContain = null) => new()
    {
        LdapName = name,
        SubClassOf = parent,
        Category = ClassCategory.Structural,
        MayContain = mayContain ?? [],
        DefaultObjectCategory = $"CN={categoryCn},{schemaNc}",
    };
}
