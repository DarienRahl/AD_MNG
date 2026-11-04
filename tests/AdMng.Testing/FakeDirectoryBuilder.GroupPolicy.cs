using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.GroupPolicy;

namespace AdMng.Testing;

/// <summary>Group policy additions of the builder for the privileged access review (U4c, local rights from GPOs).</summary>
public sealed partial class FakeDirectoryBuilder
{
    /// <summary>Creates CN=System and CN=Policies when they are missing; returns the Policies DN.</summary>
    public string EnsurePoliciesContainer()
    {
        if (!Session.Exists(SystemDn))
        {
            AddContainer("System", DomainDn, new Dictionary<string, object?> { [Attr.IsCriticalSystemObject] = true });
        }

        if (!Session.Exists(PoliciesDn))
        {
            AddContainer("Policies", SystemDn);
        }

        return PoliciesDn;
    }

    /// <summary>Adds a GPO (container) unless it exists; returns its GUID (deterministic from the name).</summary>
    public Guid AddGroupPolicyObject(string displayName, Guid? guid = null, IReadOnlyDictionary<string, object?>? attributes = null)
    {
        EnsurePoliciesContainer();
        var id = guid ?? FakeDirectorySession.DeterministicGuid("gpo:" + displayName);
        if (!Session.Exists(GroupPolicyDn(id)))
        {
            AddGroupPolicy(displayName, id, attributes);
        }

        return id;
    }

    /// <summary>
    /// Sets the gPLink (in the given order: the first link has the lowest precedence, the last is link order 1) and the
    /// block inheritance option of a domain head or OU.
    /// </summary>
    public void SetGroupPolicyLinks(string containerDn, IEnumerable<(Guid Gpo, int Options)> links, bool blockInheritance = false)
    {
        ArgumentNullException.ThrowIfNull(links);
        var value = string.Concat(links.Select(l => GpLink(l.Gpo, l.Options)));
        Session.SeedAttributes(containerDn, new Dictionary<string, object?>
        {
            [Attr.GpLink] = value.Length > 0 ? value : null,
            [GroupPolicyAttributes.GpOptions] = blockInheritance ? 1 : null,
        });
    }

    /// <summary>The objectSid of a seeded object (S-1-…; shared by the ACL and GPO samples).</summary>
    public string SidOf(string distinguishedName) =>
        Session.Snapshot(distinguishedName)?.ObjectSid ?? throw new InvalidOperationException($"'{distinguishedName}' has no objectSid.");

