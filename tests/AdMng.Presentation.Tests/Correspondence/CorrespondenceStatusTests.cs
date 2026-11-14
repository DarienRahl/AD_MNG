using AdMng.Core.Correspondence.Outlook;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Tests.Dialogs;
using static AdMng.Presentation.Tests.Correspondence.PageTests;

namespace AdMng.Presentation.Tests.Correspondence;

/// <summary>
/// The one status line of the Korespondencja window (track V, stage V2b-4, unit 5; docs/dev/v-interface-renewal.md "V2b-4 as
/// built"): the window puts the page of the tab that is shown in front of its own, says what concerns every page - the journal
/// "already sent" - and leaves the session and Outlook to the pages that need them. The pages have their own files
/// (<see cref="RecipientsStatusTests"/>, <see cref="MailboxesStatusTests"/>, <see cref="SendStatusTests"/>,
/// <see cref="ResponsesStatusTests"/>, <see cref="PreviewStatusTests"/>).
/// </summary>
public sealed class CorrespondenceStatusTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheLine_SaysWhatTheTabInFrontSays_AndTheMessageTabHasNothingToSay()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        MailboxSample(scenario);
        using var window = await scenario.OpenAsync();
        Assert.IsAssignableFrom<IHasPageStatus>(window);
        Assert.Same(window.Recipients.Status, window.Status.Front);

        window.ShowMailboxesCommand.Execute(null);
        Assert.Same(window.Mailboxes.Status, window.Status.Front);
        Assert.Equal(window.Mailboxes.SummaryText, window.Status.Current.Message);

        window.SelectedTab = CorrespondenceTab.Message;
        Assert.Null(window.Status.Front);
        Assert.Equal((PageStatusKind.Idle, string.Empty), (window.Status.Current.Kind, window.Status.Current.Message));

        window.SelectedTab = CorrespondenceTab.Preview;
        Assert.Same(window.Preview.Status, window.Status.Front);
        window.SelectedTab = CorrespondenceTab.Send;
        Assert.Same(window.Send.Status, window.Status.Front);
        window.SelectedTab = CorrespondenceTab.Responses;
        Assert.Same(window.Responses.Status, window.Status.Front);
        window.SelectedTab = CorrespondenceTab.Recipients;
        Assert.Equal(window.Recipients.CountsText, window.Status.Current.Message);
    }

    [Fact]
    public async Task AWindowThatStartsOnTheMailboxTab_HasThatPageInFrontFromTheFirstMoment()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        using var window = scenario.Factory.Create(new CorrespondenceRequest([], CorrespondenceStart.MailboxOwners));

        // Before the window is initialized: the tab is the one the property starts with, so no change was raised for it.
        Assert.Equal(CorrespondenceTab.Mailboxes, window.SelectedTab);
        Assert.Same(window.Mailboxes.Status, window.Status.Front);
        await window.InitializeAsync();
    }

    [Fact]
    public async Task ATabThatSaidSomethingWhileNotShown_SaysItAgainWhenItComesToTheFront()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        using var window = await scenario.OpenAsync();
        window.Recipients.RemoveSelectedCommand.Execute(null);      // an attention of the recipients page
        window.SelectedTab = CorrespondenceTab.Send;
        Assert.NotEqual(PageStatusKind.Attention, window.Status.Current.Kind);

        window.SelectedTab = CorrespondenceTab.Recipients;

        Assert.Equal(PageStatusKind.Attention, window.Status.Current.Kind);
        Assert.Equal(window.Recipients.StatusText, window.Status.Current.Message);
    }

    [Fact]
    public async Task AJournalThatCannotBeUsed_IsTheStateOfTheWindow_ItDoesNotCoverWhatAPageSays_AndFollowsTheLanguage()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        scenario.Protector.Failure = new PlatformNotSupportedException("brak DPAPI");

        using var window = await scenario.OpenAsync();

        var state = window.Status.Current;
        Assert.Equal(PageStatusKind.Attention, state.Kind);
        Assert.False(state.CanClose);
        Assert.Equal(window.Error, state.Message);
        Assert.Equal(
            "Dziennika „już wysłane” nie można użyć (na tym komputerze nie można zabezpieczyć jego klucza) – wysłane adresy nie będą pomijane przy kolejnych wysyłkach.",
            state.Message);
        Assert.Equal("Correspondence.JournalUnavailable", state.Log.ToString());
        Assert.Same(state, window.Status.Lasting);

        // A page that says something is not covered by it: it said it later.
        window.Recipients.RemoveSelectedCommand.Execute(null);
        Assert.Equal(PageStatusKind.Attention, window.Status.Current.Kind);
        Assert.Equal(window.Recipients.StatusText, window.Status.Current.Message);
        window.Recipients.Status.CloseCommand.Execute(null);

        // Both languages, at once.
        scenario.Harness.Localizer.SetCulture(English);
        Assert.StartsWith("The \"already sent\" journal cannot be used", window.Status.Current.Message, StringComparison.Ordinal);
        Assert.Contains("its key cannot be protected on this computer", window.Status.Current.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARestartedJournal_IsAResultThatCanBeClosed_AndItsLogCopyHasNoPath()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        await scenario.Journal.LoadAsync(TimeSpan.FromDays(365), Ct);
        await scenario.Journal.AppendAsync("jan.kowalski@contoso.test", "Temat", Ct);
        await System.IO.File.WriteAllBytesAsync(scenario.Journal.KeyPath, [0, 1, 2], Ct);

        using var window = await scenario.OpenAsync();

        // The page in front has only its counts to say: the result of the window shows, on every tab.
        var result = window.Status.Current;
        Assert.Equal(PageStatusKind.Attention, result.Kind);
        window.SelectedTab = CorrespondenceTab.Message;
        Assert.Equal(result.Message, window.Status.Current.Message);
        Assert.True(result.CanClose);
        Assert.StartsWith("Klucza dziennika „już wysłane” nie udało się odczytać", result.Message, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(scenario.Journal.Folder, "journal-"), result.Message, StringComparison.Ordinal);
        Assert.Equal("Correspondence.JournalReset", result.Log.ToString());
        Assert.Equal(result.Message, window.StatusText);
        Assert.Null(window.Error);

        window.Status.CloseCommand.Execute(null);
        Assert.Equal(PageStatusKind.Idle, window.Status.Current.Kind);
    }

    [Fact]
    public async Task TheSessionEnding_IsNotSaidByTheWindow_ASendThatGoesOnKeepsTheLine()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenWithMessageAsync();
        CorrespondencePagesScenario.AddRows(window, CorrespondencePagesScenario.Offices());
        window.SelectedTab = CorrespondenceTab.Send;
        using var entered = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        scenario.Outlook.BeforeSend = (_, _) =>
        {
            entered.Release();
            release.Wait(TimeSpan.FromSeconds(30));
        };
        scenario.Ui.ConfirmAnswers.Enqueue(true);
        var run = window.Send.SendCommand.ExecuteAsync(null);
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(20), Ct));

        // The session ends while the messages are sent: the directory is off, the run goes on and the line says how far it is.
        await scenario.Harness.Sessions.DisconnectAsync();
        await B2Harness.WaitUntilAsync(() => window.IsSessionEnded);

        Assert.Equal(PageStatusKind.Working, window.Status.Current.Kind);
        Assert.StartsWith("Wysyłam wiadomości: ", window.Status.Current.Message, StringComparison.Ordinal);
        Assert.Null(window.Status.Lasting);
        Assert.NotNull(window.Recipients.Status.Lasting);
        window.SelectedTab = CorrespondenceTab.Recipients;
        Assert.StartsWith("Sesja, w której otwarto to okno, została zakończona", window.Status.Current.Message, StringComparison.Ordinal);
        window.SelectedTab = CorrespondenceTab.Send;

        release.Release(3);
        await run.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.Equal(PageStatusKind.Done, window.Status.Current.Kind);
        Assert.Equal(3, scenario.Outlook.Items.Count);
    }

    [Fact]
    public async Task TheWindowThatIsClosed_LetsGoOfThePageInFront()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        var window = await scenario.OpenAsync();
        Assert.NotNull(window.Status.Front);

        window.Dispose();

        Assert.Null(window.Status.Front);
    }

    [Fact]
    public async Task WithoutClassicOutlook_TheRecipientsAndTheMailboxesSayNothingOfIt()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        scenario.Outlook.Availability = OutlookAvailability.NotInstalled;

        using var window = await scenario.OpenAsync();

        Assert.Null(window.Recipients.Status.Lasting);
        Assert.Null(window.Mailboxes.Status.Lasting);
        Assert.Null(window.Status.Lasting);
        Assert.Equal("Outlook: brak klasycznego Outlooka", window.Outlook.StateText);   // the footer keeps the state of the connection
    }
}
