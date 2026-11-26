using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Pki;
using AdMng.Presentation.Shell;
using AdMng.Presentation.Tests.Connections;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Pki;

/// <summary>The PKI node of the tree, the shell's PKI tabs and the <c>pki.*</c> commands (docs/dev/p-certificate-services.md 2.5).</summary>
public sealed class PkiTreeAndShellTests
{
    [Fact]
    public async Task ThePkiNode_StandsAfterTheGroupPolicyNodes_AndTheMergeKeepsIt()
    {
        var builder = new FakeDirectoryBuilder();
        builder.AddSampleCompany();
        await using var host = new ConnectionScopeHost(domains: [builder.Build()]);
        await using var window = host.CreateConnection();
        await window.ConnectAsync();
        var shell = window.Get<ShellViewModel>();
        await shell.InitializeAsync();
        var domain = shell.Tree.DomainNode!;
        await domain.EnsureChildrenLoadedAsync();

        var gpoNodes = domain.Children.TakeWhile(c => c is GroupPolicyNodeViewModel).Count();
        Assert.Equal(3, gpoNodes);
        Assert.IsType<PublicKeyServicesNodeViewModel>(domain.Children[gpoNodes]);
        Assert.Same(shell.Tree.PkiNode, domain.Children[gpoNodes]);

        await domain.RefreshAsync();
        Assert.IsType<GroupPolicyObjectsNodeViewModel>(domain.Children[0]);
        Assert.IsType<PublicKeyServicesNodeViewModel>(domain.Children[3]);

        // Without the Group Policy nodes the PKI node is the first child.
        await shell.ToggleGroupPolicyNodesCommand.ExecuteAsync(null);
        await B2Harness.WaitUntilAsync(() => !shell.Tree.IsGroupPolicyShown);
        Assert.IsType<PublicKeyServicesNodeViewModel>(domain.Children[0]);
    }

