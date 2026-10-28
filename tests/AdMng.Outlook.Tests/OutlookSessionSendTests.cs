using AdMng.Core.Correspondence.Outlook;

namespace AdMng.Outlook.Tests;

/// <summary>Building and sending an item (design note 4.2: the port of MailerWPiA Send, same order of steps).</summary>
public class OutlookSessionSendTests
{
    private static CancellationToken Ct => OutlookHarness.Ct;

    private static readonly string[] Steps =
    [
        "Application.CreateItem", "MailItem.", "Recipients.", "Recipient.", "ReplyRecipients.", "Attachments.Add", "Attachments[",
        "Attachment.PropertyAccessor.SetProperty",
    ];

    [Fact]
    public async Task SendAsync_FullMessage_FollowsTheStepOrderOfTheDesignNote()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();
        var image = OutlookHarness.Image();
        var attachment = harness.File("umowa.pdf");
        var message = OutlookHarness.Message("anna.nowak@contoso.test") with
        {
            Cc = ["lukasz.wisniewski@contoso.test"],
            Bcc = ["malgorzata.wojcik@contoso.test"],
            ReplyTo = ["sekretariat.lodz@contoso.test"],
            SendUsingAccount = OutlookHarness.Secretariat,
            SentOnBehalfOf = "  Sekretariat – Łódź  ",
            ReadReceipt = true,
            DeferredDelivery = new DateTimeOffset(2026, 10, 29, 7, 0, 0, TimeSpan.Zero),
            VotingOptions = "Tak, nadal potrzebna; Nie, można usunąć",
            Importance = OutlookImportance.High,
            CampaignId = "0123456789abcdef0123456789abcdef",
            CampaignKeys = ["sekretariat.it@contoso.test", "rekrutacja@contoso.test"],
            InternetHeaders = new Dictionary<string, string> { ["Auto-Submitted"] = "auto-generated" },
            InlineImages = [image],
            Attachments = [new OutlookAttachment(attachment)],
        };

        var result = await session.SendAsync(message, OutlookSendMode.Send, Ct);

        Assert.Equal(OutlookItemResult.Sent, result);
        Assert.Equal(
            [
                "Application.CreateItem(0)",
                "MailItem.BodyFormat=2",
                "MailItem.SendUsingAccount=Account(Sekretariat – Łódź)",
                "MailItem.SentOnBehalfOfName=Sekretariat – Łódź",
                "Recipients.Add(anna.nowak@contoso.test)",
                "Recipient.Type=1",
                "Recipient.Resolve(anna.nowak@contoso.test)",
                "Recipients.Add(lukasz.wisniewski@contoso.test)",
                "Recipient.Type=2",
                "Recipient.Resolve(lukasz.wisniewski@contoso.test)",
                "Recipients.Add(malgorzata.wojcik@contoso.test)",
                "Recipient.Type=3",
                "Recipient.Resolve(malgorzata.wojcik@contoso.test)",
                "Recipients.ResolveAll()",
                "ReplyRecipients.Add(sekretariat.lodz@contoso.test)",
                "MailItem.Subject=Czy skrzynka „Sekretariat IT” jest nadal potrzebna?",
                "MailItem.HTMLBody=",
                "MailItem.ReadReceiptRequested=True",
                "MailItem.DeferredDeliveryTime=2026-10-29 08:00",
                "MailItem.VotingOptions=Tak, nadal potrzebna;Nie, można usunąć",
                "MailItem.Importance=2",
                "MailItem.PropertyAccessor.SetProperty(" + MapiProperties.Campaign + ")",
                "MailItem.PropertyAccessor.SetProperty(" + MapiProperties.Keys + ")",
                "MailItem.PropertyAccessor.SetProperty(" + MapiProperties.Header("Auto-Submitted") + ")",
                "Attachments.Add(logo.png,1,0,logo.png)",
                "Attachment.PropertyAccessor.SetProperty(" + MapiProperties.AttachContentId + ")",
                "Attachment.PropertyAccessor.SetProperty(" + MapiProperties.AttachmentHidden + ")",
                "Attachments.Add(umowa.pdf,-,-,-)",
                "MailItem.Save()",
                "Attachments[1]",
                "Attachment.PropertyAccessor.SetProperty(" + MapiProperties.AttachContentId + ")",
                "Attachment.PropertyAccessor.SetProperty(" + MapiProperties.AttachMimeTag + ")",
                "Attachment.PropertyAccessor.SetProperty(" + MapiProperties.AttachmentHidden + ")",
                "Attachments[2]",
                "MailItem.Save()",
                "MailItem.Send()",
            ],
            harness.Log.CallsOf(Steps));

