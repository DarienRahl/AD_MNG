using System.ComponentModel;
using System.Globalization;
using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Presentation.AccountReview;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Tests.Connection;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.AccountReview;

/// <summary>
/// The status line of the account review (track V, stage V2b; docs/dev/v-interface-renewal.md "V2b-1 as built"): the
/// check - of either tab - is the operation of the window (what is checked, in which scope, how much so far, Stop);
/// how it ended is said by the tab it was for (its summary, a check that failed as the state of the tab with Retry, a
/// check that was stopped), and the tab shown is in front; the read of the exact last logon is an operation of the
/// "Account state" tab; the end of the session is the state of the window. What the checks find is covered, unchanged,
/// by <see cref="AccountReviewViewModelTests"/> and <see cref="PrivilegedAccessViewModelTests"/>.
/// </summary>
public sealed class AccountReviewStatusTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    /// <summary>The accounts the review query returns for <see cref="ReviewScenario"/> (its server filter narrows them to candidates).</summary>
    private const int AccountsRead = 14;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheLine_ShowsTheSummaryOfTheTabShown_WithItsSelection_InBothLanguages()
    {
        var scenario = await ReviewScenario.CreateAsync();
        using var review = scenario.Harness.Factory.CreateAccountReview();
        await review.InitializeAsync();
        Assert.IsAssignableFrom<IHasPageStatus>(review);
        Assert.IsAssignableFrom<IHasPageStatus>(review.Privileges);
        Assert.Same(review.AccountsStatus, review.Status.Front);

        // Before the first check there is nothing to sum up.
        Assert.Equal(PageStatusKind.Idle, review.Status.Current.Kind);
        Assert.Equal(string.Empty, review.Status.Current.Message);

        await review.RunCommand.ExecuteAsync(null);

        // After it: what the tab read and when - said by the tab, shown by the window.
        Assert.Equal(PageStatusKind.Idle, review.Status.Current.Kind);
        Assert.Same(review.AccountsStatus.Current, review.Status.Current);
        Assert.StartsWith("As of ", review.StatusText, StringComparison.Ordinal);
        Assert.Equal(review.StatusText, review.Status.Current.Message);
        Assert.False(review.Status.IsWorking);
        Assert.Null(review.Status.Lasting);
        Assert.Null(review.AccountsStatus.Lasting);

        // The selection joins the summary (the tab says it from two rows on).
        review.SelectedTile = review.Tiles.Single(t => t.Check == AccountCheck.NoMail);
        review.SelectedItems = review.Items.Take(2).ToList();
        Assert.Equal("2 selected", review.SelectionText);
        Assert.Equal($"{review.StatusText} · 2 selected", review.Status.Current.Message);

        // The other tab in front: its own summary - nothing before its first check, then what it read.
        review.SelectedTabIndex = AccountReviewViewModel.PrivilegesTab;
        Assert.Same(review.Privileges.Status, review.Status.Front);
        Assert.Equal(string.Empty, review.Status.Current.Message);
        await review.RunCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Idle, review.Status.Current.Kind);
        Assert.StartsWith("As of ", review.Privileges.StatusText, StringComparison.Ordinal);
        Assert.Equal(review.Privileges.StatusText, review.Status.Current.Message);

        // Both follow the language; back on the first tab its summary is there again.
        scenario.Harness.Localizer.SetCulture(Polish);
        Assert.StartsWith("Stan na godz. ", review.Status.Current.Message, StringComparison.Ordinal);
        Assert.Equal(review.Privileges.StatusText, review.Status.Current.Message);
        review.SelectedTabIndex = 0;
        Assert.StartsWith(review.StatusText, review.Status.Current.Message, StringComparison.Ordinal);
        Assert.StartsWith("Stan na godz. ", review.Status.Current.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en-US", "Checking the accounts in the scope “contoso.test”…", "Checking the accounts in the scope “contoso.test”: read: {0}, with findings: {1}…")]
    [InlineData("pl-PL", "Sprawdzanie kont w zakresie „contoso.test”…", "Sprawdzanie kont w zakresie „contoso.test”: odczytano: {0}, z uwagami: {1}…")]
    public async Task TheCheck_IsTheOperationOfTheWindow_AndSaysHowMuchSoFar(string culture, string working, string counted)
    {
        var scenario = await ReviewScenario.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var review = scenario.Harness.Factory.CreateAccountReview();
        await review.InitializeAsync();
        var seen = Record(review.Status);

        await review.RunCommand.ExecuteAsync(null);

        // Working from the first moment, one message with the numbers so far (14 accounts read, all of them with
        // findings), then - the result is the tab itself - the summary: nothing else is said.
        Assert.Equal((PageStatusKind.Working, working), seen[0]);
        Assert.Contains((PageStatusKind.Working, string.Format(CultureInfo.InvariantCulture, counted, AccountsRead, 14)), seen);
        Assert.All(seen.Take(seen.Count - 1), s => Assert.Equal(PageStatusKind.Working, s.Kind));
        Assert.Equal((PageStatusKind.Idle, review.StatusText), seen[^1]);
        Assert.Equal(review.StatusText, review.Status.Current.Message);
        Assert.False(review.Status.IsWorking);
        Assert.Null(review.Error);
    }

    [Fact]
    public async Task ACheckOfASavedScope_NamesThatScope()
    {
        var scenario = await ReviewScenario.CreateAsync();
        var harness = scenario.Harness;
        using var review = harness.Factory.CreateAccountReview();
        await review.InitializeAsync();
        review.ScopeKind = review.ScopeKinds.Single(k => k.Value == AccountReviewScopeKind.OrganizationalUnit);
        harness.Dialogs.PickedContainers.Enqueue(harness.Company.ItOu);
        await review.BrowseScopeCommand.ExecuteAsync(null);
        var seen = Record(review.Status);

        await review.RunCommand.ExecuteAsync(null);

        Assert.Equal((PageStatusKind.Working, "Checking the accounts in the scope “contoso.test/Firma/Pracownicy/IT”…"), seen[0]);
    }

    [Fact]
    public async Task Stop_SaysStoppingAtOnce_AndTheStoppedCheckIsAnAttentionOfItsTab_WithCheckAsItsAction()
    {
        var scenario = await ReviewScenario.CreateOnDomainControllerAsync();
        var harness = scenario.Harness;
        var hooked = new HookedSession(new FakeDomainControllerSession(harness.Session, "dc01.contoso.test")) { HoldAfter = 3 };
        await harness.ReconnectAsync(hooked);
        using var review = harness.Factory.CreateAccountReview();
        await review.InitializeAsync();

        var run = review.RunCommand.ExecuteAsync(null);
        await hooked.Held.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // The check runs: the window is working, with Stop; the tab is not.
        Assert.True(review.IsRunning);
        Assert.True(review.Status.IsWorking);
        Assert.False(review.AccountsStatus.IsWorking);
        Assert.Equal(PageStatusKind.Working, review.Status.Current.Kind);
        Assert.StartsWith("Checking the accounts in the scope “contoso.test”", review.Status.Current.Message, StringComparison.Ordinal);
        var stop = Assert.IsType<PageStatusAction>(review.Status.Current.Action);
        Assert.Equal("Stop", stop.Label);
        Assert.Same(review.Status.CancelCommand, stop.Command);

        // Pressed: "Stopping…" at once, and the check ends although the domain controller never answers.
        var seen = Record(review.Status);
        stop.Command.Execute(stop.Parameter);
        Assert.True(review.Status.IsCancelling);
        await run.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        // Stopped: what is there is incomplete, and "Check" runs it again - said by the tab, a result that can be closed.
        Assert.Equal((PageStatusKind.Working, "Stopping…"), seen[0]);
        var stopped = review.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Same(review.AccountsStatus.Current, stopped);
        Assert.Equal("Checking the accounts was stopped: the results are incomplete (read: 3). Press “Check” to check again.", stopped.Message);
        Assert.Equal("AccountReview.Status.Stopped (3)", stopped.Log.ToString());
        Assert.True(stopped.CanClose);
        Assert.False(review.IsRunning);
        Assert.False(review.Status.IsWorking);
        Assert.Null(review.Error);
        Assert.StartsWith("Stopped (as of ", review.StatusText, StringComparison.Ordinal);
        var again = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal("Check", again.Label);
        Assert.Same(review.RunCommand, again.Command);
        Assert.True(review.RunCommand.CanExecute(null));

        // Closed: the summary of the incomplete results.
        review.Status.CloseCommand.Execute(null);
        Assert.Equal(PageStatusKind.Idle, review.Status.Current.Kind);
        Assert.Equal(review.StatusText, review.Status.Current.Message);
    }

    [Fact]
    public async Task ACheckThatFails_IsTheStateOfItsTab_WithRetry_UntilTheNextCheck()
    {
        var scenario = await ReviewScenario.CreateAsync();
        var harness = scenario.Harness;
        using var review = await scenario.RunAsync();
        var fail = true;
        harness.Session.BeforeOperation = operation =>
        {
            if (fail && operation.Kind == FakeOperationKind.Search && operation.Search!.Filter.Contains("805306368", StringComparison.Ordinal))
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "dc01.contoso.test does not answer");
            }
        };

        await review.RunCommand.ExecuteAsync(null);

        // The tab shows no results and the line says why - for as long as that is so; it cannot be closed.
        var failed = review.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.Same(review.AccountsStatus.Lasting, failed);
        Assert.NotNull(review.Error);
        Assert.Equal($"Could not check the accounts: {review.Error} Press Retry to try again. (ServerUnavailable)", failed.Message);
        Assert.Equal("AccountReview.Status.Failed [ServerUnavailable]", failed.Log.ToString());
        Assert.False(failed.CanClose);
        Assert.False(review.IsRunning);
        var retry = Assert.IsType<PageStatusAction>(failed.Action);
        Assert.Equal("Retry", retry.Label);
        Assert.Same(review.RunCommand, retry.Command);

        // The other tab has nothing to do with it; back on this one the state is said again.
        review.SelectedTabIndex = AccountReviewViewModel.PrivilegesTab;
        Assert.Equal(PageStatusKind.Idle, review.Status.Current.Kind);
        review.SelectedTabIndex = 0;
        Assert.Same(failed, review.Status.Current);

        // Retry checks again: the state is over.
        fail = false;
        harness.Session.BeforeOperation = null;
        await review.RunCommand.ExecuteAsync(null);
        Assert.Null(review.AccountsStatus.Lasting);
        Assert.Null(review.Error);
        Assert.Equal(PageStatusKind.Idle, review.Status.Current.Kind);
        Assert.Equal(review.StatusText, review.Status.Current.Message);
    }

    [Fact]
    public async Task AScopeThatCannotBeResolved_IsSaidWithItsOwnReason_AndWithoutACode()
    {
        var scenario = await ReviewScenario.CreateAsync();
        var harness = scenario.Harness;
        using var review = harness.Factory.CreateAccountReview();
        await review.InitializeAsync();
        review.ScopeKind = review.ScopeKinds.Single(k => k.Value == AccountReviewScopeKind.GroupMembers);
        await harness.Session.DeleteAsync(scenario.AdminsIt, subtree: false, Ct);
        review.ScopeDn = scenario.AdminsIt;

        await review.RunCommand.ExecuteAsync(null);

        var failed = review.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.StartsWith("Could not check the accounts: The group contoso.test/Firma/Grupy/GG_Admini_IT was not found", failed.Message, StringComparison.Ordinal);
        Assert.EndsWith(" Press Retry to try again.", failed.Message, StringComparison.Ordinal);
        Assert.Equal("AccountReview.Status.Failed", failed.Log.ToString());
    }

    [Theory]
    [InlineData("en-US", "Checking the privileges of the accounts in the scope “contoso.test”…", "Checking the privileges of the accounts in the scope “contoso.test”: comparing with similar accounts…")]
    [InlineData("pl-PL", "Sprawdzanie uprawnień kont w zakresie „contoso.test”…", "Sprawdzanie uprawnień kont w zakresie „contoso.test”: porównywanie z podobnymi kontami…")]
    public async Task TheCheckOfThePrivilegesTab_IsTheOperationOfTheWindowAsWell(string culture, string working, string peers)
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        harness.Builder.AddPrivilegedAccessSample(harness.Company);
        using var review = harness.Factory.CreateAccountReview();
        await review.InitializeAsync();
        review.SelectedTabIndex = AccountReviewViewModel.PrivilegesTab;
        var seen = Record(review.Status);

        await review.RunCommand.ExecuteAsync(null);

        var tab = review.Privileges;
        Assert.Equal((PageStatusKind.Working, working), seen[0]);
        Assert.Contains(seen, s => s.Kind == PageStatusKind.Working && s.Message.Contains(": ", StringComparison.Ordinal)
            && s.Message.EndsWith($": {tab.AccountCount}…", StringComparison.Ordinal));
        Assert.Contains((PageStatusKind.Working, peers), seen);
        Assert.All(seen.Take(seen.Count - 1), s => Assert.Equal(PageStatusKind.Working, s.Kind));
        Assert.Equal((PageStatusKind.Idle, tab.StatusText), seen[^1]);
        Assert.Same(tab.Status.Current, review.Status.Current);
        Assert.False(review.Status.IsWorking);
        Assert.Null(tab.Status.Lasting);
        Assert.Null(tab.Error);
    }

    [Fact]
    public async Task ACheckIsSaidOnBothTabs_ButHowItEndedBelongsToTheTabItWasFor()
    {
        var harness = await B2Harness.CreateOnDomainControllerAsync();
        harness.Builder.AddPrivilegedAccessSample(harness.Company);
        var hooked = new HookedSession(new FakeDomainControllerSession(harness.Session, "dc01.contoso.test")) { HoldAfter = 2 };
        await harness.ReconnectAsync(hooked);
        using var review = harness.Factory.CreateAccountReview();
        await review.InitializeAsync();
        review.SelectedTabIndex = AccountReviewViewModel.PrivilegesTab;

        var run = review.RunCommand.ExecuteAsync(null);
        await hooked.Held.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(review.Privileges.IsRunning);
        Assert.True(review.Status.IsWorking);
        var working = review.Status.Current;
        Assert.StartsWith("Checking the privileges of the accounts in the scope “contoso.test”", working.Message, StringComparison.Ordinal);

        // The other tab in front: the line is still the window's - its check runs - and a second check cannot start.
        review.SelectedTabIndex = 0;
        Assert.Same(working, review.Status.Current);
        Assert.False(review.RunCommand.CanExecute(null));

        // Stopped from there: the tab in front has nothing to say about it...
        review.Status.CancelCommand.Execute(null);
        await run.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.False(review.Status.IsWorking);
        Assert.Equal(PageStatusKind.Idle, review.Status.Current.Kind);
        Assert.Equal(string.Empty, review.Status.Current.Message);

        // ...the tab that was checked says it when it is shown, with "Check" as its action.
        review.SelectedTabIndex = AccountReviewViewModel.PrivilegesTab;
        var stopped = review.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Same(review.Privileges.Status.Current, stopped);
        Assert.StartsWith("Checking the privileges was stopped: the results are incomplete (read: ", stopped.Message, StringComparison.Ordinal);
        Assert.EndsWith("). Press “Check” to check again.", stopped.Message, StringComparison.Ordinal);
        Assert.StartsWith("PrivilegedAccess.Status.Stopped (", stopped.Log.ToString(), StringComparison.Ordinal);
        Assert.True(stopped.CanClose);
        var again = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal("Check", again.Label);
        Assert.Same(review.RunCommand, again.Command);
        Assert.False(review.Privileges.IsRunning);
        Assert.StartsWith("Stopped (as of ", review.Privileges.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APrivilegesCheckThatFails_IsTheStateOfThatTab_WithRetry()
    {
        var harness = await B2Harness.CreateAsync();
        harness.Builder.AddPrivilegedAccessSample(harness.Company);
        using var review = harness.Factory.CreateAccountReview();
        await review.InitializeAsync();
        review.SelectedTabIndex = AccountReviewViewModel.PrivilegesTab;
        var fail = true;
        harness.Session.BeforeOperation = operation =>
        {
            if (fail && operation.Kind == FakeOperationKind.Search)
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "dc01.contoso.test does not answer");
            }
        };

        await review.RunCommand.ExecuteAsync(null);

        var tab = review.Privileges;
        var failed = review.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.Same(tab.Status.Lasting, failed);
        Assert.NotNull(tab.Error);
        Assert.Equal($"Could not check the privileges of the accounts: {tab.Error} Press Retry to try again. (ServerUnavailable)", failed.Message);
        Assert.Equal("PrivilegedAccess.Status.Failed [ServerUnavailable]", failed.Log.ToString());
        Assert.False(failed.CanClose);
        Assert.Same(review.RunCommand, Assert.IsType<PageStatusAction>(failed.Action).Command);

        fail = false;
        harness.Session.BeforeOperation = null;
        await review.RunCommand.ExecuteAsync(null);
        Assert.Null(tab.Status.Lasting);
        Assert.Null(tab.Error);
        Assert.Equal(tab.StatusText, review.Status.Current.Message);
    }

    [Theory]
    [InlineData("en-US", "Reading the exact last logon of the accounts (1) on all domain controllers…", "The exact last logon was read on 2 of 3 domain controllers. Accounts that are active after all: 1. No answer from: dc03.contoso.test.")]
    [InlineData("pl-PL", "Odczytywanie dokładnego ostatniego logowania kont (1) na wszystkich kontrolerach domeny…", "Dokładne ostatnie logowanie odczytano na kontrolerach domeny: 2 z 3. Konta, które jednak są aktywne: 1. Brak odpowiedzi: dc03.contoso.test.")]
    public async Task TheExactLastLogon_IsAnOperationOfItsTab_AndItsResultIsSaidInTheLine(string culture, string working, string result)
    {
        var scenario = await ReviewScenario.CreateAsync(CultureInfo.GetCultureInfo(culture));
        var harness = scenario.Harness;
        var session = harness.Session;
        var now = harness.Time.GetUtcNow();
        DomainControllerSeed.Add(session, "dc02.contoso.test");
        DomainControllerSeed.Add(session, "dc03.contoso.test");
        harness.ConnectToServer = (current, server, _) =>
        {
            if (server == "dc03.contoso.test")
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "dc03 is down");
            }

            var other = new HookedSession(new FakeDomainControllerSession(session, server));
            other.Extra[scenario.Company.PawelLewandowski] = (Core.ActiveDirectory.Attr.LastLogon, ReviewScenario.FileTime(now.AddDays(-2)));
            return Task.FromResult<IDirectorySession>(other);
        };
        using var review = scenario.FactoryWithConnector().CreateAccountReview();
        await review.InitializeAsync();
        await review.RunCommand.ExecuteAsync(null);
        review.SelectedTile = review.Tiles.Single(t => t.Check == AccountCheck.Inactive);
        var seen = Record(review.Status);

        await review.ReadExactLastLogonCommand.ExecuteAsync(null);

        // The tab's own operation - the window is not checking - and then what happened: an "attention", because one
        // domain controller did not answer. Its name is on screen only.
        Assert.Equal((PageStatusKind.Working, working), seen[0]);
        var said = review.Status.Current;
        Assert.Equal(PageStatusKind.Attention, said.Kind);
        Assert.Same(review.AccountsStatus.Current, said);
        Assert.Equal(result, said.Message);
        Assert.Equal("AccountReview.Status.ExactDone (2, 3, 1)", said.Log.ToString());
        Assert.True(said.CanClose);
        Assert.False(review.IsReadingDomainControllers);
        Assert.False(review.AccountsStatus.IsWorking);
        Assert.Empty(harness.Dialogs.Errors);
        Assert.Empty(review.Items);
    }

    [Fact]
    public async Task TheExactLastLogon_OnEveryDomainController_IsDone_AndAStoppedReadSaysThatNothingChanged()
    {
        var scenario = await ReviewScenario.CreateAsync();
        var harness = scenario.Harness;
        var session = harness.Session;
        DomainControllerSeed.Add(session, "dc02.contoso.test");
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ConnectToServer = async (current, server, ct) =>
        {
            asked.TrySetResult();
            await answer.Task.WaitAsync(ct).ConfigureAwait(false);
            return new HookedSession(new FakeDomainControllerSession(session, server));
        };
        using var review = scenario.FactoryWithConnector().CreateAccountReview();
        await review.InitializeAsync();
        await review.RunCommand.ExecuteAsync(null);
        review.SelectedTile = review.Tiles.Single(t => t.Check == AccountCheck.Inactive);
        var listed = review.Items.Count;

        // The other domain controller does not answer yet: the read is the tab's operation, with Stop.
        var reading = review.ReadExactLastLogonCommand.ExecuteAsync(null);
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(review.IsReadingDomainControllers);
        Assert.True(review.AccountsStatus.IsWorking);
        Assert.False(review.Status.IsWorking);
        Assert.Same(review.AccountsStatus.Current, review.Status.Current);
        var stop = Assert.IsType<PageStatusAction>(review.Status.Current.Action);
        Assert.Same(review.AccountsStatus.CancelCommand, stop.Command);

        var seen = Record(review.Status);
        stop.Command.Execute(stop.Parameter);
        await reading.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        // Stopped: the list is as it was, and the button that started the read reads again.
        Assert.Equal((PageStatusKind.Working, "Stopping…"), seen[0]);
        var stopped = review.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Equal("Reading the exact last logon was stopped: the list was not changed. Press “Exact last logon (all DCs)” to read it again.", stopped.Message);
        Assert.Equal("AccountReview.Status.ExactStopped", stopped.Log.ToString());
        Assert.False(review.IsReadingDomainControllers);
        Assert.Equal(listed, review.Items.Count);
        var again = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal("Exact last logon (all DCs)", again.Label);
        Assert.Same(review.ReadExactLastLogonCommand, again.Command);

        // Read again, and this time the domain controller answers: done - how many answered, what that changed.
        answer.SetResult();
        await review.ReadExactLastLogonCommand.ExecuteAsync(null);
        var done = review.Status.Current;
        Assert.Equal(PageStatusKind.Done, done.Kind);
        Assert.Equal("The exact last logon was read on 2 of 2 domain controllers. Accounts that are active after all: 0.", done.Message);
        Assert.Equal("AccountReview.Status.ExactDone (2, 2, 0)", done.Log.ToString());
        Assert.Null(done.Action);

        // A new check takes the result off: the tab says its summary again.
        await review.RunCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Idle, review.Status.Current.Kind);
        Assert.Equal(review.StatusText, review.Status.Current.Message);
    }

    [Fact]
    public async Task TheEndOfTheSession_IsTheStateOfTheWindow_OnBothTabs()
    {
        var scenario = await ReviewScenario.CreateAsync();
        var harness = scenario.Harness;
        using var review = await scenario.RunAsync();
        Assert.Equal(PageStatusKind.Idle, review.Status.Current.Kind);

        await harness.ReconnectAsync(new FakeDirectoryBuilder(timeProvider: harness.Time).Session);

        // An error that cannot be closed: the results stay, nothing can be checked or changed any more.
        Assert.True(review.IsSessionEnded);
        var ended = review.Status.Current;
        Assert.Equal(PageStatusKind.Error, ended.Kind);
        Assert.Equal(review.SessionEndedMessage, ended.Message);
        Assert.Equal("WindowSession.Ended", ended.Log.ToString());
        Assert.Same(review.Status.Lasting, ended);
        Assert.False(ended.CanClose);
        Assert.Null(ended.Action);
        Assert.False(review.RunCommand.CanExecute(null));

        // The other tab in front: the same - the state is the window's.
        review.SelectedTabIndex = AccountReviewViewModel.PrivilegesTab;
        Assert.Same(ended, review.Status.Current);

        harness.Localizer.SetCulture(Polish);
        Assert.Equal(review.SessionEndedMessage, review.Status.Current.Message);
        Assert.StartsWith("Sesja, w której otwarto to okno", review.Status.Current.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLogCopies_CarryKeysAndNumbers_NeverAScopeANameOrAServerText()
    {
        var scenario = await ReviewScenario.CreateAsync();
        var harness = scenario.Harness;
        harness.Builder.AddPrivilegedAccessSample(harness.Company);
        var logger = new ListLogger<PageStatusHolder>();
        var lists = new ObjectListServices(harness.Sessions, harness.Localizer, harness.Dispatcher, harness.Settings, harness.Errors, harness.Changes, harness.Time, logger);
        var services = new DialogServices(
            harness.Sessions, harness.Localizer, harness.Dispatcher, harness.Settings, harness.Errors, harness.Changes, harness.Time, harness.Dialogs, harness.Files,
            harness.Clipboard, harness.Notifications, harness.Coordinator, harness.Activities, lists, harness.Regional);
        using var review = new AccountReviewViewModel(services, harness.Views, new CsvCodec(), new DomainControllerReader(null, harness.Errors));
        await review.InitializeAsync();
        await review.RunCommand.ExecuteAsync(null);
        review.SelectedTabIndex = AccountReviewViewModel.PrivilegesTab;
        await review.RunCommand.ExecuteAsync(null);
        review.SelectedTabIndex = 0;
        harness.Session.BeforeOperation = operation =>
        {
            if (operation.Kind == FakeOperationKind.Search)
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "dc01.contoso.test does not answer");
            }
        };
        await review.RunCommand.ExecuteAsync(null);
        harness.Session.BeforeOperation = null;
        Assert.Equal(PageStatusKind.Error, review.Status.Current.Kind);

        var messages = logger.Entries.Select(entry => entry.Message).ToList();
        Assert.Contains(messages, m => m.Contains("AccountReview.Status.Checking", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("PrivilegedAccess.Status.Checking", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("AccountReview.Status.Failed", StringComparison.Ordinal) && m.Contains("ServerUnavailable", StringComparison.Ordinal));
        Assert.DoesNotContain("contoso", logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dc01", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Kowalski", logger.AllText, StringComparison.Ordinal);
    }

    private static List<(PageStatusKind Kind, string Message)> Record(PageStatusHolder status)
    {
        var seen = new List<(PageStatusKind Kind, string Message)>();
        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (e.PropertyName == nameof(PageStatusHolder.Current))
            {
                lock (seen)
                {
                    seen.Add((status.Current.Kind, status.Current.Message));
                }
            }
        };
        status.PropertyChanged += handler;
        return seen;
    }
}