    /// <summary>
    /// Extends <see cref="AddPrivilegedAccessSample"/> with GPOs that grant local rights, their links and the files in
    /// <paramref name="sysvol"/> (Polish names throughout):
    /// <list type="bullet">
    /// <item>Domain head: "Zasady domeny – Pulpit zdalny" (Restricted Groups MemberOf: GG_Kadry_Płace → Remote Desktop
    /// Users) and the enforced "Zasady wymuszone – Dziennik zdarzeń" (GPP: GG_Audytorzy → Event Log Readers).</item>
    /// <item>OU=Serwery blocks inheritance; "Serwery – Debugowanie (stara)" (SeDebugPrivilege for GG_Przegrana) loses
    /// against "Serwery – Administratorzy lokalni" (link order 1: Restricted Groups Members of Administrators =
    /// GG_Administratorzy_Serwerów and Domain Admins, GG_Kopie_Zapasowe MemberOf Backup Operators, SeDebugPrivilege).</item>
    /// <item>OU=Domain Controllers: the Default Domain Controllers Policy with SeDebugPrivilege for Łukasz Wiśniewski and
    /// SeInteractiveLogonRight for <c>CONTOSO\malgorzata.wojcik</c> (a name) next to the built-in defaults.</item>
    /// <item>OU=Komputery: a disabled link (GG_Sprzedaz to Administrators), a GPO with the computer configuration disabled
    /// (GG_IT to Administrators), "Stacje robocze – Pomoc techniczna" (GPP: GG_Wsparcie_Stacji and Bartłomiej Kozłowski to
    /// Administrators, <c>CONTOSO\GG_Serwis_RDP</c> to Remote Desktop Users with item-level targeting, an unknown name) and
    /// "Stacje robocze – Porządki" (link order 1: GPP REMOVE of Bartłomiej Kozłowski).</item>
    /// </list>
    /// </summary>
    public GroupPolicySample AddGroupPolicySample(SampleCompany company, PrivilegedSample sample, FakeSysvolFileReader sysvol)
    {
        ArgumentNullException.ThrowIfNull(company);
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(sysvol);

        // The groups the GPOs name.
        var serverAdmins = AddGroup("GG_Administratorzy_Serwerów", company.GroupsOu, members: [sample.EwaBlaszczyk]);
        var loser = AddGroup("GG_Przegrana", company.GroupsOu, members: [company.GrazynaDabrowska]);
        var backup = AddGroup("GG_Kopie_Zapasowe", company.GroupsOu, members: [sample.StanislawLecki]);
        var workstationSupport = AddGroup("GG_Wsparcie_Stacji", company.GroupsOu, members: [sample.HenrykZulawski]);
        var auditors = AddGroup("GG_Audytorzy", company.GroupsOu, members: [company.ZbigniewKazmierczak]);
        string Sid(string dn) => "*" + SidOf(dn);

        // Domain head.
        var remoteDesktop = AddGroupPolicyObject("Zasady domeny – Pulpit zdalny");
        sysvol.PutSecurityTemplate(remoteDesktop, Template(
            "[Group Membership]",
            $"{Sid(sample.Payroll)}__Memberof = *S-1-5-32-555",
            $"{Sid(sample.Payroll)}__Members ="));
        var eventLog = AddGroupPolicyObject("Zasady wymuszone – Dziennik zdarzeń");
        sysvol.PutGroups(eventLog, Groups(LocalGroup("Event Log Readers (built-in)", "S-1-5-32-573", members: [("CONTOSO\\GG_Audytorzy", SidOf(auditors), "ADD")])));
        SetGroupPolicyLinks(DomainDn, [(remoteDesktop, 0), (eventLog, GroupPolicyLink.EnforcedOption)]);

        // OU=Serwery (blocks inheritance): two GPOs set SeDebugPrivilege, the last link wins.
        var oldDebug = AddGroupPolicyObject("Serwery – Debugowanie (stara)");
        sysvol.PutSecurityTemplate(oldDebug, Template("[Privilege Rights]", $"SeDebugPrivilege = {Sid(loser)}"));
        var serversPolicy = AddGroupPolicyObject("Serwery – Administratorzy lokalni");
        sysvol.PutSecurityTemplate(serversPolicy, Template(
            "[Group Membership]",
            "*S-1-5-32-544__Memberof =",
            $"*S-1-5-32-544__Members = {Sid(serverAdmins)},{Sid(DomainAdminsDn)}",
            $"{Sid(backup)}__Memberof = *S-1-5-32-551",
            $"{Sid(backup)}__Members =",
            "[Privilege Rights]",
            $"SeDebugPrivilege = *S-1-5-32-544,{Sid(serverAdmins)}"));
        SetGroupPolicyLinks(sample.ServersOu, [(oldDebug, 0), (serversPolicy, 0)], blockInheritance: true);

        // OU=Domain Controllers: the Default Domain Controllers Policy.
        var dcPolicy = AddGroupPolicyObject("Default Domain Controllers Policy", DefaultDomainControllersPolicyGuid);
        sysvol.PutSecurityTemplate(dcPolicy, Template(
            "[Privilege Rights]",
            $"SeDebugPrivilege = *S-1-5-32-544,{Sid(company.LukaszWisniewski)}",
            "SeBackupPrivilege = *S-1-5-32-544,*S-1-5-32-551,*S-1-5-32-549",
            "SeInteractiveLogonRight = *S-1-5-32-544,*S-1-5-32-548,*S-1-5-32-549,*S-1-5-32-550,*S-1-5-32-551,*S-1-5-9,CONTOSO\\malgorzata.wojcik",
            "SeEnableDelegationPrivilege = *S-1-5-32-544"));
        SetGroupPolicyLinks(DomainControllersDn, [(dcPolicy, 0)]);

        // OU=Komputery.
        var disabledLink = AddGroupPolicyObject("Stacje – Wyłączone połączenie");
        sysvol.PutSecurityTemplate(disabledLink, Template("[Group Membership]", $"{Sid(company.GroupSales)}__Memberof = *S-1-5-32-544"));
        var noComputerSettings = AddGroupPolicyObject("Stacje – Bez ustawień komputera", attributes: new Dictionary<string, object?>
        {
            [GroupPolicyAttributes.Flags] = GroupPolicyObjectInfo.ComputerSettingsDisabledFlag,
        });
        sysvol.PutSecurityTemplate(noComputerSettings, Template("[Group Membership]", $"{Sid(company.GroupIt)}__Memberof = *S-1-5-32-544"));
        var support = AddGroupPolicyObject("Stacje robocze – Pomoc techniczna");
        sysvol.PutGroups(support, Groups(
            LocalGroup("Administrators (built-in)", "S-1-5-32-544", members:
            [
                ("CONTOSO\\GG_Wsparcie_Stacji", SidOf(workstationSupport), "ADD"),
                ("CONTOSO\\bartlomiej.kozlowski", SidOf(company.BartlomiejKozlowski), "ADD"),
                ("CONTOSO\\nieistniejący.użytkownik", null, "ADD"),
            ]),
            LocalGroup("Remote Desktop Users (built-in)", "S-1-5-32-555", targeted: true, members: [("CONTOSO\\GG_Serwis_RDP", null, "ADD")])));
        var cleanup = AddGroupPolicyObject("Stacje robocze – Porządki");
        sysvol.PutGroups(cleanup, Groups(LocalGroup("Administrators (built-in)", "S-1-5-32-544", members: [("CONTOSO\\bartlomiej.kozlowski", SidOf(company.BartlomiejKozlowski), "REMOVE")])));
        SetGroupPolicyLinks(company.ComputersOu,
            [(disabledLink, GroupPolicyLink.DisabledOption), (noComputerSettings, 0), (support, 0), (cleanup, 0)]);

        return new GroupPolicySample(
            remoteDesktop, eventLog, oldDebug, serversPolicy, dcPolicy, disabledLink, noComputerSettings, support, cleanup,
            serverAdmins, loser, backup, workstationSupport, auditors);
    }

