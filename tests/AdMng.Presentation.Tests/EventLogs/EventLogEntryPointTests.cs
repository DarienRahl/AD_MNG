using AdMng.Core.Csv;
using AdMng.Core.EventLogs;
using AdMng.Presentation.AccountReview;
using AdMng.Presentation.Computers;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.EventLogs;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.PropertySheets;
using AdMng.Presentation.Shell;
using AdMng.Presentation.Tests.AccountReview;
using AdMng.Testing;
using AdMng.Testing.EventLogs;

namespace AdMng.Presentation.Tests.EventLogs;

public class EventLogEntryPointTests
{
    private static ShellViewModel CreateShell(EventLogHarness harness)
    {
        var b2 = harness.B2;
        return new ShellViewModel(
            b2.Lists,
            new NavigationTreeViewModel(b2.Sessions, b2.Settings, b2.Localizer, b2.Dispatcher, b2.Errors, b2.Views, b2.Changes),
            b2.Activities,
            b2.Coordinator,
            b2.Views,
            b2.Dialogs,
            b2.Files,
            b2.Clipboard,
            b2.Notifications,
            new CsvCodec(),
            b2.Regional,
            eventLogs: harness.Services);
    }

    [Fact]
    public async Task Shell_RegistersThePaletteCommands_AndOpensEachDocumentOnce()
    {
        var harness = await EventLogHarness.CreateAsync();
        using var shell = CreateShell(harness);

        foreach (var id in new[] { "events.open", "events.failedLogons", "events.lockout", "events.computer" })
        {
            Assert.NotNull(shell.CommandPalette.Find(id));
        }

        Assert.True(shell.OpenEventLogsCommand.CanExecute(null));
        shell.OpenEventLogsCommand.Execute(null);
        shell.OpenEventLogsCommand.Execute(null);
        var reader = Assert.Single(shell.Documents.OfType<EventLogReaderDocumentViewModel>());
        Assert.Same(reader, shell.SelectedTab);

        shell.OpenFailedLogonsCommand.Execute(null);
        harness.Launcher.OpenFailedLogons();
        Assert.Single(shell.Documents.OfType<FailedLogonsDocumentViewModel>());
        Assert.IsType<FailedLogonsDocumentViewModel>(shell.SelectedTab);

        await shell.OpenComputerEventLogCommand.ExecuteAsync(harness.Company.Computer1);
        Assert.Same(reader, shell.SelectedTab);
        await EventLogHarness.WaitAsync(() => reader.Sources.Any(s => s is { Host: "waw-pc001.contoso.test", IsSelected: true }));
        Assert.Single(reader.Sources, s => s.IsSelected);
    }

    [Fact]
    public async Task Shell_LockoutSource_ForTheSelectedOrAPickedUser_OnePerAccount()
    {
        var harness = await EventLogHarness.CreateAsync();
        using var shell = CreateShell(harness);

        harness.B2.Dialogs.PickedObjects.Enqueue([harness.Company.JanKowalski]);
        await shell.OpenLockoutSourceCommand.ExecuteAsync(null);
        await shell.OpenLockoutSourceCommand.ExecuteAsync(harness.Company.JanKowalski);
        await shell.OpenLockoutSourceCommand.ExecuteAsync(harness.Company.LukaszWisniewski);

        var documents = shell.Documents.OfType<LockoutDocumentViewModel>().ToList();
        Assert.Equal([harness.Company.JanKowalski, harness.Company.LukaszWisniewski], documents.Select(d => d.UserDn));
        await EventLogHarness.WaitAsync(() => documents.All(d => !d.IsLoading));
        Assert.Equal("Skąd blokada konta – Jan Kowalski", documents[0].Title);
        Assert.True(documents[0].HasReport);
    }

    [Fact]
    public async Task Shell_ClosingADocument_DisposesIt()
    {
        var harness = await EventLogHarness.CreateAsync();
        using var shell = CreateShell(harness);
        shell.OpenFailedLogonsCommand.Execute(null);
        var document = Assert.IsType<FailedLogonsDocumentViewModel>(shell.SelectedTab);

        await shell.CloseTabCommand.ExecuteAsync(document);

        Assert.Empty(shell.Documents.OfType<FailedLogonsDocumentViewModel>());
        Assert.False(document.RunCommand.CanExecute(null) && document.Findings.Count > 0);
    }

