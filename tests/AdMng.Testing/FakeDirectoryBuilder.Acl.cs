using AdMng.Core.ActiveDirectory;

namespace AdMng.Testing;

/// <summary>
/// ACL delegations for the privileged access review (README U4b): the schema's <c>defaultSecurityDescriptor</c> of the
/// classes and <c>CN=Extended-Rights</c> (<see cref="AddAclDefinitions"/>), and real binary security descriptors with
/// delegations beyond the defaults on the sample company (<see cref="AddAclDelegationSample"/>).
/// </summary>
public sealed partial class FakeDirectoryBuilder
{
    /// <summary>Fixed GUIDs of the base schema and extended rights (the same in every forest).</summary>
    public static class AclGuids
    {
        public static readonly Guid User = new("bf967aba-0de6-11d0-a285-00aa003049e2");
        public static readonly Guid Group = new("bf967a9c-0de6-11d0-a285-00aa003049e2");
        public static readonly Guid Computer = new("bf967a86-0de6-11d0-a285-00aa003049e2");
        public static readonly Guid InetOrgPerson = new("4828cc14-1437-45bc-9b07-ad6f015e5f28");
        public static readonly Guid PrintQueue = new("bf967aa8-0de6-11d0-a285-00aa003049e2");
        public static readonly Guid GroupPolicyContainer = new("f30e3bc2-9ff0-11d1-b603-0000f80367c1");
        public static readonly Guid GetChanges = new("1131f6aa-9c07-11d1-f79f-00c04fc2dcd2");
        public static readonly Guid GetChangesAll = new("1131f6ad-9c07-11d1-f79f-00c04fc2dcd2");
        public static readonly Guid ForceChangePassword = new("00299570-246d-11d0-a768-00aa006e0529");
        public static readonly Guid ChangePassword = new("ab721a53-1e2f-11d0-9819-00aa0040529b");
        public static readonly Guid SelfMembership = new("bf9679c0-0de6-11d0-a285-00aa003049e2");
        public static readonly Guid KeyCredentialLink = new("5b47d60f-6090-40b2-9f37-2a4de88f3063");
        public static readonly Guid ValidatedDnsHostName = new("72e39547-7b18-11d1-adef-00c04fd8d5cd");
        public static readonly Guid UserAccountRestrictions = new("4c164200-20c0-11d0-a768-00aa006e0529");
        public static readonly Guid ApplyGroupPolicy = new("edacfd8f-ffb3-11d1-b41d-00a0c968f939");
    }

