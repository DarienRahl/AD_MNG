using AdMng.Core.Correspondence.Outlook;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Presentation.Tests.GroupPolicy;
using static AdMng.Presentation.Tests.Correspondence.PageTests;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Correspondence;

/// <summary>
/// The status line of "5 · Odpowiedzi" (track V, stage V2b-4, unit 5; docs/dev/v-interface-renewal.md "V2b-4 as built"): the
/// two reads of Sent Items are the operations of the page, with Stop; what they found is the summary; why a read failed or
/// that it was stopped is a result with Retry; that Outlook cannot be used is the state of the page. How the votes are read
/// and classified is covered, unchanged, by <see cref="ResponsesPageTests"/>.
/// </summary>
public sealed class ResponsesStatusTests
{
    private const string CampaignId = "4f1c2a9e0b7d4e6f8a3b5c7d9e1f2a3b";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void AddSample(FakeOutlookClient outlook)
    {
        var sent = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        outlook.Campaigns.Add(new OutlookCampaign(CampaignId, "Czy skrzynka jest nadal potrzebna?", sent, sent.AddMinutes(5), 3));
        outlook.Responses[CampaignId] =
        [
            new OutlookVoteResponse("jan.kowalski@contoso.test", "Tak, nadal potrzebna", sent.AddHours(1), sent, "Czy skrzynka jest nadal potrzebna?", ["sekretariat@contoso.test"]),
            new OutlookVoteResponse("anna.nowak@contoso.test", "Nie, można usunąć", sent.AddHours(2), sent, "Czy skrzynka jest nadal potrzebna?", ["biuro@contoso.test"]),
            new OutlookVoteResponse("ewa.zielinska@contoso.test", null, null, sent, "Czy skrzynka jest nadal potrzebna?", ["kadry@contoso.test"]),
        ];
    }

    /// <summary>Holds the next read of Outlook on a pool thread until the test lets it go.</summary>
    private static (SemaphoreSlim Entered, SemaphoreSlim Release) Hold(FakeOutlookClient outlook)
    {
        var entered = new SemaphoreSlim(0);
        var release = new SemaphoreSlim(0);
        outlook.BeforeRead = () =>
        {
            entered.Release();
            release.Wait(TimeSpan.FromSeconds(30));
        };
        return (entered, release);
    }

    [Fact]
    public async Task TheTwoReads_AreOperationsOfThePage_AndWhatTheyFoundIsTheSummary()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        AddSample(scenario.Outlook);
        using var window = await scenario.OpenAsync();
        window.SelectedTab = CorrespondenceTab.Responses;
        var responses = window.Responses;
        var line = new StatusRecorder(window.Status);
        Assert.Same(responses.Status, window.Status.Front);
        Assert.Equal(PageStatusKind.Idle, window.Status.Current.Kind);

        await responses.LoadCampaignsCommand.ExecuteAsync(null);
        await responses.LoadResponsesCommand.ExecuteAsync(null);

        // Working with Stop, then - the content is the result - the summary, never a message of its own.
        Assert.Equal(
        [
            (PageStatusKind.Working, "Przeszukuję „Elementy wysłane”…"),
            (PageStatusKind.Idle, "Znalezione wysyłki: 1 – wybierz jedną i odczytaj odpowiedzi."),
            (PageStatusKind.Working, "Odczytuję odpowiedzi…"),
            (PageStatusKind.Idle, "Odpowiedzi: 2 z 3 · Tak: 1 · Nie: 1"),
        ], line.Plain);
        Assert.Equal("Odpowiedzi: 2 z 3 · Tak: 1 · Nie: 1", responses.SummaryText);
        Assert.False(responses.Status.IsWorking);
        Assert.False(responses.IsReadingCampaigns || responses.IsReadingResponses);

