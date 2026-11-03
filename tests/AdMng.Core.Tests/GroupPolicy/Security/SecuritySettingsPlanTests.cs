using AdMng.Core.Batch;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Security;

namespace AdMng.Core.Tests.GroupPolicy.Security;

public class SecuritySettingsPlanTests : GroupPolicyTestBed
{
    private const string SecurityPair = "[{827D319E-6EAC-11D2-A4EA-00C04F79F83A}{803E14A0-B4FB-11D0-A0D0-00A0C90F574B}]";
    private const string AuditPair = "[{F3CCC681-B74C-4060-9F26-CD84525DCA2A}{0F3F3735-573D-9804-99E4-AB2A69BA5FD4}]";
    private const string GroupsPairs = "[{00000000-0000-0000-0000-000000000000}{79F92669-4224-476C-9C5C-6EFB4D87DF4A}][{17D89FEC-5C44-4972-B12D-241CAEF74509}{79F92669-4224-476C-9C5C-6EFB4D87DF4A}]";

    private static byte[] Template()
    {
        var document = SecurityTemplateDocument.New();
        document.SetPrivilegeRight("SeRemoteInteractiveLogonRight", ["S-1-5-32-544", "S-1-5-32-555"]);
        return document.ToBytes();
    }

    private static byte[] AuditCsv()
    {
        var document = AuditPolicyDocument.New();
        var logon = SecuritySettingsCatalog.AuditSubcategories.Single(s => s.Id == "Logon");
        document.SetSubcategory(logon.Guid, logon.CsvName, 3);
        return document.ToBytes();
    }

    private static byte[] Groups()
    {
        var document = GppGroupsDocument.New();
        document.Add(new GppLocalGroupEdit("Administrators (built-in)", "S-1-5-32-544", GppItemAction.Update, false, false,
            [new GppGroupMember(@"KONTOSO\Administratorzy stacji", "S-1-5-21-1-2-3-1500", GppMemberAction.Add)]), new DateTime(2026, 11, 3, 9, 0, 0));
        return document.ToBytes();
    }

    [Fact]
    public async Task Save_WritesTheThreeFiles_AddsTheirExtensions_AndRaisesTheComputerVersion()
    {
        var gpo = Sample.SitePrinters; // user version 1, computer 0
        await using var sysvol = await OpenSysvolAsync();
        var save = new SecuritySettingsSave
        {
            Template = new GpoFileSave { Content = Template() },
            Audit = new GpoFileSave { Content = AuditCsv() },
            Groups = new GpoFileSave { Content = Groups() },
        };

        var plan = await Planner.PlanSecuritySettingsAsync(Session, sysvol, gpo, save, Ct);
        var steps = plan.Objects.Single().Changes;

        Assert.Equal([ChangeAction.Modify, ChangeAction.WriteGpoFile, ChangeAction.WriteGpoFile, ChangeAction.WriteGpoFile, ChangeAction.SetGpoVersion],
            steps.Select(s => s.Action));
        Assert.Equal(GroupsPairs + SecurityPair + AuditPair, steps[0].Attributes.Single().NewValues.Single());

        await ExecuteOkAsync(plan);

        Assert.Equal(save.Template.Content, Sysvol.Get(gpo, SecurityTemplate.RelativePath));
        Assert.Equal(save.Audit.Content, Sysvol.Get(gpo, AuditPolicyDocument.RelativePath));
        Assert.Equal(save.Groups.Content, Sysvol.Get(gpo, GppGroupsDocument.RelativePath));
        Assert.Equal(GroupsPairs + SecurityPair + AuditPair, Value(GpoDn(gpo), "gPCMachineExtensionNames"));
        Assert.Equal(new GpoVersion(1, 1).ToInt32(), GptIni.ReadVersion(Sysvol.Get(gpo, GptIni.RelativePath)!));
        Assert.Single(SecurityTemplate.Parse(Sysvol.Get(gpo, SecurityTemplate.RelativePath)!).PrivilegeRights);
    }