    /// <summary>defaultSecurityDescriptor of the classes the review reads (as Windows Server 2025 / the Samba lab have them).</summary>
    public static IReadOnlyDictionary<string, string> DefaultSecurityDescriptors { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["domainDNS"] = "D:(A;;RPWPCRCCDCLCLORCWOWDSDDTSW;;;DA)(A;;RPWPCRCCDCLCLORCWOWDSDDTSW;;;SY)(A;;RPLCLORC;;;AU)",
        [ObjectClasses.OrganizationalUnit] =
            "D:(A;;RPWPCRCCDCLCLORCWOWDSDDTSW;;;SY)(A;;RPWPCRCCDCLCLORCWOWDSDDTSW;;;DA)(OA;;CCDC;bf967a86-0de6-11d0-a285-00aa003049e2;;AO)"
            + "(OA;;CCDC;bf967aba-0de6-11d0-a285-00aa003049e2;;AO)(OA;;CCDC;bf967a9c-0de6-11d0-a285-00aa003049e2;;AO)"
            + "(OA;;CCDC;bf967aa8-0de6-11d0-a285-00aa003049e2;;PO)(A;;RPLCLORC;;;AU)(A;;LCRPLORC;;;ED)(OA;;CCDC;4828CC14-1437-45bc-9B07-AD6F015E5F28;;AO)",
        [ObjectClasses.Container] = "D:(A;;RPWPCRCCDCLCLORCWOWDSDDTSW;;;DA)(A;;RPWPCRCCDCLCLORCWOWDSDDTSW;;;SY)(A;;RPLCLORC;;;AU)",
        [ObjectClasses.BuiltinDomain] = "D:(A;;RPLCLORC;;;DA)(A;;RPWPCRCCDCLCLORCWOWDSDDTSW;;;SY)(A;;RPLCLORC;;;AU)",
        ["groupPolicyContainer"] =
            "D:P(A;CI;RPWPCCDCLCLOLORCWOWDSDDTSW;;;DA)(A;CI;RPWPCCDCLCLOLORCWOWDSDDTSW;;;EA)(A;CI;RPWPCCDCLCLOLORCWOWDSDDTSW;;;CO)"
            + "(A;CI;RPWPCCDCLCLORCWOWDSDDTSW;;;SY)(A;CI;RPLCLORC;;;AU)(OA;CI;CR;edacfd8f-ffb3-11d1-b41d-00a0c968f939;;AU)(A;CI;LCRPLORC;;;ED)",
        [ObjectClasses.User] =
            "D:(A;;RPWPCRCCDCLCLORCWOWDSDDTSW;;;DA)(A;;RPWPCRCCDCLCLORCWOWDSDDTSW;;;SY)(A;;RPWPCRCCDCLCLORCWOWDSDDTSW;;;AO)(A;;RPLCLORC;;;PS)"
            + "(OA;;CR;ab721a53-1e2f-11d0-9819-00aa0040529b;;PS)(A;;RC;;;AU)(OA;;CR;ab721a53-1e2f-11d0-9819-00aa0040529b;;WD)"
            + "(OA;;RPWP;bf967a7f-0de6-11d0-a285-00aa003049e2;;CA)",
        [ObjectClasses.Group] =
            "D:(A;;RPWPCRCCDCLCLORCWOWDSDDTSW;;;DA)(A;;RPWPCRCCDCLCLORCWOWDSDDTSW;;;SY)(A;;RPLCLORC;;;AU)(A;;RPWPCRCCDCLCLORCWOWDSDDTSW;;;AO)"
            + "(A;;RPLCLORC;;;PS)(OA;;CR;ab721a55-1e2f-11d0-9819-00aa0040529b;;AU)",
        [ObjectClasses.Computer] =
            "D:(A;;RPWPCRCCDCLCLORCWOWDSDDTSW;;;DA)(A;;RPWPCRCCDCLCLORCWOWDSDDTSW;;;AO)(A;;RPWPCRCCDCLCLORCWOWDSDDTSW;;;SY)"
            + "(A;;RPCRLCLORCSDDT;;;CO)(OA;;WP;4c164200-20c0-11d0-a768-00aa006e0529;;CO)(A;;RPLCLORC;;;AU)"
            + "(OA;;CR;ab721a53-1e2f-11d0-9819-00aa0040529b;;WD)(A;;CCDC;;;PS)(OA;;CCDC;bf967aa8-0de6-11d0-a285-00aa003049e2;;PO)"
            + "(OA;;SW;72e39547-7b18-11d1-adef-00c04fd8d5cd;;PS)(OA;;SW;72e39547-7b18-11d1-adef-00c04fd8d5cd;;CO)",
    };

    /// <summary>The controlAccessRight objects the review resolves (name, rightsGuid, validAccesses).</summary>
    private static readonly (string Name, Guid Guid, int ValidAccesses)[] ControlAccessRights =
    [
        ("DS-Replication-Get-Changes", AclGuids.GetChanges, 0x100),
        ("DS-Replication-Get-Changes-All", AclGuids.GetChangesAll, 0x100),
        ("DS-Replication-Get-Changes-In-Filtered-Set", new("89e95b76-444d-4c62-991a-0facbeda640c"), 0x100),
        ("User-Force-Change-Password", AclGuids.ForceChangePassword, 0x100),
        ("User-Change-Password", AclGuids.ChangePassword, 0x100),
        ("Apply-Group-Policy", AclGuids.ApplyGroupPolicy, 0x100),
        ("Self-Membership", AclGuids.SelfMembership, 0x8),
        ("Validated-DNS-Host-Name", AclGuids.ValidatedDnsHostName, 0x8),
        ("Membership", new("bc0ac240-79a9-11d0-9020-00c04fc2d4cf"), 0x30),
        ("User-Account-Restrictions", AclGuids.UserAccountRestrictions, 0x30),
    ];

