using System.Globalization;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Tests.Dialogs;

namespace AdMng.Presentation.Tests.GroupPolicy;

/// <summary>The Group Policy nodes of the tree and the shell's Group Policy tabs and commands (design note 5.2).</summary>
public sealed class GroupPolicyTreeAndShellTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    [Fact]
    public async Task DomainNode_HasTheGroupPolicyNodesOnTop_AndTheMergeKeepsThem()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var shell = h.CreateShell();
        await shell.InitializeAsync();
        var domain = shell.Tree.DomainNode!;
        await domain.EnsureChildrenLoadedAsync();

        Assert.Equal(["Obiekty zasad grupy", "Filtry WMI", "Lokacje"], domain.Children.Take(3).Select(c => c.Name));
        Assert.Contains(domain.Children.Skip(3), c => c is ContainerNodeViewModel { Name: "Firma" });

        await domain.RefreshAsync();
        Assert.IsType<GroupPolicyObjectsNodeViewModel>(domain.Children[0]);
        Assert.Equal(3, domain.Children.OfType<GroupPolicyNodeViewModel>().Count());
    }

    [Fact]
    public async Task GpoNode_ChildrenAreSortedGpos_AndRefreshAfterAnOperation()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var shell = h.CreateShell();
        await shell.InitializeAsync();
        var node = shell.Tree.GroupPolicyObjectsNode!;
        node.IsExpanded = true;
        await node.EnsureChildrenLoadedAsync();

        var names = node.Children.Select(c => c.Name).ToList();
        Assert.Equal(7, names.Count);
        Assert.Equal(names.Order(StringComparer.Create(Polish, ignoreCase: true)), names);

        h.Windows.NewGpoName = "Żurawie – aplikacje";
        Assert.True(await h.Actions.NewAsync());
        await B2Harness.WaitUntilAsync(() => node.Children.Count == 8);
        Assert.Equal("Żurawie – aplikacje", node.Children[^1].Name);
    }

    [Fact]
    public async Task SettingHidesTheNodes_AndTheShellToggleShowsThemAgain()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var shell = h.CreateShell();
        await shell.InitializeAsync();
        Assert.True(shell.Tree.IsGroupPolicyShown);

        await shell.ToggleGroupPolicyNodesCommand.ExecuteAsync(null);
        await B2Harness.WaitUntilAsync(() => !shell.Tree.IsGroupPolicyShown);
        Assert.False(h.B2.Settings.Current.GroupPolicy.ShowInTree);
        Assert.Empty(shell.Tree.DomainNode!.Children.OfType<GroupPolicyNodeViewModel>());

        await shell.ToggleGroupPolicyNodesCommand.ExecuteAsync(null);
        await B2Harness.WaitUntilAsync(() => shell.Tree.IsGroupPolicyShown);
        Assert.IsType<GroupPolicyObjectsNodeViewModel>(shell.Tree.DomainNode!.Children[0]);
    }

    [Fact]
    public async Task SelectingNodes_OpensTheirDocuments_OncePerTarget()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var shell = h.CreateShell();
        await shell.InitializeAsync();
        var gpoRoot = shell.Tree.GroupPolicyObjectsNode!;
        await gpoRoot.EnsureChildrenLoadedAsync();

        gpoRoot.IsSelected = true;
        Assert.IsType<GroupPolicyObjectsDocumentViewModel>(shell.SelectedTab);

        var gpoNode = gpoRoot.Children.OfType<GpoNodeViewModel>().Single(n => n.GpoId == h.Sample.WorkstationPasswords);
        gpoNode.IsSelected = true;
        var gpoDocument = Assert.IsType<GroupPolicyObjectDocumentViewModel>(shell.SelectedTab);
        Assert.Equal(h.Sample.WorkstationPasswords, gpoDocument.GpoId);

        gpoRoot.IsSelected = true;
        gpoNode.IsSelected = true;
        Assert.Single(shell.Documents.OfType<GroupPolicyObjectDocumentViewModel>());

        var wmi = shell.Tree.DomainNode!.Children.OfType<WmiFiltersNodeViewModel>().Single();
        wmi.IsSelected = true;
        Assert.IsType<WmiFiltersDocumentViewModel>(shell.SelectedTab);

        var sites = shell.Tree.DomainNode.Children.OfType<SitesNodeViewModel>().Single();
        await sites.EnsureChildrenLoadedAsync();
        var site = Assert.Single(sites.Children.OfType<SiteNodeViewModel>());
        Assert.Equal("Warszawa", site.Name);
        site.IsSelected = true;
        var scope = Assert.IsType<GroupPolicyScopeDocumentViewModel>(shell.SelectedTab);
        Assert.Equal(h.Sample.SiteDn, scope.ContainerDn);

        // Closing a tab disposes it; the list tab is untouched.
        await shell.CloseTabCommand.ExecuteAsync(scope);
        Assert.DoesNotContain(scope, shell.Documents);
        Assert.Single(shell.ListDocuments);
    }

    [Fact]
    public async Task ContainerPolicies_ForTheCurrentOu_AndDisabledForOtherContainers()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var shell = h.CreateShell();
        await shell.InitializeAsync();

        await shell.NavigateAsync(ListLocation.ForContainer(h.Company.ComputersOu, "Komputery", Icons.ObjectIcon.OrganizationalUnit));
        Assert.True(shell.OpenContainerPoliciesCommand.CanExecute(null));
        shell.OpenContainerPoliciesCommand.Execute(null);
        var scope = Assert.IsType<GroupPolicyScopeDocumentViewModel>(shell.SelectedTab);
        Assert.Equal(h.Company.ComputersOu, scope.ContainerDn);
        Assert.Contains(shell.CommandDescriptors, d => d.Id == "gpo.containerPolicies");

        await shell.NavigateAsync(ListLocation.ForContainer($"CN=Users,{h.DomainDn}", "Users", Icons.ObjectIcon.Container));
        Assert.False(shell.OpenContainerPoliciesCommand.CanExecute(null));
        Assert.True(shell.OpenContainerPoliciesCommand.CanExecute(h.DomainDn));
        Assert.True(shell.OpenContainerPoliciesCommand.CanExecute(h.Sample.SiteDn));
        Assert.False(shell.ToggleBlockInheritanceCommand.CanExecute(h.Sample.SiteDn));
    }

    [Fact]
    public async Task TreeMenuCommands_LinkAndBlockOnTheRightClickedOu()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var shell = h.CreateShell();
        await shell.InitializeAsync();
        h.Windows.PickGpo = "Lokacja Warszawa – Drukarki";

        await shell.LinkExistingGpoCommand.ExecuteAsync(h.Company.EmployeesOu);
        Assert.Contains(h.Sample.SitePrinters.ToString("D"), h.Value(h.Company.EmployeesOu, Core.ActiveDirectory.Attr.GpLink) ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        await shell.ToggleBlockInheritanceCommand.ExecuteAsync(h.Company.EmployeesOu);
        Assert.Null(h.Value(h.Company.EmployeesOu, Core.GroupPolicy.GroupPolicyAttributes.GpOptions));
    }

    [Fact]
    public async Task ActionsOpenDocuments_ThroughTheShell()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var shell = h.CreateShell();
        await shell.InitializeAsync();

        h.Actions.OpenGpo(h.Sample.DriveMappings, "Użytkownicy – Mapowanie dysków", GpoDocumentTab.Status);
        var document = Assert.IsType<GroupPolicyObjectDocumentViewModel>(shell.SelectedTab);
        Assert.Equal(GpoDocumentTab.Status, document.SelectedTab);
        h.Actions.OpenGpo(h.Sample.DriveMappings, "Użytkownicy – Mapowanie dysków", GpoDocumentTab.Delegation);
        Assert.Same(document, shell.SelectedTab);
        Assert.Equal(GpoDocumentTab.Delegation, document.SelectedTab);

        await document.LoadAsync();
        document.SelectedLink = document.Links[0];
        document.OpenLinkContainerCommand.Execute(null);
        var scope = Assert.IsType<GroupPolicyScopeDocumentViewModel>(shell.SelectedTab);
        Assert.Equal(h.Company.EmployeesOu, scope.ContainerDn);
    }

    [Fact]
    public async Task ShellWithoutGroupPolicy_OffersNoGroupPolicyCommands()
    {
        var b2 = await B2Harness.CreateAsync();
        using var shell = b2.CreateShell();
        await shell.InitializeAsync();

        Assert.False(shell.IsGroupPolicyAvailable);
        Assert.False(shell.OpenGroupPolicyObjectsCommand.CanExecute(null));
        Assert.Null(shell.OpenGroupPolicyDocument(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Objects)));
        Assert.DoesNotContain(shell.Tree.DomainNode!.Children, c => c is GroupPolicyNodeViewModel);
    }
}
