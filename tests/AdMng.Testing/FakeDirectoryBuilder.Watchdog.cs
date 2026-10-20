using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;

namespace AdMng.Testing;

/// <summary>
/// AdMng Watchdog additions of the builder (watchdog specification 4.3): <see cref="AddWatchdogInfrastructure"/> seeds
/// what a real domain has and the watchdog reads (PDC with its NTDS Settings object, wellKnownObjects, domain policy,
/// CN=System with GPOs, AdminSDHolder and the PSO container, FSMO holder objects, forest singletons, PKI containers),
/// plus helpers for the objects only the watchdog tests need. Nothing of this is created by the constructor.
/// </summary>
public sealed partial class FakeDirectoryBuilder
{
    /// <summary>GUID of the Default Domain Policy (fixed in every domain).</summary>
    public static readonly Guid DefaultDomainPolicyGuid = new("31B2F340-016D-11D2-945F-00C04FB984F9");

    /// <summary>GUID of the Default Domain Controllers Policy (fixed in every domain).</summary>
    public static readonly Guid DefaultDomainControllersPolicyGuid = new("6AC1786C-016F-11D2-945F-00C04FB984F9");

    /// <summary>invocationId of the PDC seeded by <see cref="AddWatchdogInfrastructure"/>.</summary>
    public static readonly Guid PdcInvocationId = new("845842f4-6f6d-4e7a-a5cd-4f58276592e5");

    /// <summary>wellKnownObjects GUID of the default users container (GUID_USERS_CONTAINER_W).</summary>
    public const string UsersContainerWellKnownGuid = "A9D1CA15768811D1ADED00C04FD8D5CD";

    /// <summary>wellKnownObjects GUID of the default computers container (GUID_COMPUTERS_CONTAINER_W).</summary>
    public const string ComputersContainerWellKnownGuid = "AA312825768811D1ADED00C04FD8D5CD";

    /// <summary>Default lockout duration of the seeded domain policy (30 minutes, as in the lab).</summary>
    public static readonly TimeSpan DefaultLockoutDuration = TimeSpan.FromMinutes(30);

    public string SystemDn => $"CN=System,{DomainDn}";

    public string PoliciesDn => $"CN=Policies,{SystemDn}";

    public string AdminSdHolderDn => $"CN=AdminSDHolder,{SystemDn}";

    public string PasswordSettingsContainerDn => $"CN=Password Settings Container,{SystemDn}";

    public string RidManagerDn => $"CN=RID Manager$,{SystemDn}";

    public string InfrastructureDn => $"CN=Infrastructure,{DomainDn}";

    public string ManagedServiceAccountsDn => $"CN=Managed Service Accounts,{DomainDn}";

    public string ConfigurationDn => Session.Info.ConfigurationNamingContext;

    public string SchemaDn => Session.Info.SchemaNamingContext;

    public string PartitionsDn => $"CN=Partitions,{ConfigurationDn}";

    public string DirectoryServiceDn => $"CN=Directory Service,CN=Windows NT,CN=Services,{ConfigurationDn}";

    public string RecycleBinFeatureDn => $"CN=Recycle Bin Feature,CN=Optional Features,{DirectoryServiceDn}";

    public string PublicKeyServicesDn => $"CN=Public Key Services,CN=Services,{ConfigurationDn}";

    public string CertificateTemplatesDn => $"CN=Certificate Templates,{PublicKeyServicesDn}";

    public string EnrollmentServicesDn => $"CN=Enrollment Services,{PublicKeyServicesDn}";

    public string CertificationAuthoritiesDn => $"CN=Certification Authorities,{PublicKeyServicesDn}";

    public string NtAuthCertificatesDn => $"CN=NTAuthCertificates,{PublicKeyServicesDn}";

    public string DefaultDomainPolicyDn => GroupPolicyDn(DefaultDomainPolicyGuid);

    public string DefaultDomainControllersPolicyDn => GroupPolicyDn(DefaultDomainControllersPolicyGuid);

    /// <summary>NTDS Settings DN of the PDC (after <see cref="AddWatchdogInfrastructure"/>).</summary>
    public string? PdcNtdsSettingsDn { get; private set; }

