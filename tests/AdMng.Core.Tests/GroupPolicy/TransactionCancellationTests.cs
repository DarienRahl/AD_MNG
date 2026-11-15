using AdMng.Core.Batch;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Testing;

namespace AdMng.Core.Tests.GroupPolicy;

/// <summary>
/// A cancel during a SYSVOL transaction's switch: the switch ignores it (the files are committed), so every step the
/// transaction decided is in the result, the audit and the undo plan.
/// </summary>
public class TransactionCancellationTests : GroupPolicyTestBed
{
    private async Task<BatchResult> ExecuteCancellingAtAsync(ChangePlan plan, string operation)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Sysvol.BeforeCall = (called, _, _) =>
        {
            if (called == operation)
            {
                cancel.Cancel();
            }
        };
        try
        {
            return await new BatchExecutor(Audit, Time, null, Sysvol).ExecuteAsync(Session, plan, Fast, null, cancel.Token);
        }
        finally
        {
            Sysvol.BeforeCall = null;
        }
    }

    [Fact]
    public async Task CentralStore_CancelledAtTheFirstSwitch_RecordsAuditsAndUndoesEveryFile()
    {
        await using var sysvol = await OpenSysvolAsync();
        var comparison = await GroupPolicyPlanner.CompareCentralStoreAsync(sysvol, PolicyTemplatePackage.FromFiles("próbka", SampleAdministrativeTemplates.Files()),
            new CentralStoreUpdateOptions { Cultures = ["en-US", "pl-PL"] }, Ct);
        var plan = Planner.PlanCentralStoreUpdate(Session, comparison, "Magazyn centralny");
        Assert.Equal(6, plan.Objects.Single().Changes.Count);

        var result = await ExecuteCancellingAtAsync(plan, "ReplaceDefinitions");

        Assert.Equal(6, Sysvol.PolicyDefinitionFiles().Count);
        var changes = Assert.Single(result.Objects).Changes;
        Assert.Equal(6, changes.Count);
        Assert.All(changes, c => Assert.True(c.Succeeded));
        Assert.Equal(6, Audit.Entries.Count(e => e.Action == nameof(ChangeAction.WritePolicyDefinitionsFile)));

        var undo = new RollbackPlanner().CreateRollbackPlan(result);
        Assert.Equal(6, undo.Objects.Single().Changes.Count);
        await ExecuteOkAsync(undo);
        Assert.Empty(Sysvol.PolicyDefinitionFiles());
    }

    /// <summary>The plan of two registry files (machine: new, user: changed) with the extension names set first, as the planner makes it.</summary>
    private async Task<(ChangePlan Plan, Guid Gpo, byte[]? UserBefore, GpoVersion VersionBefore)> TwoFilesPlanAsync()
    {
        var gpo = Sample.SitePrinters;
        var set = SampleAdministrativeTemplates.LoadPolish();
        var machine = new RegistryPolicyDocument();
        PolicyEngine.Write(set.Find(SampleAdministrativeTemplates.HidePreview)!.Policy, PolicySetting.Enabled(new Dictionary<string, PolicyElementValue>()), machine);
        var user = new RegistryPolicyDocument();
        PolicyEngine.Write(set.Find(SampleAdministrativeTemplates.DriveMappings)!.Policy,
            PolicySetting.Enabled(new Dictionary<string, PolicyElementValue> { ["Mapy"] = new ListElementValue([new("P", @"\\pliki\Wydział Prawa")]) }), user);
        await using var sysvol = await OpenSysvolAsync();
        var before = (await Reader.ReadPolicyAsync(Session, gpo, Ct))!.DirectoryVersion!.Value;
        var userBefore = Sysvol.Get(gpo, GroupPolicyPlanner.UserRegistryPolicyPath);
        var plan = await Planner.PlanAdministrativeTemplatesAsync(Session, sysvol, gpo,
            new RegistryPolicySave { Content = machine.ToBytes() }, new RegistryPolicySave { Original = userBefore, Content = user.ToBytes() }, Ct);
        return (plan, gpo, userBefore, before);
    }

    [Fact]
    public async Task AdministrativeTemplates_StoppedAfterTheExtensionsWereSet_StillCommitTheFilesAndTheVersion()
    {
        // The first change of the object is the Modify of the extension names (on the container). The Stop comes while the
        // files are staged: before "finish the object in progress" the transaction was cancelled, and the container named
        // extensions whose files were never written.
        var (plan, gpo, _, before) = await TwoFilesPlanAsync();
        Assert.Equal(ChangeAction.Modify, plan.Objects.Single().Changes[0].Action);

        var result = await ExecuteCancellingAtAsync(plan, "WriteTemp");

        var objectResult = Assert.Single(result.Objects);
        Assert.Equal(ObjectResultStatus.Succeeded, objectResult.Status);
        Assert.All(objectResult.Changes, c => Assert.True(c.Succeeded));
        Assert.Equal(1, result.FinishedAfterStopCount);
        Assert.NotNull(Sysvol.Get(gpo, GroupPolicyPlanner.MachineRegistryPolicyPath));
        var after = (await Reader.ReadPolicyAsync(Session, gpo, Ct))!.DirectoryVersion!.Value;
        Assert.True(after.Computer > before.Computer && after.User > before.User, $"{before} -> {after}");
    }

    [Fact]
    public async Task AdministrativeTemplates_StoppedWhileStagingTheFirstChange_ChangeNothing_BecauseTheObjectNeverBegan()
    {
        // The Stop finishes the object in progress, but the first change of an object is still the operator's to stop: a
        // transaction that is its first change and is cancelled before its switch has changed no file and no version.
        var (plan, gpo, userBefore, before) = await TwoFilesPlanAsync();
        var onlyTheFiles = plan with { Objects = [.. plan.Objects.Select(o => o with { Changes = [.. o.Changes.Where(c => c.Action != ChangeAction.Modify)] })] };
        Assert.Equal(ChangeAction.WriteGpoFile, onlyTheFiles.Objects.Single().Changes[0].Action);

        var result = await ExecuteCancellingAtAsync(onlyTheFiles, "WriteTemp");

        var objectResult = Assert.Single(result.Objects);
        Assert.Equal(ObjectResultStatus.Cancelled, objectResult.Status);
        Assert.All(objectResult.Changes, c => Assert.False(c.Succeeded));
        Assert.True(result.WasCancelled);
        Assert.Equal(0, result.FinishedAfterStopCount);
        Assert.Null(Sysvol.Get(gpo, GroupPolicyPlanner.MachineRegistryPolicyPath));
        Assert.Equal(userBefore, Sysvol.Get(gpo, GroupPolicyPlanner.UserRegistryPolicyPath));
        Assert.Equal(before, (await Reader.ReadPolicyAsync(Session, gpo, Ct))!.DirectoryVersion!.Value);
    }

    [Fact]
    public async Task AdministrativeTemplates_CancelledAtTheFirstSwitch_KeepTheUserHalfAndTheVersionInTheResult()
    {
        var gpo = Sample.SitePrinters;
        var set = SampleAdministrativeTemplates.LoadPolish();
        var machine = new RegistryPolicyDocument();
        PolicyEngine.Write(set.Find(SampleAdministrativeTemplates.HidePreview)!.Policy, PolicySetting.Enabled(new Dictionary<string, PolicyElementValue>()), machine);
        var user = new RegistryPolicyDocument();
        PolicyEngine.Write(set.Find(SampleAdministrativeTemplates.DriveMappings)!.Policy,
            PolicySetting.Enabled(new Dictionary<string, PolicyElementValue> { ["Mapy"] = new ListElementValue([new("P", @"\\pliki\Wydział Prawa")]) }), user);
        await using var sysvol = await OpenSysvolAsync();
        var before = (await Reader.ReadPolicyAsync(Session, gpo, Ct))!.DirectoryVersion!.Value;
        var plan = await Planner.PlanAdministrativeTemplatesAsync(Session, sysvol, gpo,
            new RegistryPolicySave { Content = machine.ToBytes() }, new RegistryPolicySave { Original = Sysvol.Get(gpo, GroupPolicyPlanner.UserRegistryPolicyPath), Content = user.ToBytes() }, Ct);

        var result = await ExecuteCancellingAtAsync(plan, "Replace");

        var changes = Assert.Single(result.Objects).Changes;
        Assert.All(changes, c => Assert.True(c.Succeeded));
        Assert.Contains(changes, c => c.Change.Action == ChangeAction.SetGpoVersion);
        Assert.Equal(2, changes.Count(c => c.Change.Action == ChangeAction.WriteGpoFile));
        Assert.Equal(user.ToBytes(), Sysvol.Get(gpo, GroupPolicyPlanner.UserRegistryPolicyPath));

        // Undo reverts both files and raises both versions again (clients pick the reverted settings up).
        await ExecuteOkAsync(new RollbackPlanner().CreateRollbackPlan(result));
        Assert.Null(Sysvol.Get(gpo, GroupPolicyPlanner.MachineRegistryPolicyPath));
        var after = (await Reader.ReadPolicyAsync(Session, gpo, Ct))!.DirectoryVersion!.Value;
        Assert.True(after.Computer > before.Computer + 1 && after.User > before.User + 1, $"{before} -> {after}");
    }
}