        // Both languages.
        scenario.Harness.Localizer.SetCulture(English);
        Assert.Equal("Odpowiedzi: 2 z 3 · Tak: 1 · Nie: 1", window.Status.Current.Message);   // a summary keeps the language it was said in
    }

    [Fact]
    public async Task EachReadHasItsOwnFlag_AndTheWorkingTextsAndLogCopiesHaveNoNameInThem()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        AddSample(scenario.Outlook);
        using var window = await scenario.OpenAsync();
        window.SelectedTab = CorrespondenceTab.Responses;
        var responses = window.Responses;
        var flags = new List<(bool Campaigns, bool Responses, string Text)>();
        scenario.Outlook.BeforeRead = () => flags.Add((responses.IsReadingCampaigns, responses.IsReadingResponses, window.Status.Current.Log.ToString()));
        var logs = new List<string>();
        responses.Status.PropertyChanged += (_, _) => logs.Add(responses.Status.Current.Log.ToString());

        await responses.LoadCampaignsCommand.ExecuteAsync(null);
        await responses.LoadResponsesCommand.ExecuteAsync(null);

        Assert.Equal(
        [
            (true, false, "Correspondence.Responses.Searching"),
            (false, true, "Correspondence.Responses.Reading"),
        ], flags);
        Assert.All(logs, log => Assert.DoesNotContain("@", log, StringComparison.Ordinal));
        Assert.All(logs, log => Assert.DoesNotContain("skrzynka", log, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Stop_SaysStoppingAtOnce_AndAnAnswerThatComesAnywayIsNotShown()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        AddSample(scenario.Outlook);
        using var window = await scenario.OpenAsync();
        window.SelectedTab = CorrespondenceTab.Responses;
        var responses = window.Responses;
        var (entered, release) = Hold(scenario.Outlook);
        using var _ = entered;
        using var __ = release;
        var run = Task.Run(() => responses.LoadCampaignsCommand.ExecuteAsync(null), Ct);
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(20), Ct));
        Assert.Equal(PageStatusKind.Working, window.Status.Current.Kind);
        Assert.Equal("Zatrzymaj", window.Status.Current.Action?.Label);
        var line = new StatusRecorder(window.Status);

        window.Status.Current.Action!.Command.Execute(null);
        Assert.True(responses.Status.IsCancelling);
        Assert.Equal((PageStatusKind.Working, "Zatrzymywanie…"), (window.Status.Current.Kind, window.Status.Current.Message));

        release.Release();
        await run.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        var stopped = window.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Equal("Przeszukiwanie Elementów wysłanych zatrzymano: lista wysyłek nie została zmieniona.", stopped.Message);
        Assert.Equal("Correspondence.Responses.Status.CampaignsStopped", stopped.Log.ToString());
        Assert.True(stopped.CanClose);
        Assert.Equal("Ponów", stopped.Action?.Label);
        Assert.Same(responses.LoadCampaignsCommand, stopped.Action?.Command);
        Assert.Empty(responses.Campaigns);   // the answer came in, but the operator had stopped: the list is as it was
        Assert.False(responses.IsLoading);
        Assert.Equal((PageStatusKind.Working, "Zatrzymywanie…"), (line.Seen[0].Kind, line.Seen[0].Message));

        // Retry reads again, to the end.
        scenario.Outlook.BeforeRead = null;
        stopped.Action!.Command.Execute(null);
        await B2Harness.WaitUntilAsync(() => responses.Campaigns.Count == 1);
        Assert.Equal(PageStatusKind.Idle, window.Status.Current.Kind);
    }

    [Fact]
    public async Task AFailedRead_IsAnErrorWithRetry_TheKindOfTheFailureIsItsCode_AndTheListStaysAsItWas()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        AddSample(scenario.Outlook);
        using var window = await scenario.OpenAsync();
        window.SelectedTab = CorrespondenceTab.Responses;
        var responses = window.Responses;
        await responses.LoadCampaignsCommand.ExecuteAsync(null);
        scenario.Outlook.ReadFailure = FakeOutlookClient.Failure(OutlookFailureKind.NotResponding);

        await responses.LoadResponsesCommand.ExecuteAsync(null);

        var error = window.Status.Current;
        Assert.Equal(PageStatusKind.Error, error.Kind);
        Assert.Equal(
            "Nie udało się odczytać odpowiedzi: Outlook nie odpowiada – sprawdź, czy nie czeka na hasło albo wybór profilu. Wybierz Ponów, aby spróbować ponownie. (NotResponding)",
            error.Message);
        Assert.Equal("Correspondence.Responses.Status.ResponsesFailed [NotResponding]", error.Log.ToString());
        Assert.True(error.CanClose);
        Assert.Equal("Ponów", error.Action?.Label);
        Assert.Same(responses.LoadResponsesCommand, error.Action?.Command);
        Assert.Single(responses.Campaigns);                       // what was shown stays
        Assert.StartsWith("Nie udało się odczytać odpowiedzi: ", responses.SummaryText, StringComparison.Ordinal);   // the old text stays
        Assert.Equal(OutlookState.Failed, window.Outlook.State);  // a lost Outlook is reported, so that the next read connects again

        // Closed: Outlook cannot be used is what the page says - the state, with its own way out.
        responses.Status.CloseCommand.Execute(null);
        Assert.Equal(PageStatusKind.Attention, responses.Status.Current.Kind);
        Assert.StartsWith("Outlook jest niedostępny: ", responses.Status.Current.Message, StringComparison.Ordinal);
        Assert.Equal("Połącz z Outlookiem", responses.Status.Current.Action?.Label);
    }

    [Fact]
    public async Task APressWithoutOutlook_SaysSoAsAnError_NotAsASummary()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        scenario.Outlook.ConnectFailure = FakeOutlookClient.Failure(OutlookFailureKind.NotResponding, "connect");
        using var window = await scenario.OpenAsync();
        window.SelectedTab = CorrespondenceTab.Responses;

        await window.Responses.LoadCampaignsCommand.ExecuteAsync(null);

        var error = window.Status.Current;
        Assert.Equal(PageStatusKind.Error, error.Kind);
        Assert.StartsWith("Nie udało się przeszukać Elementów wysłanych: Nie można użyć Outlooka: Outlook nie odpowiada", error.Message, StringComparison.Ordinal);
        Assert.EndsWith("(NotResponding)", error.Message, StringComparison.Ordinal);
        Assert.Equal("Correspondence.Responses.Status.CampaignsFailed [NotResponding]", error.Log.ToString());
        Assert.False(window.Responses.IsLoading);
        Assert.False(window.Responses.IsReadingCampaigns);
    }
}
