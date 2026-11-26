using AdMng.Core.Batch;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Search;
using AdMng.Core.GroupPolicy.Security;

namespace AdMng.Core.Tests.GroupPolicy.Copy;

/// <summary>Review findings of the K7 copy, remove and merge planners (21.5): each test pins one fix.</summary>
public class CopySettingsReviewTests : CopySettingsTestBed
{
    private const string MachineGroupsPath = "Machine/Preferences/Groups/Groups.xml";

    private static string Groups(string userAttributes) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><Groups clsid=\"{3125E937-EB16-4b4c-9934-544FC6D24D26}\">"
        + $"<User clsid=\"{{DF5F1855-51E5-4d24-8B1A-D9BDE98BA1D1}}\" name=\"serwis\" image=\"2\" changed=\"2026-10-01 08:00:00\" uid=\"{Guid.NewGuid().ToString("B").ToUpperInvariant()}\">"
        + $"<Properties action=\"U\" userName=\"serwis\" fullName=\"Konto serwisowe – Łódź\"{userAttributes}/></User></Groups>";

    [Fact]
    public async Task PolicyOwnedRefusal_NamesTheTargetGpo_WhosePolicyOwnsTheValue()
    {
        var source = AddGpo("Rejestr – źródło surowe", computer: 1, machineNames: MachineRegistryPair);
        PutRegistry(source, false, Dword(@"Software\Policies\Contoso\Aktualizacje\AU", "ScheduledInstallDay", 3));
        var target = AddGpo("Szablony – aktualizacje Żory", computer: 1, machineNames: MachineRegistryPair);
        PutRegistry(target, false,
            Dword(@"Software\Policies\Contoso\Aktualizacje\AU", "NoAutoUpdate", 0),
            Dword(@"Software\Policies\Contoso\Aktualizacje\AU", "AUOptions", 3),
            Dword(@"Software\Policies\Contoso\Aktualizacje\AU", "ScheduledInstallDay", 1));
        var unit = Assert.Single(await UnitsOfAsync(source, Definitions));

        var plan = await CopyAsync(target, [new GpoUnitSelection(source, unit.Id)], definitions: Definitions);

        var issue = Assert.Single(Target(plan).Issues, i => i.Code == IssueCodes.GpoSettingPolicyOwned);
        Assert.Equal("Szablony – aktualizacje Żory", issue.Arguments[0]);
    }

    [Fact]
    public async Task Replace_OfATargetItemWithAPassword_IsRefused_AndNothingIsWritten()
    {
        var source = AddGpo("Konta – źródło", computer: 1);
        Sysvol.PutText(source, MachineGroupsPath, Groups(string.Empty));
        var target = AddGpo("Konta – cel", computer: 1);
        Sysvol.PutText(target, MachineGroupsPath, Groups(" cpassword=\"j1Uyj3Vx8TY9LtLZil2uAuZkFQA/4latT76ZwgdHdhw\""));

        var plan = await CopyAsync(target, [await SelectAsync(source, u => u.Kind == GpoSettingUnitKind.Preference)]);

        var outcome = Assert.Single(plan.Outcomes);
        Assert.Equal(GpoUnitOutcomeKind.Refused, outcome.Kind);
        Assert.Equal(IssueCodes.GpoSettingUnsupported, outcome.IssueCode);
        Assert.True(Target(plan).IsNoOp);
    }

    [Fact]
    public async Task Writer_RefusesMaskedSettingsAndPasswordItems_EvenWhenTheUnitHasNoRefusal()
    {
        var source = AddGpo("Konta – hasło", computer: 1);
        Sysvol.PutText(source, MachineGroupsPath, Groups(" cpassword=\"j1Uyj3Vx8TY9LtLZil2uAuZkFQA/4latT76ZwgdHdhw\""));
        PutRegistry(source, false, Text(@"Software\Microsoft\Windows NT\CurrentVersion\Winlogon", "DefaultPassword", "Tajne!2026"));
        await using var sysvol = await OpenSysvolAsync();
        var content = await GroupPolicyReader.ReadFolderAsync(sysvol, source, Ct);
        var units = await UnitsOfAsync(source);
        var writer = new GpoSettingsWriter(new GpoFolderContent([], new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)), null, _ => null, DateTime.Now);

        // A unit layer that forgot the refusal, and one that also dropped the masked marker of the cpassword.
        var registry = Assert.Single(units, u => u.Kind == GpoSettingUnitKind.RegistryValue) with { Refusal = null };
        var item = Assert.Single(units, u => u.Kind == GpoSettingUnitKind.Preference);
        var unmasked = item with { Refusal = null, Settings = [.. item.Settings.Where(s => s.Kind != GpoSettingKind.Masked)] };

