using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Content;

namespace AdMng.Testing;

/// <summary>The K7 cleanup advisor sample (docs/dev/k-computers-and-gpo.md 21.10, W3).</summary>
public sealed partial class FakeDirectoryBuilder
{
    /// <summary>Drive K: of both drive GPOs (the same share in the two departments).</summary>
    public const string CleanupDrivePath = @"\\fs01.contoso.test\Wspólne";

    /// <summary>
    /// The value <c>gPCUserExtensionNames</c> of a GPO with drive maps, as GPMC writes it (preferences core + Drive Maps,
    /// both with the Drive Maps tool; to be checked against GPMC output in the lab – the design does not depend on it).
    /// </summary>
    public const string DriveMapsUserExtensionNames =
        "[{00000000-0000-0000-0000-000000000000}{2EA1A81B-48E5-45E9-8BB7-A6E3AC170006}][{5794DAFD-BE60-433F-88A2-1A31939AC01F}{2EA1A81B-48E5-45E9-8BB7-A6E3AC170006}]";

    /// <summary>
    /// The cleanup sample (K7, Polish names) on the OUs of <see cref="AddSampleCompany"/> plus OU=Kadry below OU=Pracownicy,
    /// each GPO a managed GPO (container, default DACL, <c>versionNumber</c>, SYSVOL folder with GPT.INI):
    /// <list type="bullet">
    /// <item>"Porządki – Pusty GPO": linked to OU=Firma, version computer 2, GPT.INI only (EmptyGpo).</item>
    /// <item>"Porządki – Bez powiązań": unlinked, Machine/Registry.pol with the policy "Wyłącz podgląd"
    /// (<see cref="SampleAdministrativeTemplates.HidePreview"/>) enabled (UnlinkedGpo; never a duplicate).</item>
    /// <item>"Porządki – Szablony" (domain head) and "Porządki – Szablony (oddział)" (OU=Komputery): the same policy and
    /// value (DuplicateSetting: RemoveSetting from "oddział").</item>
    /// <item>"Porządki – Pulpit zdalny" (only SeRemoteInteractiveLogonRight) and "Porządki – Inspekcja" (audit.csv with two
    /// subcategories), both linked to OU=IT only, default filters, no WMI filter, "Inspekcja" at link order 1
    /// (MergeCandidate "Pulpit zdalny" → "Inspekcja").</item>
    /// <item>"Porządki – Dyski (Księgowość)" (OU=Księgowość) and "Porządki – Dyski (Kadry)" (OU=Kadry): User Drives.xml with
    /// the same item K: to <see cref="CleanupDrivePath"/> (same label and options), different uids, disjoint OUs (equal values
    /// but no RemoveSetting); "Kadry" also a Machine Groups.xml preference group.</item>
    /// <item>"Porządki – Wyłączony": all settings disabled, one disabled link (OU=Sprzedaż).</item>
    /// </list>
    /// Links are added to the containers' existing <c>gPLink</c> (at the lowest precedence), so the sample can be combined
    /// with <see cref="AddGroupPolicyManagementSample"/>; that sample and its counts are not changed.
    /// </summary>
    public GroupPolicyCleanupSample AddGroupPolicyCleanupSample(SampleCompany company, FakeSysvol sysvol)
    {
        ArgumentNullException.ThrowIfNull(company);
        ArgumentNullException.ThrowIfNull(sysvol);
        EnsurePoliciesContainer();
        if (Session.Snapshot(DomainDn)?.ObjectSid is null)
        {
            Session.SeedAttributes(DomainDn, new Dictionary<string, object?> { [Attr.ObjectSid] = DomainSid });
        }

        var hr = $"OU=Kadry,{company.EmployeesOu}";
        if (!Session.Exists(hr))
        {
            hr = AddOu("Kadry", company.EmployeesOu);
        }

        var templatesExtensions = GpoExtensionNames.Format([(GpoExtensionNames.Registry, [GpoExtensionNames.AdministrativeTemplatesMachineTool])]);
        var securityExtensions = GpoExtensionNames.Format([(GpoExtensionNames.Security, [GpoExtensionNames.SecurityTool])]);
        var policy = RegistryPolFile.Write(
            [new RegistryPolEntry(@"Software\Policies\Contoso\Eksplorator", "NoPreview", RegistryFormat.RegDword, RegistryPolFile.DwordData(1))]);

        var empty = AddManagedGroupPolicy(sysvol, "Porządki – Pusty GPO", 0, 2);

        Guid Templates(string name)
        {
            var id = AddManagedGroupPolicy(sysvol, name, 0, 1, attributes: new Dictionary<string, object?> { ["gPCMachineExtensionNames"] = templatesExtensions });
            sysvol.Put(id, "Machine/Registry.pol", policy);
            return id;
        }

        var unlinked = Templates("Porządki – Bez powiązań");
        var templates = Templates("Porządki – Szablony");
        var branch = Templates("Porządki – Szablony (oddział)");

        var remoteDesktop = AddManagedGroupPolicy(sysvol, "Porządki – Pulpit zdalny", 0, 1,
            attributes: new Dictionary<string, object?> { ["gPCMachineExtensionNames"] = securityExtensions });
        sysvol.Put(remoteDesktop, SecurityTemplate.RelativePath, FakeSysvolFileReader.Utf16(Template(
            "[Privilege Rights]", $"SeRemoteInteractiveLogonRight = *S-1-5-32-544,*S-1-5-32-555,*{SidOf(company.GroupIt)}")));

        var audit = AddManagedGroupPolicy(sysvol, "Porządki – Inspekcja", 0, 1, attributes: new Dictionary<string, object?>
        {
            ["gPCMachineExtensionNames"] = GpoExtensionNames.Format([(GpoExtensionNames.AuditPolicy, [GpoExtensionNames.AuditPolicyTool])]),
        });
        sysvol.Put(audit, "Machine/Microsoft/Windows NT/Audit/audit.csv", Utf8Bom(
            "Machine Name,Policy Target,Subcategory,Subcategory GUID,Inclusion Setting,Exclusion Setting,Setting Value\r\n"
            + ",System,Audit Logon,{0cce9215-69ae-11d9-bed3-505054503030},Success and Failure,,3\r\n"
            + ",System,Audit Credential Validation,{0cce923f-69ae-11d9-bed3-505054503030},Success and Failure,,3\r\n"));

        var drivesAccounting = AddManagedGroupPolicy(sysvol, "Porządki – Dyski (Księgowość)", 1, 0,
            attributes: new Dictionary<string, object?> { ["gPCUserExtensionNames"] = DriveMapsUserExtensionNames });
        sysvol.Put(drivesAccounting, "User/Preferences/Drives/Drives.xml", Utf8Bom(Drives("{6C4F2A10-3B1E-4D2A-9F0E-5A1B2C3D4E01}")));
        var drivesHr = AddManagedGroupPolicy(sysvol, "Porządki – Dyski (Kadry)", 1, 1, attributes: new Dictionary<string, object?>
        {
            ["gPCUserExtensionNames"] = DriveMapsUserExtensionNames,
            ["gPCMachineExtensionNames"] = GpoExtensionNames.Format(
            [
                (GpoExtensionNames.PreferencesCore, [GpoExtensionNames.PreferencesLocalUsersAndGroupsTool]),
                (GpoExtensionNames.PreferencesLocalUsersAndGroups, [GpoExtensionNames.PreferencesLocalUsersAndGroupsTool]),
            ]),
        });
        sysvol.Put(drivesHr, "User/Preferences/Drives/Drives.xml", Utf8Bom(Drives("{9D8E7F60-1A2B-4C3D-8E9F-0A1B2C3D4E02}")));
        sysvol.Put(drivesHr, GppGroupsParser.RelativePath, Utf8Bom(Groups(LocalGroup("Remote Desktop Users (built-in)", "S-1-5-32-555",
            members: [("CONTOSO\\GG_IT", SidOf(company.GroupIt), "ADD")]))));

        var disabled = AddManagedGroupPolicy(sysvol, "Porządki – Wyłączony", 0, 1, attributes: new Dictionary<string, object?>
        {
            ["flags"] = 3,
            ["gPCMachineExtensionNames"] = securityExtensions,
        });
        sysvol.Put(disabled, SecurityTemplate.RelativePath, FakeSysvolFileReader.Utf16(Template("[Privilege Rights]", "SeShutdownPrivilege = *S-1-5-32-544")));

        AddGroupPolicyLink(company.CompanyOu, empty);
        AddGroupPolicyLink(DomainDn, templates);
        AddGroupPolicyLink(company.ComputersOu, branch);
        AddGroupPolicyLink(company.ItOu, audit);
        AddGroupPolicyLink(company.ItOu, remoteDesktop);
        AddGroupPolicyLink(company.AccountingOu, drivesAccounting);
        AddGroupPolicyLink(hr, drivesHr);
        AddGroupPolicyLink(company.SalesOu, disabled, GroupPolicyLink.DisabledOption);

        return new GroupPolicyCleanupSample(empty, unlinked, templates, branch, remoteDesktop, audit, drivesAccounting, drivesHr, disabled, hr);

        static string Drives(string uid) =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Drives clsid=\"{8FDDCC1A-0C3C-43cd-A6B4-71A6DF20DA8C}\">"
            + $"<Drive clsid=\"{{935D1B74-9CB8-4e3c-9914-7DD559B7A417}}\" name=\"K:\" status=\"K:\" image=\"2\" changed=\"2026-10-20 10:00:00\" uid=\"{uid}\">"
            + $"<Properties action=\"U\" thisDrive=\"NOCHANGE\" allDrives=\"NOCHANGE\" userName=\"\" path=\"{Xml(CleanupDrivePath)}\" label=\"Wspólne\" persistent=\"1\" useLetter=\"1\" letter=\"K\"/>"
            + "</Drive></Drives>";
    }

