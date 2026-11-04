using AdMng.Core.Batch;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Testing;

namespace AdMng.Core.Tests.GroupPolicy.AdministrativeTemplates;

/// <summary>
/// An update that is undone, cancelled or fails leaves no empty central store behind: an empty PolicyDefinitions folder
/// would make every administrator's editor use a store without templates.
/// </summary>
public class CentralStoreFoldersTests : GroupPolicyTestBed
{
    private async Task<ChangePlan> CreationPlanAsync()
    {
        await using var sysvol = await OpenSysvolAsync();
        var comparison = await GroupPolicyPlanner.CompareCentralStoreAsync(sysvol, PolicyTemplatePackage.FromFiles("próbka", SampleAdministrativeTemplates.Files()),
            new CentralStoreUpdateOptions { Cultures = ["en-US", "pl-PL"] }, Ct);
        Assert.False(comparison.StoreExists);
        return Planner.PlanCentralStoreUpdate(Session, comparison, "Magazyn centralny");
    }

    private async Task<IReadOnlyList<Core.GroupPolicy.SysvolEntry>?> ListingAsync()
    {
        await using var sysvol = await OpenSysvolAsync();
        return await sysvol.ListPolicyDefinitionsAsync(1_000, Ct);
    }

    [Fact]
    public async Task UndoingTheCreationOfAStore_RemovesItsFolders()
    {
        var result = await ExecuteOkAsync(await CreationPlanAsync());
        Assert.Contains(await ListingAsync() ?? [], e => e.IsFolder && e.RelativePath == "pl-PL");

        await ExecuteOkAsync(new RollbackPlanner().CreateRollbackPlan(result));

        Assert.Null(await ListingAsync());
    }

    [Fact]
    public async Task ACancelWhileStaging_LeavesNoStoreBehind()
    {
        var plan = await CreationPlanAsync();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var writes = 0;
        Sysvol.BeforeCall = (operation, _, _) =>
        {
            if (operation == "WriteTempDefinitions" && ++writes == 3)
            {
                cancel.Cancel();
            }
        };

        var result = await new BatchExecutor(Audit, Time, null, Sysvol).ExecuteAsync(Session, plan, Fast, null, cancel.Token);
        Sysvol.BeforeCall = null;

        Assert.DoesNotContain(result.Objects.SelectMany(o => o.Changes), c => c.Succeeded);
        Assert.Null(await ListingAsync());
    }

    [Fact]
    public async Task AFailedStaging_LeavesNoStoreBehind_AndAnExistingStoreKeepsItsFolders()
    {
        Sysvol.Fail("WriteTempDefinitions", Core.GroupPolicy.SysvolWriteError.Failed, "windows.adml");
        var result = await ExecuteAsync(await CreationPlanAsync());
        Assert.Contains(result.Objects.SelectMany(o => o.Changes), c => !c.Succeeded);
        Assert.Null(await ListingAsync());

        // A store that exists keeps its (empty) culture folder when an update of it fails.
        Sysvol.CreateCentralStore();
        Sysvol.PutPolicyDefinition("en-US/windows.adml", SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.BaseAdmlEn));
        Sysvol.Fail("WriteTempDefinitions", Core.GroupPolicy.SysvolWriteError.Failed, "contoso.adml");
        await using (var sysvol = await OpenSysvolAsync())
        {
            var comparison = await GroupPolicyPlanner.CompareCentralStoreAsync(sysvol, PolicyTemplatePackage.FromFiles("próbka", SampleAdministrativeTemplates.Files()),
                new CentralStoreUpdateOptions { Cultures = ["en-US", "pl-PL"] }, Ct);
            await ExecuteAsync(Planner.PlanCentralStoreUpdate(Session, comparison, "Magazyn centralny"));
        }

        var listing = await ListingAsync();
        Assert.NotNull(listing);
        Assert.Contains(listing, e => e.IsFolder && e.RelativePath == "en-US");
        Assert.DoesNotContain(listing, e => e.RelativePath == "pl-PL");
    }
}