    /// <summary>
    /// Seeds the watchdog infrastructure (call once, after the constructor): <c>Info.Server</c> as PDC emulator,
    /// wellKnownObjects and the lab's domain policy on the domain head (lockout 5 attempts / 30 min, maxPwdAge 42 days),
    /// binary security descriptors of the critical objects, CN=System with the two default GPOs (linked), AdminSDHolder,
    /// the PSO container, CN=RID Manager$, CN=Infrastructure, CN=Partitions (Recycle Bin feature when enabled), the
    /// Directory Service object, the schema head and the PKI containers.
    /// </summary>
    public FakeDirectoryBuilder AddWatchdogInfrastructure()
    {
        if (PdcNtdsSettingsDn is not null)
        {
            throw new InvalidOperationException("The watchdog infrastructure is already seeded.");
        }

        var ntds = Session.SeedDomainController(Session.Info.Server, PdcInvocationId, isPdc: true);
        PdcNtdsSettingsDn = ntds;
        var critical = new Dictionary<string, object?> { [Attr.IsCriticalSystemObject] = true };

        Session.SeedAttributes(DomainDn, new Dictionary<string, object?>
        {
            ["wellKnownObjects"] = new[]
            {
                $"B:32:{UsersContainerWellKnownGuid}:{UsersDn}",
                $"B:32:{ComputersContainerWellKnownGuid}:{ComputersDn}",
            },
            ["lockoutThreshold"] = 5,
            ["lockoutDuration"] = -DefaultLockoutDuration.Ticks,
            ["lockOutObservationWindow"] = -DefaultLockoutDuration.Ticks,
            ["maxPwdAge"] = -TimeSpan.FromDays(42).Ticks,
            ["minPwdAge"] = -TimeSpan.FromDays(1).Ticks,
            ["minPwdLength"] = 7,
            ["pwdHistoryLength"] = 24,
            ["pwdProperties"] = 1,
            ["ms-DS-MachineAccountQuota"] = 10,
            ["msDS-Behavior-Version"] = 7,
            [Attr.GpLink] = GpLink(DefaultDomainPolicyGuid),
            [Attr.NtSecurityDescriptor] = DefaultSecurityDescriptor(),
        });
        Session.SeedAttributes(DomainControllersDn, new Dictionary<string, object?>
        {
            [Attr.GpLink] = GpLink(DefaultDomainControllersPolicyGuid),
            [Attr.NtSecurityDescriptor] = DefaultSecurityDescriptor(),
        });

        AddContainer("System", DomainDn, critical);
        AddContainer("Policies", SystemDn);
        AddGroupPolicy("Default Domain Policy", DefaultDomainPolicyGuid);
        AddGroupPolicy("Default Domain Controllers Policy", DefaultDomainControllersPolicyGuid);
        AddContainer("AdminSDHolder", SystemDn, new Dictionary<string, object?>
        {
            [Attr.IsCriticalSystemObject] = true,
            [Attr.NtSecurityDescriptor] = DefaultSecurityDescriptor(),
        });
        Session.Seed(PasswordSettingsContainerDn, new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "msDS-PasswordSettingsContainer" },
        });
        Session.Seed(RidManagerDn, new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "rIDManager" },
            ["fSMORoleOwner"] = ntds,
        });
        Session.Seed(InfrastructureDn, new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "infrastructureUpdate" },
            ["fSMORoleOwner"] = ntds,
        });

        Session.Seed(SchemaDn, new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "dMD" },
            ["fSMORoleOwner"] = ntds,
        });
        var services = SeedConfigContainer("Services", ConfigurationDn);
        SeedConfigContainer("Windows NT", services);
        Session.Seed(DirectoryServiceDn, new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "nTDSService" },
            [Attr.TombstoneLifetime] = 180,
            [Attr.MsDsDeletedObjectLifetime] = 180,
        });
        SeedConfigContainer("Optional Features", DirectoryServiceDn);
        Session.Seed(RecycleBinFeatureDn, new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "msDS-OptionalFeature" },
        });
        Session.Seed(PartitionsDn, new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "crossRefContainer" },
            ["fSMORoleOwner"] = ntds,
            ["msDS-EnabledFeature"] = Session.Info.RecycleBinEnabled ? RecycleBinFeatureDn : null,
        });

        SeedConfigContainer("Public Key Services", services);
        SeedConfigContainer("Certificate Templates", PublicKeyServicesDn);
        SeedConfigContainer("Enrollment Services", PublicKeyServicesDn);
        SeedConfigContainer("Certification Authorities", PublicKeyServicesDn);
        Session.Seed(NtAuthCertificatesDn, new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "certificationAuthority" },
        });
        return this;
    }

    /// <summary>DN of a group policy container by its GUID.</summary>
    public string GroupPolicyDn(Guid guid) => $"CN={{{guid.ToString("D").ToUpperInvariant()}}},{PoliciesDn}";

    /// <summary>gPLink value linking one GPO (link options 0 = enabled, 1 = disabled, 2 = enforced).</summary>
    public string GpLink(Guid policy, int options = 0) =>
        string.Create(CultureInfo.InvariantCulture, $"[LDAP://{GroupPolicyDn(policy)};{options}]");

    /// <summary>Adds a group policy container (needs <see cref="AddWatchdogInfrastructure"/>).</summary>
    public string AddGroupPolicy(string displayName, Guid? guid = null, IReadOnlyDictionary<string, object?>? attributes = null)
    {
        var id = guid ?? FakeDirectorySession.DeterministicGuid("gpo:" + displayName);
        return Seed(GroupPolicyDn(id), new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "container", "groupPolicyContainer" },
            [Attr.DisplayName] = displayName,
            ["versionNumber"] = 0,
            ["flags"] = 0,
            ["gPCFileSysPath"] = $@"\\{DomainDnsName}\SysVol\{DomainDnsName}\Policies\{{{id.ToString("D").ToUpperInvariant()}}}",
        }, attributes);
    }

    /// <summary>Adds a fine-grained password policy in the PSO container.</summary>
    public string AddPasswordSettings(string name, int precedence, TimeSpan lockoutDuration, IEnumerable<string>? appliesTo = null)
    {
        var applies = appliesTo?.ToArray() ?? [];
        return Seed(Dn("CN", name, PasswordSettingsContainerDn), new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "msDS-PasswordSettings" },
            ["msDS-PasswordSettingsPrecedence"] = precedence,
            ["msDS-MinimumPasswordLength"] = 14,
            ["msDS-PasswordHistoryLength"] = 24,
            ["msDS-PasswordComplexityEnabled"] = true,
            ["msDS-PasswordReversibleEncryptionEnabled"] = false,
            ["msDS-MinimumPasswordAge"] = -TimeSpan.FromDays(1).Ticks,
            ["msDS-MaximumPasswordAge"] = -TimeSpan.FromDays(60).Ticks,
            ["msDS-LockoutThreshold"] = 3,
            ["msDS-LockoutObservationWindow"] = -lockoutDuration.Ticks,
            ["msDS-LockoutDuration"] = -lockoutDuration.Ticks,
            ["msDS-PSOAppliesTo"] = applies.Length > 0 ? applies : null,
        }, null);
    }

    /// <summary>Adds a trusted domain object in CN=System.</summary>
    public string AddTrust(string partnerDnsName, int direction = 3)
    {
        var flatName = partnerDnsName.Split('.')[0].ToUpperInvariant();
        return Seed(Dn("CN", partnerDnsName, SystemDn), new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "leaf", "trustedDomain" },
            ["trustDirection"] = direction,
            ["trustType"] = 2,
            ["trustAttributes"] = 8,
            ["flatName"] = flatName,
            ["trustPartner"] = partnerDnsName,
        }, null);
    }

    /// <summary>Adds a managed service account (<c>msDS-ManagedServiceAccount</c>, <c>msDS-GroupManagedServiceAccount</c> or
    /// <c>msDS-DelegatedManagedServiceAccount</c>) in CN=Managed Service Accounts (the default target of
    /// <c>New-ADServiceAccount</c>) or under <paramref name="parentDn"/> – a dMSA may be created in any OU or container
    /// whose DACL allows it (BadSuccessor, stage B design 9.1).</summary>
    public string AddServiceAccount(string name, string objectClass = ObjectClasses.GroupManagedServiceAccount, IReadOnlyDictionary<string, object?>? attributes = null,
        string? parentDn = null)
    {
        if (parentDn is null && !Session.Exists(ManagedServiceAccountsDn))
        {
            AddContainer("Managed Service Accounts", DomainDn);
        }

        return Seed(Dn("CN", name, parentDn ?? ManagedServiceAccountsDn), new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "person", "organizationalPerson", ObjectClasses.User, ObjectClasses.Computer, objectClass },
            [Attr.SamAccountName] = name + "$",
            [Attr.UserAccountControl] = (int)UserAccountControl.WorkstationTrustAccount,
            [Attr.PwdLastSet] = DefaultPasswordLastSet,
            [Attr.AccountExpires] = AdConvert.FileTimeNever,
            [Attr.PrimaryGroupId] = WellKnownRids.DomainComputers,
            [Attr.ObjectSid] = NextSid(),
            [Attr.SamAccountType] = SamAccountType.MachineAccount,
        }, attributes);
    }

    /// <summary>Adds a certificate template (Configuration NC).</summary>
    public string AddCertificateTemplate(string name, IReadOnlyDictionary<string, object?>? attributes = null) =>
        Seed(Dn("CN", name, CertificateTemplatesDn), new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "pKICertificateTemplate" },
            [Attr.DisplayName] = name,
            ["flags"] = 131680,
            ["msPKI-Certificate-Name-Flag"] = 0x8000000,
            ["msPKI-Enrollment-Flag"] = 0,
            ["msPKI-RA-Signature"] = 0,
            ["pKIExtendedKeyUsage"] = new[] { "1.3.6.1.5.5.7.3.2" },
            [Attr.NtSecurityDescriptor] = DefaultSecurityDescriptor(),
        }, attributes);

    /// <summary>Adds an enterprise CA's enrollment service publishing <paramref name="templates"/>.</summary>
    public string AddEnrollmentService(string name, params string[] templates) =>
        Seed(Dn("CN", name, EnrollmentServicesDn), new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "pKIEnrollmentService" },
            [Attr.DnsHostName] = $"{name.ToLowerInvariant()}.{DomainDnsName}",
            ["certificateTemplates"] = templates.Length > 0 ? templates : null,
        }, null);

    /// <summary>Adds an attributeSchema (or classSchema) object to the schema NC.</summary>
    public string AddSchemaObject(string ldapDisplayName, bool isClass = false) =>
        Seed(Dn("CN", ldapDisplayName, SchemaDn), new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", isClass ? "classSchema" : "attributeSchema" },
            ["lDAPDisplayName"] = ldapDisplayName,
        }, null);

    /// <summary>
    /// Adds a further domain controller: its NTDS Settings object (own USN space) and, for a DC of this domain, the
    /// computer account in OU=Domain Controllers.
    /// </summary>
    public string AddDomainController(string hostName, Guid invocationId, bool isReadOnly = false, string? domainNc = null)
    {
        var ntds = Session.SeedDomainController(hostName, invocationId, isPdc: false, isReadOnly, domainNc);
        if (domainNc is null || DistinguishedName.AreEqual(domainNc, DomainDn))
        {
            var label = hostName.Split('.')[0].ToUpperInvariant();
            AddComputer(label, DomainControllersDn, new Dictionary<string, object?>
            {
                [Attr.DnsHostName] = hostName,
                [Attr.UserAccountControl] = (int)(isReadOnly
                    ? UserAccountControl.WorkstationTrustAccount | UserAccountControl.PartialSecretsAccount
                    : UserAccountControl.ServerTrustAccount | UserAccountControl.TrustedForDelegation),
                [Attr.PrimaryGroupId] = isReadOnly ? WellKnownRids.ReadOnlyDomainControllers : WellKnownRids.DomainControllers,
                [Attr.OperatingSystem] = "Windows Server 2025 Datacenter",
            });
        }

        return ntds;
    }

    /// <summary>A small realistic SD: owner Domain Admins, full control for Domain Admins and Administrators.</summary>
    public byte[] DefaultSecurityDescriptor() => FakeSecurityDescriptor.Build(
        $"{DomainSid}-{WellKnownRids.DomainAdmins}",
        $"{DomainSid}-{WellKnownRids.DomainAdmins}",
        FakeAce.FullControl($"{DomainSid}-{WellKnownRids.DomainAdmins}"),
        FakeAce.FullControl("S-1-5-32-544"));

    private string SeedConfigContainer(string name, string parentDn)
    {
        var dn = Dn("CN", name, parentDn);
        return Session.Exists(dn) ? dn : Session.Seed(dn, new Dictionary<string, object?> { [Attr.ObjectClass] = new[] { "top", "container" } });
    }
}
