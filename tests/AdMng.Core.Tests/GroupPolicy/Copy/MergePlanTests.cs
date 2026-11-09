using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Security;

namespace AdMng.Core.Tests.GroupPolicy.Copy;

/// <summary>21.5 (W2): merge A into B – one plan: B's copy, the relink, A disabled.</summary>
public class MergePlanTests : CopySettingsTestBed
{
    private Guid Other => Sample.WorkstationPasswords;

    private Guid SecurityGpo(string name, params string[] lines)
    {
        var gpo = AddGpo(name, computer: 1, machineNames: SecurityPair);
        PutTemplate(gpo, lines);
        return gpo;
    }

    private GpLinkValue Links(string dn) => GpLinkValue.Parse(Value(dn, Attr.GpLink), Builder.DomainDn);

    private SecurityTemplateDocument Template(Guid gpo) => SecurityTemplateDocument.Parse(Sysvol.Get(gpo, SecurityTemplate.RelativePath));

    [Fact]
    public async Task Merge_IsOnePlan_PutsBWhereAWas_DisablesA_AndUndoes()
    {
        var a = SecurityGpo("Porządki – Pulpit zdalny", "[Privilege Rights]", "SeRemoteInteractiveLogonRight = *S-1-5-32-544,*S-1-5-32-555");
        var b = AddGpo("Porządki – Inspekcja", computer: 1, machineNames: AuditPair);
        var audit = AuditPolicyDocument.New();
        var logon = SecuritySettingsCatalog.AuditSubcategories.Single(s => s.Id == "Logon");
        audit.SetSubcategory(logon.Guid, logon.CsvName, 3);
        Sysvol.Put(b, AuditPolicyDocument.RelativePath, audit.ToBytes());
        Builder.SetGroupPolicyLinks(Company.ItOu, [(Other, 0), (a, GroupPolicyLink.EnforcedOption), (Sample.ServerSecurity, 0)]);
        Builder.SetGroupPolicyLinks(Company.SalesOu, [(b, 0)]);
        var beforeB = Snapshot(b);
        var beforeA = Snapshot(a);
        var beforeLinks = Value(Company.ItOu, Attr.GpLink);

        var plan = await MergeAsync(a, b);

        Assert.Equal(0, plan.Plan.ErrorCount);
        Assert.True(plan.Plan.Sequential);
        Assert.Equal([GpoDn(b), Company.ItOu, GpoDn(a)], plan.Plan.Objects.Select(o => o.DistinguishedName));
        Assert.Equal(GpoUnitOutcomeKind.Added, Assert.Single(plan.Outcomes).Kind);
        Assert.Equal([ChangeAction.Modify, ChangeAction.WriteGpoFile, ChangeAction.SetGpoVersion], plan.Plan.Objects[0].Changes.Select(c => c.Action));
        var issues = IssuesOf(plan.Plan);
        var differ = Assert.Single(issues, i => i.Code == IssueCodes.GpoMergeLinksDiffer);
        Assert.Equal(["Porządki – Pulpit zdalny", "Porządki – Inspekcja", "IT", "Sprzedaż"], differ.Arguments);
        Assert.Contains("New-GPLink", Script(plan.Plan), StringComparison.Ordinal);

        var result = await ExecuteOkAsync(plan.Plan);
        var links = Links(Company.ItOu);
        Assert.False(links.Contains(a));
        Assert.Equal(GpLinkValue.Parse(beforeLinks, Builder.DomainDn).Links.Select(l => l.GpoId == a ? b : l.GpoId), links.Links.Select(l => l.GpoId));
        Assert.True(links.Find(b)!.Enforced);
        Assert.Equal(2, links.Find(b)!.Order);
        Assert.Equal("3", Value(GpoDn(a), "flags"));
        Assert.Equal(["S-1-5-32-544", "S-1-5-32-555"], Template(b).GetPrivilegeRights()["SeRemoteInteractiveLogonRight"]);
        Assert.Equal(SecurityPair + AuditPair, Value(GpoDn(b), "gPCMachineExtensionNames"));

        await ExecuteOkAsync(new RollbackPlanner().CreateRollbackPlan(result));
        Assert.Equal(beforeB, Snapshot(b));
        Assert.Equal(beforeA, Snapshot(a));
        Assert.Equal(beforeLinks, Value(Company.ItOu, Attr.GpLink));
    }

