using AdMng.Core.Model;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Tests.TestDoubles;
using AdMng.Presentation.Views;
using static AdMng.Presentation.Tests.TestDoubles.TestDirectory;

namespace AdMng.Presentation.Tests.Lists;

/// <summary>
/// A move/rename/delete of a container is published once, for the root of the subtree: lists showing a container
/// below it, or rows below it, must follow.
/// </summary>
public class SubtreeChangeTests
{
    private const string Spolka = "OU=Spółka,DC=contoso,DC=test";
    private const string ItInSpolka = "OU=IT,OU=Spółka,DC=contoso,DC=test";

    [Fact]
    public async Task ContainerAboveTheShownOne_Renamed_TheTabFollowsWithItsChipsAndSelection()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = await harness.OpenContainerAsync(It);
        list.FilterChips.Single(c => c.Kind == QuickFilterKind.Disabled).IsActive = true;
        await list.CurrentLoad;
        list.SelectedItem = list.Items.Single(r => r.Dn == Ewa);
        var navigated = 0;
        list.Navigated += (_, _) => navigated++;

        harness.Directory.MoveSubtree(Firma, Spolka);
        await list.HandleChangesAsync([new DirectoryChange(DirectoryChangeKind.Moved, Firma, Spolka, ObjectKind.OrganizationalUnit)]);

        Assert.Equal(ItInSpolka, list.Location!.BaseDn);
        Assert.Equal("IT", list.Title);
        Assert.Equal(ItInSpolka, list.LastSearch!.BaseDn);
        Assert.True(list.FilterChips.Single(c => c.Kind == QuickFilterKind.Disabled).IsActive);
        Assert.Equal(["CN=Ewa Kowalska," + ItInSpolka], list.Items.Select(r => r.Dn));
        Assert.Equal("CN=Ewa Kowalska," + ItInSpolka, list.SelectedItem?.Dn);
        Assert.Equal(1, navigated);
        Assert.Null(list.Error);
    }

    [Fact]
    public async Task ShownContainer_Renamed_GetsItsNewNameAndDn()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = await harness.OpenContainerAsync(It);
        const string renamed = "OU=Informatyka,OU=Firma,DC=contoso,DC=test";

        harness.Directory.MoveSubtree(It, renamed);
        await list.HandleChangesAsync([new DirectoryChange(DirectoryChangeKind.Moved, It, renamed, ObjectKind.OrganizationalUnit)]);

        Assert.Equal(renamed, list.Location!.BaseDn);
        Assert.Equal("Informatyka", list.Title);
        Assert.Equal(6, list.Items.Count);
        Assert.All(list.Items, r => Assert.EndsWith("," + renamed, r.Dn, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ContainerAboveTheShownOne_Deleted_ShowsTheLocationDeletedState()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = await harness.OpenContainerAsync(It);

        await list.HandleChangesAsync([new DirectoryChange(DirectoryChangeKind.Deleted, Firma, ObjectKind: ObjectKind.OrganizationalUnit)]);

        Assert.Empty(list.Items);
        Assert.Equal("This container no longer exists.", list.Error);
    }

    [Fact]
    public async Task SubtreeView_ContainerOfListedRowsMoved_RowsAreReloadedUnderTheirNewDn()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = harness.Lists.CreateList();
        var disabled = BuiltInViews.All.Single(v => v.Kind == BuiltInViewKind.DisabledUsers);
        await list.NavigateAsync(disabled.ToLocation(harness.Localizer, Domain, TestHarness.Now, 90));
        Assert.Equal([Ewa], list.Items.Select(r => r.Dn));
        const string moved = "OU=IT,OU=Księgowość,OU=Firma,DC=contoso,DC=test";

        harness.Directory.MoveSubtree(It, moved);
        await list.HandleChangesAsync([new DirectoryChange(DirectoryChangeKind.Moved, It, moved, ObjectKind.OrganizationalUnit)]);

        Assert.Equal(["CN=Ewa Kowalska," + moved], list.Items.Select(r => r.Dn));
        Assert.Equal(Domain, list.Location!.BaseDn);
    }

    [Fact]
    public async Task SubtreeView_ContainerOfListedRowsDeleted_RowsAreRemovedWithoutAReload()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = harness.Lists.CreateList();
        var disabled = BuiltInViews.All.Single(v => v.Kind == BuiltInViewKind.DisabledUsers);
        await list.NavigateAsync(disabled.ToLocation(harness.Localizer, Domain, TestHarness.Now, 90));
        harness.Directory.Put(User("CN=Wyłączony Ktoś,OU=Księgowość,OU=Firma,DC=contoso,DC=test", "wylaczony", disabled: true));
        await list.LoadAsync();
        Assert.Equal(2, list.Items.Count);
        var searches = harness.Directory.Searches.Count;

        await list.HandleChangesAsync([new DirectoryChange(DirectoryChangeKind.Deleted, It, ObjectKind: ObjectKind.OrganizationalUnit)]);

        Assert.Equal(["Wyłączony Ktoś"], list.Items.Select(r => r.Name));
        Assert.Equal(1, list.LoadedCount);
        Assert.Equal(searches, harness.Directory.Searches.Count);
        Assert.Null(list.Error);
    }

    [Fact]
    public async Task RenamedChildContainer_InAOneLevelListing_IsRefreshedAsARow_NotReloaded()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = await harness.OpenContainerAsync(Firma);
        const string renamed = "OU=Informatyka,OU=Firma,DC=contoso,DC=test";
        var searches = harness.Directory.Searches.Count;

        harness.Directory.MoveSubtree(It, renamed);
        await list.HandleChangesAsync([new DirectoryChange(DirectoryChangeKind.Moved, It, renamed, ObjectKind.OrganizationalUnit)]);

        Assert.Equal(["Informatyka", "Księgowość"], list.Items.Select(r => r.Name).Order(StringComparer.Ordinal));
        Assert.Equal(Firma, list.Location!.BaseDn);
        Assert.Equal(searches + 1, harness.Directory.Searches.Count);
    }
}
