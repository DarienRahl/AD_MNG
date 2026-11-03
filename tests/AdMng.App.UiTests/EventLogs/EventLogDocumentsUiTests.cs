using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AdMng.App.EventLogs;
using AdMng.App.GroupPolicy;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Computers.Remote;
using AdMng.Core.EventLogs.Analysis;
using AdMng.Presentation.Computers;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.EventLogs;
using AdMng.Presentation.Localization;
using AdMng.Presentation.PropertySheets;
using AdMng.Testing;
using AdMng.Testing.EventLogs;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests.EventLogs;

/// <summary>
/// Dzienniki zdarzeń (track E) in the main window, over the in-memory reader of two DCs: the reader with the DCs as
/// sources, its virtualized timeline, the details with the SID resolved and Ctrl+F; "Dziennik zdarzeń tego komputera" from
/// the Stan page; "Skąd blokada konta" from the Konto tab with its summary, sources, DCs, audit evidence and the K1 pivot;
/// "Nieudane logowania" with chips, findings and the drill-down. Binding-error free.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class EventLogDocumentsUiTests
{
    [Fact]
    public Task Reader_ReadsBothDcs_ShowsTheDetails_AndCtrlFReachesTheSearch() => WpfTestHost.RunAsync(async () =>
    {
        var connector = EventLogUiSupport.CreateConnector();
        await using var context = await UiTestContext.CreateAsync(language: "pl", EventLogUiSupport.Use(connector));
        await EventLogUiSupport.AddDomainControllersAsync(context);
        EventLogUiSupport.LockOutJan(context, connector);
        await context.ConnectAsync();
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        var shell = main.Shell;
        await shell.InitializeAsync();

        // Narzędzia → Dzienniki zdarzeń…: the DCs as sources (PDC emulator first), the Security log of the last 24 hours.
        Assert.True(shell.IsEventLogsAvailable);
        Assert.True(shell.OpenEventLogsCommand.CanExecute(null));
        shell.OpenEventLogsCommand.Execute(null);
        var reader = Assert.IsType<EventLogReaderDocumentViewModel>(shell.SelectedTab);
        await UiWait.UntilAsync(() => reader.Sources.Count == 2, "the domain controllers as sources");
        Assert.Equal([EventLogUiSupport.Dc01, EventLogUiSupport.Dc02], reader.Sources.Select(s => s.Host));
        await reader.RunCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => !reader.IsLoading && reader.Rows.Count == 7, "the records of both DCs");
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);

        var view = Assert.Single(UiWait.Descendants<EventLogReaderView>(main));
        Assert.Equal("Dzienniki zdarzeń", view.TitleText.Text);
        Assert.Equal(TextTrimming.CharacterEllipsis, view.TitleText.TextTrimming);
        Assert.False(view.NotSupportedBar.IsVisible);
        Assert.Equal(2, view.SourcesList.Items.Count);
        Assert.Equal(7, view.RecordsList.Items.Count);
        Assert.True(VirtualizingPanel.GetIsVirtualizing(view.RecordsList));
        Assert.Equal("Zdarzeń: 7 z 2 źródeł", view.StatusLine.Text);
        Assert.Equal(2, view.HostsList.Items.Count);
        Assert.True(view.DetailsEmptyText.IsVisible);
        Assert.Equal(2, UiWait.Descendants<PaneSplitter>(view).Count());

        // The lockout record: its fields with the account's SID resolved to the object.
        var lockout = reader.Rows.Single(r => r.Record.EventId == 4740);
        view.RecordsList.SelectedItem = lockout;
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.Same(lockout, reader.SelectedRow);
        Assert.True(view.DetailsScroll.IsVisible);
        Assert.Equal(reader.Fields.Count, view.FieldsList.Items.Count);
        await UiWait.UntilAsync(() => reader.Fields.Any(f => f.Name == "TargetSid" && f.ObjectDn is not null), "the account behind the SID");
        Assert.Equal(context.Company.JanKowalski, reader.Fields.Single(f => f.Name == "TargetSid").ObjectDn);
        Assert.True(view.ShowMessageButton.IsEnabled);
        await reader.ShowMessageCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.True(view.MessageBox.IsVisible);

        // Ctrl+F reaches the document's search box; the search narrows the timeline.
        await shell.FindCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.Same(view.SearchBox, FocusManager.GetFocusedElement(FocusManager.GetFocusScope(view.SearchBox)));
        view.SearchBox.Text = "zofia";
        await UiWait.UntilAsync(() => reader.Rows.Count == 1, "the search (applied after its delay)");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Single(view.RecordsList.Items);

        // The raw XPath mode validates live.
        reader.SearchText = string.Empty;
        reader.IsRawMode = true;
        reader.RawXPath = "*[System[(EventID=4740)]";
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.True(view.RawXPathErrorText.IsVisible);
        reader.RawXPath = "*[System[(EventID=4740)]]";
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.False(view.RawXPathErrorText.IsVisible);
        Assert.True(view.RawXPathValidText.IsVisible);
        context.AssertHealthy();
    });

    [Fact]
    public Task ComputerStatusPage_OpensTheReaderOfThatComputer() => WpfTestHost.RunAsync(async () =>
    {
        var connector = EventLogUiSupport.CreateConnector();
        connector.AddHost("waw-pc001.contoso.test", EventLogSamples.SecurityChannel).Add(EventLogSamples.LogonFailed(
            "WAW-PC001.contoso.test", 77, DateTimeOffset.UtcNow.AddMinutes(-20), EventLogSamples.JanKowalski, null, EventLogSamples.WawPc001,
            logonType: 2));
        await using var context = await UiTestContext.CreateAsync(language: "pl", EventLogUiSupport.Use(connector));
        await EventLogUiSupport.AddDomainControllersAsync(context);
        await context.ConnectAsync();
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        var shell = main.Shell;
        await shell.InitializeAsync();

        var factory = context.Services.GetRequiredService<IDialogViewModelFactory>();
        var window = (PropertiesWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(factory.CreatePropertySheet(context.Company.Computer1));
        UiTestSupport.ShowOffScreen(window);
        await UiWait.InitializedAsync(window.Initialization);
        var sheet = window.ViewModel;
        var status = Assert.IsType<ComputerStatusPageViewModel>(Assert.Single(sheet.Pages, p => p.Key == L.Computers.StatusPage));
        sheet.SelectedPage = status;
        await UiWait.UntilAsync(() => status.Details.Count > 0, "the facts of the Stan page");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        var button = UiWait.Descendants<Button>(window).Single(b => b.Name == "ComputerEventLogButton");
        Assert.True(button.IsVisible);
        Assert.True(button.IsEnabled);
        Assert.Equal("Dziennik zdarzeń tego komputera", button.Content);
        button.Command.Execute(button.CommandParameter);
        await UiWait.UntilAsync(() => shell.SelectedTab is EventLogReaderDocumentViewModel r && r.Sources.Any(s => s.Host == "waw-pc001.contoso.test"), "the reader of WAW-PC001");
        var reader = (EventLogReaderDocumentViewModel)shell.SelectedTab!;
        Assert.Equal(["waw-pc001.contoso.test"], reader.Sources.Where(s => s.IsSelected).Select(s => s.Host));

        await reader.RunCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => !reader.IsLoading && reader.Rows.Count == 1, "the record of WAW-PC001");
        Assert.Equal("waw-pc001.contoso.test", Assert.Single(connector.OpenedHosts));
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        var view = Assert.Single(UiWait.Descendants<EventLogReaderView>(main));
        Assert.Single(view.RecordsList.Items);
        Assert.Equal(4625, reader.Rows[0].EventId);
        context.AssertHealthy();
    });

    [Fact]
    public Task Lockout_FromTheAccountTab_ShowsTheReport_AndChecksTheSourceComputer() => WpfTestHost.RunAsync(async () =>
    {
        var connector = EventLogUiSupport.CreateConnector();
        await using var context = await UiTestContext.CreateAsync(language: "pl", EventLogUiSupport.Use(connector));
        await EventLogUiSupport.AddDomainControllersAsync(context);
        EventLogUiSupport.LockOutJan(context, connector);
        var remote = Assert.IsType<FakeRemoteComputerClient>(context.Host.Services.GetRequiredService<IRemoteComputerClient>());
        var computer = new FakeRemoteComputer();
        computer.Users.Add(new LoggedOnUser("CONTOSO\\jan.kowalski", 2, DateTimeOffset.UtcNow.AddDays(-3), IsConsole: true));
        computer.ScheduledTasks.Add(new RemoteScheduledTask("\\Firma\\", "Synchronizacja dysku Z", "jan.kowalski@contoso.test", RemoteTaskLogonType.Password, RemoteTaskState.Ready, true));
        remote.Computers["waw-pc001.contoso.test"] = computer;
        await context.ConnectAsync();
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        var shell = main.Shell;
        await shell.InitializeAsync();

        // Konto tab of Jan Kowalski: "Skąd blokada konta…" opens the investigation in the main window.
        var factory = context.Services.GetRequiredService<IDialogViewModelFactory>();
        var window = (PropertiesWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(factory.CreatePropertySheet(context.Company.JanKowalski));
        UiTestSupport.ShowOffScreen(window);
        await UiWait.InitializedAsync(window.Initialization);
        var sheet = window.ViewModel;
        sheet.SelectedPage = sheet.Pages.Single(p => p.Key == L.Pages.Account);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        var account = Assert.Single(((FieldsPageViewModel)sheet.SelectedPage).Fields.OfType<AccountStatusFieldViewModel>());
        Assert.True(account.CanFindLockoutSource);
        var button = UiWait.Descendants<Button>(window).Single(b => b.Name == "FindLockoutSourceButton");
        Assert.True(button.IsVisible);
        Assert.Equal("Skąd blokada konta…", button.Content);
        await UiWait.UntilAsync(() => button.IsEnabled, "the account state");
        button.Command.Execute(button.CommandParameter);
        await UiWait.UntilAsync(() => shell.SelectedTab is LockoutDocumentViewModel { IsLoading: false, HasReport: true }, "the lockout report");
        var document = (LockoutDocumentViewModel)shell.SelectedTab!;
        Assert.Null(document.Error);
        window.Close();
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);

        var view = Assert.Single(UiWait.Descendants<LockoutView>(main));
        Assert.Equal("Skąd blokada konta – Jan Kowalski", view.TitleText.Text);
        Assert.True(view.SummaryCard.IsVisible);
        Assert.True(document.IsLockedNow);
        Assert.StartsWith("Konto jest teraz zablokowane na: ", view.LockStateText.Text, StringComparison.Ordinal);
        Assert.Contains("DC01", view.LockStateText.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Próg blokady: 5 złych haseł w ciągu 30 min; blokada na 30 min (zasady domeny).", view.PolicyText.Text);
        Assert.True(view.UnlockButton.IsEnabled);
        Assert.True(view.NotesExpander.IsVisible);
        Assert.Equal(document.Notes.Count, view.NotesList.Items.Count);
        Assert.Equal(document.Sources.Count, view.SourcesList.Items.Count);
        Assert.Equal(document.Sources[0].Value, view.SourceValueText.Text);
        Assert.Contains(document.Sources, s => s.Value == EventLogSamples.WawPc001);
        Assert.Contains(document.Sources, s => s.Value == EventLogSamples.WawPc001Ip);
        Assert.Equal([EventLogUiSupport.Dc01, EventLogUiSupport.Dc02], document.Hosts.Select(h => h.Host));

        view.ReportTabs.SelectedItem = view.TimelineTab;
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.Equal(document.Timeline.Count, view.TimelineList.Items.Count);
        Assert.Contains(document.Timeline, t => t.IsLockout);
        view.ReportTabs.SelectedItem = view.DomainControllersTab;
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.Equal(2, view.DomainControllersList.Items.Count);
        Assert.Contains("emulator PDC", document.DomainControllerRows.Single(r => r.Host == EventLogUiSupport.Dc01).RolesText, StringComparison.Ordinal);
        view.ReportTabs.SelectedItem = view.AuditTab;
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.Equal(document.AuditRows.Count, view.AuditList.Items.Count);
        Assert.Contains(document.AuditRows, a => a.Host == EventLogUiSupport.Dc02 && a.State == AuditEvidenceState.NotSeenWhileCovered);

        // "Sprawdź komputer źródłowy" of the workstation: the K1 session, the scheduled task with the saved password.
        view.ReportTabs.SelectedItem = view.SourcesTab;
        view.SourcesList.SelectedItem = document.Sources.Single(s => s.Value == EventLogSamples.WawPc001);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.True(view.PivotButton.IsEnabled);
        await document.PivotCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.True(view.PivotPanel.IsVisible);
        Assert.Equal("Komputer źródłowy: WAW-PC001 (waw-pc001.contoso.test)", view.PivotHeadingText.Text);
        Assert.Equal(document.PivotLines!.Count, view.PivotLinesList.Items.Count);
        Assert.Contains("Zadania zaplanowane uruchamiane jako to konto: \\Firma\\Synchronizacja dysku Z (zapisane hasło)", document.PivotLines);
        Assert.Equal(context.Company.Computer1, document.PivotComputerDn);
        Assert.True(view.OpenRemoteButton.IsEnabled);

        // The same account again selects the open tab.
        await shell.OpenLockoutSourceCommand.ExecuteAsync(context.Company.JanKowalski);
        Assert.Same(document, shell.SelectedTab);
        Assert.Single(shell.EventLogDocuments.OfType<LockoutDocumentViewModel>());
        context.AssertHealthy();
    });

    [Fact]
    public Task FailedLogons_ShowsChipsFindingsAndTheDrillDown() => WpfTestHost.RunAsync(async () =>
    {
        var connector = EventLogUiSupport.CreateConnector();
        EventLogUiSupport.AddFailedLogons(connector);
        await using var context = await UiTestContext.CreateAsync(language: "pl", EventLogUiSupport.Use(connector));
        await EventLogUiSupport.AddDomainControllersAsync(context);
        await context.ConnectAsync();
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        var shell = main.Shell;
        await shell.InitializeAsync();

        Assert.True(shell.OpenFailedLogonsCommand.CanExecute(null));
        shell.OpenFailedLogonsCommand.Execute(null);
        var document = Assert.IsType<FailedLogonsDocumentViewModel>(shell.SelectedTab);
        document.EnumerationThreshold = 3;
        await document.RunCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => !document.IsLoading && document.HasReport, "the analysis");
        Assert.Null(document.Error);
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);

        var view = Assert.Single(UiWait.Descendants<FailedLogonsView>(main));
        Assert.Equal("Nieudane logowania", view.TitleText.Text);
        Assert.Equal(document.Findings.Count, view.FindingsList.Items.Count);
        Assert.True(VirtualizingPanel.GetIsVirtualizing(view.FindingsList));
        Assert.Equal(4, view.ChipsList.Items.Count);
        Assert.Contains(document.Findings, f => f.Kind == FailedLogonFindingKind.PasswordSpray);
        Assert.Contains(document.Findings, f => f.Kind == FailedLogonFindingKind.BruteForce);
        Assert.Contains(document.Findings, f => f.Kind == FailedLogonFindingKind.AccountEnumeration);

        // The first finding is selected; its details beside the list.
        Assert.Same(document.Findings[0], document.SelectedFinding);
        Assert.False(view.DetailsEmptyText.IsVisible);

        // The spraying finding: its accounts and records.
        var spray = document.Findings.Single(f => f.Kind == FailedLogonFindingKind.PasswordSpray);
        view.FindingsList.SelectedItem = spray;
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.Same(spray, document.SelectedFinding);
        Assert.True(view.DetailsScroll.IsVisible);
        Assert.Equal(spray.Title, view.DetailsTitle.Text);
        Assert.StartsWith("Rozpylanie haseł", view.DetailsTitle.Text, StringComparison.Ordinal);
        Assert.Equal(10, view.AccountsList.Items.Count);
        Assert.Equal(10, view.RecordsList.Items.Count);

        // A chip filters; Ctrl+F reaches the search.
        var chip = document.Chips.First(c => c.HasFindings);
        chip.IsSelected = true;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(chip.Count, view.FindingsList.Items.Count);
        Assert.True(view.ClearFiltersButton.IsEnabled);
        await shell.FindCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.Same(view.SearchBox, FocusManager.GetFocusedElement(FocusManager.GetFocusScope(view.SearchBox)));
        document.ClearFiltersCommand.Execute(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(document.Findings.Count, view.FindingsList.Items.Count);
        context.AssertHealthy();
    });

    [Fact]
    public Task NotSupported_ShowsTheBanner_AndDisablesTheRun() => WpfTestHost.RunAsync(async () =>
    {
        var connector = EventLogUiSupport.CreateConnector();
        connector.IsSupported = false;
        await using var context = await UiTestContext.CreateAsync(language: "pl", EventLogUiSupport.Use(connector));
        await context.ConnectAsync();
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        var shell = main.Shell;
        await shell.InitializeAsync();

        var document = shell.OpenEventLogDocument(new EventLogDocumentRequest(EventLogDocumentKind.FailedLogons));
        Assert.IsType<FailedLogonsDocumentViewModel>(document);
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        var view = Assert.Single(UiWait.Descendants<FailedLogonsView>(main));
        Assert.True(view.NotSupportedBar.IsVisible);
        Assert.False(string.IsNullOrWhiteSpace(view.NotSupportedText.Text));
        Assert.False(view.RunButton.IsEnabled);
        context.AssertHealthy();
    });
}
