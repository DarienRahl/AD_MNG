using System.ComponentModel;
using System.Globalization;
using AdMng.Core.Audit;
using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Html;
using AdMng.Core.Messaging.Images;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Presentation.UserMessages;
using AdMng.Testing;
using AdMng.Testing.RichHtml;
using AdMng.Watchdog.Testing.Client;
using CommunityToolkit.Mvvm.Input;
using static AdMng.Presentation.Tests.UserMessages.MessageScenario;

namespace AdMng.Presentation.Tests.UserMessages;

/// <summary>
/// The status line of the message window (track V, stage V2b; docs/dev/v-interface-renewal.md "V2b-2 as built"): the
/// window says what it does with the messages - preparing the preview, the test message, the send with the part done,
/// each with Stop - and how they ended, at rest the counts of the preview or of the report, and - as the state that
/// lasts - that its session ended; while the editor is shown, the editor says its own (the paste). No recipient, no
/// content of a message and no SMTP password is ever part of a log copy, and the password is in no text at all. How
/// messages are planned, sent and reported is covered, unchanged, by <see cref="UserMessageComposerTests"/>.
/// </summary>
public sealed class UserMessageComposerStatusTests
{
    private const string SmtpPassword = "Tajne-Hasło-2026";

    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    /// <summary>How long a held call waits for the test at most (a failing test must not keep a pool thread for good).</summary>
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- At rest and the preview ---------------------------------------------------------------------------------

    [Fact]
    public async Task AtRest_TheLineSaysNothing_AndTheEditorIsThePageInFront()
    {
        var scenario = await CreateAsync();
        using var composer = await scenario.OpenAsync([scenario.Company.JanKowalski], BuiltInMessageTemplates.PasswordExpiring, 14);

        Assert.IsAssignableFrom<IHasPageStatus>(composer);
        Assert.Same(composer.EditorStatus, composer.Status.Front);
        Assert.Same(PageStatus.Empty, composer.Status.Current);
        Assert.Null(composer.Status.Lasting);
        Assert.False(composer.Status.IsWorking);
        Assert.False(composer.IsPlanning);
        Assert.False(composer.IsSendStep);
    }

    [Theory]
    [InlineData(
        "en-US",
        "Reading the accounts (4) and preparing the messages…",
        "Ready: 2 · With warnings: 0 · Errors: 0 · Skipped: 2 · Excluded: 0",
        "Ready: 2 · With warnings: 0 · Errors: 0 · Skipped: 2 · Excluded: 1")]
    [InlineData(
        "pl-PL",
        "Wczytywanie kont (4) i przygotowywanie wiadomości…",
        "Gotowe: 2 · Z ostrzeżeniami: 0 · Błędy: 0 · Pominięte: 2 · Wykluczone: 0",
        "Gotowe: 2 · Z ostrzeżeniami: 0 · Błędy: 0 · Pominięte: 2 · Wykluczone: 1")]
    public async Task PreparingThePreview_IsTheOperationOfTheWindow_ThenTheLineShowsTheCountsOfThePreview(
        string culture, string working, string summary, string excluded)
    {
        var scenario = await CreateAsync(CultureInfo.GetCultureInfo(culture));
        var company = scenario.Company;
        using var composer = await scenario.OpenAsync(
            [company.JanKowalski, company.LukaszWisniewski, company.MalgorzataWojcik, company.BartlomiejKozlowski],
            BuiltInMessageTemplates.PasswordExpiring, 14);
        using var read = new HeldCall();
        scenario.Harness.Session.BeforeOperation = operation =>
        {
            if (operation.Kind == FakeOperationKind.Search)
            {
                read.HoldOnce();
            }
        };
        var seen = Record(composer.Status);
        Task planning;
        try
        {
            planning = composer.PlanCommand.ExecuteAsync(null);
            Assert.True(await read.Held.WaitAsync(Limit, Ct));

            // Working: what is read and how many accounts - never which - with Stop; the Preview button is its Cancel.
            var current = composer.Status.Current;
            Assert.Equal(PageStatusKind.Working, current.Kind);
            Assert.Equal(working, current.Message);
            Assert.Equal("MessageWindow.Status.Reading (4)", current.Log.ToString());
            Assert.True(current.Progress!.Value.IsIndeterminate);
            Assert.Same(composer.Status.CancelCommand, Assert.IsType<PageStatusAction>(current.Action).Command);
            Assert.True(composer.Status.IsWorking);
            Assert.True(composer.IsPlanning);
            Assert.True(composer.IsBusy);
            Assert.False(composer.EditorStatus.IsWorking);
        }
        finally
        {
            read.Release();
        }

        await planning.WaitAsync(Limit, Ct);
        scenario.Harness.Session.BeforeOperation = null;

        // The result is the preview itself: the line shows its counts, the editor is no longer in front.
        Assert.Equal([(PageStatusKind.Working, working), (PageStatusKind.Idle, summary)], seen);
        Assert.Equal(UserMessageStage.Preview, composer.Stage);
        Assert.Null(composer.Status.Front);
        Assert.Equal(composer.SummaryText, composer.Status.Current.Message);
        Assert.False(composer.IsPlanning);
        Assert.False(composer.IsBusy);
        Assert.True(composer.IsSendStep);

        // The counts follow the check boxes of the preview.
        Item(composer, "Jan Kowalski").IsIncluded = false;
        Assert.Equal(excluded, composer.Status.Current.Message);
        Assert.Equal(PageStatusKind.Idle, composer.Status.Current.Kind);
    }