    [Fact]
    public async Task TheNode_HasTheFourDocuments_InPolish_AndRelocalizes()
    {
        var h = await PkiHarness.CreateAsync();
        using var shell = h.CreateShell();
        await shell.InitializeAsync();
        var node = shell.Tree.PkiNode!;

        Assert.Equal("Usługi kluczy publicznych", node.Name);
        Assert.Equal(PkiDocumentKind.Overview, node.Kind);
        Assert.Equal(
            ["Szablony certyfikatów", "Przegląd bezpieczeństwa AD CS", "Gotowość na silne mapowanie i NTAuth"],
            node.Children.Select(c => c.Name));
        Assert.Equal(
            [PkiDocumentKind.Templates, PkiDocumentKind.SecurityReview, PkiDocumentKind.Readiness],
            node.Children.OfType<PkiNodeViewModel>().Select(c => c.Kind));

        h.B2.Localizer.SetCulture(System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        await B2Harness.WaitUntilAsync(() => node.Name == "Public Key Services");
        Assert.Equal("Certificate Templates", node.Children[0].Name);
    }

    [Fact]
    public async Task TheViewToggle_HidesAndShowsTheNode_ThroughTheSetting()
    {
        var h = await PkiHarness.CreateAsync();
        using var shell = h.CreateShell();
        await shell.InitializeAsync();
        Assert.True(shell.Tree.IsPkiShown);
        Assert.True(shell.IsPkiNodesShown);

        shell.Tree.PkiNode!.IsSelected = true;
        await shell.TogglePkiNodesCommand.ExecuteAsync(null);
        await B2Harness.WaitUntilAsync(() => !shell.Tree.IsPkiShown);
        Assert.False(h.B2.Settings.Current.Pki.ShowInTree);
        Assert.Null(shell.Tree.PkiNode);
        Assert.Empty(shell.Tree.DomainNode!.Children.OfType<PkiNodeViewModel>());
        Assert.False(shell.IsPkiNodesShown);
        Assert.False(shell.Tree.SelectedNode is PkiNodeViewModel);

        await shell.TogglePkiNodesCommand.ExecuteAsync(null);
        await B2Harness.WaitUntilAsync(() => shell.Tree.IsPkiShown);
        Assert.NotNull(shell.Tree.PkiNode);
    }

    [Fact]
    public async Task SelectingTheNodes_OpensTheirDocuments_OncePerKind()
    {
        var h = await PkiHarness.CreateAsync();
        using var shell = h.CreateShell();
        await shell.InitializeAsync();
        var node = shell.Tree.PkiNode!;

        node.IsSelected = true;
        var overview = Assert.IsType<PkiOverviewDocumentViewModel>(shell.SelectedTab);
        await B2Harness.WaitUntilAsync(() => !overview.IsLoading);
        Assert.Same(overview, shell.ActivePki());

        node.Children[0].IsSelected = true;
        var templates = Assert.IsType<PkiTemplatesDocumentViewModel>(shell.SelectedTab);
        await B2Harness.WaitUntilAsync(() => !templates.IsLoading);

        node.IsSelected = true;
        Assert.Same(overview, shell.SelectedTab);
        Assert.Equal(2, shell.PkiDocuments.Count());
    }

    [Fact]
    public async Task ThePalette_ListsThePkiCommands_AndTheyOpenTheDocuments()
    {
        var h = await PkiHarness.CreateAsync();
        using var shell = h.CreateShell();
        await shell.InitializeAsync();

        Assert.Equal(
            ["pki.overview", "pki.templates", "pki.security", "pki.readiness", "view.pkiNodes"],
            shell.CommandDescriptors.Where(d => d.Id.StartsWith("pki.", StringComparison.Ordinal) || d.Id == "view.pkiNodes").Select(d => d.Id));
        var templates = Assert.Single(shell.CommandDescriptors, d => d.Id == "pki.templates");
        Assert.Equal(h.B2.Localizer[L.PkiTemplates.Command], h.B2.Localizer[templates.TitleKey]);
        Assert.True(templates.Command.CanExecute(null));

        templates.Command.Execute(null);
        var document = Assert.IsType<PkiTemplatesDocumentViewModel>(shell.SelectedTab);
        await B2Harness.WaitUntilAsync(() => !document.IsLoading);
        Assert.NotEmpty(document.Rows);
    }

    [Fact]
    public async Task ADocumentRequest_OpensTheTemplates_AndSelectsTheTemplate()
    {
        var h = await PkiHarness.CreateAsync();
        using var shell = h.CreateShell();
        await shell.InitializeAsync();
        var overview = Assert.IsType<PkiOverviewDocumentViewModel>(shell.OpenPkiDocument(new PkiDocumentRequest(PkiDocumentKind.Overview)));
        await B2Harness.WaitUntilAsync(() => !overview.IsLoading);

        overview.OpenTemplateCommand.Execute(PublicKeyTemplates.Esc1);
        var templates = Assert.IsType<PkiTemplatesDocumentViewModel>(shell.SelectedTab);
        await B2Harness.WaitUntilAsync(() => !templates.IsLoading);
        Assert.Equal(PublicKeyTemplates.Esc1, templates.SelectedRow?.Cn);

        // An open document is pointed at the next request's template.
        h.Services.RequestDocument(new PkiDocumentRequest(PkiDocumentKind.Templates) { TemplateCn = PublicKeyTemplates.Esc1Safe });
        Assert.Same(templates, shell.SelectedTab);
        Assert.Equal(PublicKeyTemplates.Esc1Safe, templates.SelectedRow?.Cn);

        // ... and the overview at the CA of the request.
        h.Services.RequestDocument(new PkiDocumentRequest(PkiDocumentKind.Overview) { CaDn = h.Sample.RootCa });
        Assert.Same(overview, shell.SelectedTab);
        Assert.True(Core.Ldap.DistinguishedName.AreEqual(h.Sample.RootCa, overview.SelectedAuthority!.Dn));
    }

    [Fact]
    public async Task WithoutPkiServices_TheShellHasNoNode_AndTheCommandsAreDisabled()
    {
        var b2 = await B2Harness.CreateAsync();
        using var shell = b2.CreateShell();
        await shell.InitializeAsync();

        Assert.Null(shell.Tree.PkiNode);
        Assert.False(shell.OpenPkiOverviewCommand.CanExecute(null));
        Assert.False(shell.TogglePkiNodesCommand.CanExecute(null));
        Assert.Null(shell.OpenPkiDocument(new PkiDocumentRequest(PkiDocumentKind.Overview)));
    }

    [Fact]
    public async Task Disconnected_TheOpenCommandsWait_ButTheToggleWorks()
    {
        var h = await PkiHarness.CreateAsync();
        using var shell = h.CreateShell();
        await shell.InitializeAsync();
        await h.B2.Sessions.DisconnectAsync();
        await B2Harness.WaitUntilAsync(() => !shell.OpenPkiOverviewCommand.CanExecute(null));

        Assert.False(shell.OpenPkiTemplatesCommand.CanExecute(null));
        Assert.True(shell.TogglePkiNodesCommand.CanExecute(null));
    }
}
