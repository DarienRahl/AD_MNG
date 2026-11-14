using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.Pki;
using AdMng.App.UiTests.GroupPolicy;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Pki;
using AdMng.Core.Pki.Tls;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Pki;
using AdMng.Presentation.Session;
using AdMng.Testing;
using AdMng.Testing.Pki;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static AdMng.App.UiTests.GroupPolicy.GroupPolicyStatusUiSupport;

namespace AdMng.App.UiTests.Pki;

/// <summary>
/// The status line of the four PKI documents (track V, stage V2b-4, unit 3; docs/dev/v-interface-renewal.md "V2b-4 as built",
/// <i>Areas</i> 5), through the real views over the fake directory with the Polish PKI sample: one line under the header of each -
/// the summary, the read with its stage and Stop, a read that was stopped with "Refresh", the end of the session -, the buttons that
/// read, check and probe as action buttons that are the Cancel of their operation, no separate "Cancel" and no progress bar, and
/// the sentences that are true of the content as notes. A slow domain controller is a read the test holds, a slow HTTP check or
/// LDAPS probe a call that waits for the test; every state that is asserted lasts until the test changes it, and "Stopping…" is
/// observed through the change of the status, not by looking at the screen afterwards. What the documents show is covered by
/// <see cref="PkiOverviewUiTests"/> and <see cref="PkiSecurityUiTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class PkiStatusUiTests
{
    private static Action<IServiceCollection> Configure(FakeDomainControllerTlsProbe probe, GatedHealthProbe? health = null, GatedTlsProbe? tls = null)
    {
        var baseline = PkiSecurityUiSupport.Configure(probe);
        return services =>
        {
            baseline(services);
            if (health is not null)
            {
                services.Replace(ServiceDescriptor.Singleton<IPkiHealthProbe>(health));
            }

            if (tls is not null)
            {
                services.Replace(ServiceDescriptor.Singleton<IDomainControllerTlsProbe>(tls));
            }
        };
    }

    /// <summary>The main window of a connected window over the PKI sample, the layout done.</summary>
    private static async Task<MainWindow> OpenMainAsync(UiTestContext context)
    {
        context.Directory.AddPublicKeySample(context.Company);
        await context.ConnectAsync();
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        await main.Shell.InitializeAsync();
        return main;
    }

    private static async Task<TView> ViewOfAsync<TView>(MainWindow main)
        where TView : FrameworkElement
    {
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        return Assert.Single(UiWait.Descendants<TView>(main), v => v.IsVisible);
    }

    /// <summary>The one status line of a PKI view: named PageStatus, over the status of the document, of the fixed height at rest.</summary>
    private static void AssertTheLine(StatusLine line, FrameworkElement view, PkiDocumentBase document)
    {
        Assert.Same(line, Assert.Single(UiWait.Descendants<StatusLine>(view)));
        Assert.Equal("PageStatus", line.Name);
        Assert.IsAssignableFrom<IHasPageStatus>(document);
        Assert.Same(document.Status.Current, line.Status);
        Assert.Same(document.Status.CloseCommand, line.CloseCommand);
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);
        AssertNoOldFeedback(view);
        Assert.DoesNotContain(
            UiWait.Descendants<Button>(view),
            button => button is not ActionButton && !button.IsDescendantOf(line) && (button.Content as string is "Cancel" or "Stop"));
    }

    /// <summary>A text of the view, outside the line, that says what the line says: the summary must be said once.</summary>
    private static void AssertNotSaidTwice(StatusLine line, FrameworkElement view, params string[] texts) =>
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(view),
            text => text.IsVisible && !text.IsDescendantOf(line) && texts.Contains(text.Text, StringComparer.Ordinal));

    private static bool IsReadOfThePki(FakeOperation operation) =>
        operation.DistinguishedName.EndsWith(PkiSamples.PublicKeyServicesDn, StringComparison.OrdinalIgnoreCase);

    /// <summary>Records every message the holder says (UI thread): "Stopping…" is a state of a moment.</summary>
    private static List<string> Record(PageStatusHolder holder)
    {
        var said = new List<string>();
        holder.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PageStatusHolder.Current))
            {
                said.Add(holder.Current.Message);
            }
        };
        return said;
    }

    [Fact]
    public Task TheOverview_HasOneStatusLine_UnderTheHeader_WithTheSummary_TheSentencesAreNotes_AndTheButtonsAreActionButtons() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync("en", Configure(new FakeDomainControllerTlsProbe()));
        var main = await OpenMainAsync(context);
        var document = Assert.IsType<PkiOverviewDocumentViewModel>(PkiSecurityUiSupport.Open(main.Shell, PkiDocumentKind.Overview));
        await UiWait.UntilAsync(() => !document.IsLoading && document.Snapshot is not null, "the PKI overview");
        var view = await ViewOfAsync<PkiOverviewView>(main);
        var line = view.PageStatus;

        // One line, under the header and above the tabs; the summary - what was read, when and from where, the selected CA - is
        // said there, once.
        AssertTheLine(line, view, document);
        Assert.True(TopOf(view.TitleText, view) < TopOf(line, view), "the status line is not under the header");
        Assert.True(TopOf(line, view) < TopOf(view.OverviewTabs, view), "the status line is not above the tabs");
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.StartsWith("Enterprise CAs: 2 · templates: ", line.Message, StringComparison.Ordinal);
        Assert.Contains(" · read from ", line.Message, StringComparison.Ordinal);
        Assert.Contains(PkiSamples.DcHost, line.Message, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(" · " + document.SelectionText, line.Message, StringComparison.Ordinal);
        Assert.Equal(AppIcons.StatusInfo, line.IconKey);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        AssertNotSaidTwice(line, view, document.SummaryText, document.ReadAtText);

        // The three buttons that start an operation are action buttons over their own commands and the Cancel of their own holder.
        Assert.Equal(ActionButtonState.Idle, view.RefreshButton.State);
        Assert.Same(document.RefreshCommand, view.RefreshButton.Command);
        Assert.Same(document.Status.CancelCommand, view.RefreshButton.CancelCommand);
        Assert.Same(document.CheckHttpCommand, view.CheckHttpButton.Command);
        Assert.Same(document.HealthStatus.CancelCommand, view.CheckHttpButton.CancelCommand);
        Assert.Same(document.ProbeLdapsCommand, view.ProbeLdapsButton.Command);
        Assert.Same(document.LdapsStatus.CancelCommand, view.ProbeLdapsButton.CancelCommand);

        // What is true of the content while it is shown is a note: what the read observed, the CA that is not in NTAuth.
        var attention = Application.Current.FindResource("Ad.Note.Attention");
        Assert.Same(attention, view.NoticesBar.Style);
        Assert.Same(attention, view.NtAuthWarning.Style);
        var outsideNtAuth = document.Authorities.Single(a => a.HasNtAuthWarning);
        document.SelectedAuthority = outsideNtAuth;
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.True(view.NtAuthWarning.IsVisible);
        Assert.Equal(outsideNtAuth.NtAuthWarning, view.NtAuthWarningText.Text);

        // The health tab says its own counts in front of the document's, and why the HTTP rows are unknown - a note.
        document.SelectedTab = PkiOverviewTab.Health;
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(document.HealthSummaryText, line.Message);
        Assert.Same(Application.Current.FindResource("Ad.Note.Info"), view.HttpNoteBar.Style);
        Assert.True(view.HttpNoteBar.IsVisible);
        Assert.Equal(document.HttpNote, view.HttpNoteText.Text);
        AssertNotSaidTwice(line, view, document.HealthSummaryText);
        context.AssertHealthy();
    });

    [Fact]
    public Task AReadOfTheOverview_SaysItsStage_ItsButtonIsItsCancel_AndAStoppedReadLastsWithRefresh() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync("en", Configure(new FakeDomainControllerTlsProbe()));
        var main = await OpenMainAsync(context);
        var document = Assert.IsType<PkiOverviewDocumentViewModel>(PkiSecurityUiSupport.Open(main.Shell, PkiDocumentKind.Overview));
        await UiWait.UntilAsync(() => !document.IsLoading && document.Snapshot is not null, "the PKI overview");
        var view = await ViewOfAsync<PkiOverviewView>(main);
        var line = view.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        var tabsTop = TopOf(view.OverviewTabs, view);

        using (var held = new HeldRead(context.Directory.Session, IsReadOfThePki))
        {
            // "Refresh": the PKI objects are read again, and the first read of the domain controller waits for the test.
            ControlHost.Press(view.RefreshButton);
            await UiWait.UntilAsync(() => held.IsReached && document.IsLoading && line.Kind == PageStatusKind.Working, "the read runs");

            // Working: what is read and the stage, with Stop - and nothing under the line moved.
            Assert.Equal("Working", line.Title);
            Assert.StartsWith("Reading “Public Key Services” – AD CS objects: ", line.Message, StringComparison.Ordinal);
            Assert.True(ring.IsActive);
            Assert.Equal("Stop", Assert.IsType<string>(action.Content));
            Assert.Equal(Visibility.Collapsed, close.Visibility);
            Assert.Equal(ActionButtonState.Running, view.RefreshButton.State);
            Assert.True(view.RefreshButton.IsEnabled);
            view.UpdateLayout();
            Assert.Equal(tabsTop, TopOf(view.OverviewTabs, view));
            AssertNoOldFeedback(view);

            // Cancel where you started: the status says "Stopping…" at the moment of the press.
            var said = Record(document.Status);
            ControlHost.Press(view.RefreshButton);
            await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Attention && !document.IsLoading, "the read was stopped");
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.Contains("Stopping…", said);

            // Stopped: the tab may not show everything - a state of the tab, which cannot be closed - and Refresh reads again.
            Assert.Equal("Attention", line.Title);
            Assert.Equal("Reading “Public Key Services” was stopped: the tab may not show everything. Press “Refresh” to read it again.", line.Message);
            Assert.Equal(AppIcons.StatusWarning, line.IconKey);
            Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
            Assert.False(ring.IsActive);
            Assert.Equal("Refresh", Assert.IsType<string>(action.Content));
            Assert.Same(document.RefreshCommand, action.Command);
            Assert.Equal(Visibility.Collapsed, close.Visibility);
            Assert.Equal(ActionButtonState.Idle, view.RefreshButton.State);
            view.UpdateLayout();
            Assert.Equal(tabsTop + GrowthOf(line), TopOf(view.OverviewTabs, view), 0.5);
        }

        // Refresh, pressed in the line, reads again: the summary is back and the state is gone.
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !document.IsLoading && line.Kind == PageStatusKind.Idle && line.Message.Length > 0, "the read ran again");
        Assert.StartsWith("Enterprise CAs: 2", line.Message, StringComparison.Ordinal);
        Assert.Null(document.Status.Lasting);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheHttpCheck_IsAnActionButtonWithItsOwnStop_InFrontOfTheDocument_AndItsResultIsTheRowsAndTheSummary() => WpfTestHost.RunAsync(async () =>
    {
        var health = new GatedHealthProbe(new FakePkiHealthProbe());
        await using var context = await UiTestContext.CreateAsync("en", Configure(new FakeDomainControllerTlsProbe(), health: health));
        var main = await OpenMainAsync(context);
        var document = Assert.IsType<PkiOverviewDocumentViewModel>(PkiSecurityUiSupport.Open(main.Shell, PkiDocumentKind.Overview));
        await UiWait.UntilAsync(() => !document.IsLoading && document.Snapshot is not null, "the PKI overview");
        document.SelectedTab = PkiOverviewTab.Health;
        var view = await ViewOfAsync<PkiOverviewView>(main);
        var line = view.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var total = PkiHealth.HttpUrls(document.Snapshot!).Count;

        // Before the check: the counts of the tab, and the note that says why the HTTP rows are unknown.
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(document.HealthSummaryText, line.Message);
        Assert.True(view.HttpNoteBar.IsVisible);
        Assert.Equal(ActionButtonState.Idle, view.CheckHttpButton.State);

        // The check is the operation of the tab: how far it is, with Stop; the button is its Cancel, the document itself does not work.
        ControlHost.Press(view.CheckHttpButton);
        await UiWait.UntilAsync(
            () => health.IsReached && document.IsCheckingHttp && line.Kind == PageStatusKind.Working && line.Message.EndsWith($" – 2 of {total}…", StringComparison.Ordinal),
            "the HTTP check runs");
        Assert.Equal($"Checking the AIA, CDP and OCSP addresses – 2 of {total}…", line.Message);
        Assert.Equal(ActionButtonState.Running, view.CheckHttpButton.State);
        Assert.True(view.CheckHttpButton.IsEnabled);
        Assert.Equal(ActionButtonState.Idle, view.RefreshButton.State);
        Assert.False(document.Status.IsWorking);
        AssertNoOldFeedback(view);

        var said = Record(document.HealthStatus);
        ControlHost.Press(view.CheckHttpButton);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Attention && !document.IsCheckingHttp, "the HTTP check was stopped");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Contains("Stopping…", said);
        Assert.Equal("Checking the AIA, CDP and OCSP addresses was stopped: the results were not changed. Press “Check HTTP URLs” to check again.", line.Message);
        Assert.Equal("Check HTTP URLs", Assert.IsType<string>(action.Content));
        Assert.Same(document.CheckHttpCommand, action.Command);
        Assert.Equal(ActionButtonState.Idle, view.CheckHttpButton.State);
        Assert.False(document.HasHttpRows);

        // Again, from the line: the result is the rows and the summary of the tab - the note about the unknown rows is gone.
        health.Release();
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !document.IsCheckingHttp && document.HasHttpRows && line.Kind == PageStatusKind.Idle, "the HTTP check ran");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal($"{document.HealthSummaryText} · {document.HttpStatusText}", line.Message);
        Assert.False(view.HttpNoteBar.IsVisible);
        Assert.True(view.HttpResultsExpander.IsVisible);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheTemplates_HaveOneStatusLine_WithTheSummary_RefreshIsAnActionButton_AndWhatIsTrueOfATemplateIsANote() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync("en", Configure(new FakeDomainControllerTlsProbe()));
        var main = await OpenMainAsync(context);
        var document = Assert.IsType<PkiTemplatesDocumentViewModel>(PkiSecurityUiSupport.Open(main.Shell, PkiDocumentKind.Templates));
        await UiWait.UntilAsync(() => !document.IsLoading && document.IsExpanded, "the templates and their account counts");
        var view = await ViewOfAsync<PkiTemplatesView>(main);
        var line = view.PageStatus;

        AssertTheLine(line, view, document);
        Assert.True(TopOf(view.TitleText, view) < TopOf(line, view), "the status line is not under the header");
        Assert.True(TopOf(line, view) < TopOf(view.SearchBox, view), "the status line is not above the filters");
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.StartsWith(document.SummaryText + " · accounts counted: ", line.Message, StringComparison.Ordinal);
        Assert.EndsWith(" · " + document.SelectionText, line.Message, StringComparison.Ordinal);
        AssertNotSaidTwice(line, view, document.SummaryText, document.CountStatusText);
        Assert.Equal(ActionButtonState.Idle, view.RefreshButton.State);
        Assert.Same(document.RefreshCommand, view.RefreshButton.Command);
        Assert.Same(document.Status.CancelCommand, view.RefreshButton.CancelCommand);

        // "Its rights were not evaluated" is true of the template while it is shown: a note, in its place.
        Assert.Same(Application.Current.FindResource("Ad.Note.Attention"), view.NotEvaluatedNote.Style);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheReadiness_HasOneStatusLine_TheProbeIsAnActionButtonWithItsStop_AndWhatTheReportIsIsANote() => WpfTestHost.RunAsync(async () =>
    {
        var probe = new FakeDomainControllerTlsProbe();
        PkiSecurityUiSupport.PresentTheSampleCertificate(probe);
        var gate = new GatedTlsProbe(probe);
        await using var context = await UiTestContext.CreateAsync("en", Configure(probe, tls: gate));
        var main = await OpenMainAsync(context);
        var document = await PkiSecurityUiSupport.OpenReadinessAsync(main.Shell);
        var view = await ViewOfAsync<PkiReadinessView>(main);
        var line = view.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");

        // One line, under the header and above the note and the areas: what the rows found, then when and where - said once.
        AssertTheLine(line, view, document);
        Assert.True(TopOf(view.TitleText, view) < TopOf(line, view), "the status line is not under the header");
        Assert.True(TopOf(line, view) < TopOf(view.NotesBar, view), "the note is not under the status line");
        Assert.True(TopOf(view.NotesBar, view) < TopOf(view.GroupsList, view), "the areas are not under the note");
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal($"{document.SummaryText} · {document.LastRunText}", line.Message);
        AssertNotSaidTwice(line, view, document.SummaryText, document.LastRunText);

        // What the report is: a note.
        Assert.Same(Application.Current.FindResource("Ad.Note.Info"), view.NotesBar.Style);
        Assert.True(view.ReadOnlyNoteText.IsVisible);

        // The two buttons are action buttons; the probe says what it does, with Stop, and its button is its Cancel.
        Assert.Equal(ActionButtonState.Idle, view.CheckAgainButton.State);
        Assert.Equal(ActionButtonState.Idle, view.CheckLdapsButton.State);
        Assert.Same(document.Status.CancelCommand, view.CheckLdapsButton.CancelCommand);
        ControlHost.Press(view.CheckLdapsButton);
        await UiWait.UntilAsync(() => gate.IsReached && document.IsCheckingLdaps && line.Kind == PageStatusKind.Working, "the probe runs");
        Assert.Equal("Checking the LDAPS certificates of the domain controllers…", line.Message);
        Assert.Equal(ActionButtonState.Running, view.CheckLdapsButton.State);
        Assert.Equal(ActionButtonState.Idle, view.CheckAgainButton.State);
        AssertNoOldFeedback(view);

        var said = Record(document.Status);
        ControlHost.Press(view.CheckLdapsButton);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Attention && !document.IsCheckingLdaps, "the probe was stopped");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Contains("Stopping…", said);
        Assert.Equal(
            "Checking the LDAPS certificates of the domain controllers was stopped: the results were not changed. Press “Check LDAPS certificates” to check again.",
            line.Message);
        Assert.Equal("Check LDAPS certificates", Assert.IsType<string>(action.Content));
        Assert.Same(document.CheckLdapsCommand, action.Command);
        Assert.Equal(ActionButtonState.Idle, view.CheckLdapsButton.State);

        // Again, from the line: the area has its result and the line is the summary.
        gate.Release();
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !document.IsCheckingLdaps && line.Kind == PageStatusKind.Idle && line.Message.Length > 0, "the probe ran");
        Assert.Equal($"{document.SummaryText} · {document.LastRunText}", line.Message);
        Assert.Equal(PkiReadinessStatus.Ok, document.Groups.Single(g => g.Area == PkiReadinessArea.DomainControllerLdaps).Status);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheSecurityReview_HasOneButtonThatChecksAndCancels_NoSeparateCancel_AndTheNotesAreNotes() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync("en", Configure(new FakeDomainControllerTlsProbe()));
        var main = await OpenMainAsync(context);
        var review = await PkiSecurityUiSupport.OpenReviewAsync(main.Shell);
        var view = await ViewOfAsync<PkiSecurityReviewView>(main);
        var line = view.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var findingsTop = TopOf(view.FindingsList, view);

        // One line under the header and above the notes: the findings, when and where the check ran - said once.
        AssertTheLine(line, view, review);
        Assert.True(TopOf(view.TitleText, view) < TopOf(line, view), "the status line is not under the header");
        Assert.True(TopOf(line, view) < TopOf(view.NotesBar, view), "the notes are not under the status line");
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal($"{review.StatusText} · {review.LastRunText}", line.Message);
        AssertNotSaidTwice(line, view, review.LastRunText);

        // The scope of the review and what the check could not read are notes (the second one says nothing in the sample).
        Assert.Same(Application.Current.FindResource("Ad.Note.Info"), view.NotesBar.Style);
        Assert.Equal(review.ScopeNote, view.ScopeNoteText.Text);
        Assert.Same(Application.Current.FindResource("Ad.Note.Attention"), view.ReadNotesBar.Style);
        Assert.False(view.ReadNotesBar.IsVisible);

        // The one button checks and cancels; there is no "Cancel" button of its own any more.
        Assert.Null(view.FindName("CancelButton"));
        Assert.Equal(ActionButtonState.Idle, view.CheckAgainButton.State);
        Assert.Same(review.RefreshCommand, view.CheckAgainButton.Command);
        Assert.Same(review.Status.CancelCommand, view.CheckAgainButton.CancelCommand);
        var findings = view.FindingsList.Items.Count;

        using (var held = new HeldRead(context.Directory.Session, IsReadOfThePki))
        {
            ControlHost.Press(view.CheckAgainButton);
            await UiWait.UntilAsync(() => held.IsReached && review.IsLoading && line.Kind == PageStatusKind.Working, "the check runs");
            Assert.StartsWith("Checking AD CS security – AD CS objects: ", line.Message, StringComparison.Ordinal);
            Assert.Equal("Stop", Assert.IsType<string>(action.Content));
            Assert.Equal(Visibility.Collapsed, close.Visibility);
            Assert.Equal(ActionButtonState.Running, view.CheckAgainButton.State);
            Assert.True(view.CheckAgainButton.IsEnabled);
            view.UpdateLayout();
            Assert.Equal(findingsTop, TopOf(view.FindingsList, view));

            var said = Record(review.Status);
            ControlHost.Press(view.CheckAgainButton);
            await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Attention && !review.IsLoading, "the check was stopped");
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.Contains("Stopping…", said);

            // Stopped: the findings of the check before stay on screen, and the line says so as the state of the tab.
            Assert.Equal(
                "Checking AD CS security was stopped: the tab shows the results of the earlier check, if there was one. Press “Check again” to check again.",
                line.Message);
            Assert.Equal("Check again", Assert.IsType<string>(action.Content));
            Assert.Same(review.RefreshCommand, action.Command);
            Assert.Equal(Visibility.Collapsed, close.Visibility);
            Assert.Equal(ActionButtonState.Idle, view.CheckAgainButton.State);
            Assert.Equal(findings, view.FindingsList.Items.Count);
            view.UpdateLayout();
            Assert.Equal(findingsTop + GrowthOf(line), TopOf(view.FindingsList, view), 0.5);
        }

        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !review.IsLoading && line.Kind == PageStatusKind.Idle && line.Message.Length > 0, "the check ran again");
        Assert.Equal($"{review.StatusText} · {review.LastRunText}", line.Message);
        Assert.Null(review.Status.Lasting);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheEndOfTheConnection_IsAnErrorThatLasts_InTheOverview_AndTheDocumentCannotBeReadAgain() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync("en", Configure(new FakeDomainControllerTlsProbe()));
        var main = await OpenMainAsync(context);
        var document = Assert.IsType<PkiOverviewDocumentViewModel>(PkiSecurityUiSupport.Open(main.Shell, PkiDocumentKind.Overview));
        await UiWait.UntilAsync(() => !document.IsLoading && document.Snapshot is not null, "the PKI overview");
        var view = await ViewOfAsync<PkiOverviewView>(main);
        var line = view.PageStatus;
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");

        await context.Services.GetRequiredService<ISessionManager>().DisconnectAsync();
        await UiWait.UntilAsync(() => !document.HasSession && line.Kind == PageStatusKind.Error, "the end of the connection in the line");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        Assert.Equal("Error", line.Title);
        Assert.Equal(context.Localizer()[L.Errors.NotConnected], line.Message);
        Assert.Equal(AppIcons.StatusError, line.IconKey);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.Equal(ActionButtonState.Idle, view.RefreshButton.State);
        AssertNoOldFeedback(view);
        context.AssertHealthy();
    });
}