    [Fact]
    public async Task StopWhilePreparing_SaysStoppingAtOnce_AndEndsAsAttention_WithRetryThatPlansTheSameAccounts()
    {
        var scenario = await CreateAsync();
        var company = scenario.Company;
        using var composer = await scenario.OpenAsync([company.JanKowalski, company.LukaszWisniewski], BuiltInMessageTemplates.PasswordExpiring, 14);
        using var read = new HeldCall();
        scenario.Harness.Session.BeforeOperation = operation =>
        {
            if (operation.Kind == FakeOperationKind.Search)
            {
                read.HoldOnce();
            }
        };
        var seen = Record(composer.Status);
        Task planning;
        try
        {
            planning = composer.PlanCommand.ExecuteAsync(null);
            Assert.True(await read.Held.WaitAsync(Limit, Ct));

            // Stop - in the line or on the Preview button, it is the same command: "Stopping…" at once, for as long as
            // the read is on its way to the domain controller.
            composer.Status.CancelCommand.Execute(null);
            Assert.True(composer.Status.IsCancelling);
            Assert.Equal(PageStatusKind.Working, composer.Status.Current.Kind);
            Assert.Equal("Stopping…", composer.Status.Current.Message);
            Assert.Null(composer.Status.Current.Action);
        }
        finally
        {
            read.Release();
        }

        await planning.WaitAsync(Limit, Ct);

        // Stopped: what was stopped, what that means, what to do - and control is back.
        const string StoppedText = "Preparing the messages was stopped: no message was prepared. Press Retry to try again.";
        Assert.Equal(
            [
                (PageStatusKind.Working, "Reading the accounts (2) and preparing the messages…"),
                (PageStatusKind.Working, "Stopping…"),
                (PageStatusKind.Attention, StoppedText),
            ],
            seen);
        var stopped = composer.Status.Current;
        Assert.Equal("MessageWindow.Status.ReadStopped", stopped.Log.ToString());
        Assert.True(stopped.CanClose);
        var retry = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal("Retry", retry.Label);
        Assert.Equal(UserMessageStage.Compose, composer.Stage);
        Assert.Empty(composer.AllItems);
        Assert.False(composer.IsBusy);
        Assert.False(composer.IsPlanning);
        Assert.False(composer.Status.IsWorking);
        Assert.True(composer.PlanCommand.CanExecute(null));

        // Retry plans the same accounts again.
        Assert.True(retry.Command.CanExecute(null));
        await ((IAsyncRelayCommand)retry.Command).ExecuteAsync(null);
        Assert.Equal(UserMessageStage.Preview, composer.Stage);
        Assert.Equal(["Jan Kowalski", "Łukasz Wiśniewski"], composer.AllItems.Select(i => i.Name));
        Assert.Equal(PageStatusKind.Idle, composer.Status.Current.Kind);
        Assert.Equal(composer.SummaryText, composer.Status.Current.Message);
    }

