using AdMng.Core.Correspondence.Outlook;
using AdMng.Core.Messaging;
using AdMng.Testing;

namespace AdMng.Outlook.Tests;

/// <summary>Messages to users (U1) through Outlook (design note 5).</summary>
public class OutlookUserMessageTransportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static OutgoingUserMessage Message(params string[] to) => new()
    {
        Id = Guid.Parse("6f1c2d3e-4a5b-4c6d-8e9f-0a1b2c3d4e5f"),
        From = new MessageAddress("helpdesk@contoso.test", "Dział IT"),
        To = [.. (to.Length == 0 ? ["anna.nowak@contoso.test"] : to).Select(address => new MessageAddress(address, "Anna Nowak"))],
        Cc = [new MessageAddress("jan.kowalski@contoso.test", "Jan Kowalski")],
        ReplyTo = new MessageAddress("pomoc@contoso.test"),
        Subject = "Hasło wygasa za 5 dni",
        TextBody = "Szanowna Pani,\n\nhasło wygasa.",
        HtmlBody = "<html><body><p>Szanowna Pani,</p><p>hasło wygasa.</p></body></html>",
        InlineImages = [OutlookHarness.Image()],
    };

    [Theory]
    [InlineData(OutlookAvailability.Installed, true, null)]
    [InlineData(OutlookAvailability.NotInstalled, false, "notInstalled")]
    [InlineData(OutlookAvailability.NotSupported, false, "notSupported")]
    public async Task GetStatusAsync_MapsTheAvailability_WithoutConnecting(OutlookAvailability availability, bool available, string? reason)
    {
        var client = new FakeOutlookClient { Availability = availability };

        var status = await new OutlookUserMessageTransport(client).GetStatusAsync(Ct);

        Assert.Equal(available, status.IsAvailable);
        Assert.Equal(reason, status.Reason);
        if (available)
        {
            Assert.Same(UserMessageTransportCapabilities.Outlook, status.Capabilities);
        }

        Assert.Equal(0, client.Connections);
    }

    [Fact]
    public void Description_AndKind()
    {
        var client = new FakeOutlookClient();

        Assert.Equal(UserMessageTransportKind.Outlook, new OutlookUserMessageTransport(client).Kind);
        Assert.Equal("Outlook (default account)", new OutlookUserMessageTransport(client, "  ").Description);
        Assert.Equal("Outlook (sekretariat.lodz@contoso.test)", new OutlookUserMessageTransport(client, " sekretariat.lodz@contoso.test ").Description);
        Assert.Throws<ArgumentNullException>(() => new OutlookUserMessageTransport(null!));
    }

    [Fact]
    public async Task SendAsync_MapsTheMessage_ToOutlook()
    {
        var client = new FakeOutlookClient();
        var transport = new OutlookUserMessageTransport(client, "sekretariat.lodz@contoso.test", onBehalfOfSender: true);
        var message = Message();

        await using (var connection = await transport.ConnectAsync(Ct))
        {
            var delivery = await connection.SendAsync(message, Ct);

            Assert.Equal(UserMessageDeliveryOutcome.Accepted, delivery.Outcome);
            Assert.Equal("outlook.sent", delivery.Code);
            Assert.True(delivery.IsAccepted);
        }

        var item = Assert.Single(client.Items);
        Assert.Equal(OutlookSendMode.Send, item.Mode);
        var outlook = item.Message;
        Assert.Equal(["anna.nowak@contoso.test"], outlook.To);
        Assert.Equal(["jan.kowalski@contoso.test"], outlook.Cc);
        Assert.Empty(outlook.Bcc);
        Assert.Equal(["pomoc@contoso.test"], outlook.ReplyTo);
        Assert.Equal(message.Subject, outlook.Subject);
        Assert.Equal(message.HtmlBody, outlook.HtmlBody);
        Assert.Equal(message.InlineImages, outlook.InlineImages);
        Assert.Empty(outlook.Attachments);
        Assert.Equal("sekretariat.lodz@contoso.test", outlook.SendUsingAccount);
        Assert.Equal("helpdesk@contoso.test", outlook.SentOnBehalfOf);
        Assert.False(outlook.ReadReceipt);
        Assert.Null(outlook.DeferredDelivery);
        Assert.Null(outlook.VotingOptions);
        Assert.Null(outlook.CampaignId);
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["Auto-Submitted"] = "auto-generated",
                ["X-Auto-Response-Suppress"] = "All",
                ["X-AdMng-Message"] = "user-notification",
            },
            outlook.InternetHeaders);
        Assert.True(client.Sessions[0].IsDisposed);
    }

    [Fact]
    public async Task SendAsync_WithoutOnBehalf_AndTheDefaultAccount()
    {
        var client = new FakeOutlookClient();
        await using var connection = await new OutlookUserMessageTransport(client).ConnectAsync(Ct);

        await connection.SendAsync(Message() with { ReplyTo = null, Cc = [] }, Ct);

        var outlook = Assert.Single(client.Items).Message;
        Assert.Null(outlook.SendUsingAccount);
        Assert.Null(outlook.SentOnBehalfOf);
        Assert.Empty(outlook.ReplyTo);
        Assert.Empty(outlook.Cc);
    }

    [Theory]
    [InlineData(OutlookFailureKind.Busy, UserMessageDeliveryOutcome.Transient, "outlook.busy")]
    [InlineData(OutlookFailureKind.NotResponding, UserMessageDeliveryOutcome.Transient, "outlook.notResponding")]
    [InlineData(OutlookFailureKind.SecurityDenied, UserMessageDeliveryOutcome.TransportFailed, "outlook.denied")]
    [InlineData(OutlookFailureKind.Disconnected, UserMessageDeliveryOutcome.TransportFailed, "outlook.disconnected")]
    [InlineData(OutlookFailureKind.Elevated, UserMessageDeliveryOutcome.TransportFailed, "outlook.elevated")]
    [InlineData(OutlookFailureKind.RecipientUnresolved, UserMessageDeliveryOutcome.Rejected, "outlook.recipient")]
    [InlineData(OutlookFailureKind.InvalidAccount, UserMessageDeliveryOutcome.Rejected, "outlook.account")]
    [InlineData(OutlookFailureKind.ItemTooLarge, UserMessageDeliveryOutcome.Rejected, "outlook.tooLarge")]
    [InlineData(OutlookFailureKind.Unknown, UserMessageDeliveryOutcome.Rejected, "outlook.error")]
    public async Task SendAsync_ClassifiesTheFailure(OutlookFailureKind kind, UserMessageDeliveryOutcome outcome, string code)
    {
        var client = new FakeOutlookClient();
        client.Script("anna.nowak@contoso.test", FakeOutlookClient.Failed(kind, savedAsDraft: true));
        await using var connection = await new OutlookUserMessageTransport(client).ConnectAsync(Ct);

        var delivery = await connection.SendAsync(Message(), Ct);
        var next = await connection.SendAsync(Message(), Ct);

        Assert.Equal(outcome, delivery.Outcome);
        Assert.Equal(code, delivery.Code);
        Assert.Equal(outcome == UserMessageDeliveryOutcome.TransportFailed, delivery.StopsRun);
        Assert.Null(delivery.SmtpStatus);
        Assert.True(next.IsAccepted);
    }

    [Fact]
    public void ToDelivery_AFailureWithoutDetails_IsRejectedAsAnError()
    {
        var delivery = OutlookUserMessageTransport.ToDelivery(new OutlookItemResult(false, false, null));

        Assert.Equal(UserMessageDeliveryOutcome.Rejected, delivery.Outcome);
        Assert.Equal("outlook.error", delivery.Code);
    }

    [Theory]
    [InlineData(OutlookFailureKind.NotInstalled, "outlook.notInstalled")]
    [InlineData(OutlookFailureKind.Elevated, "outlook.elevated")]
    [InlineData(OutlookFailureKind.Busy, "outlook.busy")]
    public async Task ConnectAsync_Failure_IsATransportFailure_WithTheCode(OutlookFailureKind kind, string code)
    {
        var client = new FakeOutlookClient { ConnectFailure = FakeOutlookClient.Failure(kind, "connect") };

        var failure = await Assert.ThrowsAsync<UserMessageTransportException>(() => new OutlookUserMessageTransport(client).ConnectAsync(Ct));

        Assert.Equal(UserMessageDeliveryOutcome.TransportFailed, failure.Delivery.Outcome);
        Assert.Equal(code, failure.Delivery.Code);
        Assert.True(failure.Delivery.StopsRun);
    }

    [Fact]
    public async Task ConnectAsync_NotSupported_IsATransportFailure()
    {
        var client = new FakeOutlookClient { Availability = OutlookAvailability.NotSupported };

        var failure = await Assert.ThrowsAsync<UserMessageTransportException>(() => new OutlookUserMessageTransport(client).ConnectAsync(Ct));

        Assert.Equal("outlook.notSupported", failure.Delivery.Code);
    }

    [Fact]
    public async Task SendAsync_Cancelled_Throws()
    {
        var client = new FakeOutlookClient();
        await using var connection = await new OutlookUserMessageTransport(client).ConnectAsync(Ct);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.SendAsync(Message(), cancellation.Token));
        Assert.Empty(client.Items);
    }

    [Fact]
    public async Task SendAsync_AfterTheSessionEnded_IsATransportFailure()
    {
        var client = new FakeOutlookClient();
        var connection = await new OutlookUserMessageTransport(client).ConnectAsync(Ct);
        await client.Sessions[0].DisposeAsync();

        var delivery = await connection.SendAsync(Message(), Ct);

        Assert.Equal(UserMessageDeliveryOutcome.TransportFailed, delivery.Outcome);
        Assert.Equal("outlook.disconnected", delivery.Code);
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task EndToEnd_ThroughTheRealEngineAndTheFakeObjectModel()
    {
        await using var harness = new OutlookHarness();
        var transport = new OutlookUserMessageTransportFactory(harness.Client).Create(OutlookHarness.Secretariat, onBehalfOfSender: false);

        await using (var connection = await transport.ConnectAsync(Ct))
        {
            var delivery = await connection.SendAsync(Message(), Ct);
            Assert.Equal("outlook.sent", delivery.Code);
        }

        var mail = Assert.Single(harness.Application.Created);
        Assert.True(mail.WasSent);
        Assert.Equal("Hasło wygasa za 5 dni", mail.Subject);
        Assert.Equal(["anna.nowak@contoso.test", "jan.kowalski@contoso.test"], mail.RecipientList.Items.Select(recipient => recipient.Address));
        Assert.Equal([1, 2], mail.RecipientList.Items.Select(recipient => recipient.Type));
        Assert.Equal(["pomoc@contoso.test"], mail.ReplyList.Items.Select(recipient => recipient.Address));
        Assert.Same(harness.Application.NameSpace.AccountList.Items[1], mail.SendUsingAccount);
        Assert.Equal("user-notification", mail.Accessor.Values[MapiProperties.Header("X-AdMng-Message")]);
        Assert.Equal("auto-generated", mail.Accessor.Values[MapiProperties.Header("Auto-Submitted")]);
        Assert.Equal(Message().InlineImages[0].ContentId, Assert.Single(mail.AttachmentList.Items).Accessor.Values[MapiProperties.AttachContentId]);
        harness.AssertAllReleased(harness.Application);
    }

    [Fact]
    public async Task EndToEnd_AnUnresolvedRecipient_IsRejected_TheRunGoesOn()
    {
        await using var harness = new OutlookHarness();
        harness.Application.Unresolvable.Add("zly-adres@contoso");
        var transport = new OutlookUserMessageTransport(harness.Client);

        await using var connection = await transport.ConnectAsync(Ct);
        var rejected = await connection.SendAsync(Message("zly-adres@contoso"), Ct);
        var accepted = await connection.SendAsync(Message(), Ct);

        Assert.Equal(UserMessageDeliveryOutcome.Rejected, rejected.Outcome);
        Assert.Equal("outlook.recipient", rejected.Code);
        Assert.False(rejected.StopsRun);
        Assert.True(accepted.IsAccepted);
    }

    [Fact]
    public void Factory_CreatesTransportsOfTheClient()
    {
        var client = new FakeOutlookClient();
        var factory = new OutlookUserMessageTransportFactory(client);

        var transport = Assert.IsType<OutlookUserMessageTransport>(factory.Create(" konto@contoso.test ", onBehalfOfSender: true));

        Assert.Same(client, transport.Client);
        Assert.Equal("konto@contoso.test", transport.Account);
        Assert.True(transport.OnBehalfOfSender);
        Assert.Null(((OutlookUserMessageTransport)factory.Create(null, false)).Account);
        Assert.Throws<ArgumentNullException>(() => new OutlookUserMessageTransportFactory(null!));
        Assert.Equal(nameof(OutlookUserMessageTransportFactory), factory.ToString());
    }

    [Fact]
    public void FakeFactory_RecordsTheChoice()
    {
        var factory = new FakeOutlookUserMessageTransportFactory();

        var transport = factory.Create(" sekretariat.lodz@contoso.test ", onBehalfOfSender: true);

        Assert.Equal(UserMessageTransportKind.Outlook, transport.Kind);
        Assert.Equal("Outlook (sekretariat.lodz@contoso.test)", transport.Description);
        Assert.Equal((" sekretariat.lodz@contoso.test ", true), Assert.Single(factory.Created));
    }
}
