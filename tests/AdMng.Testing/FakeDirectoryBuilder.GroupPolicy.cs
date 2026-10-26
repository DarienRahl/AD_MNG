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
