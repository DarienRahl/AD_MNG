using AdMng.Core.Correspondence.Outlook;
using AdMng.Testing;

namespace AdMng.Outlook.Tests;

/// <summary>The public fake of AdMng.Testing behaves like the engine where the Presentation and UI tests rely on it.</summary>
public class FakeOutlookClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static OutlookMessage Message(string to) => new() { To = [to], Subject = "Temat", HtmlBody = "<p>Treść</p>" };

    [Fact]
    public async Task Connect_GivesASessionWithTheProfile_OrTheScriptedFailure()
    {
        var client = new FakeOutlookClient();

        var connected = await client.ConnectAsync(Ct);
        client.ConnectFailure = FakeOutlookClient.Failure(OutlookFailureKind.Elevated, "connect");
        var refused = await client.ConnectAsync(Ct);

        Assert.Same(FakeOutlookClient.SampleProfile, connected.Session!.Profile);
        Assert.True(connected.Session.IsConnected);
        Assert.Equal("outlook.elevated", refused.Failure!.Code);
        Assert.Null(refused.Session);
        Assert.Equal(2, client.Connections);
        Assert.Single(client.Sessions);
    }

    [Theory]
    [InlineData(OutlookAvailability.NotInstalled, "outlook.notInstalled")]
    [InlineData(OutlookAvailability.NotSupported, "outlook.notSupported")]
    public async Task Connect_FollowsTheAvailability(OutlookAvailability availability, string code)
    {
        var client = new FakeOutlookClient { Availability = availability };

        var result = await client.ConnectAsync(Ct);

        Assert.Equal(code, result.Failure!.Code);
    }

    [Fact]
    public async Task Send_AnswersPerRecipient_ThenTheDefault_AndRecordsEveryItem()
    {
        var client = new FakeOutlookClient();
        client.Script("anna.nowak@contoso.test", FakeOutlookClient.Failed(OutlookFailureKind.RecipientUnresolved, "recipients"));
        client.Script("ukryta@contoso.test", FakeOutlookClient.Failed(OutlookFailureKind.Busy));
        var session = (await client.ConnectAsync(Ct)).Session!;
        var seen = new List<OutlookSendMode>();
        client.BeforeSend = (_, mode) => seen.Add(mode);

        var first = await session.SendAsync(Message("anna.nowak@contoso.test"), OutlookSendMode.Send, Ct);
        var second = await session.SendAsync(Message("anna.nowak@contoso.test"), OutlookSendMode.Send, Ct);
        var draft = await session.SendAsync(Message("jan.kowalski@contoso.test"), OutlookSendMode.Draft, Ct);
        var bcc = await session.SendAsync(Message(FakeOutlookClient.SampleProfile.CurrentUserAddress!) with { Bcc = ["ukryta@contoso.test"] }, OutlookSendMode.Send, Ct);

        Assert.Equal("outlook.recipient", first.Failure!.Code);
        Assert.Equal(OutlookItemResult.Sent, second);
        Assert.Equal(OutlookItemResult.Draft, draft);
        Assert.Equal("outlook.busy", bcc.Failure!.Code);
        Assert.Equal(4, client.Items.Count);
        Assert.Equal([OutlookSendMode.Send, OutlookSendMode.Send, OutlookSendMode.Draft, OutlookSendMode.Send], seen);
        Assert.Single(client.Succeeded());
        Assert.Single(client.Succeeded(OutlookSendMode.Draft));
        Assert.Equal("FakeOutlookItem Send outlook.recipient", client.Items.First().ToString());
    }

    [Fact]
    public async Task Render_EchoesTheMessage_OrTheScriptedResultOrFailure()
    {
        var client = new FakeOutlookClient();
        var session = (await client.ConnectAsync(Ct)).Session!;
        var image = OutlookHarness.Image();

        var echoed = await session.RenderAsync(Message("anna.nowak@contoso.test") with { InlineImages = [image, image] }, Ct);
        client.RenderFailure = FakeOutlookClient.Failure(OutlookFailureKind.NotResponding, "render");
        var failure = await Assert.ThrowsAsync<OutlookException>(() => session.RenderAsync(Message("anna.nowak@contoso.test"), Ct));

        Assert.Equal("<p>Treść</p>", echoed.Html);
        Assert.Equal(image.Data, echoed.Images[image.ContentId].Data);
        Assert.Equal(2, echoed.Attachments);
        Assert.Equal("outlook.notResponding", failure.Failure.Code);
        Assert.Equal(2, client.Rendered.Count);
    }

    [Fact]
    public async Task CampaignsAndResponses_FilteredByDate_AndById()
    {
        var client = new FakeOutlookClient();
        var at = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.FromHours(2));
        client.Campaigns.Add(new OutlookCampaign("old", "Stara", at.AddMonths(-6), at.AddMonths(-6), 3));
        client.Campaigns.Add(new OutlookCampaign("new", "Nowa", at, at.AddMinutes(5), 2));
        client.Responses["new"] = [new OutlookVoteResponse("anna.nowak@contoso.test", "Tak", at.AddDays(1), at, "Nowa", ["sekretariat.it@contoso.test"])];
        var session = (await client.ConnectAsync(Ct)).Session!;

        var campaigns = await session.FindCampaignsAsync(at.AddDays(-30), Ct);
        var responses = await session.ReadResponsesAsync("new", Ct);

        Assert.Equal(["new"], campaigns.Select(campaign => campaign.Id));
        Assert.Equal("Tak", Assert.Single(responses).Response);
        Assert.Empty(await session.ReadResponsesAsync("brak", Ct));
        client.ReadFailure = FakeOutlookClient.Failure(OutlookFailureKind.Disconnected, "campaigns");
        await Assert.ThrowsAsync<OutlookException>(() => session.FindCampaignsAsync(at, Ct));
    }

    [Fact]
    public async Task Diagnose_DefaultsToTheProfile()
    {
        var client = new FakeOutlookClient();
        var session = (await client.ConnectAsync(Ct)).Session!;

        var diagnostics = await session.DiagnoseAsync(createTestItem: true, Ct);

        Assert.Equal(FakeOutlookClient.SampleProfile.Accounts, diagnostics.Accounts);
        Assert.Equal(["Jan Kowalski – podpis"], diagnostics.Signatures);
        Assert.True(diagnostics.TestItemCreated);
        Assert.Null((await session.DiagnoseAsync(createTestItem: false, Ct)).TestItemCreated);
    }

    [Fact]
    public async Task Session_DisposedOrCancelled_Refuses()
    {
        var client = new FakeOutlookClient();
        var session = (FakeOutlookSession)(await client.ConnectAsync(Ct)).Session!;
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SendAsync(Message("a@contoso.test"), OutlookSendMode.Send, cancellation.Token));
        session.IsConnected = false;
        Assert.False(session.IsConnected);
        await session.DisposeAsync();

        Assert.True(session.IsDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.SendAsync(Message("a@contoso.test"), OutlookSendMode.Send, Ct));
        Assert.Empty(client.Items);
    }

    [Fact]
    public void Codes_AreThoseOfTheEngine()
    {
        foreach (var kind in Enum.GetValues<OutlookFailureKind>())
        {
            Assert.Equal(OutlookErrors.Code(kind), FakeOutlookClient.Failure(kind).Code);
        }

        Assert.Equal(nameof(FakeOutlookClient), new FakeOutlookClient().ToString());
    }
}