    /// <summary>The default DACL of a new GPO container (what samba-tool gpo create and GPMC give it).</summary>
    public byte[] DefaultGroupPolicySecurityDescriptor()
    {
        var domainAdmins = SidOf(DomainAdminsDn);
        var enterpriseAdmins = DomainSid + "-519";
        return FakeSecurityDescriptor.Build(domainAdmins, domainAdmins,
            new FakeAce(FakeAce.AccessAllowed, 0x02, 0x000F00FF, domainAdmins),
            new FakeAce(FakeAce.AccessAllowed, 0x02, 0x000F00FF, enterpriseAdmins),
            new FakeAce(FakeAce.AccessAllowed, 0x0A, 0x000F00FF, "S-1-3-0"),
            new FakeAce(FakeAce.AccessAllowed, 0x02, 0x00020094, "S-1-5-11"),
            new FakeAce(FakeAce.AccessAllowedObject, 0x02, 0x00000100, "S-1-5-11", GroupPolicySecurityFilter.ApplyGroupPolicyRight),
            new FakeAce(FakeAce.AccessAllowed, 0x02, 0x000F00FF, "S-1-5-18"),
            new FakeAce(FakeAce.AccessAllowed, 0x02, 0x00020094, "S-1-5-9"));
    }

    /// <summary>
    /// A GPO for the management views (K2): the container with <c>CN=Machine</c>/<c>CN=User</c>, the default DACL (plus
    /// <paramref name="extraAces"/>), <c>versionNumber</c> and its SYSVOL folder in <paramref name="sysvol"/> with GPT.INI at
    /// <paramref name="gptVersion"/> (default: the same version).
    /// </summary>
    public Guid AddManagedGroupPolicy(FakeSysvol sysvol, string displayName, int user, int computer, Guid? guid = null,
        int? gptVersion = null, IReadOnlyDictionary<string, object?>? attributes = null, IEnumerable<FakeAce>? extraAces = null)
    {
        ArgumentNullException.ThrowIfNull(sysvol);
        var version = (user << 16) | computer;
        var id = AddGroupPolicyObject(displayName, guid, new Dictionary<string, object?>(attributes ?? new Dictionary<string, object?>())
        {
            ["versionNumber"] = version,
            ["gPCFunctionalityVersion"] = 2,
        });
        var dn = GroupPolicyDn(id);
        Session.SetValues(dn, "versionNumber", version.ToString(CultureInfo.InvariantCulture));
        var descriptor = DefaultGroupPolicySecurityDescriptor();
        if (extraAces is not null)
        {
            var domainAdmins = SidOf(DomainAdminsDn);
            var enterpriseAdmins = DomainSid + "-519";
            descriptor = FakeSecurityDescriptor.Build(domainAdmins, domainAdmins,
            [
                new FakeAce(FakeAce.AccessAllowed, 0x02, 0x000F00FF, domainAdmins),
                new FakeAce(FakeAce.AccessAllowed, 0x02, 0x000F00FF, enterpriseAdmins),
                new FakeAce(FakeAce.AccessAllowed, 0x0A, 0x000F00FF, "S-1-3-0"),
                new FakeAce(FakeAce.AccessAllowed, 0x02, 0x000F00FF, "S-1-5-18"),
                new FakeAce(FakeAce.AccessAllowed, 0x02, 0x00020094, "S-1-5-9"),
                .. extraAces,
            ]);
        }

        Session.SeedAttributes(dn, new Dictionary<string, object?> { [Attr.NtSecurityDescriptor] = descriptor });
        foreach (var child in new[] { "Machine", "User" })
        {
            var childDn = $"CN={child},{dn}";
            if (!Session.Exists(childDn))
            {
                Session.Seed(childDn, new Dictionary<string, object?> { [Attr.ObjectClass] = new[] { "top", "container" } });
            }
        }

        sysvol.AddGpoFolder(id, gptVersion ?? version);
        return id;
    }

