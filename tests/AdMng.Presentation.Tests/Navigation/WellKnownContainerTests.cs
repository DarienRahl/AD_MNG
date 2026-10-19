using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Shell;
using AdMng.Presentation.Tests.Lists;
using AdMng.Presentation.Tests.TestDoubles;
using static AdMng.Presentation.Tests.TestDoubles.TestDirectory;

namespace AdMng.Presentation.Tests.Navigation;

/// <summary>Well-known containers (wellKnownObjects) in the tree and as default targets of the New commands.</summary>
public class WellKnownContainerTests
{
    private const string Computers = "CN=Computers,DC=contoso,DC=test";
    private const string DomainControllers = "OU=Domain Controllers,DC=contoso,DC=test";

    [Fact]
    public async Task SessionManager_ReadsTheDomainHeadOncePerSession()
    {
        var harness = await TestHarness.ConnectedAsync();
        var reads = harness.Directory.GetCount;

        var first = await harness.Sessions.GetWellKnownObjectsAsync(TestContext.Current.CancellationToken);
        var second = await harness.Sessions.GetWellKnownObjectsAsync(TestContext.Current.CancellationToken);

        Assert.Same(first, second);
        Assert.Equal(reads + 1, harness.Directory.GetCount);
        Assert.Equal(Users, first.Find(WellKnownContainer.Users));
        Assert.Equal(Computers, first.Find(WellKnownContainer.Computers));
        Assert.Same(WellKnownObjectMap.Empty, await TestHarness.Disconnected().Sessions.GetWellKnownObjectsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Tree_MarksTheWellKnownContainers_WithRoleTooltips()
    {
        var harness = await TestHarness.ConnectedAsync();
        var tree = harness.CreateTree();

        await tree.InitializeAsync();

        var domain = tree.DomainNode!;
        var users = Node(domain, "Users");
        Assert.Equal(WellKnownContainer.Users, users.WellKnown);
        Assert.True(users.IsWellKnown);
        Assert.Equal("Default container for new users and groups", users.WellKnownText);
        Assert.Equal("Default container for new users and groups" + Environment.NewLine + "Default container for upgraded user accounts", users.ToolTipText);
        Assert.Equal(WellKnownContainer.Computers, Node(domain, "Computers").WellKnown);
        Assert.Equal("Default container for new computers", Node(domain, "Computers").WellKnownText);
        Assert.Equal(WellKnownContainer.DomainControllers, Node(domain, "Domain Controllers").WellKnown);

        var firma = Node(domain, "Firma");
        Assert.Null(firma.WellKnown);
        Assert.False(firma.IsWellKnown);
        Assert.Null(firma.WellKnownText);
        Assert.Null(firma.ToolTipText);
        Assert.Null(Node(domain, "Builtin").WellKnown);
        Assert.Equal(WellKnownObjectMap.Empty.Count + 4, tree.WellKnown.Count);
    }

    [Fact]
    public async Task Tree_FollowsRedirectedContainers_AndNeverGuessesByName()
    {
        // redircmp OU=IT: CN=Computers is an ordinary container now.
        var harness = await TestHarness.ConnectedAsync();
        Redirect(harness, WellKnownContainer.Computers, It);
        var tree = harness.CreateTree();

        await tree.InitializeAsync();
        var firma = Node(tree.DomainNode!, "Firma");
        firma.IsExpanded = true;
        await firma.EnsureChildrenLoadedAsync();

        Assert.Null(Node(tree.DomainNode!, "Computers").WellKnown);
        Assert.Equal(WellKnownContainer.Computers, Node(firma, "IT").WellKnown);
        Assert.Null(Node(firma, "Księgowość").WellKnown);
    }

    [Fact]
    public async Task Tree_WithoutWellKnownObjects_ShowsNoBadges()
    {
        var harness = await TestHarness.ConnectedAsync();
        harness.Directory.Put(WithAttribute(harness.Directory.Find(Domain)!, WellKnownObjects.WellKnownObjectsAttribute));
        var tree = harness.CreateTree();

        await tree.InitializeAsync();

        Assert.True(tree.WellKnown.IsEmpty);
        Assert.All(tree.DomainNode!.Children.OfType<ContainerNodeViewModel>(), c => Assert.Null(c.WellKnown));
        Assert.Equal("Default container for upgraded user accounts", Node(tree.DomainNode!, "Users").ToolTipText);
    }

    [Fact]
    public async Task PinnedShortcuts_OfAWellKnownContainer_AreMarkedToo()
    {
        var harness = await TestHarness.ConnectedAsync();
        await harness.Views.PinAsync(Users, "Users");
        var tree = harness.CreateTree();

        await tree.InitializeAsync();

        var pin = tree.ViewsNode.Children.OfType<ContainerNodeViewModel>().Single();
        Assert.Equal(WellKnownContainer.Users, pin.WellKnown);
    }

    [Fact]
    public async Task Polish_RoleTexts_FollowTheLanguageSwitch()
    {
        var harness = await TestHarness.ConnectedAsync(culture: CultureInfo.GetCultureInfo("pl-PL"));
        var tree = harness.CreateTree();
        await tree.InitializeAsync();
        var users = Node(tree.DomainNode!, "Users");
        var computers = Node(tree.DomainNode!, "Computers");

        Assert.Equal("Domyślny kontener nowych użytkowników i grup", users.WellKnownText);
        Assert.Equal("Domyślny kontener nowych komputerów", computers.WellKnownText);

        harness.Localizer.SetCulture(CultureInfo.GetCultureInfo("en-US"));

        Assert.Equal("Default container for new users and groups", users.WellKnownText);
        Assert.StartsWith("Default container for new computers", computers.ToolTipText, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRole_HasTextsInBothLanguages()
    {
        var en = new ResxLocalizer(CultureInfo.GetCultureInfo("en-US"));
        var pl = new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL"));
        foreach (var container in Enum.GetValues<WellKnownContainer>())
        {
            var key = WellKnownRoles.Key(container);
            Assert.False(en[key].StartsWith('['), key);
            Assert.False(pl[key].StartsWith('['), key);
            Assert.NotEqual(en[key], pl[key]);
        }
    }

    // ---- New commands -----------------------------------------------------------------------------------------

    [Fact]
    public async Task New_OnTheDomainRootTab_GoesToTheWellKnownContainerOfTheKind()
    {
        var (shell, harness) = await CreateShellAsync();
        Assert.Equal(Domain, shell.SelectedDocument!.Location!.BaseDn);

        await shell.NewUserCommand.ExecuteAsync(null);
        await shell.NewComputerCommand.ExecuteAsync(null);
        await shell.NewGroupCommand.ExecuteAsync(null);
        await shell.NewContactCommand.ExecuteAsync(null);
        await shell.NewOrganizationalUnitCommand.ExecuteAsync(null);

        Assert.Equal(
            [$"new:User:{Users}", $"new:Computer:{Computers}", $"new:Group:{Users}", $"new:Contact:{Users}", $"new:OrganizationalUnit:{Domain}"],
            NewDialogs(harness));
        Assert.Equal(Users, shell.DefaultCreateContainer(ObjectKind.User));
        Assert.Equal(Users, shell.DefaultCreateContainer(ObjectKind.InetOrgPerson));
        Assert.Equal(Computers, shell.DefaultCreateContainer(ObjectKind.Computer));
    }

    [Fact]
    public async Task New_FromSearchResultsOrAView_UsesTheWellKnownContainer_EvenWithAnOuSelectedInTheTree()
    {
        var (shell, harness) = await CreateShellAsync(It);
        await shell.NavigateAsync(ListLocation.ForSearch("jan", Domain, "Search: jan", "(anr=jan)"));

        await shell.NewUserCommand.ExecuteAsync(null);
        await shell.NewComputerCommand.ExecuteAsync(null);

        Assert.Equal([$"new:User:{Users}", $"new:Computer:{Computers}"], NewDialogs(harness));
    }

    [Fact]
    public async Task New_InAnOuTab_OrWithAnExplicitParent_KeepsThatContainer()
    {
        var (shell, harness) = await CreateShellAsync(It);

        await shell.NewUserCommand.ExecuteAsync(null);
        await shell.NewComputerCommand.ExecuteAsync(null);

        // The tree's context menu on the domain itself: an explicit choice.
        await shell.NewUserCommand.ExecuteAsync(Domain);

        Assert.Equal([$"new:User:{It}", $"new:Computer:{It}", $"new:User:{Domain}"], NewDialogs(harness));
    }

    [Fact]
    public async Task New_FollowsARedirectedUsersContainer()
    {
        var harness = await TestHarness.ConnectedAsync();
        Redirect(harness, WellKnownContainer.Users, Firma);
        var shell = harness.CreateShell();
        await shell.InitializeAsync();

        await shell.NewUserCommand.ExecuteAsync(null);
        await shell.NewComputerCommand.ExecuteAsync(null);

        Assert.Equal([$"new:User:{Firma}", $"new:Computer:{Computers}"], NewDialogs(harness));
    }

    [Fact]
    public async Task New_WithoutWellKnownObjects_FallsBackToTheCurrentContainer()
    {
        var harness = await TestHarness.ConnectedAsync();
        harness.Directory.Put(WithAttribute(harness.Directory.Find(Domain)!, WellKnownObjects.WellKnownObjectsAttribute));
        var shell = harness.CreateShell();
        await shell.InitializeAsync();

        await shell.NewUserCommand.ExecuteAsync(null);
        await shell.NewComputerCommand.ExecuteAsync(null);

        Assert.Equal([$"new:User:{Domain}", $"new:Computer:{Domain}"], NewDialogs(harness));
    }

    [Fact]
    public async Task New_CreatePermissions_AreThoseOfTheWellKnownTarget()
    {
        var harness = await TestHarness.ConnectedAsync();
        harness.Directory.Permissions = dn => Core.Ldap.DistinguishedName.AreEqual(dn, Users)
            ? new EffectivePermissions
            {
                IsKnown = true,
                CreatableChildClasses = new HashSet<string>(["contact"], StringComparer.OrdinalIgnoreCase),
            }
            : null;
        var shell = harness.CreateShell();
        await shell.InitializeAsync();

        await ObjectListViewModelTests.WaitUntilAsync(() => !shell.NewUserCommand.CanExecute(null));

        Assert.Equal("You do not have permission to create objects of type 'User' in this container.", shell.CreateBlockedReason(ObjectKind.User));
        Assert.Null(shell.CreateBlockedReason(ObjectKind.Contact));
        Assert.True(shell.NewComputerCommand.CanExecute(null));
        Assert.True(shell.NewOrganizationalUnitCommand.CanExecute(null));
        Assert.Contains(Users, harness.Directory.PermissionReads);
        Assert.Contains(Computers, harness.Directory.PermissionReads);
    }

    private static ContainerNodeViewModel Node(TreeNodeViewModel parent, string name) =>
        parent.Children.OfType<ContainerNodeViewModel>().Single(c => c.Name == name);

    private static void Redirect(TestHarness harness, WellKnownContainer container, string dn)
    {
        var guid = WellKnownGuids.Of(container);
        var values = WellKnownValues.Cast<string>()
            .Where(v => !v.Contains(guid, StringComparison.Ordinal))
            .Append($"B:32:{guid}:{dn}")
            .Cast<object>()
            .ToArray();
        harness.Directory.Put(WithAttribute(harness.Directory.Find(Domain)!, WellKnownObjects.WellKnownObjectsAttribute, values));
    }

    private static List<string> NewDialogs(TestHarness harness) =>
        [.. harness.Dialogs.Shown.Where(s => s.StartsWith("new:", StringComparison.Ordinal))];

    private static async Task<(ShellViewModel Shell, TestHarness Harness)> CreateShellAsync(string? container = null)
    {
        var harness = await TestHarness.ConnectedAsync();
        var shell = harness.CreateShell();
        await shell.InitializeAsync();
        if (container is not null)
        {
            await shell.NavigateAsync(ListLocation.ForContainer(container));
        }

        return (shell, harness);
    }
}
