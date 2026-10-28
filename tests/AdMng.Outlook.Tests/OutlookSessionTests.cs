using AdMng.Core.Correspondence.Outlook;

namespace AdMng.Outlook.Tests;

/// <summary>Render (MailerWPiA RenderViaOutlook), diagnostics (Diagnose) and the life of a session.</summary>
public class OutlookSessionTests
{
    private static CancellationToken Ct => OutlookHarness.Ct;

    [Fact]
    public async Task RenderAsync_ReadsTheHtmlAndTheImagesBackIntoMemory_AndDeletesTheDraft()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();
        var logo = OutlookHarness.Image(0x10, "logo.png");
        var stamp = OutlookHarness.Image(0x20, "pieczatka.png");
        var rewritten = $"<html><body><p class=MsoNormal>Szanowna Pani,<img src=\"cid:{logo.ContentId}\"><img src=\"cid:{stamp.ContentId}\"></p></body></html>";
        harness.Application.ConfigureItem = mail => mail.RenderedHtml = rewritten;
        var file = harness.File("regulamin.pdf");
        var message = OutlookHarness.Message("anna.nowak@contoso.test") with
        {
            Cc = ["kopia@contoso.test"],
            Bcc = ["ukryta@contoso.test"],
            SendUsingAccount = OutlookHarness.Secretariat,
            InlineImages = [logo, stamp],
            Attachments = [new OutlookAttachment(file), new OutlookAttachment(Path.Combine(harness.Root, "brak.pdf"))],
        };

        var rendered = await session.RenderAsync(message, Ct);

        Assert.Equal(rewritten, rendered.Html);
        Assert.Equal(3, rendered.Attachments);
        Assert.Equal(2, rendered.Images.Count);
        Assert.Equal(logo.Data, rendered.Images[logo.ContentId].Data);
        Assert.Equal("image/png", rendered.Images[logo.ContentId].MediaType);
        Assert.Equal(stamp.ContentId, rendered.Images[stamp.ContentId].ContentId);
        Assert.Equal(stamp.Data, rendered.Images[stamp.ContentId].Data);

