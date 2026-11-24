using System.Text.RegularExpressions;
using AdMng.Core.Correspondence.Outlook;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Services;
using AdMng.Presentation.Tests.GroupPolicy;
using AdMng.Testing;
using static AdMng.Presentation.Tests.Correspondence.PageTests;

namespace AdMng.Presentation.Tests.Correspondence;

/// <summary>
/// The status line of "4 · Wysyłka" (track V, stage V2b-4, unit 5; docs/dev/v-interface-renewal.md "V2b-4 as built"): a run -
/// the messages, the drafts, the test - is the operation of the page, with how far it is and Stop between two messages; how
/// it ended is a result (done, attention, error); a press that came to nothing and Outlook that cannot be used are said
/// there too. How the messages are made, sent and recorded is covered, unchanged, by <see cref="SendPageTests"/>.
/// </summary>
public sealed class SendStatusTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A window with a message, on the tab of the send page: the line of the window says what that page says.</summary>
    private static async Task<CorrespondenceViewModel> OpenOnSendAsync(CorrespondencePagesScenario scenario,
        string subject = "Zaproszenie na konferencję – {{Miejscowość}}", string body = "<p>{{zwrot}},</p><p>zapraszamy {{nazwa}} z miejscowości {{Miejscowość}} na konferencję w Łodzi.</p>")
    {
        var window = await scenario.OpenWithMessageAsync(subject, body);
        window.SelectedTab = CorrespondenceTab.Send;
        return window;
    }

    [Fact]
    public async Task ARun_IsTheOperationOfThePage_SaysHowFarItIs_AndEndsAsDone()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await OpenOnSendAsync(scenario);
        CorrespondencePagesScenario.AddRows(window, CorrespondencePagesScenario.Offices());
        scenario.Ui.ConfirmAnswers.Enqueue(true);
        var send = window.Send.Status;
        var seen = new List<PageStatus>();
        send.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PageStatusHolder.Current))
            {
                seen.Add(send.Current);
            }
        };
        var line = new StatusRecorder(window.Status);
        Assert.Same(send, window.Status.Front);

        await window.Send.SendCommand.ExecuteAsync(null);

        // Working from the first moment ("0 of 3"), then one message at a time, the part done in the ring.
        var working = seen.TakeWhile(s => s.Kind == PageStatusKind.Working).ToList();
        Assert.Equal(4, working.Count);
        Assert.Equal("Wysyłam wiadomości: 0 z 3…", working[0].Message);
        Assert.Matches(@"^Wysyłam wiadomości: 1 z 3 · zostało ok\. \d+ s…$", working[1].Message);
        Assert.Equal(["Correspondence.Send.Status.Sending (0, 3)", "Correspondence.Send.Status.SendingEta (1, 3)", "Correspondence.Send.Status.SendingEta (2, 3)",
            "Correspondence.Send.Status.Sending (3, 3)"], working.Select(s => s.Log.ToString()));
        Assert.Equal([0d, 1d / 3, 2d / 3, 1d], working.Select(s => s.Progress!.Value.Fraction ?? -1));
        Assert.All(working, s => Assert.Equal("Zatrzymaj", s.Action?.Label));

        // Done: what happened, how many, and where the result is - and the page keeps the summary of its own: nothing.
        var done = Assert.Single(seen, s => s.Kind == PageStatusKind.Done);
        Assert.Equal("Wysyłka zakończona: udane 3, błędy 0. Każda wiadomość jest na liście wyników.", done.Message);
        Assert.Equal("Correspondence.Send.DoneSend (3, 0, 0)", done.Log.ToString());
        Assert.True(done.CanClose);
        Assert.Null(done.Action);
        Assert.Equal(PageStatusKind.Done, send.Current.Kind);
        Assert.Same(send.Current, window.Status.Current);
        Assert.Equal("Wysyłka zakończona: udane 3, błędy 0.", window.Send.ProgressText);   // the old text stays for the code that reads it
        Assert.False(send.IsWorking);

        // The line of the window went from "working" to the result, never through the summary.
        var kinds = line.Seen.Select(s => s.Kind).ToList();
        Assert.Equal(PageStatusKind.Working, kinds[0]);
        Assert.Equal(PageStatusKind.Done, kinds[^1]);
        Assert.DoesNotContain(PageStatusKind.Idle, kinds.Skip(1));

        // Closed: the page has nothing of its own to say.
        send.CloseCommand.Execute(null);
        Assert.Equal(PageStatusKind.Idle, window.Status.Current.Kind);
        Assert.Equal(string.Empty, window.Status.Current.Message);
    }

    [Fact]
    public async Task Stop_SaysStoppingAtOnce_AndTheRunEndsAfterTheMessageThatIsBeingSent_AsAnAttention()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await OpenOnSendAsync(scenario);
        CorrespondencePagesScenario.AddRows(window, CorrespondencePagesScenario.Offices());
        using var entered = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        scenario.Outlook.BeforeSend = (_, _) =>
        {
            entered.Release();
            release.Wait(TimeSpan.FromSeconds(30));
        };
        scenario.Ui.ConfirmAnswers.Enqueue(true);
        var send = window.Send.Status;
        var run = window.Send.SendCommand.ExecuteAsync(null);
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(10), Ct));

        // The first message is being sent: the page works, with Stop as its action, and the button of the run is its Cancel.
        Assert.True(window.Send.IsSending);
        Assert.True(window.Send.IsSendingMessages);
        Assert.False(window.Send.IsTesting || window.Send.IsCreatingDrafts || window.Send.IsRetrying);
        Assert.Equal(PageStatusKind.Working, window.Status.Current.Kind);
        Assert.Equal("Zatrzymaj", window.Status.Current.Action?.Label);
        Assert.Same(send.CancelCommand, window.Status.Current.Action?.Command);
        var line = new StatusRecorder(window.Status);

        // Stop of the line: "Stopping…" at once - the message that is being sent is not waited for to say it.
        window.Status.Current.Action!.Command.Execute(null);
        Assert.True(send.IsCancelling);
        Assert.Equal(PageStatusKind.Working, window.Status.Current.Kind);
        Assert.Equal("Zatrzymywanie…", window.Status.Current.Message);
        Assert.Null(window.Status.Current.Action);

        release.Release(3);
        await run.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        // Attention: what was stopped, what is left, and what to press.
        var stopped = window.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Equal(
            "Wysyłka przerwana: udane 1, błędy 0, niewysłane 2. Naciśnij „Wyślij”, aby wysłać resztę: adresy, do których już wysłano, są pomijane, dopóki włączone jest „Pomijaj już wysłane”.",
            stopped.Message);
        Assert.Equal("Correspondence.Send.DoneStopped (1, 0, 2)", stopped.Log.ToString());
        Assert.True(stopped.CanClose);
        Assert.False(window.Send.IsSending);
        Assert.False(window.Send.IsSendingMessages);
        Assert.Single(scenario.Outlook.Items);
        Assert.Equal((PageStatusKind.Working, "Zatrzymywanie…"), (line.Seen[0].Kind, line.Seen[0].Message));
        Assert.Equal(PageStatusKind.Attention, line.Seen[^1].Kind);
    }

    [Fact]
    public async Task TheStopCommandOfThePage_IsTheSameStop_AsTheLines()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await OpenOnSendAsync(scenario);
        CorrespondencePagesScenario.AddRows(window, CorrespondencePagesScenario.Offices());
        var sawStopping = false;
        scenario.Outlook.BeforeSend = (_, _) =>
        {
            window.Send.StopCommand.Execute(null);
            sawStopping = window.Status.Current is { Kind: PageStatusKind.Working, Message: "Zatrzymywanie…" };
        };
        scenario.Ui.ConfirmAnswers.Enqueue(true);

        await window.Send.SendCommand.ExecuteAsync(null);

        Assert.True(sawStopping);
        Assert.Equal(PageStatusKind.Attention, window.Status.Current.Kind);
        Assert.StartsWith("Wysyłka przerwana: udane 1, błędy 0, niewysłane 2.", window.Status.Current.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedMessage_EndsTheRunAsAnAttention_WithTheButtonThatRepeatsIt()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await OpenOnSendAsync(scenario);
        CorrespondencePagesScenario.AddRows(window, CorrespondencePagesScenario.Offices());
        scenario.Outlook.Script("ug@brojce.pl", FakeOutlookClient.Failed(OutlookFailureKind.RecipientUnresolved, "recipients"));
        scenario.Ui.ConfirmAnswers.Enqueue(true);

        await window.Send.SendCommand.ExecuteAsync(null);

        var result = window.Status.Current;
        Assert.Equal(PageStatusKind.Attention, result.Kind);
        Assert.Equal("Wysyłka zakończona: udane 2, błędy 1. Naciśnij „Ponów nieudane”, aby spróbować jeszcze raz.", result.Message);
        Assert.Equal("Correspondence.Send.DoneSend (2, 1, 0)", result.Log.ToString());
        Assert.Null(result.Action);   // the button below repeats it, after its own confirmation: no link repeats a send
        Assert.True(window.Send.RetryFailedCommand.CanExecute(null));
    }

    [Fact]
    public async Task OutlookThatStopsTheRun_IsAnError_WithTheKindOfTheFailureAsItsCode_AndNothingOfTheAddresses()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await OpenOnSendAsync(scenario);
        CorrespondencePagesScenario.AddRows(window, CorrespondencePagesScenario.Offices());
        scenario.Outlook.Script("ug@brojce.pl", FakeOutlookClient.Failed(OutlookFailureKind.Disconnected));
        scenario.Ui.ConfirmAnswers.Enqueue(true);

        await window.Send.SendCommand.ExecuteAsync(null);

        var result = window.Status.Current;
        Assert.Equal(PageStatusKind.Error, result.Kind);
        Assert.Equal(
            "Wysyłka przerwana: udane 1, błędy 1, niewysłane 1: Połączenie z Outlookiem zostało przerwane (Outlook zamknięty?) – uruchom Outlooka i połącz ponownie. "
            + "Sprawdź Outlooka, potem naciśnij „Ponów nieudane”. (Disconnected)",
            result.Message);
        Assert.Equal("Correspondence.Send.DoneStopped (1, 1, 1) [Disconnected]", result.Log.ToString());

        // The state of the page that lasts is back once the error is closed: Outlook has to be connected again.
        window.Send.Status.CloseCommand.Execute(null);
        var lasting = window.Send.Status.Current;
        Assert.Equal(PageStatusKind.Attention, lasting.Kind);
        Assert.StartsWith("Outlook jest niedostępny: ", lasting.Message, StringComparison.Ordinal);
        Assert.Equal("Połącz z Outlookiem", lasting.Action?.Label);
    }

    [Fact]
    public async Task FiveFailuresInARow_EndTheRunAsAnAttention_ThatSaysWhatToDo()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await OpenOnSendAsync(scenario);
        var rows = Enumerable.Range(1, 9).Select(i => CorrespondencePagesScenario.Row($"urzad{i}@gmina.pl", "", ("Miejscowość", "Łódź"))).ToArray();
        CorrespondencePagesScenario.AddRows(window, rows);
        scenario.Outlook.DefaultResult = FakeOutlookClient.Failed(OutlookFailureKind.RecipientUnresolved, "recipients");
        scenario.Ui.ConfirmAnswers.Enqueue(true);

        await window.Send.SendCommand.ExecuteAsync(null);

        var result = window.Status.Current;
        Assert.Equal(PageStatusKind.Attention, result.Kind);
        Assert.Equal(
            "Wysyłka przerwana: udane 0, błędy 5, niewysłane 4. Zatrzymano po 5 błędach z rzędu: sprawdź Outlooka, potem naciśnij „Ponów nieudane”.",
            result.Message);
    }

    [Theory]
    [InlineData("pl-PL", "Zapisuję wersje robocze: 0 z 3…", "Utworzono wersje robocze: udane 3, błędy 0. Każda wiadomość jest na liście wyników.")]
    [InlineData("en-US", "Saving drafts: 0 of 3…", "Drafts created: 3 succeeded, 0 failed. Each message is listed under Results.")]
    public async Task Drafts_AreTheirOwnKindOfRun_InBothLanguages(string culture, string working, string done)
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync(System.Globalization.CultureInfo.GetCultureInfo(culture));
        using var window = await OpenOnSendAsync(scenario, "Zaproszenie – {{Miejscowość}}", "<p>{{zwrot}},</p><p>zapraszamy do {{Miejscowość}}.</p>");
        CorrespondencePagesScenario.AddRows(window, CorrespondencePagesScenario.Offices());
        scenario.Ui.ConfirmAnswers.Enqueue(true);
        var flags = new List<(bool Drafts, bool Sending, bool Test, bool Retry)>();
        scenario.Outlook.BeforeSend = (_, _) =>
            flags.Add((window.Send.IsCreatingDrafts, window.Send.IsSendingMessages, window.Send.IsTesting, window.Send.IsRetrying));
        var line = new StatusRecorder(window.Send.Status);

        await window.Send.CreateDraftsCommand.ExecuteAsync(null);

        Assert.Equal(working, line.Seen[0].Message);
        Assert.Equal(done, window.Send.Status.Current.Message);
        Assert.Equal(PageStatusKind.Done, window.Send.Status.Current.Kind);
        Assert.All(flags, f => Assert.Equal((true, false, false, false), f));
        Assert.False(window.Send.IsCreatingDrafts);   // the flags end with the run
    }

    [Fact]
    public async Task ATest_AndARetry_HaveTheirOwnFlags()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await OpenOnSendAsync(scenario);
        var rows = CorrespondencePagesScenario.Offices();
        CorrespondencePagesScenario.AddRows(window, rows);
        var seen = new List<(bool Drafts, bool Sending, bool Test, bool Retry, string Text)>();
        scenario.Outlook.BeforeSend = (_, _) =>
            seen.Add((window.Send.IsCreatingDrafts, window.Send.IsSendingMessages, window.Send.IsTesting, window.Send.IsRetrying, window.Status.Current.Message));
        scenario.Outlook.Script("ug@brojce.pl", FakeOutlookClient.Failed(OutlookFailureKind.RecipientUnresolved, "recipients"));
        scenario.Ui.ConfirmAnswers.Enqueue(true);
        await window.Send.SendCommand.ExecuteAsync(null);
        seen.Clear();

        // The retry of the failed one: confirmed, and its own button is the Cancel.
        scenario.Ui.ConfirmAnswers.Enqueue(true);
        await window.Send.RetryFailedCommand.ExecuteAsync(null);
        Assert.Equal([(false, false, false, true, "Wysyłam wiadomości: 0 z 1…")], seen);
        Assert.Equal(PageStatusKind.Done, window.Send.Status.Current.Kind);
        seen.Clear();

        // The test to myself.
        await window.Send.SendTestCommand.ExecuteAsync(null);
        Assert.Equal([(false, false, true, false, "Wysyłam test: 0 z 1…")], seen);
        Assert.Equal("Test: udane 1, błędy 0. Każda wiadomość jest na liście wyników.", window.Send.Status.Current.Message);
    }

    [Fact]
    public async Task APressThatCameToNothing_SaysSoInTheLine_NotInADialog_AndNamesNobody()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        scenario.Outlook.ConnectFailure = FakeOutlookClient.Failure(OutlookFailureKind.NotResponding, "connect");
        using var window = await OpenOnSendAsync(scenario);

        // Nobody to send to.
        await window.Send.SendCommand.ExecuteAsync(null);
        var none = window.Status.Current;
        Assert.Equal(PageStatusKind.Attention, none.Kind);
        Assert.Equal("Nic nie wysłano: Nie ma do kogo wysłać – sprawdź zaznaczenia i listę pominięć.", none.Message);
        Assert.Equal("Correspondence.Send.NothingToSend", none.Log.ToString());
        Assert.True(none.CanClose);

        // The drafts and the test say it in their own words.
        await window.Send.CreateDraftsCommand.ExecuteAsync(null);
        Assert.StartsWith("Nie utworzono wersji roboczych: ", window.Status.Current.Message, StringComparison.Ordinal);

        // Outlook cannot be used: an error with the kind of the failure as its code.
        CorrespondencePagesScenario.AddRows(window, CorrespondencePagesScenario.Offices());
        await window.Send.SendCommand.ExecuteAsync(null);
        var error = window.Status.Current;
        Assert.Equal(PageStatusKind.Error, error.Kind);
        Assert.StartsWith("Nic nie wysłano: Nie można użyć Outlooka: ", error.Message, StringComparison.Ordinal);
        Assert.EndsWith("(NotResponding)", error.Message, StringComparison.Ordinal);
        Assert.Equal("Correspondence.Send.OutlookUnavailable [NotResponding]", error.Log.ToString());
        Assert.DoesNotContain(scenario.Ui.Errors, e => e.Message.Contains("Outlook", StringComparison.Ordinal));
        Assert.Empty(scenario.Outlook.Items);
    }

    [Fact]
    public async Task EveryStatusOfARun_HasALogCopyWithoutAnyAddressSubjectOrName()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await OpenOnSendAsync(scenario, "Zaproszenie dla Jana Kowalskiego", "<p>Treść</p>");
        CorrespondencePagesScenario.AddRows(window, CorrespondencePagesScenario.Offices());
        scenario.Outlook.Script("ug@brojce.pl", FakeOutlookClient.Failed(OutlookFailureKind.RecipientUnresolved, "recipients"));
        scenario.Ui.ConfirmAnswers.Enqueue(true);
        var logs = new List<string>();
        window.Send.Status.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PageStatusHolder.Current))
            {
                logs.Add(window.Send.Status.Current.Log.ToString());
            }
        };

        await window.Send.SendCommand.ExecuteAsync(null);

        Assert.NotEmpty(logs);
        Assert.All(logs, log =>
        {
            Assert.Matches(@"^[A-Za-z.]+(?: \(\d+(?:, \d+)*\))?(?: \[[A-Za-z]+\])?$", log);
            Assert.DoesNotContain("@", log, StringComparison.Ordinal);
            Assert.DoesNotContain("Kowalsk", log, StringComparison.Ordinal);
            Assert.DoesNotContain("brojce", log, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task ClosingTheWindowWhileSending_EndsWithoutAMessageOfItsOwn()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        var window = await OpenOnSendAsync(scenario);
        CorrespondencePagesScenario.AddRows(window, CorrespondencePagesScenario.Offices());
        using var entered = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        scenario.Outlook.BeforeSend = (_, _) =>
        {
            entered.Release();
            release.Wait(TimeSpan.FromSeconds(30));
        };
        scenario.Ui.ConfirmAnswers.Enqueue(true);
        var run = window.Send.SendCommand.ExecuteAsync(null);
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(10), Ct));

        // The window is closed: the run is stopped as before, and the page that is gone says nothing more.
        scenario.Ui.CloseAnswers.Enqueue(CloseAnswer.StopAndClose);
        var closing = window.CanCloseAsync();
        release.Release(3);
        Assert.True(await closing);
        window.Dispose();
        await run.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        Assert.False(window.Send.IsSending);
        Assert.Null(window.Status.Front);
        Assert.Single(scenario.Outlook.Items);
    }

    [Fact]
    public async Task TheStateOfOutlook_IsTheStateOfThePage_ItFollowsTheConnectionAndTheLanguage()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        scenario.Outlook.ConnectFailure = FakeOutlookClient.Failure(OutlookFailureKind.NotResponding, "connect");
        using var window = await scenario.OpenAsync();
        var send = window.Send.Status;

        // The connection failed: the page says why it cannot send, with the way out.
        var state = send.Current;
        Assert.Equal(PageStatusKind.Attention, state.Kind);
        Assert.False(state.CanClose);
        Assert.Equal(
            "Outlook jest niedostępny: Outlook nie odpowiada – sprawdź, czy nie czeka na hasło albo wybór profilu.",
            state.Message);
        Assert.Equal("Correspondence.Outlook.Status.Unavailable [NotResponding]", state.Log.ToString());
        Assert.Equal("Połącz z Outlookiem", state.Action?.Label);
        Assert.Same(window.Outlook.ConnectCommand, state.Action?.Command);
        Assert.Equal(state.Message, window.Responses.Status.Current.Message);   // the responses need Outlook as well
        Assert.Null(window.Recipients.Status.Lasting);                          // the recipients do not
        Assert.Null(window.Status.Lasting);

        // Both languages, at once.
        scenario.Harness.Localizer.SetCulture(English);
        window.Send.UpdateLasting();
        window.Responses.UpdateLasting();
        Assert.StartsWith("Outlook is not available: ", send.Current.Message, StringComparison.Ordinal);
        Assert.Equal("Connect to Outlook", send.Current.Action?.Label);

        // Connected again: the state is gone.
        scenario.Outlook.ConnectFailure = null;
        await window.Outlook.ConnectCommand.ExecuteAsync(null);
        Assert.Null(send.Lasting);
        Assert.Null(window.Responses.Status.Lasting);
        Assert.Equal(PageStatusKind.Idle, send.Current.Kind);
    }

    [Theory]
    [InlineData(OutlookAvailability.NotInstalled, "Outlook jest niedostępny: Nie znaleziono klasycznego Outlooka na tym komputerze.")]
    [InlineData(OutlookAvailability.NotSupported, "Outlook jest niedostępny: Klasyczny Outlook działa tylko w systemie Windows.")]
    public async Task WithoutClassicOutlook_ThePageSaysSo_WithoutAnAction(OutlookAvailability availability, string expectedStart)
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        scenario.Outlook.Availability = availability;
        using var window = await scenario.OpenAsync();

        var state = window.Send.Status.Current;
        Assert.Equal(PageStatusKind.Attention, state.Kind);
        Assert.StartsWith(expectedStart, state.Message, StringComparison.Ordinal);
        Assert.Null(state.Action);
        Assert.Matches(new Regex(@"^Correspondence\.Outlook\.Status\.Unavailable \[(NotInstalled|NotSupported)\]$"), state.Log.ToString());
    }
}
