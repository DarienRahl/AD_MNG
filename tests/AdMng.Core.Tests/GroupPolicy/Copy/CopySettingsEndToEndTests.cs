using System.Text;
using AdMng.Core.Batch;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Search;
using AdMng.Core.GroupPolicy.Security;
using AdMng.Testing;

namespace AdMng.Core.Tests.GroupPolicy.Copy;

/// <summary>
/// The K7 planners on the real setting-unit layer (W1's <see cref="GpoSettingUnits"/>, principal resolver and
/// <see cref="GroupPolicyReader.ReadSettings"/>): skipped until wave 1 is merged (the lead removes the skips).
/// </summary>
public class CopySettingsEndToEndTests : GroupPolicyTestBed
{
    private const string Wave1 = "K7 wave 1 merge";
    private const string SecurityPair = "[{827D319E-6EAC-11D2-A4EA-00C04F79F83A}{803E14A0-B4FB-11D0-A0D0-00A0C90F574B}]";
    private const string DrivesPairs = "[{00000000-0000-0000-0000-000000000000}{2EA1A81B-48E5-45E9-8BB7-A6E3AC170006}][{5794DAFD-BE60-433F-88A2-1A31939AC01F}{2EA1A81B-48E5-45E9-8BB7-A6E3AC170006}]";

    private async Task<GpoUnitSelection> SelectAsync(Guid gpo, Func<GpoSettingUnit, bool> match, PolicyDefinitionSet? definitions = null)
    {
        await using var sysvol = await OpenSysvolAsync();
        var content = await GroupPolicyReader.ReadFolderAsync(sysvol, gpo, Ct);
        var unit = Assert.Single(GpoSettingUnits.Of(content, new GpoUnitContext(definitions, Builder.DomainSid)), u => match(u));
        return new GpoUnitSelection(gpo, unit.Id, unit.ItemId, unit.ValueFingerprint);
    }

    private async Task<GpoSettingsPlan> CopyAsync(Guid target, GpoUnitSelection unit, PolicyDefinitionSet? definitions = null)
    {
        await using var sysvol = await OpenSysvolAsync();
        return await Planner.PlanCopySettingsAsync(Session, sysvol, new GpoSettingsCopyRequest { TargetId = target, Units = [unit], Definitions = definitions }, Ct);
    }

    [Fact(Skip = Wave1)]
    public async Task UserRight_FromTheSample_IsCopied_AndUndone()
    {
        var target = Builder.AddManagedGroupPolicy(Sysvol, "Cel – prawa", 0, 0);
        var plan = await CopyAsync(target, await SelectAsync(Sample.ServerSecurity, u => u.Kind == GpoSettingUnitKind.UserRight));

        Assert.Equal(GpoUnitOutcomeKind.Added, Assert.Single(plan.Outcomes).Kind);
        var result = await ExecuteOkAsync(plan.Plan);
        Assert.Equal(["S-1-5-32-544"], SecurityTemplate.Parse(Sysvol.Get(target, SecurityTemplate.RelativePath)).PrivilegeRights.Single().Principals);
        Assert.Equal(SecurityPair, Value(GpoDn(target), "gPCMachineExtensionNames"));

        await ExecuteOkAsync(new RollbackPlanner().CreateRollbackPlan(result));
        Assert.Null(Sysvol.Get(target, SecurityTemplate.RelativePath));
    }

    [Fact(Skip = Wave1)]
    public async Task DriveMap_WithItsPairs_IsCopiedWithANewUid()
    {
        var source = Builder.AddManagedGroupPolicy(Sysvol, "Dyski – źródło", 1, 0, attributes: new Dictionary<string, object?> { ["gPCUserExtensionNames"] = DrivesPairs });
        Sysvol.Put(source, "User/Preferences/Drives/Drives.xml", Sysvol.Get(Sample.DriveMappings, "User/Preferences/Drives/Drives.xml")!);
        var target = Builder.AddManagedGroupPolicy(Sysvol, "Dyski – cel", 0, 0);

        var plan = await CopyAsync(target, await SelectAsync(source, u => u.Kind == GpoSettingUnitKind.Preference));
        await ExecuteOkAsync(plan.Plan);

        var xml = Encoding.UTF8.GetString(Sysvol.Get(target, "User/Preferences/Drives/Drives.xml")!);
        Assert.Contains("Księgowość", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("{0D1F7F22-6D1B-4A35-9D9C-0E3D9F3F0001}", xml, StringComparison.Ordinal);
        Assert.Equal(DrivesPairs, Value(GpoDn(target), "gPCUserExtensionNames"));
    }

    [Fact(Skip = Wave1)]
    public async Task Policy_IsCopiedByState_WithTheIndexDefinitions()
    {
        var definitions = SampleAdministrativeTemplates.LoadPolish();
        var source = Builder.AddManagedGroupPolicy(Sysvol, "Szablony – źródło", 0, 1);
        Sysvol.Put(source, GroupPolicyPlanner.MachineRegistryPolicyPath,
            RegistryPolFile.Write([new RegistryPolEntry(@"Software\Policies\Contoso\Eksplorator", "NoPreview", RegistryFormat.RegDword, RegistryPolFile.DwordData(1))]));
        var target = Builder.AddManagedGroupPolicy(Sysvol, "Szablony – cel", 0, 0);

        var plan = await CopyAsync(target, await SelectAsync(source, u => u.Kind == GpoSettingUnitKind.AdministrativeTemplate, definitions), definitions);
        await ExecuteOkAsync(plan.Plan);

        Assert.Equal(Sysvol.Get(source, GroupPolicyPlanner.MachineRegistryPolicyPath), Sysvol.Get(target, GroupPolicyPlanner.MachineRegistryPolicyPath));
    }

    [Fact(Skip = Wave1)]
    public async Task RemoveAndMerge_RunOnTheRealUnits()
    {
        var a = Builder.AddManagedGroupPolicy(Sysvol, "Scalanie – A", 0, 1);
        Sysvol.Put(a, SecurityTemplate.RelativePath, FakeSysvolFileReader.Utf16(FakeDirectoryBuilder.Template("[System Access]", "MinimumPasswordLength = 14", "LockoutBadCount = 5")));
        var b = Builder.AddManagedGroupPolicy(Sysvol, "Scalanie – B", 0, 0);
        await using var sysvol = await OpenSysvolAsync();

        var remove = await Planner.PlanRemoveSettingsAsync(Session, sysvol, a, [await SelectAsync(a, u => u.Settings.Any(s => s.Key.EndsWith("LockoutBadCount", StringComparison.Ordinal)))], null, Ct);
        Assert.Equal(GpoUnitOutcomeKind.Removed, Assert.Single(remove.Outcomes).Kind);
        await ExecuteOkAsync(remove.Plan);

        var merge = await Planner.PlanMergeAsync(Session, sysvol, new GpoMergeRequest { SourceId = a, TargetId = b }, Ct);
        Assert.Equal(0, merge.Plan.ErrorCount);
        await ExecuteOkAsync(merge.Plan);
        Assert.Equal("14", SecurityTemplateDocument.Parse(Sysvol.Get(b, SecurityTemplate.RelativePath)).Get("System Access", "MinimumPasswordLength"));
        Assert.Null(SecurityTemplateDocument.Parse(Sysvol.Get(b, SecurityTemplate.RelativePath)).Get("System Access", "LockoutBadCount"));
        Assert.Equal("3", Value(GpoDn(a), "flags"));
    }
}
