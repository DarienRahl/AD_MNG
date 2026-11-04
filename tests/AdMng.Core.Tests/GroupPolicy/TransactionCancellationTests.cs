using AdMng.Core.Batch;
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
