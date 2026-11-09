using System.Text;
using AdMng.Core.Batch;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Search;
using AdMng.Core.GroupPolicy.Security;
using AdMng.Testing;

namespace AdMng.Core.Tests.GroupPolicy.Copy;

/// <summary>21.10 (W2): conflict modes, one plan per target, extension pairs, refusals and warnings of the copy.</summary>
public class CopySettingsPlanTests : CopySettingsTestBed
{
    private Guid SecurityGpo(string name, params string[] lines)
    {
        var gpo = AddGpo(name, computer: 1, machineNames: SecurityPair);
        PutTemplate(gpo, lines);
        return gpo;
    }

    private SecurityTemplateDocument Template(Guid gpo) => SecurityTemplateDocument.Parse(Sysvol.Get(gpo, SecurityTemplate.RelativePath));

    // ---- Conflict modes -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Replace_OverwritesTheTargetsValue_AndKeepsItsOtherLines()
    {
        var source = SecurityGpo("Hasła – źródło", "[System Access]", "MinimumPasswordLength = 14");
        var target = SecurityGpo("Hasła – cel", "[System Access]", "MinimumPasswordLength = 8", "PasswordComplexity = 1", "; komentarz zostaje");

        var plan = await CopyAsync(target, [await SelectAsync(source, Key("MinimumPasswordLength"))]);

        Assert.Equal(GpoUnitOutcomeKind.Replaced, Assert.Single(plan.Outcomes).Kind);
        Assert.Equal([ChangeAction.WriteGpoFile, ChangeAction.SetGpoVersion], Target(plan).Changes.Select(c => c.Action)); // names already listed
        await ExecuteOkAsync(plan.Plan);
        Assert.Equal("14", Template(target).Get("System Access", "MinimumPasswordLength"));
        Assert.Equal("1", Template(target).Get("System Access", "PasswordComplexity"));
        Assert.Contains("; komentarz zostaje", Template(target).ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Skip_KeepsTheTargetsValue_WithAnInfo_AndPlansNoWrite()
    {
        var source = SecurityGpo("Hasła – źródło", "[System Access]", "MinimumPasswordLength = 14");
        var target = SecurityGpo("Hasła – cel", "[System Access]", "MinimumPasswordLength = 8");

        var plan = await CopyAsync(target, [await SelectAsync(source, Key("MinimumPasswordLength"))], GpoCopyConflictMode.Skip);

        var outcome = Assert.Single(plan.Outcomes);
        Assert.Equal(GpoUnitOutcomeKind.Skipped, outcome.Kind);
        Assert.Equal(IssueCodes.GpoSettingSkipped, outcome.IssueCode);
        Assert.False(plan.ChangesAnything);
        Assert.Empty(Target(plan).Changes);
        Assert.Contains(Target(plan).Issues, i => i.Code == IssueCodes.GpoSettingSkipped && i.Severity == IssueSeverity.Info && i.Arguments[0] == "Hasła – cel");
    }

    [Fact]
    public async Task Unchanged_IdenticalSource_WritesNothing_AndBumpsNoVersion()
    {
        var source = SecurityGpo("Hasła – źródło", "[System Access]", "MinimumPasswordLength = 14");
        var target = SecurityGpo("Hasła – cel", "[System Access]", "MinimumPasswordLength = 14");

        var plan = await CopyAsync(target, [await SelectAsync(source, Key("MinimumPasswordLength"))]);

        Assert.Equal(GpoUnitOutcomeKind.Unchanged, Assert.Single(plan.Outcomes).Kind);
        Assert.False(plan.ChangesAnything);
        Assert.True(Target(plan).IsNoOp);
        Assert.Equal(0, plan.Plan.ExecutableCount);
    }

    [Fact]
    public async Task Union_OfAUserRight_KeepsBothListsWithoutDuplicates()
    {
        Units.Accounts[@"KONTOSO\GG_Serwis"] = Builder.DomainSid + "-1601";
        var source = SecurityGpo("Prawa – źródło", "[Privilege Rights]", $"SeRemoteInteractiveLogonRight = *S-1-5-32-555,KONTOSO\\GG_Serwis");
        var target = SecurityGpo("Prawa – cel", "[Privilege Rights]", $"SeRemoteInteractiveLogonRight = *S-1-5-32-544,*{Builder.DomainSid}-1601");

        var plan = await CopyAsync(target, [await SelectAsync(source, Key("SeRemoteInteractiveLogonRight"))], GpoCopyConflictMode.Union);

        Assert.Equal(GpoUnitOutcomeKind.Merged, Assert.Single(plan.Outcomes).Kind);
        Assert.Equal(1, Units.ResolveCalls);
        await ExecuteOkAsync(plan.Plan);
        Assert.Equal(["S-1-5-32-544", Builder.DomainSid + "-1601", "S-1-5-32-555"], Template(target).GetPrivilegeRights()["SeRemoteInteractiveLogonRight"]);
    }

    [Fact]
    public async Task Union_ForOtherKinds_IsReplace()
    {
        var source = SecurityGpo("Hasła – źródło", "[System Access]", "MinimumPasswordLength = 14");
        var target = SecurityGpo("Hasła – cel", "[System Access]", "MinimumPasswordLength = 8");

        var plan = await CopyAsync(target, [await SelectAsync(source, Key("MinimumPasswordLength"))], GpoCopyConflictMode.Union);

        Assert.Equal(GpoUnitOutcomeKind.Replaced, Assert.Single(plan.Outcomes).Kind);
    }

    [Fact]
    public async Task RestrictedGroup_ByNameInTheTarget_AndBySidInTheSource_IsOneGroupLineAfterReplace()
    {
        var groupSid = Builder.DomainSid + "-1500";
        Units.Accounts[@"KONTOSO\Administratorzy stacji"] = groupSid;
        var source = SecurityGpo("Grupy – źródło", "[Group Membership]", $"*{groupSid}__Members = *S-1-5-32-544");
        var target = SecurityGpo("Grupy – cel", "[Group Membership]", "KONTOSO\\Administratorzy stacji__Members = *S-1-5-32-555", "*S-1-5-32-544__Memberof = *S-1-5-32-555");

        var plan = await CopyAsync(target, [await SelectAsync(source, u => u.Kind == GpoSettingUnitKind.RestrictedGroup)]);

        Assert.Equal(GpoUnitOutcomeKind.Replaced, Assert.Single(plan.Outcomes).Kind);
        await ExecuteOkAsync(plan.Plan);
        var groups = Template(target).GetRestrictedGroups();
        var members = Assert.Single(groups, g => g.Relation == RestrictedGroupRelation.Members);
        Assert.Equal(groupSid, members.Group);
        Assert.Equal(["S-1-5-32-544"], members.Principals);
        Assert.Single(groups, g => g.Relation == RestrictedGroupRelation.MemberOf);
    }

    // ---- One plan per target --------------------------------------------------------------------------------------------

    [Fact]
    public async Task UnitsOfBothHalvesAndOfSeveralSources_AreOneModify_AndOneVersionStep()
    {
        var security = SecurityGpo("Zabezpieczenia – źródło", "[Privilege Rights]", "SeDebugPrivilege = *S-1-5-32-544");
        var drives = AddGpo("Dyski – źródło", user: 1, userNames: DrivesPairs);
        PutDrives(drives, Drive(@"\\fs01.contoso.test\Kadry"));
        var target = AddGpo("Cel – oba", user: 3, computer: 5);

        var plan = await CopyAsync(target, [await SelectAsync(security, Key("SeDebugPrivilege")), await SelectAsync(drives, u => u.Kind == GpoSettingUnitKind.Preference)]);

        var steps = Assert.Single(plan.Plan.Objects).Changes;
        Assert.Equal([ChangeAction.Modify, ChangeAction.WriteGpoFile, ChangeAction.WriteGpoFile, ChangeAction.SetGpoVersion], steps.Select(s => s.Action));
        Assert.Equal(["gPCMachineExtensionNames", "gPCUserExtensionNames"], steps[0].Attributes.Select(a => a.Attribute));
        Assert.Equal(SecurityPair, steps[0].Attributes[0].NewValues.Single());
        Assert.Equal(DrivesPairs, steps[0].Attributes[1].NewValues.Single());
        Assert.Equal(new GpoVersion(4, 6), steps[3].GpoVersion!.New);
        await ExecuteOkAsync(plan.Plan);
        Assert.Equal(new GpoVersion(4, 6), DirectoryVersion(target));
    }

    [Fact]
    public async Task ConcurrentChange_BetweenPlanAndExecution_IsAConflict_AndNothingIsWritten()
    {
        var source = SecurityGpo("Hasła – źródło", "[System Access]", "MinimumPasswordLength = 14");
        var target = SecurityGpo("Hasła – cel", "[System Access]", "MinimumPasswordLength = 8");
        var plan = await CopyAsync(target, [await SelectAsync(source, Key("MinimumPasswordLength"))]);
        PutTemplate(target, "[System Access]", "MinimumPasswordLength = 10");
        var foreign = Sysvol.Get(target, SecurityTemplate.RelativePath);

        var result = await ExecuteAsync(plan.Plan);

        Assert.Contains(result.Objects.Single().Changes, c => !c.Succeeded && (c.ErrorMessage ?? string.Empty).StartsWith(BatchExecutor.ConflictMessagePrefix, StringComparison.Ordinal));
        Assert.Equal(foreign, Sysvol.Get(target, SecurityTemplate.RelativePath));
        Assert.Equal(new GpoVersion(0, 1), DirectoryVersion(target));
    }

    // ---- Preferences ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Preference_IntoANewFile_GetsTheSourceRoot_AndTheCloneKeepsTargetingAndOptions()
    {
        var source = AddGpo("Dyski – źródło", user: 1, userNames: DrivesPairs);
        const string filters = "<Filters><FilterGroup bool=\"AND\" not=\"0\" name=\"KONTOSO\\GG_Kadry\" sid=\"S-1-5-21-1-2-3-1700\" userContext=\"1\" primaryGroup=\"0\" localGroup=\"0\"/></Filters>";
        var uid = "{11111111-2222-3333-4444-555555555555}";
        PutDrives(source, Drive(@"\\fs01.contoso.test\Kadry", uid: uid, filters: filters, itemAttributes: " bypassErrors=\"1\" userContext=\"1\""));
        var target = AddGpo("Dyski – cel");

        var plan = await CopyAsync(target, [await SelectAsync(source, u => u.Kind == GpoSettingUnitKind.Preference)]);
        await ExecuteOkAsync(plan.Plan);

        var document = XDocumentOf(Sysvol.Get(target, DrivesPath)!);
        Assert.Equal("Drives", document.Root!.Name.LocalName);
        Assert.Equal(DrivesClassId, (string?)document.Root.Attribute("clsid"));
        var drive = Assert.Single(document.Root.Elements());
        Assert.NotEqual(uid, (string?)drive.Attribute("uid"));
        Assert.Matches("^\\{[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\\}$", (string?)drive.Attribute("uid"));
        Assert.Equal("2026-11-09 14:30:05", (string?)drive.Attribute("changed"));
        Assert.Equal("1", (string?)drive.Attribute("bypassErrors"));
        Assert.NotNull(drive.Element("Filters")?.Element("FilterGroup"));
        Assert.Equal(DrivesPairs, Value(GpoDn(target), "gPCUserExtensionNames"));
        Assert.Equal([0xEF, 0xBB, 0xBF], Sysvol.Get(target, DrivesPath)![..3]);
    }

    [Fact]
    public async Task Preference_ConditionalVariants_AreKeptOnReplace()
    {
        var source = AddGpo("Dyski – źródło", user: 1, userNames: DrivesPairs);
        PutDrives(source, Drive(@"\\fs01.contoso.test\Wspólne"));
        var target = AddGpo("Dyski – cel", user: 1, userNames: DrivesPairs);
        const string filters = "<Filters><FilterComputer bool=\"AND\" not=\"0\" type=\"NETBIOS\" name=\"WAW-PC001\"/></Filters>";
        PutDrives(target, Drive(@"\\fs01.contoso.test\Księgowość", filters: filters), Drive(@"\\fs01.contoso.test\Stare"));

        var plan = await CopyAsync(target, [await SelectAsync(source, u => u.Kind == GpoSettingUnitKind.Preference)]);

        Assert.Equal(GpoUnitOutcomeKind.Added, Assert.Single(plan.Outcomes).Kind);
        Assert.Contains(Target(plan).Issues, i => i.Code == IssueCodes.GpoSettingConditionalKept);
        await ExecuteOkAsync(plan.Plan);
        var paths = XDocumentOf(Sysvol.Get(target, DrivesPath)!).Root!.Elements().Select(e => (string?)e.Element("Properties")!.Attribute("path")).ToList();
        Assert.Equal([@"\\fs01.contoso.test\Księgowość", @"\\fs01.contoso.test\Wspólne"], paths);
    }

    [Fact]
    public async Task Preference_ReplacingAnItemWithRemovePolicy_Warns()
    {
        var source = AddGpo("Dyski – źródło", user: 1, userNames: DrivesPairs);
        PutDrives(source, Drive(@"\\fs01.contoso.test\Nowe"));
        var target = AddGpo("Dyski – cel", user: 1, userNames: DrivesPairs);
        PutDrives(target, Drive(@"\\fs01.contoso.test\Stare", itemAttributes: " removePolicy=\"1\""));

        var plan = await CopyAsync(target, [await SelectAsync(source, u => u.Kind == GpoSettingUnitKind.Preference)]);

        Assert.Equal(GpoUnitOutcomeKind.Replaced, Assert.Single(plan.Outcomes).Kind);
        Assert.Contains(Target(plan).Issues, i => i.Code == IssueCodes.GpoSettingRemovePolicy && i.Severity == IssueSeverity.Warning);
    }

    [Fact]
    public async Task Preference_OfAScopedCollection_IsRefused_AndOfAPlainCollection_IsCopiedToTheRoot()
    {
        var source = AddGpo("Dyski – kolekcje", user: 1, userNames: DrivesPairs);
        PutDrives(source,
            "<Collection clsid=\"{53B533F5-224C-47e3-B01B-CA3B3F3FF4BF}\" name=\"Zwykła\" uid=\"{AAAAAAAA-0000-0000-0000-000000000001}\">" + Drive(@"\\fs01\Zwykła", "L") + "</Collection>",
            "<Collection clsid=\"{53B533F5-224C-47e3-B01B-CA3B3F3FF4BF}\" name=\"Wyłączona\" disabled=\"1\" uid=\"{AAAAAAAA-0000-0000-0000-000000000002}\">" + Drive(@"\\fs01\Wyłączona", "M") + "</Collection>");
        var target = AddGpo("Dyski – cel");

        var plan = await CopyAsync(target,
        [
            await SelectAsync(source, Key("|L")),
            await SelectAsync(source, Key("|M")),
        ]);

        Assert.Equal([GpoUnitOutcomeKind.Added, GpoUnitOutcomeKind.Refused], plan.Outcomes.Select(o => o.Kind));
        Assert.Equal(IssueCodes.GpoSettingUnsupported, plan.Outcomes[1].IssueCode);
        await ExecuteOkAsync(plan.Plan);
        var root = XDocumentOf(Sysvol.Get(target, DrivesPath)!).Root!;
        Assert.Equal("Drive", Assert.Single(root.Elements()).Name.LocalName);
    }

    // ---- Registry.pol -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task InconsistentPolicy_IsCopiedByItsRawEntries()
    {
        var source = AddGpo("Szablony – niespójne", computer: 1, machineNames: MachineRegistryPair);
        PutRegistry(source, false, Dword(@"Software\Policies\Contoso\Eksplorator", "NoPreview", 5));
        var target = AddGpo("Szablony – cel");
        var unit = Assert.Single(await UnitsOfAsync(source, Definitions));
        Assert.Equal(GpoSettingUnitKind.AdministrativeTemplate, unit.Kind);
        Assert.Equal(AdMng.Core.GroupPolicy.AdministrativeTemplates.PolicyState.Inconsistent, unit.Policy!.State);

        var plan = await CopyAsync(target, [new GpoUnitSelection(source, unit.Id)], definitions: Definitions);
        await ExecuteOkAsync(plan.Plan);

        var entry = Assert.Single(RegistryPolFile.Read(Sysvol.Get(target, GroupPolicyPlanner.MachineRegistryPolicyPath)));
        Assert.Equal(RegistryPolFile.DwordData(5), entry.Data);
        Assert.Equal(MachineRegistryPair, Value(GpoDn(target), "gPCMachineExtensionNames"));
    }

    [Fact]
    public async Task DeleteAllValues_IsCopiedBeforeTheTargetsValues_AndBeforeValuesCopiedInTheSamePlan()
    {
        const string key = @"Software\Contoso\Listy";
        var source = AddGpo("Rejestr – źródło", computer: 1, machineNames: MachineRegistryPair);
        PutRegistry(source, false, DeleteAll(key), Text(key, "b", "druga"));
        var target = AddGpo("Rejestr – cel", computer: 1, machineNames: MachineRegistryPair);
        PutRegistry(target, false, Text(@"Software\Contoso\Inne", "x", "1"), Text(key, "a", "pierwsza"), Text(@"Software\Contoso\Zzz", "z", "1"));

        // The value first, then the key-level delete: the delete still goes before both.
        var plan = await CopyAsync(target, [await SelectAsync(source, Key("Listy!b")), await SelectAsync(source, Key("**delvals."))]);
        await ExecuteOkAsync(plan.Plan);

        var names = RegistryPolFile.Read(Sysvol.Get(target, GroupPolicyPlanner.MachineRegistryPolicyPath)).Select(e => e.Key.Split('\\')[^1] + "!" + e.ValueName).ToList();
        Assert.Equal(["Inne!x", "Listy!**delvals.", "Listy!a", "Listy!b", "Zzz!z"], names);
    }

    [Fact]
    public async Task RawValue_OwnedByATargetPolicy_IsRefused()
    {
        var source = AddGpo("Rejestr – surowy", computer: 1, machineNames: MachineRegistryPair);
        PutRegistry(source, false, Dword(@"Software\Policies\Contoso\Aktualizacje\AU", "ScheduledInstallDay", 3));
        var target = AddGpo("Szablony – aktualizacje", computer: 1, machineNames: MachineRegistryPair);
        PutRegistry(target, false,
            Dword(@"Software\Policies\Contoso\Aktualizacje\AU", "NoAutoUpdate", 0),
            Dword(@"Software\Policies\Contoso\Aktualizacje\AU", "AUOptions", 3),
            Dword(@"Software\Policies\Contoso\Aktualizacje\AU", "ScheduledInstallDay", 1));
        var unit = Assert.Single(await UnitsOfAsync(source, Definitions));
        Assert.Equal(GpoSettingUnitKind.RegistryValue, unit.Kind);

        var plan = await CopyAsync(target, [new GpoUnitSelection(source, unit.Id)], definitions: Definitions);

        var outcome = Assert.Single(plan.Outcomes);
        Assert.Equal(GpoUnitOutcomeKind.Refused, outcome.Kind);
        Assert.Equal(IssueCodes.GpoSettingPolicyOwned, outcome.IssueCode);
    }

    [Fact]
    public async Task AdministrativeTemplate_WithoutDefinitions_IsRefused()
    {
        var source = AddGpo("Szablony – źródło", computer: 1, machineNames: MachineRegistryPair);
        PutRegistry(source, false, Dword(@"Software\Policies\Contoso\Eksplorator", "NoPreview", 1));
        var unit = Assert.Single(await UnitsOfAsync(source, Definitions));
        var target = AddGpo("Szablony – cel");

        var plan = await CopyAsync(target, [new GpoUnitSelection(source, unit.Id)], definitions: null);

        Assert.Equal(IssueCodes.GpoSettingNoDefinitions, Assert.Single(plan.Outcomes).IssueCode);
    }

    // ---- Extension names ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Preference_FromASourceWithoutTheCse_IsRefused_UnlessTheTargetListsIt()
    {
        // The management sample's drive GPO lists no Drives pair.
        var refused = await CopyAsync(Sample.SitePrinters, [await SelectAsync(Sample.DriveMappings, u => u.Kind == GpoSettingUnitKind.Preference)]);
        Assert.Equal(IssueCodes.GpoSettingExtensionUnknown, Assert.Single(refused.Outcomes).IssueCode);

        Session.SeedAttributes(GpoDn(Sample.SitePrinters), new Dictionary<string, object?> { ["gPCUserExtensionNames"] = DrivesPairs });
        var accepted = await CopyAsync(Sample.SitePrinters, [await SelectAsync(Sample.DriveMappings, u => u.Kind == GpoSettingUnitKind.Preference)]);
        Assert.Equal(GpoUnitOutcomeKind.Added, Assert.Single(accepted.Outcomes).Kind);
        Assert.DoesNotContain(Target(accepted).Changes, c => c.Action == ChangeAction.Modify);
    }

    [Fact]
    public async Task Preference_OfAnUnknownType_IsRefused()
    {
        var source = AddGpo("Aplikacje – źródło", user: 1, userNames: DrivesPairs);
        Sysvol.Put(source, "User/Preferences/Applications/Applications.xml", Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><Applications clsid=\"{0DA274B5-EB93-47A7-AAFB-65BA532D3FE6}\"><Application clsid=\"{9F4BD4D5-4E0B-4E77-9B45-1C49D0B7A7D6}\" name=\"Kalkulator\" uid=\"{CCCCCCCC-0000-0000-0000-000000000001}\"><Properties action=\"U\" path=\"calc.exe\"/></Application></Applications>"));
        var target = AddGpo("Aplikacje – cel");

        var plan = await CopyAsync(target, [await SelectAsync(source, u => u.Kind == GpoSettingUnitKind.Preference)]);

        Assert.Equal(IssueCodes.GpoSettingExtensionUnknown, Assert.Single(plan.Outcomes).IssueCode);
    }

    [Fact]
    public async Task MachineGroupsXml_GetsTheKnownPairs()
    {
        var source = AddGpo("Grupy lokalne – źródło", computer: 1);
        Sysvol.PutText(source, GppGroupsDocument.RelativePath, FakeDirectoryBuilder.Groups(FakeDirectoryBuilder.LocalGroup("Administratorzy (wbudowana)", "S-1-5-32-544", [("KONTOSO\\GG_IT", Builder.DomainSid + "-1500", "ADD")])));
        var target = AddGpo("Grupy lokalne – cel");

        var plan = await CopyAsync(target, [await SelectAsync(source, u => u.Kind == GpoSettingUnitKind.Preference)]);

        Assert.Equal(GpoUnitOutcomeKind.Added, Assert.Single(plan.Outcomes).Kind);
        Assert.Equal("[{00000000-0000-0000-0000-000000000000}{79F92669-4224-476C-9C5C-6EFB4D87DF4A}][{17D89FEC-5C44-4972-B12D-241CAEF74509}{79F92669-4224-476C-9C5C-6EFB4D87DF4A}]",
            Target(plan).Changes[0].Attributes.Single().NewValues.Single());
    }

    // ---- Refusals ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Refusals_AreWarnings_AndTheRestOfThePlanGoesOn()
    {
        var source = AddGpo("Różne – źródło", user: 1, computer: 1, machineNames: SecurityPair);
        PutTemplate(source, "[System Access]", "MinimumPasswordLength = 14", "[File Security]", "\"%SystemRoot%\\Temp\",2,\"D:PAR(A;OICI;FA;;;BA)\"");
        Sysvol.Put(source, GroupPolicyPlanner.UserRegistryPolicyPath, RegistryPolFile.Write([Text(@"Software\Microsoft\Windows NT\CurrentVersion\Winlogon", "DefaultPassword", "Tajne!2026")]));
        Sysvol.PutText(source, "User/Preferences/Groups/Groups.xml",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><Groups clsid=\"{3125E937-EB16-4b4c-9934-544FC6D24D26}\"><User clsid=\"{DF5F1855-51E5-4d24-8B1A-D9BDE98BA1D1}\" name=\"serwis\" uid=\"{DDDDDDDD-0000-0000-0000-000000000001}\"><Properties action=\"U\" userName=\"serwis\" cpassword=\"j1Uyj3Vx8TY9LtLZil2uAuZkFQA/4latT76ZwgdHdhw\"/></User></Groups>");
        Sysvol.PutText(source, "Machine/Scripts/scripts.ini", "[Startup]\r\n0CmdLine=\\\\fs01\\skrypty\\start.cmd\r\n0Parameters=\r\n");
        var audit = "Machine Name,Policy Target,Subcategory,Subcategory GUID,Inclusion Setting,Exclusion Setting,Setting Value\r\n,System,Option:CrashOnAuditFail,,Enabled,,1\r\n";
        Sysvol.PutText(source, AuditPolicyDocument.RelativePath, audit);
        Sysvol.PutText(source, "GPO.cmt", "Uwagi – zażółć gęślą jaźń");
        var target = AddGpo("Różne – cel");
        var units = await UnitsOfAsync(source);
        GpoUnitSelection Pick(GpoSettingUnitKind kind, bool masked = false) =>
            units.Where(u => u.Kind == kind && (u.Comparability == GpoUnitComparability.Masked) == masked).Select(u => new GpoUnitSelection(source, u.Id, u.ItemId, u.ValueFingerprint)).First();

        var plan = await CopyAsync(target,
        [
            Pick(GpoSettingUnitKind.Preference, masked: true),
            Pick(GpoSettingUnitKind.RegistryValue, masked: true),
            Pick(GpoSettingUnitKind.Script),
            Pick(GpoSettingUnitKind.ObjectSecurity),
            Pick(GpoSettingUnitKind.AuditOption),
            Pick(GpoSettingUnitKind.Comment),
            Pick(GpoSettingUnitKind.SecurityValue),
        ]);

        Assert.Equal(
            [IssueCodes.GpoSettingSecret, IssueCodes.GpoSettingSecret, IssueCodes.GpoSettingUnsupported, IssueCodes.GpoSettingUnsupported,
                IssueCodes.GpoSettingUnsupported, IssueCodes.GpoSettingUnsupported, null],
            plan.Outcomes.Select(o => o.IssueCode));
        Assert.Equal(GpoUnitOutcomeKind.Added, plan.Outcomes[^1].Kind);
        Assert.Equal(0, plan.Plan.ErrorCount);
        Assert.All(Target(plan).Issues, i => Assert.NotEqual(IssueSeverity.Error, i.Severity));
        await ExecuteOkAsync(plan.Plan);
        Assert.Equal([GptIni.RelativePath, SecurityTemplate.RelativePath], Sysvol.Files(target).Order(StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(Sysvol.Files(target), f => f.Contains("Groups", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TheSameSettingFromTwoSources_IsRefusedForBoth()
    {
        var first = SecurityGpo("Hasła – pierwsze", "[System Access]", "MinimumPasswordLength = 12");
        var second = SecurityGpo("Hasła – drugie", "[System Access]", "MinimumPasswordLength = 14");
        var target = AddGpo("Hasła – cel");

        var plan = await CopyAsync(target, [await SelectAsync(first, Key("MinimumPasswordLength")), await SelectAsync(second, Key("MinimumPasswordLength"))]);

        Assert.All(plan.Outcomes, o => Assert.Equal(IssueCodes.GpoSettingAmbiguous, o.IssueCode));
        Assert.False(plan.ChangesAnything);
    }

    [Fact]
    public async Task IncompleteSource_IsRefused()
    {
        var source = SecurityGpo("Hasła – przerwany zapis", "[System Access]", "MinimumPasswordLength = 14");
        var selection = await SelectAsync(source, Key("MinimumPasswordLength"));
        Sysvol.Put(source, GpoTransaction.TempName("Machine/Registry.pol", Guid.NewGuid()), RegistryPolFile.Empty);
        var target = AddGpo("Hasła – cel");

        var plan = await CopyAsync(target, [selection]);

        Assert.Equal(IssueCodes.GpoSettingIncomplete, Assert.Single(plan.Outcomes).IssueCode);
    }

    [Fact]
    public async Task OverflowedSource_IsRefused()
    {
        var source = SecurityGpo("Hasła – przepełnione", "[System Access]", "MinimumPasswordLength = 14");
        var selection = await SelectAsync(source, Key("MinimumPasswordLength"));
        Units.Overflowed.Add(SecurityTemplate.RelativePath);
        var target = AddGpo("Hasła – cel");

        var plan = await CopyAsync(target, [selection]);

        Assert.Equal(IssueCodes.GpoSettingIncomplete, Assert.Single(plan.Outcomes).IssueCode);
    }

    [Fact]
    public async Task MoreThanTenSources_IsAnError()
    {
        var target = AddGpo("Cel – za dużo źródeł");
        var selections = Enumerable.Range(1, GpoSettingsCopyRequest.MaxSources + 1)
            .Select(i => new GpoUnitSelection(AddGpo("Źródło " + i), "K|Computer/Security/System Access/MinimumPasswordLength")).ToList();

        var plan = await CopyAsync(target, selections);

        var error = Assert.Single(Target(plan).Issues);
        Assert.Equal(IssueCodes.GpoCopyTooManySources, error.Code);
        Assert.Equal(IssueSeverity.Error, error.Severity);
        Assert.Equal(["11", "10"], error.Arguments);
        Assert.Equal(1, plan.Plan.ErrorCount);
    }

    [Fact]
    public async Task StaleItemId_IsNotFound_AndAChangedFingerprint_Warns()
    {
        var source = AddGpo("Dyski – źródło", user: 1, userNames: DrivesPairs);
        PutDrives(source, Drive(@"\\fs01.contoso.test\Kadry"));
        var selection = await SelectAsync(source, u => u.Kind == GpoSettingUnitKind.Preference);
        var target = AddGpo("Dyski – cel");

        var stale = await CopyAsync(target, [selection with { ItemId = "{00000000-0000-0000-0000-00000000DEAD}" }]);
        Assert.Equal(GpoUnitOutcomeKind.NotFound, Assert.Single(stale.Outcomes).Kind);
        Assert.Contains(Target(stale).Issues, i => i.Code == IssueCodes.GpoSettingNotFound);

        var changed = await CopyAsync(target, [selection with { ValueFingerprint = "inna wartość" }]);
        Assert.Equal(GpoUnitOutcomeKind.Added, Assert.Single(changed.Outcomes).Kind);
        Assert.Contains(Target(changed).Issues, i => i.Code == IssueCodes.GpoSettingChanged && i.Severity == IssueSeverity.Warning);
    }

    [Fact]
    public async Task SourcePartDisabled_WarnsOnCopy()
    {
        var source = AddGpo("Hasła – wyłączona część", computer: 1, machineNames: SecurityPair, flags: (int)GpoStatus.ComputerSettingsDisabled);
        PutTemplate(source, "[System Access]", "MinimumPasswordLength = 14");
        var target = AddGpo("Hasła – cel");

        var plan = await CopyAsync(target, [await SelectAsync(source, Key("MinimumPasswordLength"))]);

        Assert.Equal(GpoUnitOutcomeKind.Added, Assert.Single(plan.Outcomes).Kind);
        Assert.Contains(Target(plan).Issues, i => i.Code == IssueCodes.GpoSettingSourcePartDisabled && i.Severity == IssueSeverity.Warning);
    }

    [Fact]
    public async Task AccountAndKerberosPolicy_BelowTheDomainHead_WarnApart()
    {
        var source = SecurityGpo("Konta – źródło", "[System Access]", "MinimumPasswordLength = 14", "EnableGuestAccount = 0", "[Kerberos Policy]", "MaxTicketAge = 10");
        var below = AddGpo("Konta – poniżej domeny");
        Builder.SetGroupPolicyLinks(Company.ItOu, [(below, 0)]);

        var password = await CopyAsync(below, [await SelectAsync(source, Key("MinimumPasswordLength"))]);
        var guest = await CopyAsync(below, [await SelectAsync(source, Key("EnableGuestAccount"))]);
        var kerberos = await CopyAsync(below, [await SelectAsync(source, Key("MaxTicketAge"))]);
        var atDomain = await CopyAsync(Sample.ServerSecurity, [await SelectAsync(source, Key("MinimumPasswordLength"))]);

        Assert.Contains(Target(password).Issues, i => i.Code == IssueCodes.GpoAccountPolicyBelowDomain);
        Assert.DoesNotContain(Target(password).Issues, i => i.Code == IssueCodes.GpoKerberosPolicyBelowDomain);
        Assert.DoesNotContain(Target(guest).Issues, i => i.Code is IssueCodes.GpoAccountPolicyBelowDomain or IssueCodes.GpoKerberosPolicyBelowDomain);
        Assert.Contains(Target(kerberos).Issues, i => i.Code == IssueCodes.GpoKerberosPolicyBelowDomain);
        Assert.DoesNotContain(Target(atDomain).Issues, i => i.Code is IssueCodes.GpoAccountPolicyBelowDomain or IssueCodes.GpoKerberosPolicyBelowDomain);
    }

    [Fact]
    public async Task ToString_OfThePlanAndOutcomes_HoldsNoValues()
    {
        var source = SecurityGpo("Hasła – źródło", "[System Access]", "MinimumPasswordLength = 14");
        var target = AddGpo("Hasła – cel");

        var plan = await CopyAsync(target, [await SelectAsync(source, Key("MinimumPasswordLength"))]);

        Assert.DoesNotContain("MinimumPasswordLength", plan.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("MinimumPasswordLength", plan.Outcomes[0].ToString(), StringComparison.Ordinal);
        Assert.Contains("Get-GPO", Script(plan.Plan), StringComparison.Ordinal);
    }

    private static System.Xml.Linq.XDocument XDocumentOf(byte[] bytes) => GpoXml.Load(bytes);
}