    [Fact]
    public async Task AccountTab_OffersTheLockoutSource()
    {
        var harness = await EventLogHarness.CreateAsync();
        var b2 = harness.B2;
        var factory = new DialogViewModelFactory(b2.Services, b2.Engines, b2.Views, b2.AuditLog, new Core.Audit.AuditLogExporter(new CsvCodec()), b2.PowerShell, b2.Theme,
            eventLogs: harness.Launcher);
        var sheet = factory.CreatePropertySheet(harness.Company.JanKowalski);
        await sheet.InitializeAsync();
        var page = Assert.IsType<FieldsPageViewModel>(Assert.Single(sheet.Pages, p => p.Key == L.Pages.Account));
        var status = Assert.Single(page.Fields.OfType<AccountStatusFieldViewModel>());

        Assert.True(status.CanFindLockoutSource);
        status.FindLockoutSourceCommand.Execute(null);

        var request = Assert.Single(harness.Requests);
        Assert.Equal((EventLogDocumentKind.Lockout, harness.Company.JanKowalski), (request.Kind, request.UserDn));

        var plain = b2.Factory.CreatePropertySheet(harness.Company.JanKowalski);
        await plain.InitializeAsync();
        Assert.False(Assert.Single(Assert.IsType<FieldsPageViewModel>(Assert.Single(plain.Pages, p => p.Key == L.Pages.Account)).Fields.OfType<AccountStatusFieldViewModel>()).CanFindLockoutSource);
    }

    [Fact]
    public async Task ComputerStatusPage_OpensTheReaderForTheComputer()
    {
        var harness = await EventLogHarness.CreateAsync();
        var b2 = harness.B2;
        var sheet = new PropertySheetViewModel(b2.Services, b2.Planner, harness.Company.Computer1) { EventLogLauncher = harness.Launcher };
        await sheet.InitializeAsync();
        var status = Assert.IsType<ComputerStatusPageViewModel>(Assert.Single(sheet.Pages, p => p.Key == L.Computers.StatusPage));

        Assert.True(status.CanOpenEventLog);
        status.OpenEventLogCommand.Execute(null);

        var request = Assert.Single(harness.Requests);
        Assert.Equal((EventLogDocumentKind.Reader, harness.Company.Computer1), (request.Kind, request.ComputerDn));
    }

    [Fact]
    public async Task AccountReview_LockedRows_OfferTheLockoutSource()
    {
        var harness = await EventLogHarness.CreateAsync();
        harness.Session.SimulateLockout(harness.Company.JanKowalski);
        var b2 = harness.B2;
        var factory = new DialogViewModelFactory(b2.Services, b2.Engines, b2.Views, b2.AuditLog, new Core.Audit.AuditLogExporter(new CsvCodec()), b2.PowerShell, b2.Theme,
            eventLogs: harness.Launcher);
        var review = factory.CreateAccountReview();
        await review.InitializeAsync();
        await review.RunCommand.ExecuteAsync(null);

        var row = ReviewAssert.Row(review, AccountCheck.Locked, "Jan Kowalski");
        Assert.True(review.ShowLockoutSource);
        review.SelectedItem = row;
        Assert.True(review.FindLockoutSourceCommand.CanExecute(null));
        review.FindLockoutSourceCommand.Execute(null);

        Assert.Equal(harness.Company.JanKowalski, Assert.Single(harness.Requests).UserDn);
    }
}

public class EventLogHostRunnerTests
{
    private static readonly FakeDirectorySession Session = new FakeDirectoryBuilder().Session;

    private static EventLogQuery Security => new() { Channel = "Security" };