    [Theory]
    [InlineData("en-US", "Could not read the accounts: ", " Press Retry to try again. (ServerUnavailable)", "Retry")]
    [InlineData("pl-PL", "Nie udało się wczytać kont: ", " Wybierz Ponów, aby spróbować ponownie. (ServerUnavailable)", "Ponów")]
    public async Task AReadThatFails_IsAnError_WithTheReasonTheCodeAndRetry(string culture, string lead, string tail, string retryLabel)
    {
        var scenario = await CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var composer = await scenario.OpenAsync([scenario.Company.JanKowalski], BuiltInMessageTemplates.PasswordExpiring, 14);
        var fail = 1;
        scenario.Harness.Session.BeforeOperation = operation =>
        {
            if (operation.Kind == FakeOperationKind.Search && Volatile.Read(ref fail) == 1)
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "dc01.contoso.test does not answer");
            }
        };

        await composer.PlanCommand.ExecuteAsync(null);

        // The window shows what it showed before: a result that can be closed, with Retry.
        var failed = composer.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.NotNull(composer.Error);
        Assert.Equal(lead + composer.Error + tail, failed.Message);
        Assert.DoesNotContain("does not answer", failed.Message, StringComparison.Ordinal);
        Assert.Equal("MessageWindow.Status.ReadFailed [ServerUnavailable]", failed.Log.ToString());
        Assert.True(failed.CanClose);
        var retry = Assert.IsType<PageStatusAction>(failed.Action);
        Assert.Equal(retryLabel, retry.Label);
        Assert.Equal(UserMessageStage.Compose, composer.Stage);
        Assert.False(composer.IsBusy);
        Assert.False(composer.IsPlanning);

        // Retry, once the domain controller answers again.
        Volatile.Write(ref fail, 0);
        await ((IAsyncRelayCommand)retry.Command).ExecuteAsync(null);
        Assert.Equal(UserMessageStage.Preview, composer.Stage);
        Assert.Equal(PageStatusKind.Idle, composer.Status.Current.Kind);
        Assert.Null(composer.Error);
    }

    [Theory]
    [InlineData("en-US", "No message was prepared: ")]
    [InlineData("pl-PL", "Nie przygotowano żadnej wiadomości: ")]
    public async Task ATemplateThatStopsThePlan_IsAttention_WithTheReasons(string culture, string lead)
    {
        var scenario = await CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var composer = await scenario.OpenAsync([scenario.Company.PawelLewandowski], BuiltInMessageTemplates.InactiveDisable, 90);
        var seen = Record(composer.Status);

        // The template needs the value of its field "termin".
        await composer.PlanCommand.ExecuteAsync(null);

        var blocked = composer.Status.Current;
        Assert.Equal(PageStatusKind.Attention, blocked.Kind);
        Assert.NotNull(composer.Error);
        Assert.Equal(lead + composer.Error, blocked.Message);
        Assert.Contains("termin", blocked.Message, StringComparison.Ordinal);
        Assert.Equal("MessageWindow.Status.NotPrepared (1)", blocked.Log.ToString());
        Assert.True(blocked.CanClose);
        Assert.Null(blocked.Action);
        Assert.Equal(UserMessageStage.Compose, composer.Stage);
        Assert.Equal(2, seen.Count);
        Assert.Equal(PageStatusKind.Working, seen[0].Kind);

        // Closed: the editor is at rest again; with the value the preview is prepared.
        composer.Status.CloseCommand.Execute(null);
        Assert.Same(PageStatus.Empty, composer.Status.Current);
        Assert.Single(composer.Inputs).Value = "30 listopada 2026";
        await composer.PlanCommand.ExecuteAsync(null);
        Assert.Equal(UserMessageStage.Preview, composer.Stage);
        Assert.Equal(composer.SummaryText, composer.Status.Current.Message);
    }

    // ---- Sending -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(
        "en-US",
        "Sending the messages: 0 of 2…",
        "Sending the messages: 1 of 2…",
        "Sending the messages: 2 of 2…",
        "The server accepted 2 messages for users of contoso.test. The report is in the list below.",
        "Accepted by the server: 2 · Failed: 0 · Not sent: 0 · Not in this send: 1")]
    [InlineData(
        "pl-PL",
        "Wysyłanie wiadomości: 0 z 2…",
        "Wysyłanie wiadomości: 1 z 2…",
        "Wysyłanie wiadomości: 2 z 2…",
        "Serwer przyjął 2 wiadomości dla użytkowników domeny contoso.test. Raport jest na liście poniżej.",
        "Przyjęte przez serwer: 2 · Nieudane: 0 · Niewysłane: 0 · Poza tą wysyłką: 1")]
    public async Task Sending_SaysHowFarItIs_AndEndsAsDone_WithTheDomain_AndTheCountsOfTheReportBehindIt(
        string culture, string start, string half, string full, string done, string report)
    {
        var scenario = await CreateAsync(CultureInfo.GetCultureInfo(culture));
        var company = scenario.Company;
        using var composer = await scenario.OpenAsync(
            [company.JanKowalski, company.LukaszWisniewski, company.MalgorzataWojcik], BuiltInMessageTemplates.PasswordExpiring, 14);
        await composer.PlanCommand.ExecuteAsync(null);
        Assert.Equal(2, composer.SendCount);

        // The second message waits for the test: "1 of 2" lasts.
        using var second = new HeldCall();
        scenario.Transport.BeforeSend = message =>
        {
            if (message.To[0].Address.StartsWith("lukasz", StringComparison.Ordinal))
            {
                second.HoldOnce();
            }
        };
        scenario.Harness.Dialogs.ConfirmAnswers.Enqueue(true);
        var seen = Record(composer.Status);
        Task sending;
        try
        {
            sending = composer.SendCommand.ExecuteAsync(null);
            Assert.True(await second.Held.WaitAsync(Limit, Ct));
            await B2Harness.WaitUntilAsync(() => composer.Status.Current.Message == half);

            // Working: how far, with the part done for the ring and Stop; the Send button is shown and is its Cancel.
            var current = composer.Status.Current;
            Assert.Equal(PageStatusKind.Working, current.Kind);
            Assert.Equal("MessageWindow.Status.Sending (1, 2, 0)", current.Log.ToString());
            Assert.Equal(0.5, current.Progress!.Value.Fraction);
            Assert.Same(composer.Status.CancelCommand, Assert.IsType<PageStatusAction>(current.Action).Command);
            Assert.Equal(UserMessageStage.Sending, composer.Stage);
            Assert.True(composer.IsSending);
            Assert.True(composer.IsSendStep);
            Assert.False(composer.IsPlanning);
            Assert.True(composer.CancelSendCommand.CanExecute(null));
        }
        finally
        {
            second.Release();
        }

        await sending.WaitAsync(Limit, Ct);

        // One message for the whole send, then how it ended - with the domain - and where the report is.
        Assert.Equal(
            [(PageStatusKind.Working, start), (PageStatusKind.Working, half), (PageStatusKind.Working, full), (PageStatusKind.Done, done)],
            Distinct(seen));
        var result = composer.Status.Current;
        Assert.Equal("MessageWindow.Status.Sent (2)", result.Log.ToString());
        Assert.True(result.CanClose);
        Assert.Null(result.Action);
        Assert.Equal(UserMessageStage.Report, composer.Stage);
        Assert.False(composer.IsSendStep);

        // Behind the result: the counts of the report.
        composer.Status.CloseCommand.Execute(null);
        Assert.Equal(PageStatusKind.Idle, composer.Status.Current.Kind);
        Assert.Equal(report, composer.Status.Current.Message);
        Assert.Equal(composer.ReportSummary, composer.Status.Current.Message);

        // The notification of the main window is the one of before.
        Assert.Contains(scenario.Harness.Notifications.Items, n => n.Severity == NotificationSeverity.Success && n.Message == report);
    }

    [Fact]
    public async Task StopWhileSending_SaysStoppingAtOnce_TheRestIsNotSent_AndTheResultOffersRetryFailed()
    {
        var scenario = await CreateAsync();
        var company = scenario.Company;
        using var composer = await scenario.OpenAsync(
            [company.JanKowalski, company.LukaszWisniewski], BuiltInMessageTemplates.PasswordExpiring, 14);
        await composer.PlanCommand.ExecuteAsync(null);
        using var first = new HeldCall();
        scenario.Transport.BeforeSend = _ => first.HoldOnce();
        scenario.Harness.Dialogs.ConfirmAnswers.Enqueue(true);
        var seen = Record(composer.Status);
        Task sending;
        try
        {
            sending = composer.SendCommand.ExecuteAsync(null);
            Assert.True(await first.Held.WaitAsync(Limit, Ct));

            // Stop of the line (the Cancel of the Send button is the same command): "Stopping…" at once - the message
            // that is on its way still goes out.
            composer.Status.CancelCommand.Execute(null);
            Assert.True(composer.Status.IsCancelling);
            Assert.Equal(PageStatusKind.Working, composer.Status.Current.Kind);
            Assert.Equal("Stopping…", composer.Status.Current.Message);
            Assert.False(composer.Status.CancelCommand.CanExecute(null));
        }
        finally
        {
            first.Release();
        }

        await sending.WaitAsync(Limit, Ct);

        const string StoppedText =
            "Sending the messages was stopped: accepted by the server: 1, failed: 0, not sent: 1. "
            + "Press “Retry failed” to prepare the messages that were not sent again.";
        Assert.Equal(
            [(PageStatusKind.Working, "Sending the messages: 0 of 2…"), (PageStatusKind.Working, "Stopping…"), (PageStatusKind.Attention, StoppedText)],
            Distinct(seen));
        var stopped = composer.Status.Current;
        Assert.Equal("MessageWindow.Status.SendStopped (1, 0, 1)", stopped.Log.ToString());
        Assert.True(stopped.CanClose);
        var retry = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal("Retry failed", retry.Label);
        Assert.Same(composer.RetryFailedCommand, retry.Command);
        Assert.Equal(UserMessageStage.Report, composer.Stage);
        Assert.Equal(UserMessageReportStatus.Accepted, Item(composer, "Jan Kowalski").ReportStatus);
        Assert.Equal(UserMessageReportStatus.NotSent, Item(composer, "Łukasz Wiśniewski").ReportStatus);

        // "Retry failed", pressed in the line, prepares the message that was not sent - the line follows the preview.
        scenario.Transport.BeforeSend = null;
        await composer.RetryFailedCommand.ExecuteAsync(null);
        Assert.Equal(UserMessageStage.Preview, composer.Stage);
        Assert.Equal("Łukasz Wiśniewski", Assert.Single(composer.AllItems).Name);
        Assert.Equal(PageStatusKind.Idle, composer.Status.Current.Kind);
        Assert.Equal(composer.SummaryText, composer.Status.Current.Message);
    }

    [Fact]
    public async Task TheCancelOfTheSendCommand_IsTheSameStop()
    {
        var scenario = await CreateAsync();
        var company = scenario.Company;
        using var composer = await scenario.OpenAsync(
            [company.JanKowalski, company.LukaszWisniewski], BuiltInMessageTemplates.PasswordExpiring, 14);
        await composer.PlanCommand.ExecuteAsync(null);
        using var first = new HeldCall();
        scenario.Transport.BeforeSend = _ => first.HoldOnce();
        scenario.Harness.Dialogs.ConfirmAnswers.Enqueue(true);
        Task sending;
        try
        {
            sending = composer.SendCommand.ExecuteAsync(null);
            Assert.True(await first.Held.WaitAsync(Limit, Ct));
            composer.CancelSendCommand.Execute(null);
            Assert.True(composer.Status.IsCancelling);
            Assert.Equal("Stopping…", composer.Status.Current.Message);
        }
        finally
        {
            first.Release();
        }

        await sending.WaitAsync(Limit, Ct);
        Assert.Equal(PageStatusKind.Attention, composer.Status.Current.Kind);
        Assert.StartsWith("Sending the messages was stopped: ", composer.Status.Current.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "en-US",
        "The messages were sent only in part: accepted by the server: 1, failed: 1, not sent: 0. The reasons are in the report below. "
        + "Press “Retry failed” to prepare the messages that were not sent again.",
        "Retry failed")]
    [InlineData(
        "pl-PL",
        "Wiadomości wysłano tylko częściowo: przyjęte przez serwer: 1, nieudane: 1, niewysłane: 0. Przyczyny są w raporcie poniżej. "
        + "Wybierz „Ponów nieudane”, aby ponownie przygotować wiadomości, których nie wysłano.",
        "Ponów nieudane")]
    public async Task ASendThatDeliversOnlyAPart_IsAttention_WithTheCounts_WhereTheReasonsAre_AndRetryFailed(string culture, string text, string label)
    {
        var scenario = await CreateAsync(CultureInfo.GetCultureInfo(culture));
        var company = scenario.Company;
        scenario.Transport.Script("lukasz.wisniewski@contoso.test", FakeUserMessageTransport.Rejected(550, "5.1.1"));
        using var composer = await scenario.OpenAsync([company.JanKowalski, company.LukaszWisniewski], BuiltInMessageTemplates.PasswordExpiring, 14);
        await composer.PlanCommand.ExecuteAsync(null);
        scenario.Harness.Dialogs.ConfirmAnswers.Enqueue(true);

        await composer.SendCommand.ExecuteAsync(null);

        var partly = composer.Status.Current;
        Assert.Equal(PageStatusKind.Attention, partly.Kind);
        Assert.Equal(text, partly.Message);
        Assert.Equal("MessageWindow.Status.SentPartly (1, 1, 0)", partly.Log.ToString());
        var retry = Assert.IsType<PageStatusAction>(partly.Action);
        Assert.Equal(label, retry.Label);
        Assert.Same(composer.RetryFailedCommand, retry.Command);

        // The address and the code of the failure are in the report - not in the line.
        Assert.DoesNotContain("lukasz", partly.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("550", partly.Message, StringComparison.Ordinal);
        Assert.Equal("smtp.550.5.1.1", Item(composer, "Łukasz Wiśniewski").ReportDetail);
    }

    [Fact]
    public async Task ASendThatDeliversNothing_IsAnError_AndWithoutASessionThereIsNoRetry()
    {
        var scenario = await CreateAsync();
        var company = scenario.Company;
        scenario.Transport.DefaultOutcome = FakeUserMessageTransport.AuthenticationFailed();
        using var composer = await scenario.OpenAsync([company.JanKowalski, company.LukaszWisniewski], BuiltInMessageTemplates.PasswordExpiring, 14);
        await composer.PlanCommand.ExecuteAsync(null);
        scenario.Harness.Dialogs.ConfirmAnswers.Enqueue(true);

        await composer.SendCommand.ExecuteAsync(null);

        // The server refused the sign-in: the first message failed and the run was stopped - nothing was accepted.
        var failed = composer.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.Equal(
            "Could not send the messages: accepted by the server: 0, failed: 1, not sent: 1. The reasons are in the report below. "
            + "Press “Retry failed” to prepare the messages that were not sent again.",
            failed.Message);
        Assert.Equal("MessageWindow.Status.SendFailed (0, 1, 1)", failed.Log.ToString());
        Assert.Same(composer.RetryFailedCommand, Assert.IsType<PageStatusAction>(failed.Action).Command);
        Assert.True(failed.CanClose);

        // The same send in a window whose session ended meanwhile: nothing can be planned again, so nothing is offered.
        var other = await CreateAsync();
        other.Transport.DefaultOutcome = FakeUserMessageTransport.AuthenticationFailed();
        using var orphan = await other.OpenAsync([other.Company.JanKowalski], BuiltInMessageTemplates.PasswordExpiring, 14);
        await orphan.PlanCommand.ExecuteAsync(null);
        await other.Harness.Sessions.DisconnectAsync();
        await B2Harness.WaitUntilAsync(() => orphan.IsSessionEnded);
        other.Harness.Dialogs.ConfirmAnswers.Enqueue(true);
        await orphan.SendCommand.ExecuteAsync(null);

        var result = orphan.Status.Current;
        Assert.Equal(PageStatusKind.Error, result.Kind);
        Assert.Equal("Could not send the messages: accepted by the server: 0, failed: 1, not sent: 0. The reasons are in the report below.", result.Message);
        Assert.Null(result.Action);

        // Closed: the state that lasts is back - the session of the window ended.
        orphan.Status.CloseCommand.Execute(null);
        Assert.Equal(PageStatusKind.Error, orphan.Status.Current.Kind);
        Assert.Equal(other.Harness.Localizer[L.WindowSession.Ended], orphan.Status.Current.Message);
        Assert.False(orphan.Status.Current.CanClose);
    }

    [Theory]
    [InlineData("en-US", "No message was sent: Enter the SMTP password (kept in memory only until AD Manager closes).")]
    [InlineData("pl-PL", "Nie wysłano żadnej wiadomości: Wpisz hasło SMTP (jest przechowywane tylko w pamięci do zamknięcia AD Managera).")]
    public async Task WithoutTheSmtpPassword_NothingIsSent_AndTheLineSaysWhy_ThePasswordItselfIsInNoText(string culture, string text)
    {
        var scenario = await CreateAsync(CultureInfo.GetCultureInfo(culture));
        await scenario.Harness.Settings.UpdateAsync(s =>
        {
            s.UserMessages.Smtp.Authentication = SmtpAuthentication.Basic;
            s.UserMessages.Smtp.UserName = "helpdesk@contoso.test";
        }, Ct);
        using var composer = await scenario.OpenAsync([scenario.Company.JanKowalski], BuiltInMessageTemplates.PasswordExpiring, 14);
        await composer.PlanCommand.ExecuteAsync(null);
        var summary = composer.SummaryText;
        var seen = Record(composer.Status);

        // The button was pressed and nothing was started: an attention the operator can close.
        await composer.SendCommand.ExecuteAsync(null);
        var missing = composer.Status.Current;
        Assert.Equal(PageStatusKind.Attention, missing.Kind);
        Assert.Equal(text, missing.Message);
        Assert.Equal("UserMessages.PasswordRequired", missing.Log.ToString());
        Assert.True(missing.CanClose);
        Assert.Null(missing.Action);
        Assert.Empty(scenario.Transport.Attempts);
        Assert.Equal(UserMessageStage.Preview, composer.Stage);
        composer.Status.CloseCommand.Execute(null);
        Assert.Equal(summary, composer.Status.Current.Message);

        // With the password the messages go out - and no text the line ever said holds it.
        composer.SmtpPasswordInput = new TestPasswordInput(SmtpPassword);
        scenario.Harness.Dialogs.ConfirmAnswers.Enqueue(true);
        await composer.SendCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Done, composer.Status.Current.Kind);
        Assert.Equal(SmtpPassword, scenario.Transport.LastCredential!.Password);
        Assert.DoesNotContain(seen, s => s.Message.Contains(SmtpPassword, StringComparison.Ordinal));
        Assert.DoesNotContain(seen, s => s.Message.Contains("Tajne", StringComparison.Ordinal));
        Assert.DoesNotContain(seen, s => s.Message.Contains("helpdesk@", StringComparison.Ordinal));
    }

    // ---- The test message ----------------------------------------------------------------------------------------

    [Fact]
    public async Task TheTestMessage_IsAnOperationWithStop_AndSaysHowItEnded()
    {
        var scenario = await CreateAsync();
        using var composer = await scenario.OpenAsync([scenario.Company.JanKowalski], BuiltInMessageTemplates.PasswordExpiring, 14);
        await composer.PlanCommand.ExecuteAsync(null);
        composer.SelectedItem = Item(composer, "Jan Kowalski");
        var summary = composer.SummaryText;
        using var send = new HeldCall();
        scenario.Transport.BeforeSend = _ => send.HoldOnce();
        var seen = Record(composer.Status);
        Task testing;
        try
        {
            testing = composer.SendTestCommand.ExecuteAsync(null);
            Assert.True(await send.Held.WaitAsync(Limit, Ct));

            // Working: where the test goes - the operator's own address - with Stop.
            var current = composer.Status.Current;
            Assert.Equal(PageStatusKind.Working, current.Kind);
            Assert.Equal("Sending the test message to operator@contoso.test…", current.Message);
            Assert.Equal("MessageWindow.Status.TestSending", current.Log.ToString());
            Assert.Same(composer.Status.CancelCommand, Assert.IsType<PageStatusAction>(current.Action).Command);
            Assert.True(composer.IsBusy);
            Assert.False(composer.IsPlanning);
            Assert.False(composer.IsSending);
        }
        finally
        {
            send.Release();
        }

        await testing.WaitAsync(Limit, Ct);
        scenario.Transport.BeforeSend = null;

        const string SentText = "The server accepted the test message for operator@contoso.test.";
        Assert.Equal(
            [(PageStatusKind.Working, "Sending the test message to operator@contoso.test…"), (PageStatusKind.Done, SentText)],
            seen);
        Assert.Equal("UserMessages.TestSent", composer.Status.Current.Log.ToString());
        Assert.Equal(SentText, composer.TestResultText);
        Assert.Equal(UserMessageStage.Preview, composer.Stage);
        Assert.False(composer.IsBusy);

        // Closed: the counts of the preview again.
        composer.Status.CloseCommand.Execute(null);
        Assert.Equal(summary, composer.Status.Current.Message);

        // The server refuses the test message: an error with the reason of the transport.
        scenario.Transport.Script("operator@contoso.test", FakeUserMessageTransport.Rejected(550, "5.1.1"));
        await composer.SendTestCommand.ExecuteAsync(null);
        var refused = composer.Status.Current;
        Assert.Equal(PageStatusKind.Error, refused.Kind);
        Assert.Equal("The test message was not sent: smtp.550.5.1.1.", refused.Message);
        Assert.Equal("MessageWindow.Status.TestNotSent", refused.Log.ToString());
        Assert.Equal("The test message was not sent: smtp.550.5.1.1", composer.TestResultText);

        // Back takes what the line said about the preview with it.
        composer.BackCommand.Execute(null);
        Assert.Same(PageStatus.Empty, composer.Status.Current);
        Assert.Same(composer.EditorStatus, composer.Status.Front);
    }

    [Fact]
    public async Task ATestMessage_ThatWasStoppedOrHasNoAddress_IsAttention()
    {
        var scenario = await CreateAsync();
        using var composer = await scenario.OpenAsync([scenario.Company.JanKowalski], BuiltInMessageTemplates.PasswordExpiring, 14);
        await composer.PlanCommand.ExecuteAsync(null);
        composer.SelectedItem = Item(composer, "Jan Kowalski");

        // Stopped before anything reached the server.
        using var connect = new HeldCall();
        scenario.Transport.BeforeConnect = connect.HoldOnce;
        var seen = Record(composer.Status);
        Task testing;
        try
        {
            testing = composer.SendTestCommand.ExecuteAsync(null);
            Assert.True(await connect.Held.WaitAsync(Limit, Ct));
            composer.Status.CancelCommand.Execute(null);
            Assert.Equal("Stopping…", composer.Status.Current.Message);
        }
        finally
        {
            connect.Release();
        }

        await testing.WaitAsync(Limit, Ct);
        scenario.Transport.BeforeConnect = null;
        const string StoppedText = "Sending the test message was stopped: the message was not sent.";
        Assert.Equal(
            [
                (PageStatusKind.Working, "Sending the test message to operator@contoso.test…"),
                (PageStatusKind.Working, "Stopping…"),
                (PageStatusKind.Attention, StoppedText),
            ],
            seen);
        Assert.Equal("MessageWindow.Status.TestStopped", composer.Status.Current.Log.ToString());
        Assert.Empty(scenario.Transport.Attempts);
        Assert.False(composer.IsBusy);

        // No address for test messages in the settings: nothing is started, and the line says what to do.
        await scenario.Harness.Settings.UpdateAsync(s => s.UserMessages.TestAddress = null, Ct);
        await composer.SendTestCommand.ExecuteAsync(null);
        var missing = composer.Status.Current;
        Assert.Equal(PageStatusKind.Attention, missing.Kind);
        Assert.Equal("The test message was not sent: Set your address for test messages in Settings → E-mail messages.", missing.Message);
        Assert.Equal("UserMessages.TestAddressMissing", missing.Log.ToString());
        Assert.Empty(scenario.Transport.Attempts);
    }

    // ---- The editor ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Pasting_IsTheOperationOfTheEditor_WithStop_AndThePreviewCanBePreparedMeanwhile()
    {
        var images = new HeldImages();
        var (scenario, composer) = await OpenEditorAsync(images);
        using var opened = composer;
        scenario.Harness.Clipboard.Formatted = WithFileImage();
        var seen = Record(composer.Status);
        Task pasting;
        try
        {
            pasting = composer.PasteFormattedCommand.ExecuteAsync(null);
            Assert.True(await images.Call.Held.WaitAsync(Limit, Ct));

            // Working: the operation is the editor's - shown by the window, whose own holder is at rest.
            Assert.True(composer.EditorStatus.IsWorking);
            Assert.False(composer.Status.IsWorking);
            Assert.True(composer.IsPasting);
            var current = composer.Status.Current;
            Assert.Same(composer.EditorStatus.Current, current);
            Assert.Equal(PageStatusKind.Working, current.Kind);
            Assert.Equal("Pasting the formatted content…", current.Message);
            Assert.Equal("UserMessages.Rich.Pasting", current.Log.ToString());
            Assert.Same(composer.EditorStatus.CancelCommand, Assert.IsType<PageStatusAction>(current.Action).Command);

            // The preview is prepared meanwhile: the window's own operation takes the line, the paste goes on.
            await composer.PlanCommand.ExecuteAsync(null);
            Assert.Equal(UserMessageStage.Preview, composer.Stage);
            Assert.True(composer.EditorStatus.IsWorking);
            Assert.True(composer.IsPasting);
            Assert.Equal(PageStatusKind.Idle, composer.Status.Current.Kind);
            Assert.Equal(composer.SummaryText, composer.Status.Current.Message);

            // Back in the editor the paste is what the line says.
            composer.BackCommand.Execute(null);
            Assert.Equal("Pasting the formatted content…", composer.Status.Current.Message);
        }
        finally
        {
            images.Call.Release();
        }

        await pasting.WaitAsync(Limit, Ct);

        // What became of it: pasted, with the remarks listed under the content (the image could not be read - a warning).
        Assert.False(composer.IsPasting);
        Assert.False(composer.EditorStatus.IsWorking);
        var pasted = composer.Status.Current;
        Assert.Equal(PageStatusKind.Attention, pasted.Kind);
        Assert.Equal(composer.RichStatusText, pasted.Message);
        Assert.StartsWith("Pasted: 0 image(s), ", pasted.Message, StringComparison.Ordinal);
        Assert.StartsWith("UserMessages.Rich.Pasted (0, ", pasted.Log.ToString(), StringComparison.Ordinal);
        Assert.True(pasted.CanClose);
        Assert.True(composer.HasPasteProblems);
        Assert.Contains("Dzień dobry", composer.Html, StringComparison.Ordinal);
        Assert.Contains(seen, s => s == (PageStatusKind.Working, "Pasting the formatted content…"));
    }

    [Fact]
    public async Task StopWhilePasting_SaysStoppingAtOnce_AndNothingIsPasted_EvenWhenTheImportFinished()
    {
        var images = new HeldImages();
        var (scenario, composer) = await OpenEditorAsync(images);
        using var opened = composer;
        scenario.Harness.Clipboard.Formatted = WithFileImage();
        var before = composer.Html;
        var seen = Record(composer.Status);
        Task pasting;
        try
        {
            pasting = composer.PasteFormattedCommand.ExecuteAsync(null);
            Assert.True(await images.Call.Held.WaitAsync(Limit, Ct));

            // The Cancel of the paste button and Stop of the line are the editor's command.
            composer.EditorStatus.CancelCommand.Execute(null);
            Assert.True(composer.EditorStatus.IsCancelling);
            Assert.Equal(PageStatusKind.Working, composer.Status.Current.Kind);
            Assert.Equal("Stopping…", composer.Status.Current.Message);
        }
        finally
        {
            // The read returns normally: the import finishes although Stop was pressed.
            images.Call.Release();
        }

        await pasting.WaitAsync(Limit, Ct);

        const string StoppedText = "Pasting was stopped: the content was not changed.";
        Assert.Equal(
            [(PageStatusKind.Working, "Pasting the formatted content…"), (PageStatusKind.Working, "Stopping…"), (PageStatusKind.Attention, StoppedText)],
            seen);
        Assert.Equal("MessageWindow.Status.PasteStopped", composer.Status.Current.Log.ToString());
        Assert.Equal(before, composer.Html);
        Assert.False(composer.HasPasteProblems);
        Assert.False(composer.IsPasting);
        Assert.True(composer.PasteFormattedCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheSmallStepsOfTheEditor_AreResultsOfTheEditor_AndGoWithTheTemplate()
    {
        var scenario = await CreateAsync();
        using var composer = await scenario.OpenAsync([scenario.Company.JanKowalski]);

        // Nothing to paste: why, as an attention.
        scenario.Harness.Clipboard.Formatted = new ClipboardContent(null, null, null, "tylko tekst");
        composer.PasteImageCommand.Execute(null);
        var nothing = composer.Status.Current;
        Assert.Same(composer.EditorStatus.Current, nothing);
        Assert.Equal(PageStatusKind.Attention, nothing.Kind);
        Assert.Equal("Nothing was pasted: The clipboard holds no image.", nothing.Message);
        Assert.Equal("UserMessages.Rich.ClipboardNoImage", nothing.Log.ToString());
        Assert.True(nothing.CanClose);

        // An image at the caret: done.
        scenario.Harness.Clipboard.Formatted = new ClipboardContent(null, null, MessageImageSamples.Png(6, 6, seed: 0x41), null);
        composer.PasteImageCommand.Execute(null);
        var inserted = composer.Status.Current;
        Assert.Equal(PageStatusKind.Done, inserted.Kind);
        Assert.Equal("Image obraz1.png inserted.", inserted.Message);
        Assert.Equal("UserMessages.Rich.ImageInserted", inserted.Log.ToString());
        Assert.Single(composer.TemplateImages);

        // An empty clipboard for "Paste formatted content".
        scenario.Harness.Clipboard.Formatted = null;
        await composer.PasteFormattedCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Attention, composer.Status.Current.Kind);
        Assert.Equal("Nothing was pasted: The clipboard holds nothing that can be pasted.", composer.Status.Current.Message);

        // Another template: what the editor said was about the one before.
        composer.SelectedTemplate = composer.Templates.First(t => t.Value == BuiltInMessageTemplates.PasswordExpiring);
        Assert.Same(PageStatus.Empty, composer.Status.Current);
        Assert.Same(PageStatus.Empty, composer.EditorStatus.Current);
    }

    [Fact]
    public async Task WhatIsTrueOfTheContent_StaysNextToIt_TheTransportThatDoesNotFitIsANote()
    {
        var scenario = await CreateAsync();
        scenario.Watchdog.UserMessagingStatus = FakeWatchdogClient.WithoutInlineImages(scenario.Watchdog.UserMessagingStatus with { Enabled = true });
        await scenario.Harness.Settings.UpdateAsync(s => s.UserMessages.Transport = UserMessageTransportChoice.Watchdog, Ct);
        using var composer = await scenario.OpenAsync([scenario.Company.JanKowalski], BuiltInMessageTemplates.PasswordExpiring, 14);
        var logo = MessageImage.Create(MessageImageSamples.Png(14, 6, seed: 0x75, withMetadata: true), "logo.png");
        scenario.Harness.Clipboard.Formatted = new ClipboardContent(
            CfHtmlFixture.Build($"<html><body><!--StartFragment--><p>Dzień dobry {{givenName}},</p><p><img src=\"{MessageImageSamples.DataUri(logo.Data, "image/png")}\" alt=\"Logo – Dział IT\"></p><!--EndFragment--></body></html>"),
            null,
            null,
            null);
        scenario.Harness.Dialogs.ConfirmAnswers.Enqueue(true);
        await composer.PasteFormattedCommand.ExecuteAsync(null);

        // Pasted without a remark that warns: done - said by the editor.
        Assert.Equal(PageStatusKind.Done, composer.Status.Current.Kind);
        Assert.Equal("Pasted: 1 image(s), 0 note(s) below.", composer.Status.Current.Message);
        Assert.Equal("UserMessages.Rich.Pasted (1, 0)", composer.Status.Current.Log.ToString());
        Assert.False(composer.IsLoadingImages);
        Assert.Equal(string.Empty, composer.ImagesLoadingText);
        Assert.True(composer.ShowTemplateImages);
        Assert.Null(composer.MissingImagesText);

        await composer.PlanCommand.ExecuteAsync(null);
        var summary = composer.SummaryText;
        Assert.Equal(summary, composer.Status.Current.Message);

        // Something the line still says about an earlier attempt (no address for test messages) ...
        await scenario.Harness.Settings.UpdateAsync(s => s.UserMessages.TestAddress = null, Ct);
        composer.SelectedItem = Item(composer, "Jan Kowalski");
        await composer.SendTestCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Attention, composer.Status.Current.Kind);

        // ... is over with the next attempt. This Watchdog takes no images: nothing is sent, and why - with the
        // choices that help - is a note next to the Send button, true for as long as these messages and this
        // transport are shown. The line shows its summary.
        await composer.SendCommand.ExecuteAsync(null);
        Assert.True(composer.IsTransportFitVisible);
        Assert.StartsWith("AdMng Watchdog does not take images", composer.TransportFitText, StringComparison.Ordinal);
        Assert.Equal(PageStatusKind.Idle, composer.Status.Current.Kind);
        Assert.Equal(summary, composer.Status.Current.Message);
        Assert.Equal(UserMessageStage.Preview, composer.Stage);
        Assert.Empty(scenario.Watchdog.UserMessages);
    }

    // ---- The state that lasts, closing, the log ------------------------------------------------------------------

    [Fact]
    public async Task TheEndOfTheSession_IsTheStateThatLasts_AndEndsAPreparationWithoutAWord()
    {
        var scenario = await CreateAsync();
        var company = scenario.Company;
        using var composer = await scenario.OpenAsync([company.JanKowalski, company.LukaszWisniewski], BuiltInMessageTemplates.PasswordExpiring, 14);
        using var read = new HeldCall();
        scenario.Harness.Session.BeforeOperation = operation =>
        {
            if (operation.Kind == FakeOperationKind.Search)
            {
                read.HoldOnce();
            }
        };
        var seen = Record(composer.Status);
        Task planning;
        try
        {
            planning = composer.PlanCommand.ExecuteAsync(null);
            Assert.True(await read.Held.WaitAsync(Limit, Ct));
            await scenario.Harness.Sessions.DisconnectAsync();
            await B2Harness.WaitUntilAsync(() => composer.IsSessionEnded);

            // At once, although the read is still on its way: an error that cannot be closed, and nothing is "busy".
            var ended = composer.Status.Current;
            Assert.Equal(PageStatusKind.Error, ended.Kind);
            Assert.Equal(scenario.Harness.Localizer[L.WindowSession.Ended], ended.Message);
            Assert.Equal("WindowSession.Ended", ended.Log.ToString());
            Assert.False(ended.CanClose);
            Assert.Null(ended.Action);
            Assert.False(composer.Status.IsWorking);
            Assert.False(composer.IsBusy);
            Assert.False(composer.IsPlanning);
        }
        finally
        {
            read.Release();
        }

        await planning.WaitAsync(Limit, Ct);
        scenario.Harness.Session.BeforeOperation = null;

        // The preparation ended without a word of its own, and "busy" ended with it.
        Assert.DoesNotContain(seen, s => s.Kind == PageStatusKind.Attention);
        Assert.Equal(PageStatusKind.Error, composer.Status.Current.Kind);
        Assert.False(composer.IsBusy);
        Assert.False(composer.IsPlanning);
        Assert.False(composer.PlanCommand.CanExecute(null));
        Assert.Equal(UserMessageStage.Compose, composer.Stage);

        // The state follows the language.
        scenario.Harness.Localizer.SetCulture(Polish);
        Assert.Equal(scenario.Harness.Localizer[L.WindowSession.Ended], composer.Status.Current.Message);
        Assert.StartsWith("Sesja, w której otwarto to okno", composer.Status.Current.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClosingTheWindow_EndsWhatRuns_WithoutAWord()
    {
        var scenario = await CreateAsync();
        var composer = await scenario.OpenAsync([scenario.Company.JanKowalski], BuiltInMessageTemplates.PasswordExpiring, 14);
        using var read = new HeldCall();
        scenario.Harness.Session.BeforeOperation = operation =>
        {
            if (operation.Kind == FakeOperationKind.Search)
            {
                read.HoldOnce();
            }
        };
        var seen = Record(composer.Status);
        Task planning;
        try
        {
            planning = composer.PlanCommand.ExecuteAsync(null);
            Assert.True(await read.Held.WaitAsync(Limit, Ct));
            composer.Dispose();
            Assert.False(composer.Status.IsWorking);
            Assert.Null(composer.Status.Front);
        }
        finally
        {
            read.Release();
        }

        await planning.WaitAsync(Limit, Ct);
        Assert.Equal([(PageStatusKind.Working, "Reading the accounts (1) and preparing the messages…"), (PageStatusKind.Idle, string.Empty)], seen);
        Assert.Equal(UserMessageStage.Compose, composer.Stage);
        Assert.Empty(composer.AllItems);
    }

    [Fact]
    public async Task TheLogCopies_CarryKeysAndNumbers_NeverAnAddressANameTheContentOrThePassword()
    {
        var scenario = await CreateAsync();
        var harness = scenario.Harness;
        var company = scenario.Company;
        await harness.Settings.UpdateAsync(s =>
        {
            s.UserMessages.Smtp.Authentication = SmtpAuthentication.Basic;
            s.UserMessages.Smtp.UserName = "helpdesk@contoso.test";
        }, Ct);
        var logger = new ListLogger<PageStatusHolder>();
        var lists = new ObjectListServices(harness.Sessions, harness.Localizer, harness.Dispatcher, harness.Settings, harness.Errors, harness.Changes, harness.Time, logger);
        var services = new DialogServices(
            harness.Sessions, harness.Localizer, harness.Dispatcher, harness.Settings, harness.Errors, harness.Changes, harness.Time, harness.Dialogs, harness.Files,
            harness.Clipboard, harness.Notifications, harness.Coordinator, harness.Activities, lists, harness.Regional);
        using var composer = new UserMessageComposerViewModel(
            services, scenario.Services, new UserMessageRequest([company.JanKowalski, company.LukaszWisniewski], BuiltInMessageTemplates.PasswordExpiring, 14));
        await composer.InitializeAsync();
        composer.SmtpPasswordInput = new TestPasswordInput(SmtpPassword);
        scenario.Transport.Script("lukasz.wisniewski@contoso.test", FakeUserMessageTransport.Rejected(550, "5.1.1"));

        composer.PasteImageCommand.Execute(null);
        await composer.PlanCommand.ExecuteAsync(null);
        composer.SelectedItem = Item(composer, "Jan Kowalski");
        await composer.SendTestCommand.ExecuteAsync(null);
        harness.Dialogs.ConfirmAnswers.Enqueue(true);
        await composer.SendCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Attention, composer.Status.Current.Kind);

        var messages = logger.Entries.Select(entry => entry.Message).ToList();
        Assert.Contains(messages, m => m.Contains("UserMessages.Rich.ClipboardNoImage", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("MessageWindow.Status.Reading (2)", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("MessageWindow.Status.TestSending", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("UserMessages.TestSent", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("MessageWindow.Status.Sending (0, 2, 0)", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("MessageWindow.Status.SentPartly (1, 1, 0)", StringComparison.Ordinal));

        // One log entry for the whole send, whatever it reported meanwhile.
        Assert.Single(messages, m => m.Contains("MessageWindow.Status.Sending", StringComparison.Ordinal));
        Assert.DoesNotContain(SmtpPassword, logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("@", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("contoso", logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Kowalski", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Wiśniewski", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Hasło", logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("550", logger.AllText, StringComparison.Ordinal);
    }

    // ---- Helpers -------------------------------------------------------------------------------------------------

    /// <summary>A message window in the editor whose pasted <c>file:</c> images are read through <paramref name="images"/>.</summary>
    private static async Task<(MessageScenario Scenario, UserMessageComposerViewModel Composer)> OpenEditorAsync(IPastedImageSource images)
    {
        var scenario = await CreateAsync();
        var harness = scenario.Harness;
        var services = new UserMessageServices(
            new UserMessageTemplateLibrary(harness.Settings, harness.Localizer),
            scenario.Services.Transports,
            new CsvCodec(),
            harness.AuditLog,
            harness.Time,
            (delay, _) =>
            {
                harness.Time.Advance(delay);
                return Task.CompletedTask;
            },
            pastedImages: images);
        var factory = new DialogViewModelFactory(
            harness.Services, harness.Engines, harness.Views, harness.AuditLog, new AuditLogExporter(new CsvCodec()), harness.PowerShell,
            harness.Theme, messages: scenario.Launcher, userMessages: services);
        var composer = factory.CreateUserMessageComposer(new UserMessageRequest([scenario.Company.JanKowalski]));
        await composer.InitializeAsync();
        composer.Subject = "Przerwa serwisowa w sobotę";
        return (scenario, composer);
    }

    /// <summary>Formatted clipboard content with one image in a local file (which the image source is asked for).</summary>
    private static ClipboardContent WithFileImage() => new(
        CfHtmlFixture.Build("<html><body><!--StartFragment--><p>Dzień dobry {givenName},</p><p><img src=\"file:///C:/Users/JKOWAL~1/AppData/Local/Temp/msohtmlclip1/01/clip_image001.png\" alt=\"Schemat\"></p><!--EndFragment--></body></html>"),
        null,
        null,
        null);

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

    /// <summary>The sequence without repeats of the same text (a send reports after every message and once more at its end).</summary>
    private static List<(PageStatusKind Kind, string Message)> Distinct(List<(PageStatusKind Kind, string Message)> seen)
    {
        lock (seen)
        {
            var result = new List<(PageStatusKind Kind, string Message)>();
            foreach (var entry in seen)
            {
                if (result.Count == 0 || result[^1] != entry)
                {
                    result.Add(entry);
                }
            }

            return result;
        }
    }

    /// <summary>A call the test holds once and releases deliberately: what a slow server is.</summary>
    private sealed class HeldCall : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private int _hold = 1;

        /// <summary>Signalled when the call arrived and waits.</summary>
        public SemaphoreSlim Held { get; } = new(0);

        /// <summary>Called by the code under test: the first call waits for <see cref="Release"/>, later ones pass.</summary>
        public void HoldOnce()
        {
            if (Interlocked.Exchange(ref _hold, 0) == 1)
            {
                Held.Release();
                _release.Wait(HoldLimit);
            }
        }

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _release.Dispose();
            Held.Dispose();
        }
    }

    /// <summary>
    /// An image source whose first read waits for the test and then answers that the file cannot be read - without
    /// looking at the cancellation, like a call that is already on its way.
    /// </summary>
    private sealed class HeldImages : IPastedImageSource
    {
        public HeldCall Call { get; } = new();

        public Task<PastedImageRead> ReadAsync(string fileUri, int maxBytes, CancellationToken cancellationToken) => Task.Run(
            () =>
            {
                Call.HoldOnce();
                return new PastedImageRead(null, "clip_image001.png", PastedImageFailure.NotFound);
            },
            CancellationToken.None);
    }
}
