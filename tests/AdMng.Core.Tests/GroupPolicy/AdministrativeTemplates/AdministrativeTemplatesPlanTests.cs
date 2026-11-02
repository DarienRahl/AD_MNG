using AdMng.Core.Batch;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Testing;
using static AdMng.Core.Tests.GroupPolicy.AdministrativeTemplates.RegistryPolicyDocumentTests;

namespace AdMng.Core.Tests.GroupPolicy.AdministrativeTemplates;

public class AdministrativeTemplatesPlanTests : GroupPolicyTestBed
{
    private const string RegistryAndMachineTool = "[{35378EAC-683F-11D2-A89A-00C04FBBCFA2}{0F6B957D-509E-11D1-A7CC-0000F87571E3}]";
    private static readonly PolicyDefinitionSet Set = SampleAdministrativeTemplates.LoadPolish();

    private static byte[] Enabled(string policyId, RegistryPolicyDocument? document = null, params (string Id, PolicyElementValue Value)[] values)
    {
        document ??= new RegistryPolicyDocument();
        PolicyEngine.Write(Set.Find(policyId)!.Policy, PolicySetting.Enabled(values.ToDictionary(v => v.Id, v => v.Value)), document);
        return document.ToBytes();
    }

    [Fact]
    public async Task Save_WritesRegistryPol_AddsTheExtensions_AndRaisesTheComputerVersion()
    {
        var gpo = Sample.SitePrinters; // no computer settings yet: user version 1, computer 0
        await using var sysvol = await OpenSysvolAsync();
        var content = Enabled(SampleAdministrativeTemplates.HidePreview);

        var plan = await Planner.PlanAdministrativeTemplatesAsync(Session, sysvol, gpo, new RegistryPolicySave { Content = content }, null, Ct);
        var steps = plan.Objects.Single().Changes;

        Assert.Equal([ChangeAction.Modify, ChangeAction.WriteGpoFile, ChangeAction.SetGpoVersion], steps.Select(s => s.Action));
        Assert.Equal(RegistryAndMachineTool, steps[0].Attributes.Single().NewValues.Single());
        Assert.Equal(GroupPolicyPlanner.MachineRegistryPolicyPath, steps[1].Sysvol!.RelativePath);
        Assert.Null(steps[1].Sysvol!.OldContent);

        await ExecuteOkAsync(plan);

        Assert.Equal(content, Sysvol.Get(gpo, "Machine/Registry.pol"));
        Assert.Equal(RegistryAndMachineTool, Value(GpoDn(gpo), "gPCMachineExtensionNames"));
        Assert.Equal(new GpoVersion(1, 1).ToInt32().ToString(System.Globalization.CultureInfo.InvariantCulture), Value(GpoDn(gpo), "versionNumber"));
        Assert.Equal(new GpoVersion(1, 1).ToInt32(), GptIni.ReadVersion(Sysvol.Get(gpo, GptIni.RelativePath)!));
    }

    [Fact]
    public async Task Save_OfTheUserHalf_MergesTheExtensionsInOrder_AndRaisesTheUserVersion()
    {
        var gpo = Sample.DriveMappings; // user version 2, GPT.INI behind
        var gpc = GpoDn(gpo);
        Session.SeedAttributes(gpc, new Dictionary<string, object?> { ["gPCUserExtensionNames"] = "[{5794DAFD-BE60-433F-88A2-1A31939AC01F}{2EA1A81B-48E5-45E9-8BB7-A6E3AC170006}]" });
        await using var sysvol = await OpenSysvolAsync();
        var content = Enabled(SampleAdministrativeTemplates.DriveMappings, null, ("Mapy", new ListElementValue([new("P", @"\\pliki\Wydział Prawa")])));

        var plan = await Planner.PlanAdministrativeTemplatesAsync(Session, sysvol, gpo, null, new RegistryPolicySave { Content = content }, Ct);
        await ExecuteOkAsync(plan);

        // The Registry extension's GUID sorts before the drive maps one.
        Assert.Equal("[{35378EAC-683F-11D2-A89A-00C04FBBCFA2}{0F6B957E-509E-11D1-A7CC-0000F87571E3}][{5794DAFD-BE60-433F-88A2-1A31939AC01F}{2EA1A81B-48E5-45E9-8BB7-A6E3AC170006}]",
            Value(gpc, "gPCUserExtensionNames"));
        Assert.Equal(content, Sysvol.Get(gpo, "User/Registry.pol"));
        Assert.Equal(new GpoVersion(3, 0).ToInt32(), GptIni.ReadVersion(Sysvol.Get(gpo, GptIni.RelativePath)!));
    }

