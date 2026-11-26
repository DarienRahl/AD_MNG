using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Computers;
using AdMng.Core.Directory;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Tests.GroupPolicy;
using AdMng.Testing;
using CommunityToolkit.Mvvm.Input;
using static AdMng.Presentation.Tests.Computers.ComputerScenario;

namespace AdMng.Presentation.Tests.Computers;

/// <summary>
/// What the computer review says in its status line (track V, stage V2b-4; docs/dev/v-interface-renewal.md "V2b-4 as built"),
/// en-US unless a test says otherwise, over the computer inventory sample: the summary with the selection, the check as the
/// operation of the window with the computers read so far and Stop, a check that was stopped or failed, the read of the exact
/// last logon, the finding of the sites and a ping as operations of their own with how they ended, the end of the session
/// and the window without one - and nothing of a computer in a log copy. A slow domain controller is a call the test holds.
/// How the review finds computers is covered, unchanged, by <see cref="ComputerReviewViewModelTests"/>.
/// </summary>
public sealed class ComputerReviewStatusTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    /// <summary>9:00 in the short time format of en-US.</summary>
    private static string Nine => new DateTime(2026, 10, 27, 9, 0, 0).ToString("t", CultureInfo.GetCultureInfo("en-US"));

    private static List<string> LogsOf(PageStatusHolder status)
    {
        var logs = new List<string>();
        status.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PageStatusHolder.Current))
            {
                logs.Add(status.Current.ToString());
            }
        };
        return logs;
    }

    /// <summary>The query that streams the computers (the owners and the creators are read with other attributes).</summary>
    private static bool IsComputerQuery(FakeOperation operation) =>
        operation is { Kind: FakeOperationKind.Search, Search: { } search } && search.Attributes.Contains(Attr.OperatingSystem, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task TheSummary_IsTheLastCheckWithTheSelection_AndFollowsTheLanguage()
    {
        var scenario = await CreateAsync();
        using var review = scenario.Factory.CreateReview();
        await review.InitializeAsync();
        Assert.IsAssignableFrom<IHasPageStatus>(review);

        // Before the first check there is nothing to sum up.
        Assert.Equal((PageStatusKind.Idle, string.Empty), (review.Status.Current.Kind, review.Status.Current.Message));

        await review.RunCommand.ExecuteAsync(null);

        Assert.Equal(PageStatusKind.Idle, review.Status.Current.Kind);
        Assert.Equal($"As of {Nine} · 23 computers read · 23 computers in the reports", review.Status.Current.Message);
        Assert.Null(review.Status.Lasting);
        Assert.False(review.Status.IsWorking);

        // The selection joins it.
        review.SelectedTile = review.Tiles.Single(t => t.Report == ComputerReport.EndOfSupport);
        review.SelectedItems = review.Items.Take(3).ToList();
        Assert.Equal($"As of {Nine} · 23 computers read · 23 computers in the reports · 3 selected", review.Status.Current.Message);

        scenario.Harness.Localizer.SetCulture(CultureInfo.GetCultureInfo("pl-PL"));
        Assert.StartsWith("Stan na godz. ", review.Status.Current.Message, StringComparison.Ordinal);
        Assert.EndsWith(" · Zaznaczono: 3", review.Status.Current.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCheck_SaysTheScopeAndTheComputersReadSoFar_AndEndsAsTheSummary_WithoutAnyNameInALogCopy()
    {
        var scenario = await CreateAsync();
        using var review = scenario.Factory.CreateReview();
        await review.InitializeAsync();
        var seen = new StatusRecorder(review.Status);
        var logs = LogsOf(review.Status);

        await review.RunCommand.ExecuteAsync(null);

        var plain = seen.Plain;
        Assert.Equal((PageStatusKind.Working, "Checking the computers in the scope “contoso.test”…"), plain[0]);
        Assert.Equal(PageStatusKind.Working, plain[1].Kind);
        Assert.Matches(@"^Checking the computers in the scope “contoso\.test”: read: 23, with findings: \d+…$", plain[1].Message);
        Assert.Equal((PageStatusKind.Idle, review.StatusText), plain[^1]);
        Assert.Equal(review.StatusText, review.Status.Current.Message);
        Assert.Contains(logs, log => log.StartsWith("Working: ComputerReview.Status.Checking (23, ", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, log => log.Contains("contoso", StringComparison.OrdinalIgnoreCase) || log.Contains("PC10", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TheScope_IsNamedAsTheOperatorChoseIt()
    {
        var scenario = await CreateAsync();
        using var review = scenario.Factory.CreateReview();
        await review.InitializeAsync();
        review.ScopeKind = review.ScopeKinds.Single(k => k.Value == AccountReviewScopeKind.OrganizationalUnit);
        review.ScopeDn = scenario.Company.ComputersOu;
        var seen = new StatusRecorder(review.Status);

        await review.RunCommand.ExecuteAsync(null);

        Assert.Equal(PageStatusKind.Working, seen.Plain[0].Kind);
        Assert.Equal($"Checking the computers in the scope “{AddressResolver.ToDisplayPath(scenario.Company.ComputersOu)}”…", seen.Plain[0].Message);
    }

    [Fact]
    public async Task Stop_ReturnsControlAtOnce_TheStoppedCheckIsAnAttentionWithCheck_AndRunsAgain()
    {
        var scenario = await CreateAsync();
        using var review = scenario.Factory.CreateReview();
        await review.InitializeAsync();
        using var held = new HeldDirectoryCall(scenario.Harness.Session, IsComputerQuery);
        var seen = new StatusRecorder(review.Status);

        var run = review.RunCommand.ExecuteAsync(null);
        await held.Reached.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Working, with Stop; "Sprawdź" is its Cancel (the flag of the button), and nothing else runs meanwhile.
        var working = review.Status.Current;
        Assert.Equal((PageStatusKind.Working, "Checking the computers in the scope “contoso.test”…"), (working.Kind, working.Message));
        var stop = Assert.IsType<PageStatusAction>(working.Action);
        Assert.Equal("Stop", stop.Label);
        Assert.Same(review.Status.CancelCommand, stop.Command);
        Assert.True(review.IsRunning);
        Assert.False(review.RunCommand.CanExecute(null));
        Assert.False(review.ExportCsvCommand.CanExecute(null));

        // "Stopping…" at once - the query that is on its way to the domain controller has not returned.
        stop.Command.Execute(null);
        Assert.Equal((PageStatusKind.Working, "Stopping…"), (review.Status.Current.Kind, review.Status.Current.Message));
        Assert.True(review.Status.IsCancelling);
        held.Release();
        await run.WaitAsync(Limit, TestContext.Current.CancellationToken);

        var stopped = review.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Equal("Checking the computers was stopped: the results are incomplete (read: 0). Press “Check” to check again.", stopped.Message);
        Assert.Equal("ComputerReview.Status.Stopped (0)", stopped.Log.ToString());
        Assert.True(stopped.CanClose);
        Assert.Equal("Check", stopped.Action?.Label);
        Assert.Same(review.RunCommand, stopped.Action?.Command);
        Assert.False(review.IsRunning);
        Assert.Null(review.Error);
        Assert.Contains((PageStatusKind.Working, "Stopping…"), seen.Plain);

        // Closing it shows the summary of the stopped check; the action checks again.
        review.Status.CloseCommand.Execute(null);
        Assert.StartsWith("Stopped at ", review.Status.Current.Message, StringComparison.Ordinal);
        scenario.Harness.Session.BeforeOperation = null;
        await review.RunCommand.ExecuteAsync(null);
        Assert.Equal($"As of {Nine} · 23 computers read · 23 computers in the reports", review.Status.Current.Message);
    }

    [Fact]
    public async Task ACheckThatFails_IsTheStateOfTheWindow_WithRetry_UntilTheNextCheck()
    {
        var scenario = await CreateAsync();
        var session = scenario.Harness.Session;
        using var review = scenario.Factory.CreateReview();
        await review.InitializeAsync();
        var fail = true;
        session.BeforeOperation = operation =>
        {
            if (fail && IsComputerQuery(operation))
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "dc01.contoso.test does not answer");
            }
        };
        var logs = LogsOf(review.Status);

        await review.RunCommand.ExecuteAsync(null);

        // The list shows nothing and the line says why - for as long as that is so; it cannot be closed.
        var failed = review.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.Same(review.Status.Lasting, failed);
        Assert.NotNull(review.Error);
        Assert.Equal($"Could not check the computers: {review.Error} Press Retry to try again. (ServerUnavailable)", failed.Message);
        Assert.Equal("ComputerReview.Status.Failed [ServerUnavailable]", failed.Log.ToString());
        Assert.False(failed.CanClose);
        Assert.False(review.IsRunning);
        Assert.Equal("Retry", failed.Action?.Label);
        Assert.Same(review.RunCommand, failed.Action?.Command);
        Assert.DoesNotContain(logs, log => log.Contains("dc01", StringComparison.OrdinalIgnoreCase));

        // Retry checks again: the state is over.
        fail = false;
        session.BeforeOperation = null;
        await review.RunCommand.ExecuteAsync(null);
        Assert.Null(review.Status.Lasting);
        Assert.Null(review.Error);
        Assert.Equal(PageStatusKind.Idle, review.Status.Current.Kind);
    }

    [Fact]
    public async Task AScopeThatCannotBeResolved_IsSaidWithItsOwnReason_WithoutACodeAndWithoutRetry()
    {
        var scenario = await CreateAsync();
        using var review = scenario.Factory.CreateReview();
        await review.InitializeAsync();
        review.ScopeKind = review.ScopeKinds.Single(k => k.Value == AccountReviewScopeKind.SavedView);
        review.SavedView = new Option<string>("gone", "Gone");

        await review.RunCommand.ExecuteAsync(null);

        var state = review.Status.Current;
        Assert.Equal((PageStatusKind.Error, "The computers were not checked: The saved view no longer exists."), (state.Kind, state.Message));
        Assert.Equal("ComputerReview.Status.NotChecked", state.Log.ToString());
        Assert.False(state.CanClose);
        Assert.Null(state.Action);
        Assert.Equal("The saved view no longer exists.", review.Error);
    }

    [Fact]
    public async Task ExactLastLogon_SaysHowManyControllersAnswered_AndWhichCouldNotBeReadWithWhy_AsAnAttention()
    {
        var scenario = await CreateAsync();
        var harness = scenario.Harness;
        harness.ConnectToServer = (_, server, _) => server.StartsWith("dc02", StringComparison.OrdinalIgnoreCase)
            ? throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "dc02 does not answer")
            : Task.FromResult<IDirectorySession>(new FakeDomainControllerSession(harness.Session, server));
        using var review = await scenario.RunAsync();
        review.SelectedTile = review.Tiles.Single(t => t.Report == ComputerReport.Inactive);
        var rows = review.Items.Count;
        var seen = new StatusRecorder(review.Status);
        var logs = LogsOf(review.Status);

        await review.ReadExactLastLogonCommand.ExecuteAsync(null);

        Assert.Equal(PageStatusKind.Working, seen.Plain[0].Kind);
        Assert.Equal($"Reading the exact last logon of the computers ({rows}) on all domain controllers…", seen.Plain[0].Message);
        var done = review.Status.Current;
        Assert.Equal(PageStatusKind.Attention, done.Kind);
        Assert.StartsWith("1 of 2 domain controllers answered; ", done.Message, StringComparison.Ordinal);
        Assert.EndsWith(" Could not read from: dc02.contoso.test (The domain controller is not available).", done.Message, StringComparison.Ordinal);
        Assert.Equal("ComputerReview.ExactDone (1, 2, 0)", done.Log.ToString());
        Assert.True(done.CanClose);
        Assert.DoesNotContain(logs, log => log.Contains("dc02", StringComparison.OrdinalIgnoreCase));
        Assert.False(review.IsReadingExact);
        Assert.False(review.IsReading);
        Assert.DoesNotContain(harness.Notifications.Items, n => n.Message.Contains("domain controllers answered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExactLastLogon_Stop_ReturnsControlAtOnce_AndTheListIsAsItWas()
    {
        var scenario = await CreateAsync();
        var harness = scenario.Harness;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        harness.ConnectToServer = (_, server, _) =>
        {
            entered.Set();
            release.Wait(Limit);
            return Task.FromResult<IDirectorySession>(new FakeDomainControllerSession(harness.Session, server));
        };
        using var review = await scenario.RunAsync();
        review.SelectedTile = review.Tiles.Single(t => t.Report == ComputerReport.Inactive);
        var names = review.Items.Select(r => r.Name).ToList();

        var reading = review.ReadExactLastLogonCommand.ExecuteAsync(null);
        await Task.Run(() => entered.Wait(Limit), TestContext.Current.CancellationToken);
        Assert.True(review.IsReadingExact);
        Assert.False(review.IsResolvingSites);

        // One operation at a time: a ping would take the line from the read.
        review.SelectedItem = review.Items[0];
        Assert.False(review.PingCommand.CanExecute(null));
        var stop = Assert.IsType<PageStatusAction>(review.Status.Current.Action);
        stop.Command.Execute(null);
        Assert.Equal("Stopping…", review.Status.Current.Message);
        release.Set();
        await reading.WaitAsync(Limit, TestContext.Current.CancellationToken);

        var stopped = review.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Equal("Reading the exact last logon was stopped: the list was not changed. Press “Exact last logon (all DCs)” to read it again.", stopped.Message);
        Assert.Same(review.ReadExactLastLogonCommand, stopped.Action?.Command);
        Assert.Equal(names, review.Items.Select(r => r.Name).ToList());
        Assert.False(review.IsReadingExact);
    }

    [Fact]
    public async Task ResolveSites_TheStoppedOneChangesNothing_AndTheOtherSaysHowFarItIs()
    {
        var scenario = await CreateAsync();
        scenario.Resolver.Add("krk-pc101.contoso.test", "10.20.5.17");
        using var review = await scenario.RunAsync();
        review.SelectedTile = review.Tiles.Single(t => t.Report == ComputerReport.EndOfSupport);
        var rows = review.Items.Count;
        var before = review.Columns.Count;

        // Held on its way to the domain controller (the sites and subnets of the forest): stopped, nothing is applied.
        using (var held = new HeldDirectoryCall(scenario.Harness.Session))
        {
            var stopping = review.ResolveSitesCommand.ExecuteAsync(null);
            await held.Reached.WaitAsync(Limit, TestContext.Current.CancellationToken);
            Assert.True(review.IsResolvingSites);
            Assert.False(review.IsReadingExact);
            Assert.Equal((PageStatusKind.Working, $"Finding the sites of the computers ({rows})…"), (review.Status.Current.Kind, review.Status.Current.Message));
            review.Status.CancelCommand.Execute(null);
            Assert.Equal("Stopping…", review.Status.Current.Message);
            held.Release();
            await stopping.WaitAsync(Limit, TestContext.Current.CancellationToken);
        }

        Assert.Equal(PageStatusKind.Attention, review.Status.Current.Kind);
        Assert.Equal("Finding the sites was stopped: the list was not changed. Press “Determine site” to find them again.", review.Status.Current.Message);
        Assert.Same(review.ResolveSitesCommand, review.Status.Current.Action?.Command);
        Assert.Equal(before, review.Columns.Count);
        Assert.False(review.HasSites);
        Assert.False(review.IsResolvingSites);

        // The action finds them: how far it is (the part done is the ring), then how many have a site.
        var seen = new StatusRecorder(review.Status);
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)review.Status.Current.Action!.Command).ExecuteAsync(null);

        Assert.Equal((PageStatusKind.Working, $"Finding the sites of the computers ({rows})…"), seen.Plain[0]);
        Assert.Contains(seen.Plain, s => s.Message == $"Finding the sites of the computers: {rows} of {rows}…");
        Assert.Contains(seen.Seen, s => s.Kind == PageStatusKind.Working && s.Fraction == 1d);
        Assert.Equal(PageStatusKind.Attention, review.Status.Current.Kind);
        Assert.StartsWith("Sites of the computers determined: ", review.Status.Current.Message, StringComparison.Ordinal);
        Assert.EndsWith($" of {rows}. Subnets with an invalid name were skipped: 10.99.0.0/33", review.Status.Current.Message, StringComparison.Ordinal);
        Assert.True(review.HasSites);
        Assert.False(review.IsResolvingSites);
    }

    [Fact]
    public async Task ExactLastLogon_ThatFailsAsAWhole_IsAnErrorWithRetry_AndTheListIsAsItWas()
    {
        var scenario = await CreateAsync();
        var harness = scenario.Harness;
        harness.ConnectToServer = (_, server, _) => Task.FromResult<IDirectorySession>(new FakeDomainControllerSession(harness.Session, server));
        using var review = await scenario.RunAsync();
        review.SelectedTile = review.Tiles.Single(t => t.Report == ComputerReport.Inactive);
        var names = review.Items.Select(r => r.Name).ToList();
        var logs = LogsOf(review.Status);

        // The list of the domain controllers fails with an error the reader does not sort out per controller (it says a
        // controller that cannot be read with its reason, but this is no controller's answer): the read fails as a whole.
        harness.Session.BeforeOperation = operation =>
        {
            if (operation is { Kind: FakeOperationKind.Search, Search.BaseDn: { } baseDn } && baseDn.StartsWith("CN=Sites,", StringComparison.OrdinalIgnoreCase))
            {
                throw new TimeoutException("the configuration partition did not answer");
            }
        };

        await review.ReadExactLastLogonCommand.ExecuteAsync(null);

        var failed = review.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.Equal("Could not read the exact last logon: The operation timed out. Press Retry to try again. (TimeoutException)", failed.Message);
        Assert.Equal("ComputerReview.Status.ExactFailed [TimeoutException]", failed.Log.ToString());
        Assert.True(failed.CanClose);
        Assert.Equal("Retry", failed.Action?.Label);
        Assert.Same(review.ReadExactLastLogonCommand, failed.Action?.Command);
        Assert.Equal(names, review.Items.Select(r => r.Name).ToList());
        Assert.False(review.IsReadingExact);
        Assert.False(review.IsReading);
        Assert.DoesNotContain(logs, log => log.Contains("configuration", StringComparison.OrdinalIgnoreCase));

        // Retry reads again: every controller answers now.
        harness.Session.BeforeOperation = null;
        await ((IAsyncRelayCommand)failed.Action!.Command).ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Done, review.Status.Current.Kind);
        Assert.Equal("ComputerReview.ExactDone (2, 2, 0)", review.Status.Current.Log.ToString());
    }

    [Fact]
    public async Task ResolveSites_ThatCannotReadTheSites_IsAnErrorWithRetry_AndNothingIsApplied()
    {
        var scenario = await CreateAsync();
        scenario.Resolver.Add("krk-pc101.contoso.test", "10.20.5.17");
        using var review = await scenario.RunAsync();
        review.SelectedTile = review.Tiles.Single(t => t.Report == ComputerReport.EndOfSupport);
        var columns = review.Columns.Count;
        var logs = LogsOf(review.Status);

        // The sites and subnets of the forest cannot be read: no computer is looked at, no site column appears.
        scenario.Harness.Session.FailNext(FakeOperationKind.Search, null, DirectoryErrorKind.AccessDenied);
        await review.ResolveSitesCommand.ExecuteAsync(null);

        var failed = review.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.Equal("Could not find the sites of the computers: Access is denied. Press Retry to try again. (LDAP 50)", failed.Message);
        Assert.Equal("ComputerReview.Status.SitesFailed [LDAP 50]", failed.Log.ToString());
        Assert.True(failed.CanClose);
        Assert.Equal("Retry", failed.Action?.Label);
        Assert.Same(review.ResolveSitesCommand, failed.Action?.Command);
        Assert.False(review.HasSites);
        Assert.Equal(columns, review.Columns.Count);
        Assert.Empty(scenario.Resolver.Lookups);
        Assert.False(review.IsResolvingSites);
        Assert.DoesNotContain(logs, log => log.Contains("krk", StringComparison.OrdinalIgnoreCase));

        // Retry finds them.
        await ((IAsyncRelayCommand)failed.Action!.Command).ExecuteAsync(null);
        Assert.True(review.HasSites);
        Assert.NotEqual(PageStatusKind.Error, review.Status.Current.Kind);
    }

    [Fact]
    public async Task Ping_IsAnOperationWithStop_AndItsResultIsSaidInTheLine_NotInAToast()
    {
        var scenario = await CreateAsync();
        scenario.Ping.Answering["srv-app02.contoso.test"] = TimeSpan.FromMilliseconds(3);
        using var review = await scenario.RunAsync();
        review.SelectedItem = Row(review, ComputerReport.ServersWithoutOwner, "SRV-APP02");
        var seen = new StatusRecorder(review.Status);

        await review.PingCommand.ExecuteAsync(null);

        Assert.Equal(
            [
                (PageStatusKind.Working, "Pinging SRV-APP02…"),
                (PageStatusKind.Done, "Ping srv-app02.contoso.test (10.10.1.15): 4 of 4 replies, average 3 ms."),
            ],
            seen.Plain);
        Assert.Equal("Computers.PingReplies (4, 4)", review.Status.Current.Log.ToString());
        Assert.Empty(scenario.Harness.Notifications.Items);

        // No reply: an attention, with the same words.
        review.SelectedItem = Row(review, ComputerReport.ServersWithoutOwner, "SRV-SQL03");
        await review.PingCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Attention, review.Status.Current.Kind);
        Assert.StartsWith("Ping srv-sql03.contoso.test: no reply", review.Status.Current.Message, StringComparison.Ordinal);

        // Stopped while the echoes are on their way (a computer that drops ICMP): the line says so, with Ping as the action.
        scenario.Ping.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = review.PingCommand.ExecuteAsync(null);
        await scenario.Ping.Held.Task.WaitAsync(Limit, TestContext.Current.CancellationToken);
        Assert.Equal((PageStatusKind.Working, "Pinging SRV-SQL03…"), (review.Status.Current.Kind, review.Status.Current.Message));
        review.Status.CancelCommand.Execute(null);
        await stopped.WaitAsync(Limit, TestContext.Current.CancellationToken);
        Assert.Equal(PageStatusKind.Attention, review.Status.Current.Kind);
        Assert.Equal("Pinging SRV-SQL03 was stopped. Press “Ping” to try again.", review.Status.Current.Message);
        Assert.Same(review.PingCommand, review.Status.Current.Action?.Command);
    }

    [Fact]
    public async Task Ping_OfAComputerWithoutADnsName_IsAnErrorInTheLine_NotADialog()
    {
        var scenario = await CreateAsync();
        using var review = await scenario.RunAsync();
        review.SelectedItem = Row(review, ComputerReport.NeverLoggedOn, "PREST-PC201");

        await review.PingCommand.ExecuteAsync(null);

        var failed = review.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.StartsWith("Could not ping PREST-PC201: PREST-PC201 has no DNS host name in Active Directory", failed.Message, StringComparison.Ordinal);
        Assert.True(failed.CanClose);
        Assert.Empty(scenario.Harness.Dialogs.Errors);
        Assert.Empty(scenario.Ping.Pinged);
    }

    [Fact]
    public async Task TheEndOfTheSession_IsTheStateOfTheWindow_AndCoversWhatRuns()
    {
        var scenario = await CreateAsync();
        var harness = scenario.Harness;
        using var review = scenario.Factory.CreateReview();
        await review.InitializeAsync();
        using var held = new HeldDirectoryCall(harness.Session, IsComputerQuery);
        var seen = new StatusRecorder(review.Status);

        var run = review.RunCommand.ExecuteAsync(null);
        await held.Reached.WaitAsync(Limit, TestContext.Current.CancellationToken);
        await harness.ReconnectAsync(new FakeDirectoryBuilder(timeProvider: harness.Time).Session);
        held.Release();
        await run.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // From "working" straight to the end of the session - no "Stopping…", no summary in between.
        Assert.True(review.IsSessionEnded);
        var ended = review.Status.Current;
        Assert.Equal(PageStatusKind.Error, ended.Kind);
        Assert.False(ended.CanClose);
        Assert.Same(review.Status.Lasting, ended);
        Assert.DoesNotContain(seen.Plain, s => s.Message == "Stopping…");
        Assert.Equal([PageStatusKind.Working, PageStatusKind.Error], seen.Plain.Select(s => s.Kind).Distinct().ToList());
        Assert.False(review.IsRunning);
        Assert.False(review.Status.IsWorking);
    }

    [Fact]
    public async Task AWindowOpenedWithoutASession_SaysNotConnected_FromTheFirstMoment()
    {
        var scenario = await CreateAsync();
        await scenario.Harness.Sessions.DisconnectAsync();
        using var review = scenario.Factory.CreateReview();
        await review.InitializeAsync();

        var state = review.Status.Current;
        Assert.Equal((PageStatusKind.Error, "Not connected to a domain."), (state.Kind, state.Message));
        Assert.False(state.CanClose);
        Assert.Equal("Errors.NotConnected", state.Log.Key);
        Assert.False(review.RunCommand.CanExecute(null));
    }

    [Fact]
    public async Task ClosingTheWindow_DuringACheck_EndsTheOperation()
    {
        var scenario = await CreateAsync();
        var review = scenario.Factory.CreateReview();
        await review.InitializeAsync();
        using var held = new HeldDirectoryCall(scenario.Harness.Session, IsComputerQuery);

        var run = review.RunCommand.ExecuteAsync(null);
        await held.Reached.WaitAsync(Limit, TestContext.Current.CancellationToken);
        review.Dispose();
        held.Release();
        await run.WaitAsync(Limit, TestContext.Current.CancellationToken);

        Assert.False(review.Status.IsWorking);
        Assert.Null(review.Status.Operation);
    }
}