        var mail = Assert.Single(harness.Application.Created);
        Assert.Equal(
            new Dictionary<string, object>
            {
                [MapiProperties.Campaign] = "0123456789abcdef0123456789abcdef",
                [MapiProperties.Keys] = "sekretariat.it@contoso.test\nrekrutacja@contoso.test",
                [MapiProperties.Header("Auto-Submitted")] = "auto-generated",
            },
            mail.Accessor.Values);
        Assert.Equal(new DateTime(2026, 10, 29, 8, 0, 0), mail.DeferredDeliveryTime);
        Assert.Equal(DateTimeKind.Unspecified, mail.DeferredDeliveryTime.Kind);
        Assert.Same(harness.Application.NameSpace.AccountList.Items[1], mail.SendUsingAccount);
        Assert.True(mail.WasSent);
        Assert.Null(mail.ClosedWith);
        Assert.All(harness.Log.Threads, thread => Assert.Equal(session.Worker.ThreadId, thread));
    }

    [Fact]
    public async Task SendAsync_InlineImages_AreAddedByContentId_Hidden_BeforeAndAfterTheSave()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();
        var first = OutlookHarness.Image(0x10, "logo.png");
        var second = OutlookHarness.Image(0x20, "logo.png");
        var gif = Core.Messaging.Images.MessageImage.Create(Testing.MessageImageSamples.Gif1x1, "pieczątka.gif");

        var result = await session.SendAsync(OutlookHarness.Message() with { InlineImages = [first, second, gif] }, OutlookSendMode.Send, Ct);

        Assert.True(result.Succeeded);
        var mail = Assert.Single(harness.Application.Created);
        var attachments = mail.AttachmentList.Items;
        Assert.Equal(["logo.png", "logo-2.png", gif.FileName], attachments.Select(attachment => attachment.FileName));
        foreach (var (attachment, image) in attachments.Zip([first, second, gif]))
        {
            Assert.Equal(0, attachment.Position);
            Assert.Equal(image.Data, attachment.Data);
            Assert.Equal(image.ContentId, attachment.Accessor.Values[MapiProperties.AttachContentId]);
            Assert.Equal(true, attachment.Accessor.Values[MapiProperties.AttachmentHidden]);
            Assert.Equal(image.MediaType, attachment.Accessor.Values[MapiProperties.AttachMimeTag]);
        }

        // Content id and hidden flag are set when the image is added and again after the save (MailerWPiA).
        Assert.Equal(6, harness.Log.CallsOf("Attachment.PropertyAccessor.SetProperty(" + MapiProperties.AttachContentId).Count);
        Assert.Equal(2, mail.SaveCount);
        Assert.Empty(harness.TempFolders());
    }

    [Fact]
    public async Task SendAsync_TheImageFilesExistOnlyWhileTheItemIsBuilt()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();
        var image = OutlookHarness.Image();

        var result = await session.SendAsync(OutlookHarness.Message() with { InlineImages = [image] }, OutlookSendMode.Send, Ct);

        // The fake read the file when it was added (as Outlook copies it): it existed then, in a private item folder …
        Assert.True(result.Succeeded);
        var attachment = Assert.Single(Assert.Single(harness.Application.Created).AttachmentList.Items);
        Assert.Equal(image.Data, attachment.Data);
        var folder = Path.GetDirectoryName(attachment.SourcePath)!;
        Assert.StartsWith(Path.Combine(harness.TempRoot, OutlookTempFiles.Prefix + Environment.ProcessId + "-"), folder, StringComparison.Ordinal);

        // … which is gone with everything in it once the item is done.
        Assert.False(Directory.Exists(folder));
        Assert.Empty(harness.TempFolders());
    }

    [Fact]
    public async Task SendAsync_FailureOfSend_LeavesTheDraft()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();
        harness.Log.FailWith("MailItem.Send()", 0x80004004);

        var result = await session.SendAsync(OutlookHarness.Message() with { InlineImages = [OutlookHarness.Image()] }, OutlookSendMode.Send, Ct);

        Assert.False(result.Succeeded);
        Assert.True(result.SavedAsDraft);
        Assert.Equal(OutlookFailureKind.SecurityDenied, result.Failure!.Kind);
        Assert.Equal("outlook.denied", result.Failure.Code);
        Assert.Equal("send", result.Failure.Step);
        var mail = Assert.Single(harness.Application.Created);
        Assert.Equal(2, mail.SaveCount);
        Assert.Null(mail.ClosedWith);
        Assert.False(mail.WasDeleted);
        Assert.Empty(harness.TempFolders());
        Assert.True(session.IsConnected);
    }

    [Fact]
    public async Task SendAsync_FailureOfTheSecondSave_LeavesTheDraft_AtTheInlineStep()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();
        harness.Log.FailWith("MailItem.Save()", 0x80040116, skip: 1);

        var result = await session.SendAsync(OutlookHarness.Message() with { InlineImages = [OutlookHarness.Image()] }, OutlookSendMode.Send, Ct);

        Assert.True(result.SavedAsDraft);
        Assert.Equal(OutlookFailureKind.ItemTooLarge, result.Failure!.Kind);
        Assert.Equal("inline", result.Failure.Step);
        Assert.False(Assert.Single(harness.Application.Created).WasSent);
    }

    [Theory]
    [InlineData("MailItem.SentOnBehalfOfName=", "onBehalf")]
    [InlineData("Recipients.Add", "recipients")]
    [InlineData("ReplyRecipients.Add", "replyTo")]
    [InlineData("MailItem.Subject=", "subject")]
    [InlineData("MailItem.HTMLBody=", "body")]
    [InlineData("MailItem.DeferredDeliveryTime=", "defer")]
    [InlineData("MailItem.VotingOptions=", "voting")]
    [InlineData("Attachments.Add(logo", "images")]
    [InlineData("Attachments.Add(umowa", "attachments")]
    [InlineData("MailItem.Save()", "save")]
    public async Task SendAsync_FailureBeforeTheSave_DiscardsTheItem_AndNamesTheStep(string member, string step)
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();
        harness.Log.FailWith(member, 0x80004005);
        var message = OutlookHarness.Message() with
        {
            SentOnBehalfOf = "sekretariat.lodz@contoso.test",
            ReplyTo = ["sekretariat.lodz@contoso.test"],
            DeferredDelivery = new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero),
            VotingOptions = "Tak;Nie",
            InlineImages = [OutlookHarness.Image()],
            Attachments = [new OutlookAttachment(harness.File("umowa.pdf"))],
        };

        var result = await session.SendAsync(message, OutlookSendMode.Send, Ct);

        Assert.False(result.Succeeded);
        Assert.False(result.SavedAsDraft);
        Assert.Equal(OutlookFailureKind.Unknown, result.Failure!.Kind);
        Assert.Equal("outlook.error", result.Failure.Code);
        Assert.Equal(step, result.Failure.Step);
        Assert.Equal(unchecked((int)0x80004005), result.Failure.HResult);
        var mail = Assert.Single(harness.Application.Created);
        Assert.Equal(0, mail.SaveCount);
        Assert.Equal(1, mail.ClosedWith);
        Assert.False(mail.WasSent);
        Assert.Empty(harness.TempFolders());
        harness.AssertAllReleased();
    }

    [Fact]
    public async Task SendAsync_UnresolvedRecipient_RefusesTheItem_NothingIsSaved()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();
        harness.Application.Unresolvable.Add("Kowalski Jan");

        var result = await session.SendAsync(OutlookHarness.Message("anna.nowak@contoso.test") with { Bcc = ["Kowalski Jan"] }, OutlookSendMode.Send, Ct);

        Assert.False(result.Succeeded);
        Assert.False(result.SavedAsDraft);
        Assert.Equal(OutlookFailureKind.RecipientUnresolved, result.Failure!.Kind);
        Assert.Equal("outlook.recipient", result.Failure.Code);
        Assert.Equal("recipients", result.Failure.Step);
        Assert.Null(result.Failure.HResult);
        var mail = Assert.Single(harness.Application.Created);
        Assert.Equal(0, mail.SaveCount);
        Assert.False(mail.WasSent);
        Assert.Empty(harness.Log.CallsOf("MailItem.Subject=", "MailItem.HTMLBody="));
    }

    [Fact]
    public async Task SendAsync_ResolveAllFailingWithAnUnknownError_TheRecipientsDecide()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();
        harness.Log.FailWith("Recipients.ResolveAll()", 0x80004005);

        var result = await session.SendAsync(OutlookHarness.Message(), OutlookSendMode.Send, Ct);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task SendAsync_ResolveAllRefusedByTheObjectModelGuard_IsSecurityDenied()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();
        harness.Log.FailWith("Recipients.ResolveAll()", 0x80004004);

        var result = await session.SendAsync(OutlookHarness.Message(), OutlookSendMode.Send, Ct);

        Assert.Equal(OutlookFailureKind.SecurityDenied, result.Failure!.Kind);
        Assert.Equal("recipients", result.Failure.Step);
        Assert.True(result.Failure.StopsRun);
    }

    [Fact]
    public async Task SendAsync_RecipientStatesUnreadable_ResolveAllDecides()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();
        harness.Application.ResolvedUnreadable = true;

        // Unreadable states with ResolveAll true: sent (MailerWPiA never looked at them) …
        var sent = await session.SendAsync(OutlookHarness.Message(), OutlookSendMode.Send, Ct);
        Assert.True(sent.Succeeded);

        // … with ResolveAll false and no readable state: refused.
        harness.Application.Unresolvable.Add("nieznany@contoso.test");
        var refused = await session.SendAsync(OutlookHarness.Message("nieznany@contoso.test"), OutlookSendMode.Send, Ct);
        Assert.Equal(OutlookFailureKind.RecipientUnresolved, refused.Failure!.Kind);
        Assert.False(harness.Application.Created[1].WasSent);
    }

    [Fact]
    public async Task SendAsync_AFailedBccType_FailsTheItem_SoNoBccBecomesVisible()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();
        harness.Log.FailWith("Recipient.Type=3", 0x80004005);

        var result = await session.SendAsync(
            OutlookHarness.Message(OutlookHarness.Own) with { Bcc = ["anna.nowak@contoso.test", "jan.nowak@contoso.test"] }, OutlookSendMode.Send, Ct);

        Assert.False(result.Succeeded);
        Assert.Equal("recipients", result.Failure!.Step);
        Assert.False(Assert.Single(harness.Application.Created).WasSent);
    }

    [Fact]
    public async Task SendAsync_BestEffortSettings_NeverFailTheItem()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();
        foreach (var member in new[] { "MailItem.BodyFormat=", "MailItem.ReadReceiptRequested=", "MailItem.Importance=", "Recipient.Resolve(", "MailItem.PropertyAccessor.SetProperty", "Attachment.PropertyAccessor.SetProperty" })
        {
            harness.Log.FailWith(member, 0x80004005);
        }

        var result = await session.SendAsync(
            OutlookHarness.Message() with
            {
                ReadReceipt = true,
                Importance = OutlookImportance.Low,
                CampaignId = "0123456789abcdef0123456789abcdef",
                InlineImages = [OutlookHarness.Image()],
            },
            OutlookSendMode.Send,
            Ct);

        Assert.Equal(OutlookItemResult.Sent, result);
        Assert.True(Assert.Single(harness.Application.Created).WasSent);
    }

    [Fact]
    public async Task SendAsync_DraftMode_SavesWithoutSending()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();

        var result = await session.SendAsync(OutlookHarness.Message(), OutlookSendMode.Draft, Ct);

        Assert.Equal(OutlookItemResult.Draft, result);
        var mail = Assert.Single(harness.Application.Created);
        Assert.Equal(1, mail.SaveCount);
        Assert.False(mail.WasSent);
        Assert.Empty(harness.Log.CallsOf("MailItem.Send()"));
    }

    [Fact]
    public async Task SendAsync_MissingAttachment_RefusesTheItem()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();
        var existing = harness.File("regulamin.pdf");

        var result = await session.SendAsync(
            OutlookHarness.Message() with { Attachments = [new OutlookAttachment(existing), new OutlookAttachment(Path.Combine(harness.Root, "brak – usunięty.pdf"))] },
            OutlookSendMode.Send,
            Ct);

        Assert.Equal(OutlookFailureKind.AttachmentMissing, result.Failure!.Kind);
        Assert.Equal("outlook.attachment", result.Failure.Code);
        Assert.Equal("attachments", result.Failure.Step);
        Assert.False(result.SavedAsDraft);
        Assert.Equal(1, Assert.Single(harness.Application.Created).ClosedWith);
    }

    [Fact]
    public async Task SendAsync_AttachmentWithADisplayName_UsesTheFourArgumentAdd()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();
        var file = harness.File("umowa.pdf");

        await session.SendAsync(
            OutlookHarness.Message() with { Attachments = [new OutlookAttachment(file, "Umowa – wersja ostateczna.pdf")] }, OutlookSendMode.Send, Ct);

        Assert.Equal(["Attachments.Add(umowa.pdf,1,missing,Umowa – wersja ostateczna.pdf)"], harness.Log.CallsOf("Attachments.Add"));
        Assert.Equal("Umowa – wersja ostateczna.pdf", Assert.Single(Assert.Single(harness.Application.Created).AttachmentList.Items).DisplayName);
    }

    [Theory]
    [InlineData(OutlookHarness.Own, 0)]
    [InlineData("SEKRETARIAT.LODZ@contoso.test", 1)]
    [InlineData(" archiwum.zolc@contoso.test ", 2)]
    public async Task SendAsync_Account_IsMatchedBySmtpAddress(string address, int index)
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();

        var result = await session.SendAsync(OutlookHarness.Message() with { SendUsingAccount = address }, OutlookSendMode.Send, Ct);

        Assert.True(result.Succeeded);
        Assert.Same(harness.Application.NameSpace.AccountList.Items[index], Assert.Single(harness.Application.Created).SendUsingAccount);
        harness.AssertAllReleased();
    }

    [Fact]
    public async Task SendAsync_UnknownAccount_IsInvalidAccount_BeforeAnythingElse()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();

        var result = await session.SendAsync(OutlookHarness.Message() with { SendUsingAccount = "obca.skrzynka@fabrikam.test" }, OutlookSendMode.Send, Ct);

        Assert.Equal(OutlookFailureKind.InvalidAccount, result.Failure!.Kind);
        Assert.Equal("outlook.account", result.Failure.Code);
        Assert.Equal("account", result.Failure.Step);
        Assert.Empty(harness.Log.CallsOf("Recipients.Add", "MailItem.Save()"));
        Assert.Equal(1, Assert.Single(harness.Application.Created).ClosedWith);
    }

    [Fact]
    public async Task SendAsync_TheCurrentUsersAddressInAProfileWithoutAccounts_KeepsTheDefaultAccount()
    {
        await using var harness = new OutlookHarness();
        harness.Application.NameSpace.AccountList.Items.Clear();
        var session = await harness.ConnectAsync();

        var result = await session.SendAsync(OutlookHarness.Message() with { SendUsingAccount = OutlookHarness.Own }, OutlookSendMode.Send, Ct);

        Assert.True(result.Succeeded);
        Assert.Null(Assert.Single(harness.Application.Created).SendUsingAccount);
    }

    [Fact]
    public async Task SendAsync_AccountRefusedAsAProperty_IsSetByReference()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();
        harness.Log.FailWith("MailItem.SendUsingAccount=", 0x80020003, times: 1);

        var result = await session.SendAsync(OutlookHarness.Message() with { SendUsingAccount = OutlookHarness.Secretariat }, OutlookSendMode.Send, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(2, harness.Log.CallsOf("MailItem.SendUsingAccount=").Count);
        Assert.Same(harness.Application.NameSpace.AccountList.Items[1], Assert.Single(harness.Application.Created).SendUsingAccount);
    }

    [Fact]
    public async Task SendAsync_AccountRefusedBothWays_FailsAtTheAccountStep()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();
        harness.Log.FailWith("MailItem.SendUsingAccount=", 0x80020003);

        var result = await session.SendAsync(OutlookHarness.Message() with { SendUsingAccount = OutlookHarness.Secretariat }, OutlookSendMode.Send, Ct);

        Assert.False(result.Succeeded);
        Assert.Equal("account", result.Failure!.Step);
        Assert.Equal(unchecked((int)0x80020003), result.Failure.HResult);
        Assert.False(Assert.Single(harness.Application.Created).WasSent);
    }

    [Fact]
    public async Task SendAsync_ReleasesEveryComObject_OfASuccessAndOfAFailure()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();
        var message = OutlookHarness.Message() with
        {
            Cc = ["lukasz.wisniewski@contoso.test"],
            ReplyTo = ["sekretariat.lodz@contoso.test"],
            SendUsingAccount = OutlookHarness.Archive,
            CampaignId = "c0ffee00c0ffee00c0ffee00c0ffee00",
            InlineImages = [OutlookHarness.Image(0x10), OutlookHarness.Image(0x20, "druga.png")],
            Attachments = [new OutlookAttachment(harness.File("umowa.pdf"))],
        };

        await session.SendAsync(message, OutlookSendMode.Send, Ct);
        harness.Log.FailWith("MailItem.Send()", 0x80010108);
        await session.SendAsync(message, OutlookSendMode.Send, Ct);

        harness.AssertAllReleased();
    }

    [Fact]
    public async Task SendAsync_Disconnected_EndsIsConnected()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();
        harness.Log.FailWith("Application.CreateItem", 0x800706BA);

        var result = await session.SendAsync(OutlookHarness.Message(), OutlookSendMode.Send, Ct);

        Assert.Equal(OutlookFailureKind.Disconnected, result.Failure!.Kind);
        Assert.Equal("create", result.Failure.Step);
        Assert.True(result.Failure.StopsRun);
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task SendAsync_FailureNeverCarriesTheMessageOfOutlook()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();
        harness.Log.FailWith("MailItem.Send()", 0x80004005);

        var result = await session.SendAsync(OutlookHarness.Message(), OutlookSendMode.Send, Ct);

        Assert.DoesNotContain("contoso", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Fake failure", result.Failure!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_WithoutAnyRecipient_IsRefused()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => session.SendAsync(OutlookHarness.Message() with { To = ["  ", ""] }, OutlookSendMode.Send, Ct));
        Assert.Empty(harness.Application.Created);
    }

    [Fact]
    public async Task SendAsync_BccOnly_IsAccepted()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();

        var result = await session.SendAsync(OutlookHarness.Message() with { To = [], Bcc = ["anna.nowak@contoso.test"] }, OutlookSendMode.Send, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(3, Assert.Single(Assert.Single(harness.Application.Created).RecipientList.Items).Type);
    }

    [Fact]
    public async Task SendAsync_CancelledBeforeTheItemStarted_Throws_AndBuildsNothing()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SendAsync(OutlookHarness.Message(), OutlookSendMode.Send, cancellation.Token));
        Assert.Empty(harness.Application.Created);
    }

    [Fact]
    public async Task SendAsync_InvalidCampaignAndHeaders_AreNotWritten()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();

        var result = await session.SendAsync(
            OutlookHarness.Message() with
            {
                CampaignId = "x' OR 1=1",
                CampaignKeys = ["skrzynka@contoso.test"],
                InternetHeaders = new Dictionary<string, string>
                {
                    ["X-AdMng-Message"] = "user-notification\r\nBcc: wszyscy@contoso.test",
                    ["X Bad"] = "x",
                    ["X-Empty"] = " \t ",
                },
            },
            OutlookSendMode.Send,
            Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(
            new Dictionary<string, object> { [MapiProperties.Header("X-AdMng-Message")] = "user-notification Bcc: wszyscy@contoso.test" },
            Assert.Single(harness.Application.Created).Accessor.Values);
    }

    [Fact]
    public async Task SendAsync_EmptyVotingOptions_AreNotSet()
    {
        await using var harness = new OutlookHarness();
        var session = await harness.ConnectAsync();

        await session.SendAsync(OutlookHarness.Message() with { VotingOptions = " ; ;" }, OutlookSendMode.Send, Ct);

        Assert.Empty(harness.Log.CallsOf("MailItem.VotingOptions="));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(" ; ", null)]
    [InlineData("Tak", "Tak")]
    [InlineData(" Tak; Nie ;;Może ", "Tak;Nie;Może")]
    public void NormalizeVoting_TrimsAndDropsEmptyOptions(string? options, string? expected) =>
        Assert.Equal(expected, OutlookMailBuilder.NormalizeVoting(options));

    [Fact]
    public void JoinKeys_WholeKeysOnly_UpTo4000Characters_WithoutLineBreaksInside()
    {
        var keys = Enumerable.Range(0, 300).Select(index => $"skrzynka{index:000}@contoso.test").ToList();

        var joined = OutlookMailBuilder.JoinKeys(["  ", "a\r\nb@contoso.test", .. keys]);

        Assert.True(joined.Length <= OutlookMailBuilder.MaxKeysLength);
        var parts = joined.Split('\n');
        Assert.Equal("a b@contoso.test", parts[0]);
        Assert.All(parts.Skip(1), part => Assert.Contains(part, keys));
        Assert.True(joined.Length + keys[parts.Length - 1].Length + 1 > OutlookMailBuilder.MaxKeysLength);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("kampania-2026", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("a'b", false)]
    [InlineData("zażółć", false)]
    public void IsCampaignId_AllowsOnlyTokensSafeInAFilter(string? id, bool expected) => Assert.Equal(expected, OutlookMailBuilder.IsCampaignId(id));
}