    /// <summary>
    /// Seeds the classSchema objects (lDAPDisplayName, defaultSecurityDescriptor of <see cref="DefaultSecurityDescriptors"/>)
    /// and CN=Extended-Rights in the configuration. Seeds <see cref="AddWatchdogInfrastructure"/> first when needed.
    /// </summary>
    public void AddAclDefinitions()
    {
        if (PdcNtdsSettingsDn is null)
        {
            AddWatchdogInfrastructure();
        }

        foreach (var (name, sddl) in DefaultSecurityDescriptors)
        {
            Session.Seed(Dn("CN", name, SchemaDn), new Dictionary<string, object?>
            {
                [Attr.ObjectClass] = new[] { "top", "classSchema" },
                ["lDAPDisplayName"] = name,
                ["defaultSecurityDescriptor"] = sddl,
            });
        }

        var rights = SeedConfigContainer("Extended-Rights", ConfigurationDn);
        foreach (var (name, guid, validAccesses) in ControlAccessRights)
        {
            Session.Seed(Dn("CN", name, rights), new Dictionary<string, object?>
            {
                [Attr.ObjectClass] = new[] { "top", "controlAccessRight" },
                ["rightsGuid"] = guid.ToString("D"),
                ["validAccesses"] = validAccesses,
            });
        }
    }

    /// <summary>The objectSid of an object of the fake directory (S-1-…).</summary>
    public string SidOf(string distinguishedName) => Session.GetValues(distinguishedName, Attr.ObjectSid).Single();

    /// <summary>Replaces the binary nTSecurityDescriptor of an object.</summary>
    public void SetSecurityDescriptor(string distinguishedName, string ownerSid, params IEnumerable<FakeAce> aces) =>
        Session.SetValues(distinguishedName, Attr.NtSecurityDescriptor, FakeSecurityDescriptor.Build(ownerSid, ownerSid, aces));