    /// <summary>
    /// Adds a link to a container's existing <c>gPLink</c> at the lowest precedence (GPMC puts a new link first in the value,
    /// so it gets the highest link order number).
    /// </summary>
    public void AddGroupPolicyLink(string containerDn, Guid gpo, int options = 0)
    {
        var current = Session.GetValues(containerDn, Attr.GpLink).SingleOrDefault();
        Session.SeedAttributes(containerDn, new Dictionary<string, object?> { [Attr.GpLink] = GpLink(gpo, options) + current });
    }

    private static byte[] Utf8Bom(string text) => [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)];
}

/// <summary>What <see cref="FakeDirectoryBuilder.AddGroupPolicyCleanupSample"/> created (GPO GUIDs by role).</summary>
/// <param name="EmptyGpo">"Porządki – Pusty GPO" (OU=Firma).</param>
/// <param name="Unlinked">"Porządki – Bez powiązań".</param>
/// <param name="Templates">"Porządki – Szablony" (domain head) – keeps the policy.</param>
/// <param name="TemplatesBranch">"Porządki – Szablony (oddział)" (OU=Komputery) – the covered duplicate.</param>
/// <param name="RemoteDesktop">"Porządki – Pulpit zdalny" (OU=IT) – single setting, merge source.</param>
/// <param name="Audit">"Porządki – Inspekcja" (OU=IT) – merge target.</param>
/// <param name="DrivesAccounting">"Porządki – Dyski (Księgowość)" (OU=Księgowość).</param>
/// <param name="DrivesHr">"Porządki – Dyski (Kadry)" (OU=Kadry).</param>
/// <param name="Disabled">"Porządki – Wyłączony" (all settings disabled, disabled link on OU=Sprzedaż).</param>
/// <param name="HrOu">OU=Kadry,OU=Pracownicy (added by the sample).</param>
public sealed record GroupPolicyCleanupSample(
    Guid EmptyGpo,
    Guid Unlinked,
    Guid Templates,
    Guid TemplatesBranch,
    Guid RemoteDesktop,
    Guid Audit,
    Guid DrivesAccounting,
    Guid DrivesHr,
    Guid Disabled,
    string HrOu);
