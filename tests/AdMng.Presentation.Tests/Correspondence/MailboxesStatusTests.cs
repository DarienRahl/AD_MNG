using AdMng.Core.Directory;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Presentation.Tests.GroupPolicy;
using AdMng.Testing;
using static AdMng.Presentation.Tests.Correspondence.PageTests;

namespace AdMng.Presentation.Tests.Correspondence;

/// <summary>
/// The status line of "Skrzynki" (track V, stage V2b-4, unit 5; docs/dev/v-interface-renewal.md "V2b-4 as built"): the
/// search for the owners and the read of the owners that are added are the operations of the page, with how far they are and
/// Stop; the counts of the matches are its summary; why a search failed is the state of the page until the next one; the
/// session of the window ending is said by the page too. How owners are found and added is covered, unchanged, by
/// <see cref="MailboxOwnersPageTests"/> and <see cref="MailboxOwnersReviewTests"/>.
/// </summary>
public sealed class MailboxesStatusTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(CorrespondenceShellScenario Scenario, CorrespondenceViewModel Window, MailboxOwnerSample Sample)> OpenAsync()
    {
        var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        var sample = MailboxSample(scenario);
        var window = await scenario.OpenAsync(new CorrespondenceRequest([], CorrespondenceStart.MailboxOwners));
        await UseSampleOuAsync(scenario, window.Mailboxes, sample);
        Assert.Same(window.Mailboxes.Status, window.Status.Front);
        return (scenario, window, sample);
    }

    [Fact]
    public async Task TheSearch_SaysHowFarItIs_AndLeavesTheCountsOfTheMatchesAsTheSummary()
    {
        var (scenario, window, _) = await OpenAsync();
        using var _ = scenario;
        using var __ = window;
        var page = window.Mailboxes;
        var line = new StatusRecorder(window.Status);

        await page.FindOwnersCommand.ExecuteAsync(null);

        var plain = line.Plain;
        Assert.Equal((PageStatusKind.Working, "Szukam właścicieli skrzynek…"), plain[0]);
        Assert.Contains(plain, s => s is { Kind: PageStatusKind.Working } && s.Message == "Szukam właścicieli skrzynek: odczytano skrzynek: 13…");
        Assert.Contains(plain, s => s is { Kind: PageStatusKind.Working } && s.Message.StartsWith("Szukam właścicieli skrzynek: skrzynek 13, osób ", StringComparison.Ordinal));
        Assert.Equal(PageStatusKind.Idle, plain[^1].Kind);
        Assert.Equal(page.SummaryText, plain[^1].Message);
        Assert.StartsWith("Dopasowane: ", window.Status.Current.Message, StringComparison.Ordinal);
        Assert.False(page.IsFinding);
        Assert.False(window.Status.IsWorking || page.Status.IsWorking);
        Assert.Equal("Skrzynki: 13 · osoby: ", page.ProgressText[..22]);   // the old text of the page stays for the code that reads it
        Assert.Null(page.Status.Lasting);

        // The summary follows the review of the rows and the language.
        scenario.Harness.Localizer.SetCulture(English);
        Assert.StartsWith("Matched: ", window.Status.Current.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_SaysStoppingAtOnce_AndTheSearchEndsAsAnAttention_WithRetry()
    {
        var (scenario, window, _) = await OpenAsync();
        using var _ = scenario;
        using var __ = window;
        var page = window.Mailboxes;
        using var held = new HeldDirectoryCall(scenario.Harness.Session, operation => operation.Search?.Filter.Contains("givenName=*", StringComparison.Ordinal) == true);
        var run = page.FindOwnersCommand.ExecuteAsync(null);
        await held.Reached.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        // The persons are being read: the button that started the search is its Cancel, and so is the line.
        Assert.True(page.IsFinding);
        Assert.Equal(PageStatusKind.Working, window.Status.Current.Kind);
        Assert.Equal("Zatrzymaj", window.Status.Current.Action?.Label);
        var line = new StatusRecorder(window.Status);
        window.Status.Current.Action!.Command.Execute(null);
        Assert.True(page.Status.IsCancelling);
        Assert.Equal((PageStatusKind.Working, "Zatrzymywanie…"), (window.Status.Current.Kind, window.Status.Current.Message));

        held.Release();
        await run.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        var stopped = window.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Equal(
            "Wyszukiwanie właścicieli skrzynek zatrzymano: Lista może być niepełna. Naciśnij „Ponów”, aby wyszukać jeszcze raz.",
            stopped.Message);
        Assert.Equal("Correspondence.Mailboxes.Status.FindStopped (0)", stopped.Log.ToString());
        Assert.True(stopped.CanClose);
        Assert.Equal("Ponów", stopped.Action?.Label);
        Assert.Same(page.FindOwnersCommand, stopped.Action?.Command);
        Assert.False(page.IsFinding);
        Assert.False(page.IsRunning);
        Assert.Equal((PageStatusKind.Working, "Zatrzymywanie…"), (line.Seen[0].Kind, line.Seen[0].Message));

        // Retry (the action of the line) searches again, to the end.
        await page.FindOwnersCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Idle, window.Status.Current.Kind);
        Assert.NotEmpty(page.Rows);
    }

    [Fact]
    public async Task ASearchThatFails_LeavesTheListEmpty_AndSaysWhyUntilTheNextSearch()
    {
        var (scenario, window, sample) = await OpenAsync();
        using var _ = scenario;
        using var __ = window;
        var page = window.Mailboxes;
        scenario.Harness.Session.FailNext(FakeOperationKind.Search, sample.Ou, DirectoryErrorKind.Busy);

        await page.FindOwnersCommand.ExecuteAsync(null);

        // The line says what was tried and why, with Retry; it cannot be closed - it is the only explanation of the empty list.
        var state = window.Status.Current;
        Assert.Equal(PageStatusKind.Error, state.Kind);
        Assert.StartsWith("Nie udało się znaleźć właścicieli skrzynek: ", state.Message, StringComparison.Ordinal);
        Assert.EndsWith("(LDAP 51)", state.Message, StringComparison.Ordinal);
        Assert.False(state.CanClose);
        Assert.Equal("Ponów", state.Action?.Label);
        Assert.Same(state, page.Status.Lasting);
        Assert.Equal("Correspondence.Mailboxes.Status.FindFailed (0) [LDAP 51]", state.Log.ToString());
        Assert.StartsWith("Nie udało się odczytać skrzynek: ", page.ProgressText, StringComparison.Ordinal);   // the old text stays

        // The next search starts without it, and ends with the summary.
        state.Action!.Command.Execute(null);
        await B2Harness.WaitUntilAsync(() => page.Rows.Count > 0 && !page.IsRunning);
        Assert.Null(page.Status.Lasting);
        Assert.Equal(PageStatusKind.Idle, window.Status.Current.Kind);
    }

    [Theory]
    [InlineData(3, 300_000, "Znaleziono ponad 3 skrzynek – zawęź zakres, rodzaj skrzynek albo filtr.")]
    [InlineData(20_000, 5, "W miejscu szukania właścicieli jest ponad 5 kont – wybierz węższą jednostkę.")]
    public async Task ALimit_IsTheReasonOfTheState_WithTheNumberOfTheLimitInTheLogCopy(int mailboxes, int persons, string reason)
    {
        var (scenario, window, _) = await OpenAsync();
        using var _ = scenario;
        using var __ = window;
        var page = window.Mailboxes;
        page.MaxMailboxes = mailboxes;
        page.MaxPersons = persons;

        await page.FindOwnersCommand.ExecuteAsync(null);

        var state = window.Status.Current;
        Assert.Equal(PageStatusKind.Error, state.Kind);
        Assert.StartsWith("Nie udało się znaleźć właścicieli skrzynek: " + reason, state.Message, StringComparison.Ordinal);
        Assert.Equal($"Correspondence.Mailboxes.Status.FindFailed ({(mailboxes < 100 ? mailboxes : persons)})", state.Log.ToString());
        Assert.Same(state, page.Status.Lasting);
    }

    [Fact]
    public async Task APressThatCameToNothing_SaysWhatWasNotSearched_AndNoMailboxesIsAnAttention()
    {
        var (scenario, window, _) = await OpenAsync();
        using var _ = scenario;
        using var __ = window;
        var page = window.Mailboxes;

        // The scope is "the selected objects" and nothing was selected.
        page.ScopeKind = MailboxScopeKind.Selection;
        await page.FindOwnersCommand.ExecuteAsync(null);
        var refused = window.Status.Current;
        Assert.Equal(PageStatusKind.Attention, refused.Kind);
        Assert.Equal("Niczego nie wyszukano: Nie zaznaczono obiektów.", refused.Message);
        Assert.Equal("Correspondence.Mailboxes.ScopeSelectionNone", refused.Log.ToString());
        Assert.True(refused.CanClose);
        Assert.Null(page.Status.Lasting);

        // A unit with no mailboxes: the search ran and found none.
        scenario.Harness.Dialogs.PickedContainers.Enqueue(scenario.Company.ItOu);
        await page.BrowseScopeCommand.ExecuteAsync(null);
        await page.FindOwnersCommand.ExecuteAsync(null);
        var none = window.Status.Current;
        Assert.Equal(PageStatusKind.Attention, none.Kind);
        Assert.Equal("W tym zakresie nie znaleziono skrzynek.", none.Message);
        Assert.Equal("Correspondence.Mailboxes.NoMailboxes", none.Log.ToString());
        Assert.False(page.IsFinding);
    }

    [Fact]
    public async Task AddingTheOwners_IsAnOperation_WithStop_AndEndsWithWhatWasAdded_OnBothPages()
    {
        var (scenario, window, _) = await OpenAsync();
        using var _ = scenario;
        using var __ = window;
        var page = window.Mailboxes;
        await page.FindOwnersCommand.ExecuteAsync(null);
        var summary = page.SummaryText;
        var mailboxesLine = new StatusRecorder(page.Status);
        var recipientsLine = new StatusRecorder(window.Recipients.Status);

        await page.AddToRecipientsCommand.ExecuteAsync(null);

        // The mailbox page: working, then straight to the result.
        Assert.Equal((PageStatusKind.Working, "Odczytuję dane właścicieli (9)…"), mailboxesLine.Plain[0]);
        Assert.Equal((PageStatusKind.Done, "Dodano do odbiorców: skrzynki – 9, właściciele – 9."), mailboxesLine.Plain[^1]);
        Assert.DoesNotContain(PageStatusKind.Idle, mailboxesLine.Seen.Skip(1).Select(s => s.Kind));
        Assert.Equal("Correspondence.Mailboxes.Added (9, 9)", page.Status.Current.Log.ToString());

        // The recipients page says the same, and the window shows it after the switch to that tab.
        Assert.Equal((PageStatusKind.Done, "Dodano do odbiorców: skrzynki – 9, właściciele – 9."), recipientsLine.Plain[^1]);
        Assert.Equal(CorrespondenceTab.Recipients, window.SelectedTab);
        Assert.Equal(PageStatusKind.Done, window.Status.Current.Kind);
        Assert.Equal("Dodano do odbiorców: skrzynki – 9, właściciele – 9.", window.Status.Current.Message);

        // Closed: the counts of the recipients are the summary again.
        window.Status.CloseCommand.Execute(null);
        window.Recipients.Status.CloseCommand.Execute(null);
        Assert.Equal(PageStatusKind.Idle, window.Status.Current.Kind);
        Assert.StartsWith("Odbiorcy: ", window.Status.Current.Message, StringComparison.Ordinal);
        Assert.Equal(summary, page.Status.Idle.Message);
    }

    [Fact]
    public async Task AddingTheOwners_Stopped_SaysThatNobodyWasAdded_AndNothingOfTheirNames()
    {
        var (scenario, window, _) = await OpenAsync();
        using var _ = scenario;
        using var __ = window;
        var page = window.Mailboxes;
        await page.FindOwnersCommand.ExecuteAsync(null);
        using var held = new HeldDirectoryCall(scenario.Harness.Session, operation => operation.Search?.Filter.Contains("distinguishedName=", StringComparison.OrdinalIgnoreCase) == true);
        var run = page.AddToRecipientsCommand.ExecuteAsync(null);
        await held.Reached.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        Assert.Equal(PageStatusKind.Working, window.Status.Current.Kind);
        window.Status.Current.Action!.Command.Execute(null);
        Assert.Equal("Zatrzymywanie…", window.Status.Current.Message);
        held.Release();
        await run.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        var stopped = window.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Equal("Przerwano – nie dodano właścicieli do odbiorców", stopped.Message);
        Assert.Equal("Correspondence.Mailboxes.AddCancelled (9)", stopped.Log.ToString());
        Assert.Equal("Ponów", stopped.Action?.Label);
        Assert.Same(page.AddToRecipientsCommand, stopped.Action?.Command);
        Assert.Empty(window.Recipients.Items);
        Assert.False(page.IsRunning);
    }

    [Fact]
    public async Task AddingTheOwners_ThatTheDirectoryRefuses_IsAnError_WithTheReasonTheCodeAndRetry()
    {
        var (scenario, window, _) = await OpenAsync();
        using var _ = scenario;
        using var __ = window;
        var page = window.Mailboxes;
        await page.FindOwnersCommand.ExecuteAsync(null);
        scenario.Harness.Session.FailNext(FakeOperationKind.Search, null, DirectoryErrorKind.Busy);
        var recipientsLine = new StatusRecorder(window.Recipients.Status);

        await page.AddToRecipientsCommand.ExecuteAsync(null);

        // What was not done, why and what to do, with the code; Retry reads the owners again. Nobody was added - the
        // recipients page has nothing to say about it.
        var failed = window.Status.Current;
        Assert.Same(page.Status.Current, failed);
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.StartsWith("Nie dodano właścicieli do odbiorców: ", failed.Message, StringComparison.Ordinal);
        Assert.EndsWith(" Wybierz Ponów, aby spróbować ponownie. (LDAP 51)", failed.Message, StringComparison.Ordinal);
        Assert.Equal("Correspondence.Mailboxes.Status.AddFailed (9) [LDAP 51]", failed.Log.ToString());
        Assert.True(failed.CanClose);
        Assert.Equal("Ponów", failed.Action?.Label);
        Assert.Same(page.AddToRecipientsCommand, failed.Action?.Command);
        Assert.Empty(window.Recipients.Items);
        Assert.Empty(recipientsLine.Seen);
        Assert.False(page.IsRunning);
        Assert.True(page.AddToRecipientsCommand.CanExecute(null));

        // Retry (the action of the line) reads the owners again and adds them.
        await page.AddToRecipientsCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Done, window.Status.Current.Kind);
        Assert.Equal("Dodano do odbiorców: skrzynki – 9, właściciele – 9.", window.Status.Current.Message);
        Assert.Equal(9, window.Recipients.Items.Count);
    }

    [Fact]
    public async Task AddingTheOwners_AfterTheSessionEnded_SaysThatNobodyWasAdded_AndWhy()
    {
        var (scenario, window, _) = await OpenAsync();
        using var _ = scenario;
        using var __ = window;
        var page = window.Mailboxes;
        await page.FindOwnersCommand.ExecuteAsync(null);
        await scenario.Harness.Sessions.DisconnectAsync();
        await B2Harness.WaitUntilAsync(() => !window.Recipients.CanUseDirectory);
        Assert.Equal(PageStatusKind.Attention, window.Status.Current.Kind);
        Assert.Equal("WindowSession.Ended", window.Status.Current.Log.ToString());

        // The rows found before are still there, so the button can be pressed: the owners are read again from the session of
        // the window, which has ended - the press is answered, nothing is added and no read is sent.
        var searches = scenario.Harness.Session.Operations.Count(o => o.Kind == FakeOperationKind.Search);
        await page.AddToRecipientsCommand.ExecuteAsync(null);

        var failed = window.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.Equal(
            "Nie dodano właścicieli do odbiorców: Sesja, w której otwarto to okno, została zakończona (rozłączenie lub ponowne połączenie). "
            + "Zamknij to okno i otwórz je ponownie.",
            failed.Message);
        Assert.Equal("Correspondence.Mailboxes.Status.AddFailed (9)", failed.Log.ToString());
        Assert.Null(failed.Action);
        Assert.Empty(window.Recipients.Items);
        Assert.False(page.IsRunning);
        Assert.Equal(searches, scenario.Harness.Session.Operations.Count(o => o.Kind == FakeOperationKind.Search));

        // Closed: the state of the page that lasts says it again.
        window.Status.CloseCommand.Execute(null);
        Assert.Equal("WindowSession.Ended", window.Status.Current.Log.ToString());
        Assert.False(window.Status.Current.CanClose);
    }

    [Fact]
    public async Task TheSessionEnding_IsSaidByThePage_AndTheRunGoesFromWorkingStraightToIt()
    {
        var (scenario, window, _) = await OpenAsync();
        using var _ = scenario;
        using var __ = window;
        var page = window.Mailboxes;
        using var held = new HeldDirectoryCall(scenario.Harness.Session, operation => operation.Search?.Filter.Contains("givenName=*", StringComparison.Ordinal) == true);
        var run = page.FindOwnersCommand.ExecuteAsync(null);
        await held.Reached.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        var line = new StatusRecorder(window.Status);

        await scenario.Harness.Sessions.DisconnectAsync();
        await B2Harness.WaitUntilAsync(() => !window.Recipients.CanUseDirectory);
        held.Release();
        await run.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        // The page says it and it cannot be closed; the window does not - sending through Outlook still works.
        var ended = window.Status.Current;
        Assert.Equal(PageStatusKind.Attention, ended.Kind);
        Assert.False(ended.CanClose);
        Assert.StartsWith("Sesja, w której otwarto to okno, została zakończona", ended.Message, StringComparison.Ordinal);
        Assert.EndsWith("Wysyłanie przez Outlooka nadal działa.", ended.Message, StringComparison.Ordinal);
        Assert.Equal("WindowSession.Ended", ended.Log.ToString());
        Assert.Same(ended, page.Status.Lasting);
        Assert.Same(ended, window.Recipients.Status.Lasting);
        Assert.Null(window.Status.Lasting);
        Assert.Null(window.Send.Status.Lasting);

        // From "working" straight to the end of the session: not through the summary, not through "Stopping…", and no message of
        // a stop the operator did not ask for.
        Assert.Equal([PageStatusKind.Attention], line.Seen.Select(s => s.Kind));

        // Both languages.
        scenario.Harness.Localizer.SetCulture(English);
        Assert.StartsWith("The session this window was opened in has ended", window.Status.Current.Message, StringComparison.Ordinal);
        Assert.EndsWith("Sending through Outlook still works.", window.Status.Current.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryStatusOfThePage_HasALogCopyWithoutAMailboxOrAName()
    {
        var (scenario, window, _) = await OpenAsync();
        using var _ = scenario;
        using var __ = window;
        var page = window.Mailboxes;
        var logs = new List<string>();
        page.Status.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PageStatusHolder.Current))
            {
                logs.Add(page.Status.Current.Log.ToString());
            }
        };

        await page.FindOwnersCommand.ExecuteAsync(null);
        await page.AddToRecipientsCommand.ExecuteAsync(null);

        Assert.NotEmpty(logs);
        Assert.All(logs.Where(log => log.Length > 0), log =>
        {
            Assert.Matches(@"^[A-Za-z.]+(?: \(\d+(?:, \d+)*\))?(?: \[[A-Za-z0-9 ]+\])?$", log);
            Assert.DoesNotContain("@", log, StringComparison.Ordinal);
            Assert.DoesNotContain("Sekretariat", log, StringComparison.Ordinal);
            Assert.DoesNotContain("Kowalski", log, StringComparison.Ordinal);
        });
    }
}
