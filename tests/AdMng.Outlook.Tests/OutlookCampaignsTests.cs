using AdMng.Core.Correspondence.Outlook;
using AdMng.Outlook.Tests.FakeOutlook;

namespace AdMng.Outlook.Tests;

/// <summary>Campaigns and votes in "Elementy wysłane" (design note 4.4).</summary>
public class OutlookCampaignsTests
{
    private const string CampaignA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string CampaignB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Never = "4501-01-01";

    private static CancellationToken Ct => OutlookHarness.Ct;

    [Fact]
    public void CampaignsFilter_IsTheDaslOfTheDesignNote_InUtc()
    {
        var filter = OutlookCampaigns.CampaignsFilter(new DateTimeOffset(2026, 7, 30, 9, 15, 40, TimeSpan.FromHours(2)));

        Assert.Equal(
            "@SQL=\"http://schemas.microsoft.com/mapi/string/{00020329-0000-0000-C000-000000000046}/AdMngCampaign\" IS NOT NULL"
            + " AND \"urn:schemas:httpmail:date\" >= '2026-07-30 07:15'",
            filter);
    }

    [Fact]
    public void ResponsesFilter_SelectsOneCampaign()
    {
        Assert.Equal(
            "@SQL=\"http://schemas.microsoft.com/mapi/string/{00020329-0000-0000-C000-000000000046}/AdMngCampaign\" = '" + CampaignA + "'",
            OutlookCampaigns.ResponsesFilter(CampaignA));
    }

    [Fact]
    public void NewId_Is32HexCharacters_AndAValidCampaignId()
    {
        var id = OutlookCampaigns.NewId();

        Assert.Matches("^[0-9a-f]{32}$", id);
        Assert.True(OutlookMailBuilder.IsCampaignId(id));
        Assert.NotEqual(id, OutlookCampaigns.NewId());
    }

    [Fact]
    public async Task FindCampaignsAsync_GroupsTheSentItemsById_NewestFirst()
    {
        await using var harness = new OutlookHarness();
        Sent(harness, CampaignA, "2026-10-01 10:00", "Czy skrzynka „Rekrutacja” jest nadal potrzebna?");
        Sent(harness, CampaignA, "2026-10-01 09:58", "Czy skrzynka „Archiwum” jest nadal potrzebna?");
        Sent(harness, CampaignA, "2026-10-01 10:05", "Czy skrzynka „Wnioski” jest nadal potrzebna?");
        Sent(harness, CampaignB, "2026-10-20 14:30", "Zmiana godzin pracy sekretariatu");
        Sent(harness, null, "2026-10-21 08:00", "Zwykła wiadomość");
        Sent(harness, "zły'identyfikator", "2026-10-22 08:00", "Obca kampania");
        Sent(harness, CampaignB, "2026-06-01 08:00", "Stara wiadomość tej kampanii");
        var session = await harness.ConnectAsync();
        var since = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(1));

        var campaigns = await session.FindCampaignsAsync(since, Ct);