/// <summary>An HTTP check that is on its way until the test lets it go (it ends with its token, like the real one does).</summary>
internal sealed class GatedHealthProbe(FakePkiHealthProbe inner) : IPkiHealthProbe
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _reached;

    /// <summary>The check is being held.</summary>
    public bool IsReached => Volatile.Read(ref _reached) == 1;

    /// <summary>Lets the checks go on; later ones are not held either.</summary>
    public void Release() => _release.TrySetResult();

    public async Task<IReadOnlyList<PkiHttpCheckResult>> CheckAsync(IReadOnlyList<PkiUrl> urls, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(2);
        Volatile.Write(ref _reached, 1);
        await _release.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        return await inner.CheckAsync(urls, progress, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>A probe of the domain controllers that is on its way until the test lets it go (it ends with its token).</summary>
internal sealed class GatedTlsProbe(FakeDomainControllerTlsProbe inner) : IDomainControllerTlsProbe
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _reached;

    /// <summary>The probe is being held.</summary>
    public bool IsReached => Volatile.Read(ref _reached) == 1;

    /// <summary>Lets the probes go on; later ones are not held either.</summary>
    public void Release() => _release.TrySetResult();

    public async Task<TlsProbeResult> ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _reached, 1);
        await _release.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        return await inner.ProbeAsync(host, port, timeout, cancellationToken).ConfigureAwait(false);
    }
}
