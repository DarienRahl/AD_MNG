using System.Text;
using AdMng.Core.Batch;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Diagnostics;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Testing;

namespace AdMng.Core.Tests.GroupPolicy.Diagnostics;

/// <summary>The planner repairs the diagnostics offer: extension names and duplicate links, executed on the fake directory and SYSVOL.</summary>
public sealed class DiagnosticsRepairPlannerTests : GroupPolicyTestBed
{
    private const string SecurityPair = "[{827D319E-6EAC-11D2-A4EA-00C04F79F83A}{803E14A0-B4FB-11D0-A0D0-00A0C90F574B}]";

    [Fact]
    public async Task RepairExtensionNames_AddsTheSecurityPair_AndBumpsTheComputerVersion()
    {
        await using var sysvol = await OpenSysvolAsync();
        var gpo = Sample.DefaultDomainControllersPolicy;

        var plan = await Planner.PlanRepairExtensionNamesAsync(Session, sysvol, gpo, Ct);

        var steps = Assert.Single(plan.Objects).Changes;
        Assert.Equal([ChangeAction.Modify, ChangeAction.SetGpoVersion], steps.Select(s => s.Action));
        var names = Assert.Single(steps[0].Attributes);
        Assert.Equal((GroupPolicyAttributes.MachineExtensionNames, SecurityPair), (names.Attribute, names.NewValues.Single()));
        Assert.Contains("have no GroupPolicy cmdlet", Script(plan), StringComparison.Ordinal);

        await ExecuteOkAsync(plan);

        Assert.Equal(SecurityPair, Value(GpoDn(gpo), GroupPolicyAttributes.MachineExtensionNames));
        Assert.Equal(new GpoVersion(0, 2).ToInt32().ToString(System.Globalization.CultureInfo.InvariantCulture), Value(GpoDn(gpo), GroupPolicyAttributes.VersionNumber));
        Assert.Equal(new GpoVersion(0, 2), await GroupPolicyPlanner.GptVersionAsync(sysvol, gpo, Ct));
    }

    [Fact]
    public async Task RepairExtensionNames_BothHalves_RegistryAndLocalUsersAndGroups()
    {
        var gpo = Sample.SitePrinters; // user 1, computer 0, no extension names
        Sysvol.Put(gpo, GroupPolicyPlanner.UserRegistryPolicyPath,
            RegistryPolFile.Write([new RegistryPolEntry(@"Software\Policies\Contoso\Drukarki", "Domyślna", RegistryFormat.RegDword, RegistryPolFile.DwordData(1))]));
        Sysvol.PutText(gpo, GppGroupsParser.RelativePath, FakeDirectoryBuilder.Groups(
            FakeDirectoryBuilder.LocalGroup("Administrators (built-in)", "S-1-5-32-544", [("CONTOSO\\GG_IT", Builder.SidOf(Company.GroupIt), "ADD")])));
        await using var sysvol = await OpenSysvolAsync();

        var plan = await Planner.PlanRepairExtensionNamesAsync(Session, sysvol, gpo, Ct);
        await ExecuteOkAsync(plan);

        Assert.Equal(
            "[{00000000-0000-0000-0000-000000000000}{79F92669-4224-476C-9C5C-6EFB4D87DF4A}][{17D89FEC-5C44-4972-B12D-241CAEF74509}{79F92669-4224-476C-9C5C-6EFB4D87DF4A}]",
            Value(GpoDn(gpo), GroupPolicyAttributes.MachineExtensionNames));
        Assert.Equal("[{35378EAC-683F-11D2-A89A-00C04FBBCFA2}{0F6B957E-509E-11D1-A7CC-0000F87571E3}]", Value(GpoDn(gpo), GroupPolicyAttributes.UserExtensionNames));
        Assert.Equal(new GpoVersion(2, 1), await GroupPolicyPlanner.GptVersionAsync(sysvol, gpo, Ct));

        // The diagnostics no longer report the GPO.
        var snapshot = await GroupPolicyDiagnosticsReader.ReadAsync(Session, sysvol, new GpoDiagnosticsRequest(gpo, GpoDiagnosticsDepth.Full), null, Ct);
        Findings.None(GroupPolicyDiagnostics.Analyze(snapshot), GpoFindingCodes.ExtensionNamesMissing);
    }

    [Fact]
    public async Task RepairExtensionNames_NothingWhenComplete_NorForAnEmptyOrUnreadableFile()
    {
        Sysvol.Put(Sample.SitePrinters, GroupPolicyPlanner.MachineRegistryPolicyPath, RegistryPolFile.Empty);
        Sysvol.Put(Sample.SitePrinters, GppGroupsParser.RelativePath, Encoding.UTF8.GetBytes("<Groups><Group"));
        await using var sysvol = await OpenSysvolAsync();

        var complete = await Planner.PlanRepairExtensionNamesAsync(Session, sysvol, Sample.WorkstationPasswords, Ct);
        var empty = await Planner.PlanRepairExtensionNamesAsync(Session, sysvol, Sample.SitePrinters, Ct);

        Assert.Empty(complete.Objects);
        Assert.Empty(empty.Objects);
    }

    [Fact]
    public void MissingExtensions_ByHalf_TheCoreEntryIsNoClientExtension()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["machine/microsoft/windows nt/secedit/gpttmpl.inf"] = FakeSysvolFileReader.Utf16(FakeDirectoryBuilder.Template("[System Access]", "MinimumPasswordLength = 12")),
            [GppGroupsParser.RelativePath] = Encoding.UTF8.GetBytes(FakeDirectoryBuilder.Groups(
                FakeDirectoryBuilder.LocalGroup("Administrators (built-in)", "S-1-5-32-544", [("CONTOSO\\GG_IT", null, "ADD")]))),
        };

        var missing = GroupPolicyPlanner.MissingExtensions(SecurityPair, null, files);

        var requirement = Assert.Single(missing);
        Assert.Equal((GppGroupsParser.RelativePath, GpoExtensionNames.PreferencesLocalUsersAndGroups, false), (requirement.RelativePath, requirement.Extension, requirement.User));
        Assert.Equal(3, GroupPolicyPlanner.RequiredExtensions(files).Count); // Security, the preference core entry, Local Users and Groups
    }

    [Fact]
    public async Task RemoveDuplicateLinks_KeepsTheLinkThatCounts_WithItsOptionsAndOrder()
    {
        Builder.SetGroupPolicyLinks(Company.SalesOu, [(Sample.DriveMappings, 0), (Sample.SitePrinters, 0), (Sample.DriveMappings, GroupPolicyLink.EnforcedOption)]);

        var plan = await Planner.PlanRemoveDuplicateLinksAsync(Session, Company.SalesOu, Sample.DriveMappings, Ct);
        var script = Script(plan);
        await ExecuteOkAsync(plan);

        var container = await Reader.ReadContainerAsync(Session, Company.SalesOu, Ct);
        Assert.Equal(
            [(Sample.SitePrinters, 2, false), (Sample.DriveMappings, 1, true)],
            container!.Links.Links.Select(l => (l.GpoId, l.Order, l.Enforced)));
        Assert.Contains("Remove-GPLink", script, StringComparison.Ordinal);
        Assert.Contains("New-GPLink", script, StringComparison.Ordinal);
        Assert.Contains("-LinkEnabled 'Yes' -Enforced 'Yes' -Order 1", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoveDuplicateLinks_NothingWithoutDuplicates()
    {
        var plan = await Planner.PlanRemoveDuplicateLinksAsync(Session, Company.ComputersOu, Sample.WorkstationPasswords, Ct);

        Assert.Empty(plan.Objects);
    }
}