        Assert.Equal(
            [
                new OutlookCampaign(CampaignB, "Zmiana godzin pracy sekretariatu", Local("2026-10-20 14:30"), Local("2026-10-20 14:30"), 1),
                new OutlookCampaign(CampaignA, "Czy skrzynka „Archiwum” jest nadal potrzebna?", Local("2026-10-01 09:58"), Local("2026-10-01 10:05"), 3),
            ],
            campaigns);
        var filter = Assert.Single(harness.Application.NameSpace.SentItems.All.Filters);
        Assert.Equal(OutlookCampaigns.CampaignsFilter(since), filter);
        Assert.Contains("NameSpace.GetDefaultFolder(5)", harness.Log.Calls);
        Assert.Contains("Items.Sort([SentOn],False)", harness.Log.Calls);
        harness.AssertAllReleased();
    }

    [Fact]
    public async Task FindCampaignsAsync_NothingSent_IsEmpty()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();

        Assert.Empty(await session.FindCampaignsAsync(DateTimeOffset.MinValue, Ct));
    }

    [Fact]
    public async Task FindCampaignsAsync_SearchFailure_IsClassified()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();
        harness.Log.FailWith("Items.Restrict", 0x80010108);

        var failure = await Assert.ThrowsAsync<OutlookException>(() => session.FindCampaignsAsync(DateTimeOffset.MinValue, Ct));

        Assert.Equal(OutlookFailureKind.Disconnected, failure.Failure.Kind);
        Assert.Equal("campaigns", failure.Failure.Step);
        Assert.False(session.IsConnected);
        harness.AssertAllReleased();
    }

    [Fact]
    public async Task ReadResponsesAsync_ReadsTheVotePerRecipient_WithTheKeysOfTheItem()
    {
        await using var harness = new OutlookHarness();
        Sent(
            harness,
            CampaignA,
            "2026-10-01 10:00",
            "Czy skrzynka jest nadal potrzebna?",
            "sekretariat.it@contoso.test\nrekrutacja@contoso.test",
            Vote(harness, "jan.kowalski@contoso.test", 1, "Tak, nadal potrzebna", "2026-10-02 08:30"),
            Vote(harness, "anna.nowak@contoso.test", 2, string.Empty, Never));
        Sent(harness, CampaignA, "2026-10-01 10:01", "Czy skrzynka jest nadal potrzebna?", "wnioski@contoso.test",
            Vote(harness, "agnieszka.zolc@contoso.test", 1, "Nie, można usunąć", Never));
        Sent(harness, CampaignB, "2026-10-01 10:02", "Inna kampania", "x@contoso.test", Vote(harness, "inna@contoso.test", 1, "Tak", "2026-10-02 08:30"));
        var session = await harness.ConnectAsync();

        var responses = await session.ReadResponsesAsync(CampaignA, Ct);

        Assert.Equal(3, responses.Count);
        Assert.Equal("jan.kowalski@contoso.test", responses[0].Recipient);
        Assert.Equal("Tak, nadal potrzebna", responses[0].Response);
        Assert.Equal(Local("2026-10-02 08:30"), responses[0].RespondedAt);
        Assert.Equal(Local("2026-10-01 10:00"), responses[0].SentAt);
        Assert.Equal("Czy skrzynka jest nadal potrzebna?", responses[0].Subject);
        Assert.Equal(["sekretariat.it@contoso.test", "rekrutacja@contoso.test"], responses[0].Keys);
        Assert.Equal("anna.nowak@contoso.test", responses[1].Recipient);
        Assert.Null(responses[1].Response);
        Assert.Null(responses[1].RespondedAt);

        // A vote with Outlook's "no date": the vote counts, the time is unknown.
        Assert.Equal("Nie, można usunąć", responses[2].Response);
        Assert.Null(responses[2].RespondedAt);
        Assert.Equal(["wnioski@contoso.test"], responses[2].Keys);
        Assert.Equal(OutlookCampaigns.ResponsesFilter(CampaignA), Assert.Single(harness.Application.NameSpace.SentItems.All.Filters));
        Assert.Equal(nameof(OutlookVoteResponse), responses[0].ToString());
        harness.AssertAllReleased();
    }

    [Fact]
    public async Task ReadResponsesAsync_InABccMessage_LeavesOutTheSendersOwnToRecipient()
    {
        await using var harness = new OutlookHarness();
        Sent(harness, CampaignA, "2026-10-01 10:00", "Komunikat", string.Empty,
            Vote(harness, OutlookHarness.Own, 1, string.Empty, Never),
            Vote(harness, "anna.nowak@contoso.test", 3, "Tak", "2026-10-02 09:00"),
            Vote(harness, "jan.nowak@contoso.test", 3, string.Empty, Never));
        Sent(harness, CampaignA, "2026-10-01 10:05", "Test do siebie", string.Empty, Vote(harness, OutlookHarness.Own, 1, string.Empty, Never));
        var session = await harness.ConnectAsync();

        var responses = await session.ReadResponsesAsync(CampaignA, Ct);

        Assert.Equal(["anna.nowak@contoso.test", "jan.nowak@contoso.test", OutlookHarness.Own], responses.Select(response => response.Recipient));
        Assert.Empty(responses[0].Keys);
    }

    [Fact]
    public async Task ReadResponsesAsync_ExchangeRecipients_GetTheirSmtpAddress()
    {
        await using var harness = new OutlookHarness();
        var byProperty = Vote(harness, "/o=ExchangeLabs/cn=Recipients/cn=ewa", 1, "Tak", "2026-10-02 09:00");
        byProperty.Accessor.Values[MapiProperties.SmtpAddress] = "ewa.zielinska@contoso.test";
        var byExchangeUser = Vote(harness, "/o=ExchangeLabs/cn=Recipients/cn=pawel", 1, "Nie", "2026-10-02 09:00");
        byExchangeUser.Entry = new AddressEntry(harness.Application) { Exchange = new ExchangeUser { PrimarySmtpAddress = "pawel.lewandowski@contoso.test" } };
        var byName = Vote(harness, "/o=ExchangeLabs/cn=Recipients/cn=zbigniew", 1, string.Empty, Never);
        byName.Name = "Zbigniew Kaźmierczak";
        Sent(harness, CampaignA, "2026-10-01 10:00", "Pytanie", string.Empty, byProperty, byExchangeUser, byName);
        var session = await harness.ConnectAsync();

        var responses = await session.ReadResponsesAsync(CampaignA, Ct);

        Assert.Equal(["ewa.zielinska@contoso.test", "pawel.lewandowski@contoso.test", "Zbigniew Kaźmierczak"], responses.Select(response => response.Recipient));
        harness.AssertAllReleased();
    }

    [Theory]
    [InlineData("")]
    [InlineData("x' OR 'a'='a")]
    [InlineData("\"AdMngCampaign\" IS NOT NULL")]
    public async Task ReadResponsesAsync_RefusesAnythingButACampaignId(string id)
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();

        Assert.Throws<ArgumentException>(() =>
        {
            _ = session.ReadResponsesAsync(id, Ct);
        });
        Assert.Empty(harness.Application.NameSpace.SentItems.All.Filters);
    }

    [Fact]
    public async Task ReadResponsesAsync_UnknownCampaign_IsEmpty()
    {
        await using var harness = new OutlookHarness();
        Sent(harness, CampaignA, "2026-10-01 10:00", "Pytanie", string.Empty, Vote(harness, "anna.nowak@contoso.test", 1, "Tak", Never));
        var session = await harness.ConnectAsync();

        Assert.Empty(await session.ReadResponsesAsync(CampaignB, Ct));
    }

    [Fact]
    public async Task ReadResponsesAsync_FolderFailure_IsClassified()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();
        harness.Log.FailWith("NameSpace.GetDefaultFolder", 0x8001010A);

        var failure = await Assert.ThrowsAsync<OutlookException>(() => session.ReadResponsesAsync(CampaignA, Ct));

        Assert.Equal(OutlookFailureKind.Busy, failure.Failure.Kind);
        Assert.Equal("responses", failure.Failure.Step);
        Assert.True(failure.Failure.IsTransient);
        Assert.True(session.IsConnected);
    }

    [Fact]
    public async Task SentItemsOfASend_AreFoundAgain_ByTheirCampaign()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();
        var id = OutlookCampaigns.NewId();
        await session.SendAsync(
            OutlookHarness.Message("anna.nowak@contoso.test") with { CampaignId = id, CampaignKeys = ["sekretariat.it@contoso.test"] },
            OutlookSendMode.Send,
            Ct);
        var sent = Assert.Single(harness.Application.Created);
        sent.SentOn = new DateTime(2026, 10, 28, 10, 0, 0);
        harness.Application.NameSpace.SentItems.All.List.Add(sent);

        var campaign = Assert.Single(await session.FindCampaignsAsync(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), Ct));
        var response = Assert.Single(await session.ReadResponsesAsync(campaign.Id, Ct));

        Assert.Equal(id, campaign.Id);
        Assert.Equal("anna.nowak@contoso.test", response.Recipient);
        Assert.Equal(["sekretariat.it@contoso.test"], response.Keys);
    }

    private static DateTimeOffset Local(string text) =>
        new(DateTime.ParseExact(text, "yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture), TimeSpan.FromHours(1));

    private static Recipient Vote(OutlookHarness harness, string address, int type, string vote, string at) => new(harness.Application)
    {
        Address = address,
        Name = address,
        Type = type,
        AutoResponse = vote,
        TrackingStatus = vote.Length > 0 ? 7 : 1,
        TrackingStatusTime = at == Never ? new DateTime(4501, 1, 1) : Local(at).DateTime,
    };

    private static MailItem Sent(OutlookHarness harness, string? campaign, string sentOn, string subject, string keys = "", params Recipient[] recipients)
    {
        var item = new MailItem(harness.Application) { Subject = subject, SentOn = Local(sentOn).DateTime };
        if (campaign is not null)
        {
            item.Accessor.Values[MapiProperties.Campaign] = campaign;
            item.Accessor.Values[MapiProperties.Keys] = keys;
        }

        item.RecipientList.Items.AddRange(recipients);
        harness.Application.NameSpace.SentItems.All.List.Add(item);
        return item;
    }
}