    [Fact]
    public async Task Merge_WhereBIsLinkedAlready_OnlyRemovesA_AndDerivesTheModeFromThePrecedence()
    {
        // A has the higher precedence (order 1, the last entry): its values win, so the mode is Replace.
        var a = SecurityGpo("Hasła – A", "[System Access]", "MinimumPasswordLength = 14");
        var b = SecurityGpo("Hasła – B", "[System Access]", "MinimumPasswordLength = 8");
        Builder.SetGroupPolicyLinks(Company.ItOu, [(b, 0), (a, 0)]);

        var plan = await MergeAsync(a, b);

        Assert.Equal(GpoUnitOutcomeKind.Replaced, Assert.Single(plan.Outcomes).Kind);
        Assert.Contains(IssuesOf(plan.Plan), i => i.Code == IssueCodes.GpoMergeLinkOptionsDiffer && i.Arguments[2] == "IT");
        Assert.DoesNotContain(IssuesOf(plan.Plan), i => i.Code == IssueCodes.GpoMergeLinksDiffer);
        await ExecuteOkAsync(plan.Plan);
        Assert.Equal([b], Links(Company.ItOu).Links.Select(l => l.GpoId));
        Assert.Equal("14", Template(b).Get("System Access", "MinimumPasswordLength"));
    }

    [Fact]
    public async Task Merge_WhereBPrecedesA_KeepsBsValues()
    {
        var a = SecurityGpo("Hasła – A", "[System Access]", "MinimumPasswordLength = 14", "PasswordComplexity = 1");
        var b = SecurityGpo("Hasła – B", "[System Access]", "MinimumPasswordLength = 8");
        Builder.SetGroupPolicyLinks(Company.ItOu, [(a, 0), (b, 0)]);

        var plan = await MergeAsync(a, b);

        Assert.Equal([GpoUnitOutcomeKind.Skipped, GpoUnitOutcomeKind.Added], plan.Outcomes.Select(o => o.Kind));
        Assert.DoesNotContain(IssuesOf(plan.Plan), i => i.Code == IssueCodes.GpoMergeLinkOptionsDiffer);
        await ExecuteOkAsync(plan.Plan);
        Assert.Equal("8", Template(b).Get("System Access", "MinimumPasswordLength"));
        Assert.Equal("1", Template(b).Get("System Access", "PasswordComplexity"));
    }

    [Fact]
    public async Task Merge_WithAMixedOrder_NeedsAnExplicitMode()
    {
        var a = SecurityGpo("Hasła – A", "[System Access]", "MinimumPasswordLength = 14");
        var b = SecurityGpo("Hasła – B", "[System Access]", "MinimumPasswordLength = 8");
        Builder.SetGroupPolicyLinks(Company.ItOu, [(a, 0), (b, 0)]);
        Builder.SetGroupPolicyLinks(Company.SalesOu, [(b, 0), (a, 0)]);

        var derived = await MergeAsync(a, b);
        var chosen = await MergeAsync(a, b, GpoCopyConflictMode.Skip);

        Assert.True(derived.Plan.IsBlocked);
        Assert.Contains(IssuesOf(derived.Plan), i => i.Code == IssueCodes.GpoMergeOrderMixed && i.Severity == IssueSeverity.Error);
        Assert.Equal(0, chosen.Plan.ErrorCount);
        Assert.Equal(GpoUnitOutcomeKind.Skipped, Assert.Single(chosen.Outcomes).Kind);
    }

    [Fact]
    public async Task Merge_OfAGpoWithBlockingUnits_IsAnError()
    {
        var a = SecurityGpo("Skrypty – A", "[System Access]", "MinimumPasswordLength = 14");
        Sysvol.PutText(a, "Machine/Scripts/scripts.ini", "[Startup]\r\n0CmdLine=start.cmd\r\n0Parameters=\r\n");
        var b = AddGpo("Skrypty – B");

        var plan = await MergeAsync(a, b);

        var error = Assert.Single(IssuesOf(plan.Plan), i => i.Code == IssueCodes.GpoMergeNotCopyable);
        Assert.Equal(IssueSeverity.Error, error.Severity);
        Assert.Equal(["Skrypty – A", "1"], error.Arguments);
        Assert.Equal(0, plan.Plan.ExecutableCount);
    }

