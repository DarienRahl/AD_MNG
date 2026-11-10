using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AdMng.App.GroupPolicy;
using AdMng.App.Infrastructure;
using AdMng.App.Pki;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Pki;
using AdMng.Core.Pki.Tls;
using AdMng.Presentation.Pki;
using AdMng.Presentation.Shell;
using AdMng.Testing;
using AdMng.Testing.Pki;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.App.UiTests.Pki;

/// <summary>
/// <i>Przegląd bezpieczeństwa AD CS</i> and <i>Gotowość na silne mapowanie i NTAuth</i> in the main window (P1 W6, design note
/// docs/dev/p-certificate-services.md 2.5, 2.10) over the Polish PKI sample: the review's virtualized list grouped by object
/// with severity dots, the chips, the details behind a splitter with the navigation (no <i>Napraw</i>), Ctrl+F into the search;
/// the readiness report's six areas, the LDAPS check only on its button; binding-error free.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class PkiSecurityUiTests
{
    [Fact]
    public Task SecurityReview_ShowsTheFindingsGroupedByObject_TheDetails_TheChips_AndTheSearch() => WpfTestHost.RunAsync(async () =>
    {
        var probe = new FakeDomainControllerTlsProbe();
        await using var context = await UiTestContext.CreateAsync(language: "pl", PkiSecurityUiSupport.Configure(probe));
        context.Directory.AddPublicKeySample(context.Company);
        await context.ConnectAsync();
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        var shell = main.Shell;
        await shell.InitializeAsync();

        var review = await PkiSecurityUiSupport.OpenReviewAsync(shell);
        Assert.Same(review, PkiSecurityUiSupport.Open(shell, PkiDocumentKind.SecurityReview));
        Assert.Single(shell.Documents.OfType<PkiSecurityReviewDocumentViewModel>());
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        var view = Assert.Single(UiWait.Descendants<PkiSecurityReviewView>(main), v => v.IsVisible);

        Assert.Equal("Przegląd bezpieczeństwa AD CS", view.TitleText.Text);
        Assert.Equal(TextTrimming.CharacterEllipsis, view.TitleText.TextTrimming);
        Assert.Equal(review.Rows.Count, view.FindingsList.Items.Count);
        Assert.Equal(review.Rows.Select(r => r.GroupText).Distinct(StringComparer.Ordinal).Count(), view.FindingsList.Items.Groups!.Count);
        Assert.True(VirtualizingPanel.GetIsVirtualizing(view.FindingsList));
        Assert.True(VirtualizingPanel.GetIsVirtualizingWhenGrouping(view.FindingsList));
        Assert.Equal(5, view.ChipsList.Items.Count);
        Assert.True(view.NotesBar.IsVisible);
        Assert.Equal(review.ScopeNote, view.ScopeNoteText.Text);
        Assert.False(view.NotesList.IsVisible);
        Assert.Single(UiWait.Descendants<PaneSplitter>(view));
        Assert.True(view.DetailsEmptyText.IsVisible);
        Assert.True(view.NoFixNoteText.IsVisible);
        Assert.Null(view.FindName("FixButton"));
        Assert.Empty(probe.Calls);

        // Headers wider than their texts (nothing trimmed).
        var gridView = (GridView)view.FindingsList.View;
        foreach (var column in gridView.Columns)
        {
            var text = Assert.IsType<string>(column.Header);
            var header = new TextBlock { Text = text };
            header.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.True(column.Width > header.DesiredSize.Width + 16, text);
        }

        // A template finding: its details and the navigation to the template and the CA; nothing to open as an object.
        var esc1 = review.Rows.Single(r => r.TemplateCn == PublicKeyTemplates.Esc1);
        view.FindingsList.SelectedItem = esc1;
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.Same(esc1, review.SelectedRow);
        Assert.True(view.DetailsScroll.IsVisible);
        Assert.Equal(("ESC1", esc1.Title, esc1.Message), (view.DetailsEsc.Text, view.DetailsTitle.Text, view.DetailsMessage.Text));
        Assert.Equal("Certipy: ESC1 · Locksmith: ESC1 · MDI: –", view.DetailsIds.Text);
        Assert.Equal((1, 1, 1), (view.PrincipalsList.Items.Count, view.FindingNotesList.Items.Count, view.RelatedList.Items.Count));
        Assert.True(view.OpenTemplateButton.IsEnabled);
        Assert.True(view.OpenCaButton.IsEnabled);
        Assert.False(view.OpenObjectButton.IsEnabled);
        Assert.Equal("Otwórz obiekt", view.OpenObjectButton.Content);

        // An account finding opens the account.
        view.FindingsList.SelectedItem = review.Rows.First(r => r.Esc == "ESC14" && r.OpenDn is not null);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal("Otwórz konto", view.OpenObjectButton.Content);
        Assert.True(view.OpenObjectButton.IsEnabled);
        Assert.False(view.OpenTemplateButton.IsEnabled);

        // A chip filters; Ctrl+F reaches the document's search box.
        var high = review.Chips.Single(c => c.Severity == PkiFindingSeverity.High);
        high.IsSelected = true;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(high.Count, view.FindingsList.Items.Count);
        Assert.True(view.ClearFiltersButton.IsVisible);
        await shell.FindCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.Same(view.SearchBox, FocusManager.GetFocusedElement(FocusManager.GetFocusScope(view.SearchBox)));
        review.ClearFiltersCommand.Execute(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(review.TotalCount, view.FindingsList.Items.Count);
        Assert.False(view.ClearFiltersButton.IsVisible);
        context.AssertHealthy();
    });

    [Fact]
    public Task Readiness_ShowsTheSixAreas_AndProbesTheDomainControllersOnlyOnItsButton() => WpfTestHost.RunAsync(async () =>
    {
        var probe = new FakeDomainControllerTlsProbe();
        await using var context = await UiTestContext.CreateAsync(language: "pl", PkiSecurityUiSupport.Configure(probe));
        context.Directory.AddPublicKeySample(context.Company);
        PkiSecurityUiSupport.PresentTheSampleCertificate(probe);
        await context.ConnectAsync();
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        var shell = main.Shell;
        await shell.InitializeAsync();

        var readiness = await PkiSecurityUiSupport.OpenReadinessAsync(shell);
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        var view = Assert.Single(UiWait.Descendants<PkiReadinessView>(main), v => v.IsVisible);

        Assert.Equal("Gotowość na silne mapowanie i NTAuth", view.TitleText.Text);
        Assert.Equal(6, view.GroupsList.Items.Count);
        Assert.True(view.SummaryTextBlock.IsVisible);
        Assert.Equal(readiness.SummaryText, view.SummaryTextBlock.Text);
        Assert.True(view.ReadOnlyNoteText.IsVisible);
        Assert.False(view.ProgressPanel.IsVisible);
        Assert.Equal(PkiReadinessStatus.NotChecked, readiness.Groups.Single(g => g.Area == PkiReadinessArea.DomainControllerLdaps).Status);
        Assert.Empty(probe.Calls);

        Assert.True(view.CheckLdapsButton.IsEnabled);
        await PkiSecurityUiSupport.CheckLdapsAsync(readiness);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.NotEmpty(probe.Calls);
        Assert.All(probe.Calls, c => Assert.Equal("dc01.contoso.test", c.Host, ignoreCase: true));
        var ldaps = readiness.Groups.Single(g => g.Area == PkiReadinessArea.DomainControllerLdaps);
        Assert.Equal(PkiReadinessStatus.Ok, ldaps.Status);
        Assert.All(ldaps.AllRows, r => Assert.StartsWith("Wystawca: Kontoso Wystawiający CA", r.Detail, StringComparison.Ordinal));
        Assert.Equal(PkiReadinessStatus.Problem, readiness.Groups.Single(g => g.Area == PkiReadinessArea.NtAuth).Status);
        Assert.Equal(6, view.GroupsList.Items.Count);
        context.AssertHealthy();
    });
}

/// <summary>The shared set-up of the W6 UI and screenshot tests (the sample's clock, the fake TLS probe, opening the documents).</summary>
internal static class PkiSecurityUiSupport
{
    /// <summary>
    /// The application on the sample's clock (2026-10-13 12:00 UTC, UTC as the local zone: the certificates' validity and the
    /// shown dates do not depend on the day the tests run) and with <paramref name="probe"/> as the DC LDAPS probe.
    /// </summary>
    public static Action<IServiceCollection> Configure(FakeDomainControllerTlsProbe probe) => services =>
    {
        services.Replace(ServiceDescriptor.Singleton<TimeProvider>(new ManualTimeProvider(PkiSamples.Now)));
        services.Replace(ServiceDescriptor.Singleton<IDomainControllerTlsProbe>(probe));
    };

    /// <summary>The sample DC presents its LDAPS certificate (issued by the issuing CA) on 636 and 3269.</summary>
    public static void PresentTheSampleCertificate(FakeDomainControllerTlsProbe probe)
    {
        foreach (var port in (int[])[636, 3269])
        {
            probe.Set(PkiSamples.DcHost, port, TlsProbeOutcome.Ok, PkiSamples.DcLdapsDer(), PkiSamples.IssuingCaDer);
        }
    }

    public static PkiDocumentBase Open(ShellViewModel shell, PkiDocumentKind kind) =>
        shell.OpenPkiDocument(new PkiDocumentRequest(kind)) ?? throw new InvalidOperationException($"The PKI document {kind} did not open.");

    /// <summary>Opens the security review and waits for its check.</summary>
    public static async Task<PkiSecurityReviewDocumentViewModel> OpenReviewAsync(ShellViewModel shell)
    {
        var review = Assert.IsType<PkiSecurityReviewDocumentViewModel>(Open(shell, PkiDocumentKind.SecurityReview));
        await UiWait.UntilAsync(() => !review.IsLoading && review.HasResult, "the AD CS security review");
        Assert.Null(review.Error);
        return review;
    }

    /// <summary>Opens the readiness report and waits for its reads.</summary>
    public static async Task<PkiReadinessDocumentViewModel> OpenReadinessAsync(ShellViewModel shell)
    {
        var readiness = Assert.IsType<PkiReadinessDocumentViewModel>(Open(shell, PkiDocumentKind.Readiness));
        await UiWait.UntilAsync(() => !readiness.IsLoading && readiness.HasResult, "the strong mapping and NTAuth readiness report");
        Assert.Null(readiness.Error);
        return readiness;
    }

    /// <summary><i>Sprawdź certyfikaty LDAPS</i> and the wait for its probe.</summary>
    public static async Task CheckLdapsAsync(PkiReadinessDocumentViewModel readiness)
    {
        Assert.True(readiness.CheckLdapsCommand.CanExecute(null));
        await readiness.CheckLdapsCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => !readiness.IsCheckingLdaps, "the LDAPS check");
        Assert.Null(readiness.Error);
    }
}
