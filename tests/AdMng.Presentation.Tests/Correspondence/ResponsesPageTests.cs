using System.Globalization;
using AdMng.Core.Correspondence.Outlook;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Services;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Correspondence;

/// <summary>
/// "5 · Odpowiedzi" (design note 6.4, 7.5): the campaigns of "Elementy wysłane" since a day, the votes of one campaign with
/// the mailboxes they concern, yes/no by the voting options, the summary, the CSV export and Outlook failures.
/// </summary>
public sealed class ResponsesPageTests
{
    private const string CampaignId = "4f1c2a9e0b7d4e6f8a3b5c7d9e1f2a3b";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Campaigns_SinceTheChosenDay_AndTheVotesOfOne()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        AddSample(scenario.Outlook);
        using var window = await scenario.OpenAsync();
        var responses = window.Responses;
        Assert.Equal(new DateTime(2026, 7, 15), responses.Since);
        Assert.False(responses.LoadResponsesCommand.CanExecute(null));
        Assert.False(responses.ExportCommand.CanExecute(null));

        await responses.LoadCampaignsCommand.ExecuteAsync(null);

        var campaign = Assert.Single(responses.Campaigns);
        Assert.Equal($"{DayText(10, 1)} · Czy skrzynka pocztowa jest nadal potrzebna? · 4 wiad.", campaign.Text);
        Assert.Same(campaign, responses.SelectedCampaign);
        Assert.Equal("Znalezione wysyłki: 1 – wybierz jedną i odczytaj odpowiedzi.", responses.SummaryText);
        Assert.True(responses.LoadResponsesCommand.CanExecute(null));

        await responses.LoadResponsesCommand.ExecuteAsync(null);

        Assert.Equal(
            [
                ("jan.kowalski@contoso.test", "sekretariat.it@contoso.test, rekrutacja@contoso.test", "Tak, nadal potrzebna", TimeText(Day(10, 2, 8, 15)), ResponseKind.Yes),
                ("anna.nowak@contoso.test", "biuro.projektu@contoso.test", "Nie, można usunąć", TimeText(Day(10, 2, 9, 40)), ResponseKind.No),
                ("piotr.wisniewski@contoso.test", "archiwum@contoso.test", "brak odpowiedzi", "", ResponseKind.None),
                ("ewa.zielinska@contoso.test", "kadry@contoso.test", "Proszę o kontakt telefoniczny", TimeText(Day(10, 3, 14, 5)), ResponseKind.Other),
            ],
            responses.Rows.Select(r => (r.Recipient, r.KeysText, r.Response, r.RespondedAtText, r.Kind)));
        Assert.Equal("Odpowiedzi: 3 z 4 · Tak: 1 · Nie: 1 · inne: 1", responses.SummaryText);
        Assert.True(responses.ExportCommand.CanExecute(null));