    [Fact]
    public async Task Runner_ReadsAtMostFourHostsAtOnce_AndReportsEveryHost()
    {
        var connector = new FakeEventLogConnector();
        var hosts = Enumerable.Range(1, 6).Select(i => $"dc{i:00}.contoso.test").ToList();
        foreach (var (host, index) in hosts.Select((h, i) => (h, i)))
        {
            connector.AddHost(host, "Security").Add(EventLogSamples.AccountLockedOut(host, index + 1, EventLogSamples.Morning.AddMinutes(index), "jan.kowalski", "WAW-PC001"));
        }

        var running = 0;
        var peak = 0;
        connector.BeforeRead = async (_, _, token) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            await Task.Delay(30, token);
            Interlocked.Decrement(ref running);
        };
        var reports = new List<EventLogHostProgress>();

        var result = await new EventLogHostRunner(connector).RunAsync(Session, [.. hosts.Select(h => new EventLogTarget(h, [Security]))],
            new SyncProgress<EventLogHostProgress>(p => { lock (reports) { reports.Add(p); } }), TestContext.Current.CancellationToken);

        Assert.InRange(peak, 1, EventLogLimits.MaxParallelHosts);
        Assert.Equal(6, result.Records.Count);
        Assert.Equal(hosts, result.Hosts.Select(h => h.Host));
        Assert.All(result.Hosts, h => Assert.Equal(EventLogHostState.Done, h.State));
        Assert.Equal(6, result.Coverage.Count);
        Assert.Contains(reports, r => r.State == EventLogHostState.Opening);
        Assert.Equal(0, connector.OpenReaders);
    }

    [Fact]
    public async Task Runner_KeepsTheNewestRecords_AndCountsTheDropped()
    {
        var connector = new FakeEventLogConnector().AddHost(EventLogSamples.Dc01, "Security");
        connector.Add(Enumerable.Range(0, 20).Select(i => EventLogSamples.AccountLockedOut(EventLogSamples.Dc01, i, EventLogSamples.Morning.AddMinutes(i), "jan.kowalski", null)));

        var result = await new EventLogHostRunner(connector) { MaxRecords = 5 }.RunAsync(Session, [new EventLogTarget(EventLogSamples.Dc01, [Security with { NewestFirst = false }])],
            null, TestContext.Current.CancellationToken);

        Assert.Equal([19L, 18, 17, 16, 15], result.Records.Select(r => r.RecordId));
        Assert.Equal(15, result.Dropped);
        Assert.Equal(20, result.Total);
    }

    [Fact]
    public async Task Runner_OneFailingHost_DoesNotStopTheOthers()
    {
        var connector = new FakeEventLogConnector().AddHost(EventLogSamples.Dc01, "Security").AddHost(EventLogSamples.Dc02, "Security");
        connector.Add(EventLogSamples.LockoutScenario());
        connector.FailHost(EventLogSamples.Dc02, EventLogAccessError.Unreachable, 11001);

        var result = await new EventLogHostRunner(connector).RunAsync(Session,
            [new EventLogTarget(EventLogSamples.Dc01, [Security]), new EventLogTarget(EventLogSamples.Dc02, [Security]), new EventLogTarget("dc09.contoso.test", [Security])],
            null, TestContext.Current.CancellationToken);

        Assert.Equal(EventLogHostState.Done, result.Hosts[0].State);
        Assert.Equal((EventLogHostState.Failed, EventLogAccessError.Unreachable, 11001), (result.Hosts[1].State, result.Hosts[1].Error, result.Hosts[1].NativeErrorCode));
        Assert.Equal(EventLogAccessError.Unreachable, result.Failures["dc09.contoso.test"]);
        Assert.Equal(4, result.Records.Count);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public async Task Runner_Cancelled_KeepsWhatWasRead()
    {
        var connector = new FakeEventLogConnector().AddHost(EventLogSamples.Dc01, "Security");
        connector.Add(EventLogSamples.LockoutScenario().Where(r => EventHostNames.AreEqual(r.Host, EventLogSamples.Dc01)));
        using var cancel = new CancellationTokenSource();
        connector.BeforeRead = (_, _, _) =>
        {
            cancel.Cancel();
            return Task.CompletedTask;
        };

        var result = await new EventLogHostRunner(connector).RunAsync(Session, [new EventLogTarget(EventLogSamples.Dc01, [Security])], null, cancel.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(EventLogHostState.Cancelled, result.Hosts[0].State);
        Assert.Equal(0, connector.OpenReaders);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
