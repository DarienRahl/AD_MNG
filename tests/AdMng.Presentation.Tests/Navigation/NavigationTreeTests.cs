using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Tests.Lists;
using AdMng.Presentation.Tests.TestDoubles;
using AdMng.Presentation.Views;
using static AdMng.Presentation.Tests.TestDoubles.TestDirectory;

namespace AdMng.Presentation.Tests.Navigation;

public class NavigationTreeTests
{
    [Fact]
    public async Task Initialize_ShowsViewsAndDomain_WithChildContainersSortedAndAdvancedHidden()
    {
        var harness = await TestHarness.ConnectedAsync();
        var tree = harness.CreateTree();

        await tree.InitializeAsync();

        Assert.Equal(["Views", "contoso.test"], tree.Roots.Select(r => r.Name));
        var domain = tree.DomainNode!;
        Assert.True(domain.IsExpanded);
        Assert.Equal(ObjectIcon.Domain, domain.Icon);
        Assert.Equal(["Builtin", "Computers", "Domain Controllers", "Firma", "Users"], domain.Children.Select(c => c.Name));
        Assert.DoesNotContain(domain.Children, c => c.Name == "System");
        var search = harness.Directory.Searches.Last(s => s.BaseDn == Domain);
        Assert.Equal(SearchScope.OneLevel, search.Scope);
        Assert.Equal(ObjectKinds.ContainerFilter, search.Filter);
        Assert.Equal(ContainerNodeViewModel.ChildAttributes, search.Attributes);
    }

    [Fact]
    public async Task Containers_LoadChildrenLazily_OnExpand()
    {
        var harness = await TestHarness.ConnectedAsync();
        var tree = harness.CreateTree();
        await tree.InitializeAsync();
        var firma = (ContainerNodeViewModel)tree.DomainNode!.Children.Single(c => c.Name == "Firma");
        Assert.IsType<PlaceholderNodeViewModel>(Assert.Single(firma.Children));
        var searches = harness.Directory.Searches.Count;

        firma.IsExpanded = true;
        await firma.EnsureChildrenLoadedAsync();

        Assert.Equal(searches + 1, harness.Directory.Searches.Count);
        Assert.Equal(["IT", "Księgowość"], firma.Children.Select(c => c.Name));
        Assert.Equal(ObjectIcon.OrganizationalUnit, firma.Children[0].Icon);

        var it = (ContainerNodeViewModel)firma.Children[0];
        it.IsExpanded = true;
        await it.EnsureChildrenLoadedAsync();
        Assert.Empty(it.Children);
    }

    [Fact]
    public async Task AdvancedFeatures_ShowsHiddenContainers_AfterReload()
    {
        var harness = await TestHarness.ConnectedAsync();
        var tree = harness.CreateTree();
        await tree.InitializeAsync();

        await harness.Settings.UpdateAsync(s => s.ShowAdvancedFeatures = true, TestContext.Current.CancellationToken);
        await tree.ReloadAsync();

        Assert.Contains(tree.DomainNode!.Children, c => c.Name == "System");
    }

    [Fact]
    public async Task ExpandTo_ExpandsThePath_AndSelectsWithoutActivation()
    {
        var harness = await TestHarness.ConnectedAsync();
        var tree = harness.CreateTree();
        await tree.InitializeAsync();
        var activated = 0;
        tree.NodeActivated += (_, _) => activated++;

        var node = await tree.ExpandToAsync(It);

        Assert.NotNull(node);
        Assert.Equal("IT", node.Name);
        Assert.True(node.IsSelected);
        Assert.Same(node, tree.SelectedNode);
        Assert.True(node.Parent!.IsExpanded);
        Assert.Equal(0, activated);
        Assert.Null(await tree.ExpandToAsync("OU=Elsewhere,DC=fabrikam,DC=com"));

        var users = tree.DomainNode!.Children.Single(c => c.Name == "Users");
        users.IsSelected = true;
        Assert.Equal(1, activated);
        Assert.False(node.IsSelected);
    }

    [Fact]
    public async Task ViewsNode_ContainsQuickFilters_SavedViews_AndPinnedContainers()
    {
        var harness = await TestHarness.ConnectedAsync();
        await harness.Views.SaveViewAsync(new SavedView { Id = "v1", Name = "Dział IT – wszyscy" });
        await harness.Views.PinAsync(It, "IT");
        var tree = harness.CreateTree();

        await tree.InitializeAsync();

        var children = tree.ViewsNode.Children;
        var quick = Assert.IsType<FolderNodeViewModel>(children[0]);
        Assert.Equal("Quick filters", quick.Name);
        Assert.Equal(BuiltInViews.All.Count, quick.Children.Count);
        Assert.Contains(quick.Children, c => c.Name == "Inactive users (90 days)");
        Assert.Equal("Dział IT – wszyscy", Assert.IsType<ViewNodeViewModel>(children[1]).Name);
        var pinned = Assert.IsType<ContainerNodeViewModel>(children[2]);
        Assert.True(pinned.IsPinnedEntry);
        Assert.Equal(ObjectIcon.Pinned, pinned.Icon);

        await harness.Views.UnpinAsync(It);
        Assert.Equal(2, tree.ViewsNode.Children.Count);
    }

    [Fact]
    public async Task CreatedContainer_RefreshesItsLoadedParent()
    {
        var harness = await TestHarness.ConnectedAsync();
        var tree = harness.CreateTree();
        await tree.InitializeAsync();
        const string created = "OU=Nowy Dział,DC=contoso,DC=test";
        harness.Directory.Put(Ou(created));

        harness.Changes.Publish([new DirectoryChange(DirectoryChangeKind.Created, created, ObjectKind: ObjectKind.OrganizationalUnit)]);

        await ObjectListViewModelTests.WaitUntilAsync(() => tree.DomainNode!.Children.Any(c => c.Name == "Nowy Dział"));
    }

    [Fact]
    public async Task ChildLoadFailure_IsShownOnTheNode()
    {
        var harness = await TestHarness.ConnectedAsync();
        var tree = harness.CreateTree();
        await tree.InitializeAsync();
        var firma = (ContainerNodeViewModel)tree.DomainNode!.Children.Single(c => c.Name == "Firma");
        harness.Directory.FailSearch = _ => new DirectoryException(DirectoryErrorKind.AccessDenied, "denied");

        await firma.EnsureChildrenLoadedAsync();

        Assert.Equal("Access is denied.", firma.LoadError);
        Assert.Empty(firma.Children);
    }
}