        Assert.Equal(IssueCodes.GpoSettingSecret, writer.CheckCopy(registry, content));
        Assert.Equal(IssueCodes.GpoSettingSecret, writer.CheckCopy(unmasked, content));
        Assert.Empty(writer.ChangedFiles());
    }

    [Fact]
    public async Task AuditValue_TheDocumentCannotWrite_IsRefused_NotAnException()
    {
        var logon = SecuritySettingsCatalog.AuditSubcategories.Single(s => s.Id == "Logon");
        var source = AddGpo("Inspekcja – nowsza", computer: 1, machineNames: AuditPair);
        Sysvol.PutText(source, AuditPolicyDocument.RelativePath,
            "Machine Name,Policy Target,Subcategory,Subcategory GUID,Inclusion Setting,Exclusion Setting,Setting Value\r\n"
            + $",System,{logon.CsvName},{logon.Guid:B},Success and Failure,,4\r\n");
        var target = AddGpo("Inspekcja – cel");
        var units = await UnitsOfAsync(source);
        var unit = Assert.Single(units, u => u.Kind == GpoSettingUnitKind.AuditSubcategory);

        var plan = await CopyAsync(target, [new GpoUnitSelection(source, unit.Id)]);

        var outcome = Assert.Single(plan.Outcomes);
        Assert.Equal(GpoUnitOutcomeKind.Refused, outcome.Kind);
        Assert.Equal(IssueCodes.GpoSettingUnsupported, outcome.IssueCode);
    }

    [Fact]
    public async Task Remove_OfAUnitSelectedTwice_RemovesItOnce_WithoutAFalseRefusal()
    {
        var gpo = AddGpo("Dyski – podwójnie", user: 1, userNames: DrivesPairs);
        PutDrives(gpo, Drive(@"\\fs01.contoso.test\Księgowość"));
        var selection = await SelectAsync(gpo, u => u.Kind == GpoSettingUnitKind.Preference);

        var plan = await RemoveAsync(gpo, [selection, selection]);

        Assert.Equal(GpoUnitOutcomeKind.Removed, Assert.Single(plan.Outcomes).Kind);
        Assert.DoesNotContain(Target(plan).Issues, i => i.Severity != IssueSeverity.Info);
    }

    [Fact]
    public async Task ASelectionFromTheTargetItself_DoesNotMakeTheSourcesUnitAmbiguous()
    {
        var source = AddGpo("Hasła – źródło", computer: 1, machineNames: SecurityPair);
        PutTemplate(source, "[System Access]", "MinimumPasswordLength = 14");
        var target = AddGpo("Hasła – cel", computer: 1, machineNames: SecurityPair);
        PutTemplate(target, "[System Access]", "MinimumPasswordLength = 8");

        var plan = await CopyAsync(target, [await SelectAsync(source, Key("MinimumPasswordLength")), await SelectAsync(target, Key("MinimumPasswordLength"))]);

        Assert.Equal([GpoUnitOutcomeKind.Replaced, GpoUnitOutcomeKind.Refused], plan.Outcomes.Select(o => o.Kind));
        Assert.DoesNotContain(Target(plan).Issues, i => i.Code == IssueCodes.GpoSettingAmbiguous);
    }

    [Fact]
    public async Task Merge_UnitsOfADisabledHalfOfA_AreSkipped_EvenWhenTheyCouldNotBeCopied()
    {
        // The user half is disabled and holds a GPP password item (it would block the merge if it applied).
        var a = AddGpo("Konta – A", user: 1, computer: 1, machineNames: SecurityPair, flags: (int)GpoStatus.UserSettingsDisabled);
        PutTemplate(a, "[System Access]", "MinimumPasswordLength = 14");
        Sysvol.PutText(a, "User/Preferences/Groups/Groups.xml", Groups(" cpassword=\"j1Uyj3Vx8TY9LtLZil2uAuZkFQA/4latT76ZwgdHdhw\""));
        var b = AddGpo("Konta – B");

        var plan = await MergeAsync(a, b);

        Assert.Equal(0, plan.Plan.ErrorCount);
        Assert.Contains(plan.Outcomes, o => o.Kind == GpoUnitOutcomeKind.Skipped && o.IssueCode == IssueCodes.GpoSettingSourcePartDisabled);
        Assert.Contains(plan.Outcomes, o => o.Kind == GpoUnitOutcomeKind.Added);
        Assert.DoesNotContain(IssuesOf(plan.Plan), i => i.Code is IssueCodes.GpoMergeNotCopyable or IssueCodes.GpoSettingSecret);
        await ExecuteOkAsync(plan.Plan);
        Assert.Null(Sysvol.Get(b, "User/Preferences/Groups/Groups.xml"));
    }
}