    [Fact]
    public async Task Merge_OfAGpoWithDirectorySettings_IsAnError()
    {
        var a = SecurityGpo("Instalacja – A", "[System Access]", "MinimumPasswordLength = 14");
        Session.Seed($"CN=Class Store,CN=Machine,{GpoDn(a)}", new Dictionary<string, object?> { [Attr.ObjectClass] = new[] { "top", "classStore" } });

        var plan = await MergeAsync(a, AddGpo("Instalacja – B"));

        Assert.Contains(IssuesOf(plan.Plan), i => i.Code == IssueCodes.GpoDirectorySettings && i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public async Task Merge_OfAGpoWithUnparsedFiles_IsAnError()
    {
        var a = SecurityGpo("Przekierowanie – A", "[System Access]", "MinimumPasswordLength = 14");
        Sysvol.PutText(a, "User/Documents & Settings/fdeploy1.ini", "[FolderStatus]\r\nDesktop=11\r\n");

        var plan = await MergeAsync(a, AddGpo("Przekierowanie – B"));

        var error = Assert.Single(IssuesOf(plan.Plan), i => i.Code == IssueCodes.GpoMergeUnparsedFiles);
        Assert.Equal(IssueSeverity.Error, error.Severity);
        Assert.Equal("1", error.Arguments[1]);
    }

    [Fact]
    public async Task Merge_OfAGpoWithACommentAndAnOrphanedScriptFile_GoesOn_AndSaysWhatIsNotCarried()
    {
        var a = SecurityGpo("Komentarz – A", "[System Access]", "MinimumPasswordLength = 14");
        Sysvol.PutText(a, "GPO.cmt", "Hasła uzgodnione z Łukaszem Wiśniewskim");
        Sysvol.PutText(a, "Machine/Scripts/Startup/stary.cmd", "echo zażółć");
        var b = AddGpo("Komentarz – B");

        var plan = await MergeAsync(a, b);

        Assert.Equal(0, plan.Plan.ErrorCount);
        var info = Assert.Single(IssuesOf(plan.Plan), i => i.Code == IssueCodes.GpoMergeNotCarried);
        Assert.Equal(["Komentarz – A", "2"], info.Arguments);
        await ExecuteOkAsync(plan.Plan);
        Assert.Equal([GptIni.RelativePath, SecurityTemplate.RelativePath], Sysvol.Files(b).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Merge_LeavesOutTheUnitsOfADisabledHalfOfA()
    {
        var a = AddGpo("Dyski – A", user: 1, computer: 1, machineNames: SecurityPair, userNames: DrivesPairs, flags: (int)GpoStatus.UserSettingsDisabled);
        PutTemplate(a, "[System Access]", "MinimumPasswordLength = 14");
        PutDrives(a, Drive(@"\\fs01.contoso.test\Kadry"));
        var b = AddGpo("Dyski – B");

        var plan = await MergeAsync(a, b);

        Assert.Equal(0, plan.Plan.ErrorCount);
        Assert.Contains(plan.Outcomes, o => o.Kind == GpoUnitOutcomeKind.Skipped && o.IssueCode == IssueCodes.GpoSettingSourcePartDisabled);
        Assert.Contains(plan.Outcomes, o => o.Kind == GpoUnitOutcomeKind.Added);
        await ExecuteOkAsync(plan.Plan);
        Assert.Null(Sysvol.Get(b, DrivesPath));
    }

    [Fact]
    public async Task Merge_WarnsAboutFilteringWmiAndOtherDomains()
    {
        AddOtherDomain();
        var b = AddGpo("Pulpit zdalny – B");

        var plan = await MergeAsync(Sample.WorkstationRemoteDesktop, b);

        var codes = IssuesOf(plan.Plan).Select(i => i.Code).ToList();
        Assert.Contains(IssueCodes.GpoMergeSecurityFilterDiffers, codes);
        Assert.Contains(IssueCodes.GpoLinkedElsewhere, codes);
        Assert.DoesNotContain(IssueCodes.GpoMergeWmiFilterDiffers, codes);

        var wmi = await MergeAsync(Sample.WorkstationPasswords, b);
        Assert.Contains(IssuesOf(wmi.Plan), i => i.Code == IssueCodes.GpoMergeWmiFilterDiffers);
    }

    private const string KadryFilter = "<Filters><FilterGroup bool=\"AND\" not=\"0\" name=\"CONTOSO\\GG_Kadry\" sid=\"S-1-5-21-1000-2000-3000-1301\" userContext=\"1\" primaryGroup=\"0\" localGroup=\"0\"/></Filters>";

    private static System.Xml.Linq.XDocument XDocumentOf(byte[] bytes) => GpoXml.Load(bytes);

    private List<string?> DrivePaths(Guid gpo) =>
        [.. XDocumentOf(Sysvol.Get(gpo, DrivesPath)!).Root!.Elements().Select(e => (string?)e.Element("Properties")!.Attribute("path"))];

    [Fact]
    public async Task Merge_OfTwoTargetingVariants_ReplacesEachTargetVariantOfTheSameTargeting()
    {
        // A precedes B (Replace): each of A's K: variants replaces B's variant of the same targeting, none of B's stays.
        var a = AddGpo("Dyski – A", user: 1, userNames: DrivesPairs);
        PutDrives(a, Drive(@"\\fs01.contoso.test\Wspólne"), Drive(@"\\fs01.contoso.test\Kadry – nowe", filters: KadryFilter));
        var b = AddGpo("Dyski – B", user: 1, userNames: DrivesPairs);
        PutDrives(b, Drive(@"\\fs01.contoso.test\Stare"), Drive(@"\\fs01.contoso.test\Kadry – stare", filters: KadryFilter));
        Builder.SetGroupPolicyLinks(Company.ItOu, [(b, 0), (a, 0)]);

        var plan = await MergeAsync(a, b);

        Assert.Equal(0, plan.Plan.ErrorCount);
        Assert.Equal([GpoUnitOutcomeKind.Replaced, GpoUnitOutcomeKind.Replaced], plan.Outcomes.Select(o => o.Kind));
        await ExecuteOkAsync(plan.Plan);
        Assert.Equal([@"\\fs01.contoso.test\Wspólne", @"\\fs01.contoso.test\Kadry – nowe"], DrivePaths(b));
    }

    [Fact]
    public async Task Merge_InSkipMode_CopiesAnItemTheTargetOnlyHasDisabledOrWithOtherTargeting()
    {
        // B precedes A (Skip), but B's K: does not apply to the users A's K: reaches: A's item is carried over.
        var a = AddGpo("Dyski – A", user: 1, userNames: DrivesPairs);
        PutDrives(a, Drive(@"\\fs01.contoso.test\Wspólne"));
        var disabled = AddGpo("Dyski – B wyłączony", user: 1, userNames: DrivesPairs);
        PutDrives(disabled, Drive(@"\\fs01.contoso.test\Stare", itemAttributes: " disabled=\"1\""));
        var targeted = AddGpo("Dyski – B Kadry", user: 1, userNames: DrivesPairs);
        PutDrives(targeted, Drive(@"\\fs01.contoso.test\Kadry", filters: KadryFilter));
        var applying = AddGpo("Dyski – B wszyscy", user: 1, userNames: DrivesPairs);
        PutDrives(applying, Drive(@"\\fs01.contoso.test\Inne"));
        Builder.SetGroupPolicyLinks(Company.ItOu, [(a, 0), (disabled, 0)]);
        Builder.SetGroupPolicyLinks(Company.SalesOu, [(a, 0), (targeted, 0)]);
        Builder.SetGroupPolicyLinks(Builder.DomainDn, [(a, 0), (applying, 0)]);

        var intoDisabled = await MergeAsync(a, disabled);
        var intoTargeted = await MergeAsync(a, targeted);
        var intoApplying = await MergeAsync(a, applying);

        Assert.Equal(GpoUnitOutcomeKind.Added, Assert.Single(intoDisabled.Outcomes).Kind);
        Assert.Equal(GpoUnitOutcomeKind.Added, Assert.Single(intoTargeted.Outcomes).Kind);
        Assert.Equal(GpoUnitOutcomeKind.Skipped, Assert.Single(intoApplying.Outcomes).Kind);
        await ExecuteOkAsync(intoTargeted.Plan);
        Assert.Equal([@"\\fs01.contoso.test\Kadry", @"\\fs01.contoso.test\Wspólne"], DrivePaths(targeted));
    }

    private const string MachineGroupsPath = "Machine/Preferences/Groups/Groups.xml";

    private static string AdministratorsGroup(string member, string memberSid, string properties = "") =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><Groups clsid=\"{3125E937-EB16-4b4c-9934-544FC6D24D26}\">"
        + $"<Group clsid=\"{GppGroupsDocument.GroupClassId}\" name=\"Administrators (built-in)\" image=\"2\" changed=\"2026-10-01 08:00:00\" uid=\"{Guid.NewGuid().ToString("B").ToUpperInvariant()}\">"
        + $"<Properties action=\"U\" newName=\"\" description=\"\" deleteAllUsers=\"0\" deleteAllGroups=\"0\" removeAccounts=\"0\" groupSid=\"S-1-5-32-544\" groupName=\"Administrators (built-in)\"{properties}>"
        + $"<Members><Member name=\"{member}\" action=\"ADD\" sid=\"{memberSid}\"/></Members></Properties></Group></Groups>";

    private List<string> GroupMembers(Guid gpo) =>
        [.. XDocumentOf(Sysvol.Get(gpo, MachineGroupsPath)!).Descendants("Member").Select(m => (string)m.Attribute("name")!)];

    [Fact]
    public async Task Merge_KeepsBothLocalGroupItemsThatOnlyAddMembers_InEitherMode()
    {
        Guid Gpo(string name, string member, string sid, string properties = "")
        {
            var gpo = AddGpo(name, computer: 1);
            Sysvol.PutText(gpo, MachineGroupsPath, AdministratorsGroup(member, sid, properties));
            return gpo;
        }

        var replaceA = Gpo("Administratorzy – A", @"CONTOSO\GG_IT", "S-1-5-21-1000-2000-3000-1105");
        var replaceB = Gpo("Administratorzy – B", @"CONTOSO\GG_Helpdesk", "S-1-5-21-1000-2000-3000-1106");
        Builder.SetGroupPolicyLinks(Company.ItOu, [(replaceB, 0), (replaceA, 0)]);
        var skipA = Gpo("Administratorzy – Łódź A", @"CONTOSO\GG_IT", "S-1-5-21-1000-2000-3000-1105");
        var skipB = Gpo("Administratorzy – Łódź B", @"CONTOSO\GG_Helpdesk", "S-1-5-21-1000-2000-3000-1106");
        Builder.SetGroupPolicyLinks(Company.SalesOu, [(skipA, 0), (skipB, 0)]);
        var resetB = Gpo("Administratorzy – reset B", @"CONTOSO\GG_Helpdesk", "S-1-5-21-1000-2000-3000-1106", " deleteAllUsers=\"1\"".Replace("1\"", "1\" x=\"\"", StringComparison.Ordinal));

        var replace = await MergeAsync(replaceA, replaceB);
        var skip = await MergeAsync(skipA, skipB);

        Assert.Equal(GpoUnitOutcomeKind.Added, Assert.Single(replace.Outcomes).Kind);
        Assert.Equal(GpoUnitOutcomeKind.Added, Assert.Single(skip.Outcomes).Kind);
        await ExecuteOkAsync(replace.Plan);
        await ExecuteOkAsync(skip.Plan);
        Assert.Equal([@"CONTOSO\GG_Helpdesk", @"CONTOSO\GG_IT"], GroupMembers(replaceB));
        Assert.Equal([@"CONTOSO\GG_Helpdesk", @"CONTOSO\GG_IT"], GroupMembers(skipB));
    }

    [Fact]
    public async Task Merge_OfADefaultGpo_OrIntoItself_IsAnError()
    {
        var b = AddGpo("Domyślne – B");

        var fromDefault = await MergeAsync(Sample.DefaultDomainPolicy, b);
        var itself = await MergeAsync(b, b);

        Assert.Contains(IssuesOf(fromDefault.Plan), i => i.Code == IssueCodes.DefaultGpo && i.Severity == IssueSeverity.Error);
        Assert.True(itself.Plan.IsBlocked);
    }
}