        var mail = Assert.Single(harness.Application.Created);
        Assert.True(mail.WasDeleted);
        Assert.False(mail.WasSent);
        Assert.Same(harness.Application.NameSpace.AccountList.Items[1], mail.SendUsingAccount);
        Assert.Equal(["anna.nowak@contoso.test"], mail.RecipientList.Items.Select(recipient => recipient.Address));
        Assert.Equal(2, harness.Log.CallsOf("Attachment.SaveAsFile").Count);
        Assert.Empty(harness.TempFolders());
        harness.AssertAllReleased();
    }

    [Fact]
    public async Task RenderAsync_AnImageThatCannotBeSaved_IsLeftOut_ButALostOutlookFails()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();
        var logo = OutlookHarness.Image(0x10, "logo.png");
        var stamp = OutlookHarness.Image(0x20, "pieczatka.png");
        harness.Log.FailWith("Attachment.SaveAsFile(logo.png)", 0x80004005, times: 1);

        var rendered = await session.RenderAsync(OutlookHarness.Message() with { InlineImages = [logo, stamp] }, Ct);

        Assert.Equal([stamp.ContentId], rendered.Images.Keys);
        Assert.Equal(2, rendered.Attachments);

        harness.Log.FailWith("Attachment.SaveAsFile", 0x80010108, times: 1);
        var failure = await Assert.ThrowsAsync<OutlookException>(() => session.RenderAsync(OutlookHarness.Message() with { InlineImages = [logo] }, Ct));
        Assert.Equal(OutlookFailureKind.Disconnected, failure.Failure.Kind);
        Assert.Equal("render", failure.Failure.Step);
        Assert.All(harness.Application.Created, mail => Assert.True(mail.WasDeleted));
        Assert.Empty(harness.TempFolders());
    }

    [Fact]
    public async Task RenderAsync_AnUnresolvedRecipient_StillRenders()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();
        harness.Application.Unresolvable.Add("adres@odbiorcy.pl");

        var rendered = await session.RenderAsync(OutlookHarness.Message("adres@odbiorcy.pl"), Ct);

        Assert.Contains("zażółć", rendered.Html, StringComparison.Ordinal);
        Assert.Empty(rendered.Images);
        Assert.Equal(0, rendered.Attachments);
    }

    [Fact]
    public async Task RenderAsync_FailureBeforeTheSave_Throws_AndDiscardsTheItem()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();
        harness.Log.FailWith("MailItem.HTMLBody=", 0x8001010A);

        var failure = await Assert.ThrowsAsync<OutlookException>(() => session.RenderAsync(OutlookHarness.Message(), Ct));

        Assert.Equal(OutlookFailureKind.Busy, failure.Failure.Kind);
        Assert.Equal("body", failure.Failure.Step);
        var mail = Assert.Single(harness.Application.Created);
        Assert.Equal(1, mail.ClosedWith);
        Assert.False(mail.WasDeleted);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain("contoso", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderAsync_FailureAfterTheSave_Throws_AndDeletesTheDraft()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();
        harness.Log.FailWith("Attachments.Count", 0x80010105);

        var failure = await Assert.ThrowsAsync<OutlookException>(() => session.RenderAsync(OutlookHarness.Message(), Ct));

        Assert.Equal(OutlookFailureKind.NotResponding, failure.Failure.Kind);
        Assert.Equal("render", failure.Failure.Step);
        Assert.True(Assert.Single(harness.Application.Created).WasDeleted);
        Assert.Empty(harness.TempFolders());
    }

    [Fact]
    public async Task RenderAsync_DeleteRefused_Throws_AtTheDeleteStep()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();
        harness.Log.FailWith("MailItem.Delete()", 0x80004005);

        var failure = await Assert.ThrowsAsync<OutlookException>(() => session.RenderAsync(OutlookHarness.Message(), Ct));

        Assert.Equal("delete", failure.Failure.Step);
        Assert.Single(harness.Log.CallsOf("MailItem.Delete()"));
    }

    [Fact]
    public async Task RenderAsync_Disconnected_EndsIsConnected()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();
        harness.Log.FailWith("Application.CreateItem", 0x80010108);

        var failure = await Assert.ThrowsAsync<OutlookException>(() => session.RenderAsync(OutlookHarness.Message(), Ct));

        Assert.Equal(OutlookFailureKind.Disconnected, failure.Failure.Kind);
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task DiagnoseAsync_WithTestItem_ReadsEverythingAgain_AndDiscardsTheTestItem()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();
        harness.Application.NameSpace.ProfileName = "Praca";

        var diagnostics = await session.DiagnoseAsync(createTestItem: true, Ct);

        Assert.Equal("16.0.0.17928", diagnostics.Version);
        Assert.Equal("Praca", diagnostics.ProfileName);
        Assert.Equal(session.Profile.Accounts, diagnostics.Accounts);
        Assert.Equal(OutlookHarness.Own, diagnostics.CurrentUserAddress);
        Assert.Equal(["Jan Kowalski – podpis", "Krótki"], diagnostics.Signatures);
        Assert.Equal(Environment.Is64BitProcess, diagnostics.ProcessIs64Bit);
        Assert.True(diagnostics.TestItemCreated);
        Assert.Null(diagnostics.TestItemFailure);
        Assert.Equal(
            ["Application.CreateItem(0)", "MailItem.Subject=test", "MailItem.HTMLBody=", "MailItem.Close(1)"],
            harness.Log.CallsOf("Application.CreateItem", "MailItem."));
        Assert.Equal(0, Assert.Single(harness.Application.Created).SaveCount);
        harness.AssertAllReleased();
    }

    [Fact]
    public async Task DiagnoseAsync_WithoutTestItem_CreatesNothing()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();

        var diagnostics = await session.DiagnoseAsync(createTestItem: false, Ct);

        Assert.Null(diagnostics.TestItemCreated);
        Assert.Null(diagnostics.TestItemFailure);
        Assert.Empty(harness.Application.Created);
    }

    [Fact]
    public async Task DiagnoseAsync_TestItemRefused_IsReported_NotThrown()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();
        harness.Log.FailWith("MailItem.HTMLBody=", 0x80004005);

        var diagnostics = await session.DiagnoseAsync(createTestItem: true, Ct);

        Assert.False(diagnostics.TestItemCreated);
        Assert.Equal("testItem", diagnostics.TestItemFailure!.Step);
        Assert.Equal(OutlookFailureKind.Unknown, diagnostics.TestItemFailure.Kind);
    }

    [Fact]
    public async Task DiagnoseAsync_OutlookGone_ThrowsClassified_AndEndsIsConnected()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();
        harness.Log.FailWith("Application.Session", 0x800706BA);

        var failure = await Assert.ThrowsAsync<OutlookException>(() => session.DiagnoseAsync(createTestItem: true, Ct));

        Assert.Equal(OutlookFailureKind.Disconnected, failure.Failure.Kind);
        Assert.Equal("diagnose", failure.Failure.Step);
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task DiagnoseAsync_WithoutASignatureSource_ListsNoSignatures()
    {
        await using var harness = new OutlookHarness();
        var client = new OutlookClient(harness.Provider, harness.Time, signatures: null, harness.TempFiles, harness.Log.Release, () => false);
        var result = await client.ConnectAsync(Ct);
        await using var session = (OutlookSession)result.Session!;

        var diagnostics = await session.DiagnoseAsync(createTestItem: false, Ct);

        Assert.Empty(diagnostics.Signatures);
    }

    [Fact]
    public async Task DisposeAsync_ReleasesTheApplication_EndsTheWorker_AndRefusesFurtherCalls()
    {
        await using var harness = new OutlookHarness();
        var result = await harness.Client.ConnectAsync(Ct);
        var session = (OutlookSession)result.Session!;
        await session.SendAsync(OutlookHarness.Message(), OutlookSendMode.Send, Ct);

        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.False(session.IsConnected);
        Assert.True(session.Worker.Completion.IsCompleted);
        harness.AssertAllReleased(harness.Application);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.SendAsync(OutlookHarness.Message(), OutlookSendMode.Send, Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.RenderAsync(OutlookHarness.Message(), Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.DiagnoseAsync(false, Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.FindCampaignsAsync(DateTimeOffset.MinValue, Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.ReadResponsesAsync("0123456789abcdef0123456789abcdef", Ct));
    }

    [Fact]
    public async Task Calls_AreSerializedOnTheWorker_InTheOrderTheyWereMade()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();
        var messages = Enumerable.Range(1, 20).Select(index => OutlookHarness.Message($"osoba{index:00}@contoso.test")).ToList();

        var results = await Task.WhenAll(messages.Select(message => session.SendAsync(message, OutlookSendMode.Draft, Ct)));

        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Equal(
            messages.Select(message => $"Recipients.Add({message.To[0]})"),
            harness.Log.CallsOf("Recipients.Add("));
        Assert.All(harness.Log.Threads, thread => Assert.Equal(session.Worker.ThreadId, thread));
    }

    [Fact]
    public void ToString_NeverShowsData()
    {
        Assert.Equal(nameof(OutlookSessionOptions), new OutlookSessionOptions(new OutlookTempFiles(), TimeProvider.System, null, _ => { }).ToString());
        Assert.Equal(nameof(OutlookUserMessageTransport), new OutlookUserMessageTransport(new Testing.FakeOutlookClient(), OutlookHarness.Own).ToString());
    }
}