    /// <summary>
    /// The GPO management sample (K2, Polish names): the two default GPOs, "Stacje robocze – Zasady haseł" (a security
    /// template, the WMI filter "Tylko stacje robocze", Łukasz Wiśniewski may edit it), "Stacje robocze – Pulpit zdalny"
    /// (security filtering: GG_IT only; its link is disabled), "Serwery – Zabezpieczenia" (enforced on the domain head),
    /// "Użytkownicy – Mapowanie dysków" (GPT.INI behind AD: a version mismatch) and "Lokacja Warszawa – Drukarki" (linked to
    /// the site Warszawa). OU=Pracownicy blocks inheritance. New GPO containers get the default DACL
    /// (<see cref="FakeDirectorySession.NewObjectSecurityDescriptor"/>).
    /// </summary>
    public GroupPolicyManagementSample AddGroupPolicyManagementSample(SampleCompany company, FakeSysvol sysvol)
    {
        ArgumentNullException.ThrowIfNull(company);
        ArgumentNullException.ThrowIfNull(sysvol);
        EnsurePoliciesContainer();
        if (Session.Snapshot(DomainDn)?.ObjectSid is null)
        {
            Session.SeedAttributes(DomainDn, new Dictionary<string, object?> { [Attr.ObjectSid] = DomainSid });
        }

        var policies = PoliciesDn;
        var previous = Session.NewObjectSecurityDescriptor;
        Session.NewObjectSecurityDescriptor = dn =>
            AdMng.Core.Ldap.DistinguishedName.TryParse(dn, out var parsed) && parsed.Parent is { } parent
            && AdMng.Core.Ldap.DistinguishedName.AreEqual(parent.ToString(), policies)
                ? DefaultGroupPolicySecurityDescriptor()
                : previous?.Invoke(dn);

        // WMI filters.
        var wmiPolicy = $"CN=WMIPolicy,{SystemDn}";
        if (!Session.Exists(wmiPolicy))
        {
            AddContainer("WMIPolicy", SystemDn);
        }

        var som = $"CN=SOM,{wmiPolicy}";
        if (!Session.Exists(som))
        {
            AddContainer("SOM", wmiPolicy);
        }

        const string filterId = "{8E0A3C12-6B4D-4F0E-9C1A-2B3D4E5F6A7B}";
        var filterDn = $"CN={filterId},{som}";
        Session.Seed(filterDn, new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "msWMI-Som" },
            ["msWMI-ID"] = filterId,
            ["msWMI-Name"] = "Tylko stacje robocze",
            ["msWMI-Parm1"] = "Systemy klienckie (ProductType = 1) – bez serwerów",
            ["msWMI-Parm2"] = AdMng.Core.GroupPolicy.Management.WmiFilterQueries.Format(
                [new AdMng.Core.GroupPolicy.Management.WmiFilterQuery(@"root\CIMv2", "SELECT * FROM Win32_OperatingSystem WHERE ProductType = 1")]),
            ["msWMI-Author"] = "Administrator@contoso.test",
            ["msWMI-CreationDate"] = "20261001080000.000000-000",
            ["msWMI-ChangeDate"] = "20261015120000.000000-000",
        });

        // The GPOs.
        var defaultDomain = AddManagedGroupPolicy(sysvol, "Default Domain Policy", 0, 3, DefaultDomainPolicyGuid,
            attributes: new Dictionary<string, object?> { ["gPCMachineExtensionNames"] = "[{35378EAC-683F-11D2-A89A-00C04FBBCFA2}{53D6AB1B-2488-11D1-A28C-00C04FB94F17}][{827D319E-6EAC-11D2-A4EA-00C04F79F83A}{803E14A0-B4FB-11D0-A0D0-00A0C90F574B}]" });
        sysvol.Put(defaultDomain, SecurityTemplate.RelativePath, FakeSysvolFileReader.Utf16(Template(
            "[System Access]", "MinimumPasswordAge = 1", "MaximumPasswordAge = 42", "MinimumPasswordLength = 7", "PasswordComplexity = 1")));
        var defaultDc = AddManagedGroupPolicy(sysvol, "Default Domain Controllers Policy", 0, 1, DefaultDomainControllersPolicyGuid);
        sysvol.Put(defaultDc, SecurityTemplate.RelativePath, FakeSysvolFileReader.Utf16(Template(
            "[Privilege Rights]", "SeBackupPrivilege = *S-1-5-32-544,*S-1-5-32-551,*S-1-5-32-549")));

        var passwords = AddManagedGroupPolicy(sysvol, "Stacje robocze – Zasady haseł", 0, 4,
            attributes: new Dictionary<string, object?>
            {
                ["gPCWQLFilter"] = WmiFilterLinkValue(filterId),
                ["gPCMachineExtensionNames"] = "[{827D319E-6EAC-11D2-A4EA-00C04F79F83A}{803E14A0-B4FB-11D0-A0D0-00A0C90F574B}]",
            },
            extraAces:
            [
                new FakeAce(FakeAce.AccessAllowed, 0x02, 0x00020094, "S-1-5-11"),
                new FakeAce(FakeAce.AccessAllowedObject, 0x02, 0x00000100, "S-1-5-11", GroupPolicySecurityFilter.ApplyGroupPolicyRight),
                new FakeAce(FakeAce.AccessAllowed, 0x02, 0x000200BF, SidOf(company.LukaszWisniewski)),
            ]);
        sysvol.Put(passwords, SecurityTemplate.RelativePath, FakeSysvolFileReader.Utf16(Template(
            "[System Access]", "MinimumPasswordLength = 12", "PasswordComplexity = 1", "LockoutBadCount = 5")));
        sysvol.PutText(passwords, "GPO.cmt", "Stacje robocze: hasła min. 12 znaków – uzgodnione z Łukaszem Wiśniewskim");

        var remoteDesktop = AddManagedGroupPolicy(sysvol, "Stacje robocze – Pulpit zdalny", 0, 2,
            extraAces:
            [
                new FakeAce(FakeAce.AccessAllowed, 0x02, 0x00020094, "S-1-5-11"),
                new FakeAce(FakeAce.AccessAllowed, 0x02, 0x00020094, SidOf(company.GroupIt)),
                new FakeAce(FakeAce.AccessAllowedObject, 0x02, 0x00000100, SidOf(company.GroupIt), GroupPolicySecurityFilter.ApplyGroupPolicyRight),
            ]);
        sysvol.Put(remoteDesktop, SecurityTemplate.RelativePath, FakeSysvolFileReader.Utf16(Template(
            "[Group Membership]", $"*{SidOf(company.GroupIt)}__Memberof = *S-1-5-32-555", $"*{SidOf(company.GroupIt)}__Members =")));

        var servers = AddManagedGroupPolicy(sysvol, "Serwery – Zabezpieczenia", 0, 7, attributes: new Dictionary<string, object?> { ["flags"] = 1 });
        sysvol.Put(servers, SecurityTemplate.RelativePath, FakeSysvolFileReader.Utf16(Template("[Privilege Rights]", "SeDebugPrivilege = *S-1-5-32-544")));

        var drives = AddManagedGroupPolicy(sysvol, "Użytkownicy – Mapowanie dysków", 2, 0, gptVersion: 1 << 16);
        sysvol.PutText(drives, "User/Preferences/Drives/Drives.xml",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><Drives clsid=\"{8FDDCC1A-0C3C-43cd-A6B4-71A6DF20DA8C}\"><Drive clsid=\"{935D1B74-9CB8-4e3c-9914-7DD559B7A417}\" name=\"K:\" uid=\"{0D1F7F22-6D1B-4A35-9D9C-0E3D9F3F0001}\"><Properties action=\"U\" path=\"\\\\fs01.contoso.test\\Księgowość\" label=\"Księgowość\" letter=\"K\"/></Drive></Drives>");

        var printers = AddManagedGroupPolicy(sysvol, "Lokacja Warszawa – Drukarki", 1, 0);

        // Links: the domain head, OU=Komputery, OU=Domain Controllers, OU=Pracownicy (blocks inheritance), the site.
        SetGroupPolicyLinks(DomainDn, [(servers, GroupPolicyLink.EnforcedOption), (defaultDomain, 0)]);
        SetGroupPolicyLinks(DomainControllersDn, [(defaultDc, 0)]);
        SetGroupPolicyLinks(company.ComputersOu, [(remoteDesktop, GroupPolicyLink.DisabledOption), (passwords, 0)]);
        SetGroupPolicyLinks(company.EmployeesOu, [(drives, 0)], blockInheritance: true);
        var configuration = Session.Info.ConfigurationNamingContext;
        var sites = $"CN=Sites,{configuration}";
        if (!Session.Exists(configuration))
        {
            Session.Seed(configuration, new Dictionary<string, object?> { [Attr.ObjectClass] = new[] { "top", "configuration" } });
        }

        if (!Session.Exists(sites))
        {
            Session.Seed(sites, new Dictionary<string, object?> { [Attr.ObjectClass] = new[] { "top", "sitesContainer" } });
        }

        var site = $"CN=Warszawa,{sites}";
        Session.Seed(site, new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "site" },
            [Attr.GpLink] = GpLink(printers),
        });
        return new GroupPolicyManagementSample(defaultDomain, defaultDc, passwords, remoteDesktop, servers, drives, printers, site, filterDn, filterId,
            DomainSid + "-519");
    }

    /// <summary>gPCWQLFilter of a filter of this domain.</summary>
    public string WmiFilterLinkValue(string filterId) => $"[{DomainDnsName};{filterId};0]";

    /// <summary>A GptTmpl.inf with the usual header and the given lines.</summary>
    public static string Template(params string[] lines) =>
        string.Join("\r\n", ["[Unicode]", "Unicode=yes", "[Version]", "signature=\"$CHICAGO$\"", "Revision=1", .. lines]) + "\r\n";

    /// <summary>A Groups.xml with the given items.</summary>
    public static string Groups(params string[] items) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Groups clsid=\"{3125E937-EB16-4b4c-9934-544FC6D24D26}\">"
        + string.Concat(items) + "</Groups>";

    /// <summary>A GPP local group item (action U unless given).</summary>
    public static string LocalGroup(
        string groupName,
        string? groupSid,
        IEnumerable<(string Name, string? Sid, string Action)> members,
        string action = "U",
        bool deleteAllUsers = false,
        bool deleteAllGroups = false,
        bool targeted = false,
        bool disabled = false)
    {
        var uid = FakeDirectorySession.DeterministicGuid("gpp:" + groupName + action).ToString("B", CultureInfo.InvariantCulture).ToUpperInvariant();
        var memberXml = string.Concat(members.Select(m =>
            $"<Member name=\"{Xml(m.Name)}\" action=\"{m.Action}\" sid=\"{m.Sid ?? string.Empty}\"/>"));
        var filters = targeted
            ? "<Filters><FilterComputer bool=\"AND\" not=\"0\" type=\"NETBIOS\" name=\"WAW-PC001\"/></Filters>"
            : string.Empty;
        return $"<Group clsid=\"{{6D4A79E4-529C-4481-ABD0-F5BD7EA93BA7}}\" name=\"{Xml(groupName)}\" image=\"2\" changed=\"2026-10-20 10:00:00\" uid=\"{uid}\""
            + (disabled ? " disabled=\"1\"" : string.Empty) + ">"
            + $"<Properties action=\"{action}\" newName=\"\" description=\"\" deleteAllUsers=\"{(deleteAllUsers ? 1 : 0)}\" deleteAllGroups=\"{(deleteAllGroups ? 1 : 0)}\" removeAccounts=\"0\" groupSid=\"{groupSid ?? string.Empty}\" groupName=\"{Xml(groupName)}\">"
            + $"<Members>{memberXml}</Members></Properties>{filters}</Group>";
    }

    private static string Xml(string text) => System.Security.SecurityElement.Escape(text);
}

/// <summary>What <see cref="FakeDirectoryBuilder.AddGroupPolicySample"/> created.</summary>
public sealed record GroupPolicySample(
    Guid DomainRemoteDesktop,
    Guid EnforcedEventLog,
    Guid ServersOldDebug,
    Guid ServersPolicy,
    Guid DomainControllersPolicy,
    Guid DisabledLink,
    Guid NoComputerSettings,
    Guid WorkstationSupport,
    Guid WorkstationCleanup,
    string ServerAdmins,
    string Loser,
    string Backup,
    string WorkstationSupportGroup,
    string Auditors);

/// <summary>What <see cref="FakeDirectoryBuilder.AddGroupPolicyManagementSample"/> created.</summary>
public sealed record GroupPolicyManagementSample(
    Guid DefaultDomainPolicy,
    Guid DefaultDomainControllersPolicy,
    Guid WorkstationPasswords,
    Guid WorkstationRemoteDesktop,
    Guid ServerSecurity,
    Guid DriveMappings,
    Guid SitePrinters,
    string SiteDn,
    string WmiFilterDn,
    string WmiFilterId,
    string EnterpriseAdminsSid);