    /// <summary>
    /// Extends <see cref="AddPrivilegedAccessSample"/> with ACL delegations (README U4b) on real binary descriptors; every
    /// object also carries its class defaults and the ACEs of the default trustees, which must not be reported:
    /// <list type="bullet">
    /// <item>domain head: DCSync (Get-Changes + Get-Changes-All) for Usługa SQL; a deny ACE; CN=Users and CN=Computers with
    /// the Account/Print Operators rights AD sets up there;</item>
    /// <item>OU=Firma: reset password on users for GG_Helpdesk, member write on groups for GG_Wsparcie_IT, create/delete
    /// computers for Jolanta Ćwik - inherited by every OU below (reported once, at OU=Firma);</item>
    /// <item>OU=IT: full control for Ewa Błaszczyk; OU=Serwery: owner Ignacy Wróblewski, msDS-KeyCredentialLink of computers
    /// for Usługa WWW;</item>
    /// <item>AdminSDHolder: write DACL for Wojciech Ząbek; Domain Admins: member write for Stanisław Łęcki; OU=Domain
    /// Controllers: full control of the computers for Bożena Gałązka (inherit-only);</item>
    /// <item>CN=System (not read): full control for Halina Źrebiec, inherited by CN=Policies (reported there);</item>
    /// <item>Default Domain Policy (linked to the domain): write for GG_ERP_Administratorzy; "Mapowanie dysków" (linked to
    /// OU=Firma): owner Dorota Łukasiewicz, displayName write for GG_Serwis_RDP;</item>
    /// <item>SRV-APP01: resource-based delegation for Usługa WWW; created by Paweł Lewandowski (owner, CREATOR OWNER
    /// defaults - not reported);</item>
    /// <item>only for the deep scan: reset password on Jan Kowalski (Domain Admins) for Krystyna Ślusarczyk, full control
    /// of Łukasz Wiśniewski for Halina Źrebiec.</item>
    /// </list>
    /// </summary>
    public AclSample AddAclDelegationSample(SampleCompany company, PrivilegedSample privileged)
    {
        ArgumentNullException.ThrowIfNull(company);
        ArgumentNullException.ThrowIfNull(privileged);
        AddAclDefinitions();
        var da = $"{DomainSid}-{WellKnownRids.DomainAdmins}";
        var ea = $"{DomainSid}-{WellKnownRids.EnterpriseAdmins}";
        var dcs = $"{DomainSid}-{WellKnownRids.DomainControllers}";
        var keyAdmins = $"{DomainSid}-{WellKnownRids.KeyAdmins}";
        var policyCreators = $"{DomainSid}-{WellKnownRids.GroupPolicyCreatorOwners}";
        var member = Session.Schema.FindAttribute(Attr.Member)!.SchemaIdGuid;
        var displayName = Session.Schema.FindAttribute(Attr.DisplayName)!.SchemaIdGuid;
        FakeAce[] common = [FakeAce.FullControl(da), FakeAce.FullControl("S-1-5-18"), new(FakeAce.AccessAllowed, 0, 0x20094, "S-1-5-11")];
        FakeAce[] ouDefaults =
        [
            .. common,
            Object(0x3, "S-1-5-32-548", AclGuids.User), Object(0x3, "S-1-5-32-548", AclGuids.Group), Object(0x3, "S-1-5-32-548", AclGuids.Computer),
            Object(0x3, "S-1-5-32-548", AclGuids.InetOrgPerson), Object(0x3, "S-1-5-32-550", AclGuids.PrintQueue),
        ];

        // Inheritable ACEs of the domain head (defaults of the default trustees), as every object below inherits them.
        FakeAce[] fromHead = [new(FakeAce.AccessAllowed, 0x12, 0xF01FF, "S-1-5-32-544"), new(FakeAce.AccessAllowed, 0x12, 0xF01FF, ea)];

        var sql = SidOf(privileged.SqlService);
        SetSecurityDescriptor(DomainDn, da,
        [
            .. common,
            new(FakeAce.AccessAllowed, 0x02, 0xF01FF, "S-1-5-32-544"),
            new(FakeAce.AccessAllowed, 0x02, 0xF01FF, ea),
            FakeAce.ExtendedRight(dcs, AclGuids.GetChangesAll),
            FakeAce.ExtendedRight(dcs, AclGuids.GetChanges),
            new(FakeAce.AccessAllowedObject, 0x02, 0x30, keyAdmins, AclGuids.KeyCredentialLink),
            FakeAce.ExtendedRight(sql, AclGuids.GetChanges),
            FakeAce.ExtendedRight(sql, AclGuids.GetChangesAll),
            new(FakeAce.AccessDenied, 0, 0xF01FF, SidOf(privileged.WebService)),
        ]);

        // OU=Firma and the inherited copies below it.
        FakeAce[] delegations =
        [
            new(FakeAce.AccessAllowedObject, 0x0A, 0x100, SidOf(privileged.Helpdesk), AclGuids.ForceChangePassword, AclGuids.User),
            new(FakeAce.AccessAllowedObject, 0x0A, 0x20, SidOf(privileged.Support), member, AclGuids.Group),
            new(FakeAce.AccessAllowedObject, 0x02, 0x3, SidOf(privileged.JolantaCwik), AclGuids.Computer),
        ];
        SetSecurityDescriptor(company.CompanyOu, da, [.. ouDefaults, .. fromHead, .. delegations]);
        FakeAce[] inherited = [.. fromHead, .. delegations.Select(d => d with { Flags = (byte)(d.Flags | 0x10) })];
        foreach (var ou in new[] { company.EmployeesOu, company.AccountingOu, company.SalesOu, company.ComputersOu, company.GroupsOu, privileged.ServiceAccountsOu })
        {
            SetSecurityDescriptor(ou, da, [.. ouDefaults, .. inherited]);
        }

        SetSecurityDescriptor(company.ItOu, da, [.. ouDefaults, .. inherited, new(FakeAce.AccessAllowed, 0, 0x10000000, SidOf(privileged.EwaBlaszczyk))]);
        SetSecurityDescriptor(privileged.ServersOu, SidOf(privileged.IgnacyWroblewski),
        [
            .. ouDefaults, .. inherited,
            new(FakeAce.AccessAllowedObject, 0x0A, 0x20, SidOf(privileged.WebService), AclGuids.KeyCredentialLink, AclGuids.Computer),
        ]);

        // Tier 0 objects.
        SetSecurityDescriptor(AdminSdHolderDn, da, [.. common, new(FakeAce.AccessAllowed, 0, 0x40000, SidOf(privileged.WojciechZabek))]);
        SetSecurityDescriptor(DomainAdminsDn, da, [.. common, new(FakeAce.AccessAllowedObject, 0, 0x20, SidOf(privileged.StanislawLecki), member)]);
        SetSecurityDescriptor(DomainControllersDn, da,
        [
            .. ouDefaults, .. fromHead,
            new(FakeAce.AccessAllowedObject, 0x0A, 0x10000000, SidOf(privileged.BozenaGalazka), null, AclGuids.Computer),
        ]);
        // CN=Users and CN=Computers: Account and Print Operators create/delete children (set up by AD, not in the class default).
        SetSecurityDescriptor(UsersDn, da, [.. ouDefaults, .. fromHead]);
        SetSecurityDescriptor(ComputersDn, da, [.. ouDefaults, .. fromHead]);
        SetSecurityDescriptor(BuiltinDn, da, [.. common, .. fromHead]);
        SetSecurityDescriptor(KrbtgtDn, da, [.. common, FakeAce.FullControl("S-1-5-32-548"), .. fromHead]);

        // CN=System is not read: its delegation is reported where it arrives, at CN=Policies.
        var halina = SidOf(privileged.HalinaZrebiec);
        SetSecurityDescriptor(SystemDn, da, [.. common, .. fromHead, new(FakeAce.AccessAllowed, 0x02, 0xF01FF, halina)]);
        SetSecurityDescriptor(PoliciesDn, da,
        [
            .. common, .. fromHead,
            new(FakeAce.AccessAllowed, 0x12, 0xF01FF, halina),
            new(FakeAce.AccessAllowedObject, 0, 0x1, policyCreators, AclGuids.GroupPolicyContainer),
        ]);

        // GPOs: the domain's default policy (linked to the domain head: Tier 0) and one linked to OU=Firma.
        FakeAce[] gpoDefaults =
        [
            new(FakeAce.AccessAllowed, 0x02, 0xF00FF, da), new(FakeAce.AccessAllowed, 0x02, 0xF00FF, ea), new(FakeAce.AccessAllowed, 0x02, 0xF00FF, "S-1-5-18"),
            new(FakeAce.AccessAllowed, 0x02, 0x20094, "S-1-5-11"), new(FakeAce.AccessAllowedObject, 0x02, 0x100, "S-1-5-11", AclGuids.ApplyGroupPolicy),
            new(FakeAce.AccessAllowed, 0x0A, 0xF00FF, "S-1-3-0"),
        ];
        SetSecurityDescriptor(DefaultDomainPolicyDn, da, [.. gpoDefaults, new(FakeAce.AccessAllowed, 0x02, 0x20, SidOf(privileged.ErpAdmins))]);
        SetSecurityDescriptor(DefaultDomainControllersPolicyDn, da, gpoDefaults);
        var drives = AddGroupPolicy("Mapowanie dysków");
        var dorota = SidOf(privileged.DorotaLukasiewicz);
        SetSecurityDescriptor(drives, dorota,
        [
            .. gpoDefaults,
            new(FakeAce.AccessAllowed, 0, 0xF00FF, dorota), // CREATOR OWNER of the class defaults, put in by AD
            new(FakeAce.AccessAllowedObject, 0, 0x20, SidOf(privileged.RemoteDesktopGroup), displayName),
        ]);
        Session.SetValues(company.CompanyOu, Attr.GpLink, GpLink(FakeDirectorySession.DeterministicGuid("gpo:Mapowanie dysków")));

        // SRV-APP01: joined by Paweł (owner, CREATOR OWNER defaults), Usługa WWW may act on behalf of other identities.
        var pawel = SidOf(company.PawelLewandowski);
        SetSecurityDescriptor(privileged.AppServer, pawel,
        [
            .. common, FakeAce.FullControl("S-1-5-32-548"),
            new(FakeAce.AccessAllowed, 0, 0x301D4, pawel),
            new(FakeAce.AccessAllowedObject, 0, 0x8, pawel, AclGuids.ValidatedDnsHostName),
            new(FakeAce.AccessAllowedObject, 0, 0x20, pawel, AclGuids.UserAccountRestrictions),
            .. inherited,
        ]);
        Session.SetValues(privileged.AppServer, Attr.AllowedToActOnBehalfOfOtherIdentity,
            FakeSecurityDescriptor.Build("S-1-5-32-544", null, FakeAce.FullControl(SidOf(privileged.WebService))));

        // Explicit ACEs on single accounts: only the deep scan reads them.
        SetSecurityDescriptor(company.JanKowalski, da,
        [
            .. common, FakeAce.FullControl("S-1-5-32-548"), .. inherited,
            FakeAce.ExtendedRight(SidOf(privileged.KrystynaSlusarczyk), AclGuids.ForceChangePassword),
        ]);
        SetSecurityDescriptor(company.LukaszWisniewski, da, [.. common, FakeAce.FullControl("S-1-5-32-548"), .. inherited, FakeAce.FullControl(halina)]);

        return new AclSample(drives, SystemDn, PoliciesDn, AdminSdHolderDn, DefaultDomainPolicyDn);

        static FakeAce Object(int mask, string sid, Guid objectType) => new(FakeAce.AccessAllowedObject, 0, mask, sid, objectType);
    }
}

/// <summary>DNs of <see cref="FakeDirectoryBuilder.AddAclDelegationSample"/>.</summary>
public sealed record AclSample(string DrivesPolicy, string System, string Policies, string AdminSdHolder, string DefaultDomainPolicy);