    [Fact]
    public async Task Save_WithExtensionsAlreadyThere_LeavesTheAttributeAlone()
    {
        var gpo = Sample.SitePrinters;
        Session.SeedAttributes(GpoDn(gpo), new Dictionary<string, object?> { ["gPCMachineExtensionNames"] = RegistryAndMachineTool.ToLowerInvariant() });
        await using var sysvol = await OpenSysvolAsync();

        var plan = await Planner.PlanAdministrativeTemplatesAsync(Session, sysvol, gpo, new RegistryPolicySave { Content = Enabled(SampleAdministrativeTemplates.HidePreview) }, null, Ct);

        Assert.Equal([ChangeAction.WriteGpoFile, ChangeAction.SetGpoVersion], plan.Objects.Single().Changes.Select(s => s.Action));
    }

    [Fact]
    public async Task Save_OfAPolicyWithAClientExtension_AddsThatExtensionWithTheTool()
    {
        var gpo = Sample.SitePrinters;
        await using var sysvol = await OpenSysvolAsync();
        var cse = Guid.Parse("C6DC5466-785A-11D2-84D0-00C04FB169F7");

        var plan = await Planner.PlanAdministrativeTemplatesAsync(Session, sysvol, gpo,
            new RegistryPolicySave { Content = Enabled(SampleAdministrativeTemplates.HidePreview), ClientExtensions = [cse] }, null, Ct);

        Assert.Equal(RegistryAndMachineTool + "[{C6DC5466-785A-11D2-84D0-00C04FB169F7}{0F6B957D-509E-11D1-A7CC-0000F87571E3}]",
            plan.Objects.Single().Changes[0].Attributes.Single().NewValues.Single());
    }

    [Fact]
    public async Task Save_WithoutChanges_PlansNothing()
    {
        var gpo = Sample.SitePrinters;
        var existing = Enabled(SampleAdministrativeTemplates.HidePreview);
        await using var sysvol = await OpenSysvolAsync();

        var same = await Planner.PlanAdministrativeTemplatesAsync(Session, sysvol, gpo, new RegistryPolicySave { Original = existing, Content = existing }, null, Ct);
        var emptyForNone = await Planner.PlanAdministrativeTemplatesAsync(Session, sysvol, gpo, new RegistryPolicySave { Content = RegistryPolFile.Empty }, null, Ct);

        Assert.Empty(same.Objects);
        Assert.Empty(emptyForNone.Objects);
    }

    [Fact]
    public async Task Save_OfAFileChangedBehindTheEditor_IsAConflict_AndChangesNothing()
    {
        var gpo = Sample.SitePrinters;
        var original = Enabled(SampleAdministrativeTemplates.HidePreview);
        Sysvol.Put(gpo, "Machine/Registry.pol", original);
        await using var sysvol = await OpenSysvolAsync();
        var plan = await Planner.PlanAdministrativeTemplatesAsync(Session, sysvol, gpo,
            new RegistryPolicySave { Original = original, Content = Disabled(original) }, null, Ct);
        var other = RegistryPolFile.Write([Dword(@"Software\Policies\Contoso\Inny", "X", 1)]);
        Sysvol.Put(gpo, "Machine/Registry.pol", other); // another administrator saved meanwhile

        var result = await ExecuteAsync(plan);

        Assert.Contains(result.Objects.Single().Changes, c => !c.Succeeded && (c.ErrorMessage ?? string.Empty).StartsWith(BatchExecutor.ConflictMessagePrefix, StringComparison.Ordinal));
        Assert.Equal(other, Sysvol.Get(gpo, "Machine/Registry.pol"));
        Assert.Equal(new GpoVersion(1, 0).ToInt32().ToString(System.Globalization.CultureInfo.InvariantCulture), Value(GpoDn(gpo), "versionNumber"));
    }