        responses.Since = new DateTime(2026, 5, 1);
        await responses.LoadCampaignsCommand.ExecuteAsync(null);
        Assert.Equal(2, responses.Campaigns.Count);
        Assert.Empty(responses.Rows);
    }

    [Fact]
    public async Task TheVotingOptionsOfTheMessage_DecideYesAndNo()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        scenario.Outlook.Campaigns.Add(new OutlookCampaign(CampaignId, "Zaproszenie na konferencję", Day(10, 1, 9, 0), Day(10, 1, 9, 5), 2));
        scenario.Outlook.Responses[CampaignId] =
        [
            Vote("urzad@andrespol.pl", "Będę", ["urzad@andrespol.pl"]),
            Vote("ug@brojce.pl", "nie będę ", []),
        ];
        using var window = await scenario.OpenAsync();
        window.Message.VotingOptions = "Będę; Nie będę";

        window.SelectedTab = CorrespondenceTab.Responses;
        await window.Responses.LoadCampaignsCommand.ExecuteAsync(null);
        await window.Responses.LoadResponsesCommand.ExecuteAsync(null);

        Assert.Equal("Będę;Nie będę", window.Responses.VotingOptions);
        Assert.Equal([ResponseKind.Yes, ResponseKind.No], window.Responses.Rows.Select(r => r.Kind));
        Assert.Equal("Odpowiedzi: 2 z 2 · Tak: 1 · Nie: 1", window.Responses.SummaryText);
    }

    [Theory]
    [InlineData("Tak, nadal potrzebna", "Tak;Nie", ResponseKind.Yes)]
    [InlineData("tak", "Tak;Nie", ResponseKind.Yes)]
    [InlineData("Nie", "Tak;Nie", ResponseKind.No)]
    [InlineData("Nie, można usunąć", "Będę;Nie będę", ResponseKind.No)]
    [InlineData("Może", "Tak;Nie", ResponseKind.Other)]
    [InlineData(" ", "Tak;Nie", ResponseKind.None)]
    [InlineData(null, "Tak;Nie", ResponseKind.None)]
    public void KindOf_FirstOptionIsYes_SecondIsNo(string? response, string options, ResponseKind expected) =>
        Assert.Equal(expected, ResponsesPageViewModel.KindOf(response, options));

    [Fact]
    public async Task Export_WritesTheRows_WithFormulasProtected()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        scenario.Outlook.Campaigns.Add(new OutlookCampaign(CampaignId, "Ankieta", Day(10, 1, 9, 0), Day(10, 1, 9, 0), 2));
        scenario.Outlook.Responses[CampaignId] =
        [
            Vote("jan.kowalski@contoso.test", "Tak, nadal potrzebna", ["sekretariat.it@contoso.test"]),
            Vote("zofia.żak@contoso.test", "=HYPERLINK(\"http://zly.example\")", []),
        ];
        using var window = await scenario.OpenAsync();
        await window.Responses.LoadCampaignsCommand.ExecuteAsync(null);
        await window.Responses.LoadResponsesCommand.ExecuteAsync(null);
        Directory.CreateDirectory(scenario.Paths.SettingsDirectory);
        var file = Path.Combine(scenario.Paths.SettingsDirectory, "odpowiedzi.csv");
        scenario.Harness.Files.NextSavePath = file;

        await window.Responses.ExportCommand.ExecuteAsync(null);

        Assert.Equal(ResponsesPageViewModel.ExportFileName, scenario.Harness.Files.LastDefaultFileName);
        var lines = await File.ReadAllLinesAsync(file, Token);
        Assert.Equal("Odbiorca;Skrzynki;Odpowiedź;Rodzaj odpowiedzi;Kiedy;Wysłano", lines[0]);
        Assert.Equal(
            $"jan.kowalski@contoso.test;sekretariat.it@contoso.test;Tak, nadal potrzebna;tak;{TimeText(Day(10, 2, 7, 0))};{TimeText(Day(10, 1, 9, 0))}",
            lines[1]);
        Assert.Contains("'=HYPERLINK", lines[2], StringComparison.Ordinal);
        Assert.Contains("inna", lines[2], StringComparison.Ordinal);
        Assert.Contains((NotificationSeverity.Success, $"Zapisano odpowiedzi: {file}"), scenario.Harness.Notifications.Items);
    }

    [Fact]
    public async Task Failures_AndNoCampaigns_AreShownInTheSummary()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenAsync();

        await window.Responses.LoadCampaignsCommand.ExecuteAsync(null);
        Assert.Equal("Nie znaleziono w „Elementach wysłanych” wysyłek Korespondencji od 15.07.2026.", window.Responses.SummaryText);
        Assert.Empty(window.Responses.Campaigns);

        scenario.Outlook.ReadFailure = FakeOutlookClient.Failure(OutlookFailureKind.Busy, "campaigns");
        await window.Responses.LoadCampaignsCommand.ExecuteAsync(null);
        Assert.StartsWith("Nie udało się odczytać odpowiedzi: Outlook jest zajęty", window.Responses.SummaryText, StringComparison.Ordinal);
        Assert.False(window.Responses.IsLoading);
    }

    [Fact]
    public async Task WithoutOutlook_ResponsesCannotBeRead()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync(withOutlook: false);
        using var window = await scenario.OpenAsync();

        Assert.False(window.Responses.LoadCampaignsCommand.CanExecute(null));
        Assert.False(window.Responses.LoadResponsesCommand.CanExecute(null));
    }

    private static void AddSample(FakeOutlookClient outlook)
    {
        outlook.Campaigns.Add(new OutlookCampaign(CampaignId, "Czy skrzynka pocztowa jest nadal potrzebna?", Day(10, 1, 9, 0), Day(10, 1, 10, 0), 4));
        outlook.Campaigns.Add(new OutlookCampaign("0aa0bb0cc0dd0ee0ff00112233445566", "Stara wysyłka", Day(6, 1, 9, 0), Day(6, 1, 9, 0), 10));
        outlook.Responses[CampaignId] =
        [
            Vote("jan.kowalski@contoso.test", "Tak, nadal potrzebna", ["sekretariat.it@contoso.test", "rekrutacja@contoso.test"], Day(10, 2, 8, 15)),
            Vote("anna.nowak@contoso.test", "Nie, można usunąć", ["biuro.projektu@contoso.test"], Day(10, 2, 9, 40)),
            Vote("piotr.wisniewski@contoso.test", null, ["archiwum@contoso.test"]),
            Vote("ewa.zielinska@contoso.test", "Proszę o kontakt telefoniczny", ["kadry@contoso.test"], Day(10, 3, 14, 5)),
        ];
    }

    private static OutlookVoteResponse Vote(string recipient, string? response, IReadOnlyList<string> keys, DateTimeOffset? at = null) =>
        new(recipient, response, response is null ? null : at ?? Day(10, 2, 7, 0), Day(10, 1, 9, 0), "Ankieta", keys);

    private static DateTimeOffset Day(int month, int day, int hour, int minute) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    /// <summary>A day as the page shows it (the short date of pl-PL, which differs between Windows and ICU).</summary>
    private static string DayText(int month, int day) => new DateTime(2026, month, day).ToString("d", Polish);

    /// <summary>A moment as the page shows it (the harness clock is in UTC).</summary>
    private static string TimeText(DateTimeOffset at) => at.UtcDateTime.ToString("g", Polish);

    private static CultureInfo Polish => CultureInfo.GetCultureInfo("pl-PL");
}
