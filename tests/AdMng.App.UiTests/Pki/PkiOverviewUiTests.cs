using System.Windows.Controls;
using AdMng.App.Behaviors;
using AdMng.App.Infrastructure;
using AdMng.App.Pki;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Pki.Tls;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Pki;
using AdMng.Presentation.Settings;
using AdMng.Testing;
using AdMng.Testing.Pki;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests.Pki;

/// <summary>
/// AD CS (P1, docs/dev/p-certificate-services.md 2.10) in the main window over the PKI sample: the tree node and its menus,
/// <i>Usługi kluczy publicznych</i> with every tab (the LDAPS tab through the host's fake probe), <i>Szablony certyfikatów</i>
/// with the details and the compare pane, and the <i>Certyfikaty</i> section of the settings – without a binding error.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class PkiOverviewUiTests
{
    [Fact]
    public Task TheNode_TheMenus_AndEveryOverviewTab_Render() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language: "pl");
        context.Directory.AddPublicKeySample(context.Company);
        var probe = Assert.IsType<FakeDomainControllerTlsProbe>(context.Services.GetRequiredService<IDomainControllerTlsProbe>());
        probe.Set(PkiSamples.DcHost, 636, TlsProbeOutcome.Ok, PkiSamples.DcLdapsDer(), PkiSamples.IssuingCaDer);
        await context.ConnectAsync();
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        var shell = main.Shell;
        await shell.InitializeAsync();
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Narzędzia → Usługi certyfikatów (AD CS) and Widok → Usługi kluczy publicznych w drzewie.
        Assert.Equal(["pki.overview", "pki.templates", "pki.security", "pki.readiness"], main.PkiMenu.Items.OfType<MenuItem>().Select(CommandMenu.GetId));
        Assert.Equal("view.pkiNodes", CommandMenu.GetId(main.PkiNodesMenuItem));
        Assert.True(main.PkiNodesMenuItem.IsChecked);
        Assert.Same(shell.OpenPkiOverviewCommand, main.PkiMenu.Items.OfType<MenuItem>().First().Command);

        // The node under the domain, rendered by its own template.
        var node = Assert.IsType<PublicKeyServicesNodeViewModel>(shell.Tree.PkiNode);
        Assert.Contains(UiWait.Descendants<TreeViewItem>(main), i => ReferenceEquals(i.DataContext, node));
        node.IsSelected = true;
        var document = Assert.IsType<PkiOverviewDocumentViewModel>(shell.SelectedTab);
        await UiWait.UntilAsync(() => !document.IsLoading && document.Snapshot is not null, "the PKI overview");
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);

        var view = Assert.Single(UiWait.Descendants<PkiOverviewView>(main));
        Assert.Equal("Usługi kluczy publicznych", view.TitleText.Text);
        Assert.Equal(2, view.AuthoritiesList.Items.Count);
        Assert.Equal(document.SummaryText, view.SummaryText.Text);
        Assert.True(view.OpenComputerButton.IsEnabled);
        Assert.NotEmpty(view.PublishedTemplatesList.Items.Cast<object>());

        foreach (var tab in Enum.GetValues<PkiOverviewTab>())
        {
            document.SelectedTab = tab;
            main.UpdateLayout();
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.Equal((int)tab, view.OverviewTabs.SelectedIndex);
        }

        // The LDAPS tab probed on its first activation (the host's fake), the KDC column from the DC object.
        await UiWait.UntilAsync(() => document.HasLdapsRows && !document.IsProbingLdaps, "the DC LDAPS checks");
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.NotEmpty(probe.Calls);
        Assert.True(view.LdapsList.Items.Count >= 1);
        Assert.True(view.ProbeLdapsButton.IsEnabled);

        // The health tab: the rows, then the HTTP check on the button only.
        document.SelectedTab = PkiOverviewTab.Health;
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.True(view.HealthList.Items.Count > 0);
        var http = Assert.IsType<FakePkiHealthProbe>(context.Services.GetRequiredService<IPkiHealthProbe>());
        Assert.Empty(http.Calls);
        await document.CheckHttpCommand.ExecuteAsync(null);
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Single(http.Calls);
        Assert.True(view.HttpResultsExpander.IsVisible);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheTemplates_ShowTheFlagsTheRightsAndTheComparePane() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language: "pl");
        context.Directory.AddPublicKeySample(context.Company);
        await context.ConnectAsync();
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        var shell = main.Shell;
        await shell.InitializeAsync();

        var document = Assert.IsType<PkiTemplatesDocumentViewModel>(
            shell.OpenPkiDocument(new PkiDocumentRequest(PkiDocumentKind.Templates) { TemplateCn = PublicKeyTemplates.Esc1 }));
        await UiWait.UntilAsync(() => !document.IsLoading && document.IsExpanded, "the templates and their account counts");
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);

        var view = Assert.Single(UiWait.Descendants<PkiTemplatesView>(main));
        Assert.Equal("Szablony certyfikatów", view.TitleText.Text);
        Assert.Equal(PublicKeyTemplates.All.Count, view.TemplatesList.Items.Count);
        Assert.Equal(PublicKeyTemplates.Esc1, document.SelectedRow?.Cn);
        Assert.Equal(4, view.FlagGroupsList.Items.Count);
        Assert.True(view.EnrollTrusteesList.Items.Count > 0);
        Assert.True(view.ExportCsvButton.IsEnabled);
        Assert.False(view.ComparePane.IsVisible);

        // Two selected rows compare with each other.
        view.TemplatesList.SelectedItems.Clear();
        view.TemplatesList.SelectedItems.Add(document.Rows.Single(r => r.Cn == PublicKeyTemplates.Esc1));
        view.TemplatesList.SelectedItems.Add(document.Rows.Single(r => r.Cn == PublicKeyTemplates.Esc1Safe));
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(2, document.SelectedRows.Count);
        view.CompareButton.Command.Execute(null);
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.True(view.ComparePane.IsVisible);
        Assert.True(view.CompareList.Items.Count > 0);

        // The search box filters (Ctrl+F reaches it through the shell).
        view.SearchBox.Text = "bezpieczn";
        await UiWait.UntilAsync(() => document.Rows.Count == PublicKeyTemplates.Safe.Count, "the search");
        context.AssertHealthy();
    });

    [Fact]
    public Task TheSettings_HaveTheCertificatesSection() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language: "pl");
        var settings = context.Services.GetRequiredService<IDialogViewModelFactory>().CreateSettings();
        var window = (SettingsWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(settings);
        UiTestSupport.ShowOffScreen(window);
        await window.Initialization;
        await UiTestSupport.WaitForIdleAsync();

        Assert.Same(settings.Pki, window.PkiSection.DataContext);
        Assert.Equal("30", window.CertificateExpiryBox.Text);
        Assert.Equal("180", window.CaCertificateExpiryBox.Text);

        window.CaCertificateExpiryBox.Text = "0";
        await UiTestSupport.WaitForIdleAsync();
        Assert.NotNull(settings.Error);
        Assert.False(settings.SaveCommand.CanExecute(null));

        window.CaCertificateExpiryBox.Text = "365";
        await UiTestSupport.WaitForIdleAsync();
        await settings.SaveCommand.ExecuteAsync(null);
        Assert.Equal(365, context.Services.GetRequiredService<ISettingsStore>().Current.Pki.CaCertificateExpiryWarningDays);
        context.AssertHealthy();
    });
}