    [Fact]
    public async Task Save_OfOneFile_WritesOnlyIt_AndKeepsOtherExtensions()
    {
        var gpo = Sample.SitePrinters;
        Session.SeedAttributes(GpoDn(gpo), new Dictionary<string, object?> { ["gPCMachineExtensionNames"] = "[{35378EAC-683F-11D2-A89A-00C04FBBCFA2}{0F6B957D-509E-11D1-A7CC-0000F87571E3}]" });
        await using var sysvol = await OpenSysvolAsync();

        var plan = await Planner.PlanSecuritySettingsAsync(Session, sysvol, gpo,
            new SecuritySettingsSave { Template = new GpoFileSave { Content = Template() }, Audit = new GpoFileSave { Original = AuditCsv(), Content = AuditCsv() } }, Ct);
        var steps = plan.Objects.Single().Changes;

        Assert.Equal([ChangeAction.Modify, ChangeAction.WriteGpoFile, ChangeAction.SetGpoVersion], steps.Select(s => s.Action));
        Assert.Equal("[{35378EAC-683F-11D2-A89A-00C04FBBCFA2}{0F6B957D-509E-11D1-A7CC-0000F87571E3}]" + SecurityPair, steps[0].Attributes.Single().NewValues.Single());
        Assert.Equal(SecurityTemplate.RelativePath, steps[1].Sysvol!.RelativePath);
    }

    [Fact]
    public async Task Save_WithoutChanges_PlansNothing()
    {
        await using var sysvol = await OpenSysvolAsync();
        var same = Template();

        var plan = await Planner.PlanSecuritySettingsAsync(Session, sysvol, Sample.SitePrinters,
            new SecuritySettingsSave { Template = new GpoFileSave { Original = same, Content = same } }, Ct);

        Assert.Empty(plan.Objects);
    }

    [Fact]
    public async Task Save_OfAFileChangedBehindTheEditor_IsAConflict()
    {
        var gpo = Sample.SitePrinters;
        var original = Template();
        Sysvol.Put(gpo, SecurityTemplate.RelativePath, original);
        await using var sysvol = await OpenSysvolAsync();
        var edited = SecurityTemplateDocument.Parse(original);
        edited.SetPrivilegeRight("SeDebugPrivilege", ["S-1-5-32-544"]);
        var plan = await Planner.PlanSecuritySettingsAsync(Session, sysvol, gpo,
            new SecuritySettingsSave { Template = new GpoFileSave { Original = original, Content = edited.ToBytes() } }, Ct);
        var other = SecurityTemplateDocument.New();
        other.Set(SecurityTemplateDocument.SystemAccess, "MinimumPasswordLength", "8");
        Sysvol.Put(gpo, SecurityTemplate.RelativePath, other.ToBytes());

        var result = await ExecuteAsync(plan);

        Assert.Contains(result.Objects.Single().Changes, c => !c.Succeeded && (c.ErrorMessage ?? string.Empty).StartsWith(BatchExecutor.ConflictMessagePrefix, StringComparison.Ordinal));
        Assert.Equal(other.ToBytes(), Sysvol.Get(gpo, SecurityTemplate.RelativePath));
    }

    [Fact]
    public async Task Save_WithTheComputerPartDisabled_Warns()
    {
        var gpo = Sample.SitePrinters;
        Session.SeedAttributes(GpoDn(gpo), new Dictionary<string, object?> { ["flags"] = "2" });
        await using var sysvol = await OpenSysvolAsync();

        var plan = await Planner.PlanSecuritySettingsAsync(Session, sysvol, gpo, new SecuritySettingsSave { Template = new GpoFileSave { Content = Template() } }, Ct);

        Assert.Contains(plan.Objects.Single().Changes.SelectMany(c => c.Issues), i => i.Code == IssueCodes.GpoComputerPartDisabled);
        Assert.Contains("no GroupPolicy cmdlet", plan.Objects.Single().PowerShell!.Commands.Single().Comment, StringComparison.Ordinal);
    }
}