    [Fact]
    public async Task Save_IntoADisabledComputerPart_Warns()
    {
        var gpo = Sample.SitePrinters;
        Session.SeedAttributes(GpoDn(gpo), new Dictionary<string, object?> { ["flags"] = 2 });
        await using var sysvol = await OpenSysvolAsync();

        var plan = await Planner.PlanAdministrativeTemplatesAsync(Session, sysvol, gpo, new RegistryPolicySave { Content = Enabled(SampleAdministrativeTemplates.HidePreview) }, null, Ct);

        Assert.Contains(plan.Objects.Single().Changes.SelectMany(c => c.Issues), i => i.Code == IssueCodes.GpoComputerPartDisabled && i.Severity == IssueSeverity.Warning);
        Assert.Equal(0, plan.ErrorCount);
    }

    [Fact]
    public async Task PowerShell_SetsChangedValues_DisablesDeletions_RemovesForgottenOnes()
    {
        var gpo = Sample.SitePrinters;
        var before = new RegistryPolicyDocument();
        PolicyEngine.Write(Set.Find(SampleAdministrativeTemplates.HidePreview)!.Policy, PolicySetting.Enabled(new Dictionary<string, PolicyElementValue>()), before);
        var original = before.ToBytes();
        var after = before.Clone();
        PolicyEngine.Write(Set.Find(SampleAdministrativeTemplates.HidePreview)!.Policy, PolicySetting.NotConfigured, after);
        PolicyEngine.Write(Set.Find(SampleAdministrativeTemplates.Updates)!.Policy, PolicySetting.Disabled, after);
        PolicyEngine.Write(Set.Find(SampleAdministrativeTemplates.TrustedSites)!.Policy, PolicySetting.Enabled(new Dictionary<string, PolicyElementValue>
        {
            ["Lista"] = new ListElementValue([new("", "https://intranet.contoso.test")]),
        }), after);
        Sysvol.Put(gpo, "Machine/Registry.pol", original);
        await using var sysvol = await OpenSysvolAsync();

        var plan = await Planner.PlanAdministrativeTemplatesAsync(Session, sysvol, gpo, new RegistryPolicySave { Original = original, Content = after.ToBytes() }, null, Ct);
        var script = Script(plan);

        Assert.Contains("Set-GPRegistryValue -Guid '" + gpo.ToString("D") + "' -Domain 'contoso.test' -Server $server -Key 'HKLM\\Software\\Policies\\Contoso\\Aktualizacje\\AU' -ValueName 'NoAutoUpdate' -Type 'DWord' -Value 1", script, StringComparison.Ordinal);
        Assert.Contains("-ValueName 'AUOptions' -Disable", script, StringComparison.Ordinal);
        Assert.Contains("-Key 'HKLM\\Software\\Policies\\Contoso\\Przegladarka\\ZaufaneWitryny' -Disable", script, StringComparison.Ordinal);
        Assert.Contains("-ValueName '1' -Type 'String' -Value 'https://intranet.contoso.test'", script, StringComparison.Ordinal);
        Assert.Contains("Remove-GPRegistryValue -Guid '" + gpo.ToString("D") + "' -Domain 'contoso.test' -Server $server -Key 'HKLM\\Software\\Policies\\Contoso\\Eksplorator'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PowerShell_WritesALiteralList()
    {
        var plan = new ChangePlan
        {
            Title = "t",
            Objects =
            [
                new ObjectPlan
                {
                    DistinguishedName = "CN=x",
                    DisplayName = "x",
                    Kind = AdMng.Core.Model.ObjectKind.GroupPolicyContainer,
                    Changes = [new PlannedChange { Action = ChangeAction.Modify, Attributes = [new AttributeChange("description", AttributeChangeKind.Replace, [], ["x"])] }],
                    PowerShell = PowerShellEquivalent.Of(PowerShellCommand.Of("Set-GPRegistryValue", PowerShellParameter.Literals("Value", ["Dzień dobry", "it's"]))),
                },
            ],
        };

        Assert.Contains("Set-GPRegistryValue -Value @('Dzień dobry', 'it''s')", new PowerShellExporter().Export(plan, null), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CentralStore_IsReadThroughTheSysvolConnection()
    {
        foreach (var (path, content) in SampleAdministrativeTemplates.Files())
        {
            Sysvol.PutPolicyDefinition(path, content);
        }

        await using var sysvol = await Sysvol.OpenAsync(Session, Ct);
        var (set, exists) = await PolicyDefinitionLoader.LoadAsync(new SysvolPolicyDefinitionSource(sysvol, "Magazyn centralny"), ["pl-PL"], Ct);

        Assert.True(exists);
        Assert.Equal(7, set.PolicyCount);
        Assert.Empty(set.Issues);
        Assert.All(Sysvol.Calls.Where(c => c.Operation != "Open"), c => Assert.Contains(c.Operation, new[] { "ListDefinitions", "ReadDefinitions" }));
        Assert.Empty(Sysvol.Writes);
    }

    [Fact]
    public async Task CentralStore_ThatDoesNotExist_IsSaidSo()
    {
        await using var sysvol = await Sysvol.OpenAsync(Session, Ct);

        var (set, exists) = await PolicyDefinitionLoader.LoadAsync(new SysvolPolicyDefinitionSource(sysvol, "x"), ["pl-PL"], Ct);

        Assert.False(exists);
        Assert.Equal(0, set.PolicyCount);
    }

    [Fact]
    public void ExtensionNames_ParseFormatAndMerge()
    {
        var registry = GpoExtensionNames.Registry;
        var tool = GpoExtensionNames.AdministrativeTemplatesMachineTool;

        Assert.Equal(RegistryAndMachineTool, GpoExtensionNames.With(null, registry, tool));
        Assert.Equal(RegistryAndMachineTool, GpoExtensionNames.With("", registry, tool));
        Assert.Equal("[{35378EAC-683F-11D2-A89A-00C04FBBCFA2}{0F6B957D-509E-11D1-A7CC-0000F87571E3}{D02B1F72-3407-48AE-BA88-E8213C6761F1}]",
            GpoExtensionNames.With("[{35378EAC-683F-11D2-A89A-00C04FBBCFA2}{D02B1F72-3407-48AE-BA88-E8213C6761F1}]", registry, tool));
        Assert.Equal("[{827D319E-6EAC-11D2-A4EA-00C04F79F83A}{803E14A0-B4FB-11D0-A0D0-00A0C90F574B}]" + RegistryAndMachineTool is var _ ?
            "[{35378EAC-683F-11D2-A89A-00C04FBBCFA2}{0F6B957D-509E-11D1-A7CC-0000F87571E3}][{827D319E-6EAC-11D2-A4EA-00C04F79F83A}{803E14A0-B4FB-11D0-A0D0-00A0C90F574B}]" : null,
            GpoExtensionNames.With("[{827D319E-6EAC-11D2-A4EA-00C04F79F83A}{803E14A0-B4FB-11D0-A0D0-00A0C90F574B}]", registry, tool));
        Assert.Equal("garbage[{35378EAC-683F-11D2-A89A-00C04FBBCFA2}{0F6B957D-509E-11D1-A7CC-0000F87571E3}]", GpoExtensionNames.With("garbage", registry, tool));
        Assert.Null(GpoExtensionNames.Parse("[{not-a-guid}]"));
    }

    private static byte[] Disabled(byte[] original)
    {
        var document = RegistryPolicyDocument.Parse(original);
        PolicyEngine.Write(Set.Find(SampleAdministrativeTemplates.HidePreview)!.Policy, PolicySetting.Disabled, document);
        return document.ToBytes();
    }
}
