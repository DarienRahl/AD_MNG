using AdMng.Core.Batch;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Search;
using AdMng.Core.GroupPolicy.Security;

namespace AdMng.Core.Tests.GroupPolicy.Copy;

/// <summary>21.10 (W2): every kind with a writer is copied and removed, executed and undone (bytes and versions back).</summary>
public class CopySettingsKindsTests : CopySettingsTestBed
{
    public static TheoryData<string> Kinds() =>
    [
        nameof(GpoSettingUnitKind.AdministrativeTemplate), nameof(GpoSettingUnitKind.RegistryValue), nameof(GpoSettingUnitKind.SecurityValue),
        nameof(GpoSettingUnitKind.UserRight), nameof(GpoSettingUnitKind.RestrictedGroup), nameof(GpoSettingUnitKind.SecurityRegistryValue),
        nameof(GpoSettingUnitKind.Service), nameof(GpoSettingUnitKind.AuditSubcategory), nameof(GpoSettingUnitKind.Preference),
    ];

    /// <summary>A source GPO holding one unit of <paramref name="kind"/>, the kind and the definitions to use.</summary>
    private (Guid Source, GpoSettingUnitKind Kind, PolicyDefinitionSet? Definitions, string Path) Arrange(string kind)
    {
        var parsed = Enum.Parse<GpoSettingUnitKind>(kind);
        switch (parsed)
        {
            case GpoSettingUnitKind.AdministrativeTemplate:
            {
                var gpo = AddGpo("Źródło – szablony", computer: 1, machineNames: MachineRegistryPair);
                PutRegistry(gpo, false, Dword(@"Software\Policies\Contoso\Eksplorator", "NoPreview", 1));
                return (gpo, parsed, Definitions, GroupPolicyPlanner.MachineRegistryPolicyPath);
            }

            case GpoSettingUnitKind.RegistryValue:
            {
                var gpo = AddGpo("Źródło – rejestr", computer: 1, machineNames: MachineRegistryPair);
                PutRegistry(gpo, false, Text(@"Software\Contoso\Księgowość", "Kolor", "zielony"));
                return (gpo, parsed, null, GroupPolicyPlanner.MachineRegistryPolicyPath);
            }

            case GpoSettingUnitKind.AuditSubcategory:
            {
                var gpo = AddGpo("Źródło – inspekcja", computer: 1, machineNames: AuditPair);
                var audit = AuditPolicyDocument.New();
                var logon = SecuritySettingsCatalog.AuditSubcategories.Single(s => s.Id == "Logon");
                audit.SetSubcategory(logon.Guid, logon.CsvName, 3);
                Sysvol.Put(gpo, AuditPolicyDocument.RelativePath, audit.ToBytes());
                return (gpo, parsed, null, AuditPolicyDocument.RelativePath);
            }

            case GpoSettingUnitKind.Preference:
            {
                var gpo = AddGpo("Źródło – dyski", user: 1, userNames: DrivesPairs);
                PutDrives(gpo, Drive(@"\\fs01.contoso.test\Księgowość"));
                return (gpo, parsed, null, DrivesPath);
            }

            default:
            {
                var gpo = AddGpo("Źródło – zabezpieczenia " + kind, computer: 1, machineNames: SecurityPair);
                string[] lines = parsed switch
                {
                    GpoSettingUnitKind.SecurityValue => ["[System Access]", "MinimumPasswordLength = 14"],
                    GpoSettingUnitKind.UserRight => ["[Privilege Rights]", "SeRemoteInteractiveLogonRight = *S-1-5-32-544,*S-1-5-32-555"],
                    GpoSettingUnitKind.RestrictedGroup => ["[Group Membership]", $"*S-1-5-32-544__Members = *{Builder.DomainSid}-1500"],
                    GpoSettingUnitKind.SecurityRegistryValue => ["[Registry Values]", @"MACHINE\System\CurrentControlSet\Services\LanManServer\Parameters\RequireSecuritySignature=4,1"],
                    _ => ["[Service General Setting]", "\"Spooler\",4,\"\""],
                };
                PutTemplate(gpo, lines);
                return (gpo, parsed, null, SecurityTemplate.RelativePath);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Copy_IntoAnEmptyGpo_IsOneObjectPlan_ExecutesAndUndoes(string kind)
    {
        var (source, unitKind, definitions, path) = Arrange(kind);
        var target = AddGpo("Cel – pusty GPO");
        var before = Snapshot(target);
        var selection = await SelectAsync(source, u => u.Kind == unitKind, definitions);

        var plan = await CopyAsync(target, [selection], definitions: definitions);

        Assert.Equal(GpoUnitOutcomeKind.Added, Assert.Single(plan.Outcomes).Kind);
        Assert.True(plan.ChangesAnything);
        var steps = Assert.Single(plan.Plan.Objects).Changes;
        Assert.Equal([ChangeAction.Modify, ChangeAction.WriteGpoFile, ChangeAction.SetGpoVersion], steps.Select(s => s.Action));
        Assert.Equal(path, steps[1].Sysvol!.RelativePath);
        Assert.Null(steps[1].Sysvol!.OldContent);

        var sourceUnit = Assert.Single(await UnitsOfAsync(source, definitions), u => u.Kind == unitKind);
        await ExecuteAndUndoAsync(plan.Plan, () =>
        {
            var copied = Assert.Single(UnitsOfAsync(target, definitions).GetAwaiter().GetResult(), u => u.Kind == unitKind);
            Assert.Equal(sourceUnit.BaseId, copied.BaseId);
            Assert.True(HandUnits.SameValue(sourceUnit, copied));
            var user = path.StartsWith("User/", StringComparison.Ordinal);
            Assert.Equal(new GpoVersion(user ? 1 : 0, user ? 0 : 1), DirectoryVersion(target));
            Assert.Equal(DirectoryVersion(target), GptVersion(target));
        });

        Assert.Equal(before, Snapshot(target));
        Assert.Equal(DirectoryVersion(target), GptVersion(target));
        Assert.True(DirectoryVersion(target).ToInt32() > new GpoVersion(path.StartsWith("User/", StringComparison.Ordinal) ? 1 : 0, path.StartsWith("User/", StringComparison.Ordinal) ? 0 : 1).ToInt32());
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Remove_ExecutesAndUndoes(string kind)
    {
        var (source, unitKind, definitions, path) = Arrange(kind);
        var before = Snapshot(source);
        var selection = await SelectAsync(source, u => u.Kind == unitKind, definitions);

        var plan = await RemoveAsync(source, [selection], definitions);

        Assert.Equal(GpoUnitOutcomeKind.Removed, Assert.Single(plan.Outcomes).Kind);
        var steps = Assert.Single(plan.Plan.Objects).Changes;
        Assert.Equal([ChangeAction.WriteGpoFile, ChangeAction.SetGpoVersion], steps.Select(s => s.Action));
        Assert.Equal(path, steps[0].Sysvol!.RelativePath);
        Assert.Equal(Sysvol.Get(source, path), steps[0].Sysvol!.OldContent);

        await ExecuteAndUndoAsync(plan.Plan, () =>
            Assert.DoesNotContain(UnitsOfAsync(source, definitions).GetAwaiter().GetResult(), u => u.Kind == unitKind));

        Assert.Equal(before, Snapshot(source));
    }
}
