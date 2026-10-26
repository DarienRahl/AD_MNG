using AdMng.Core.Messaging;
using AdMng.Presentation.UserMessages;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Testing.Client;

namespace AdMng.Presentation.Tests.UserMessages;

/// <summary>
/// <see cref="WatchdogUserMessageTransport"/> against <see cref="FakeWatchdogClient"/> (README U1b): status reasons, the
/// request built from a rendered message, the mapping of every answer and failure of the service to the dispatcher's
/// outcomes, and cancellation.
/// </summary>
public sealed class WatchdogUserMessageTransportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static OutgoingUserMessage Message() => new()
    {
        Id = Guid.Parse("0192f3a4-5b6c-7d8e-9f00-112233445566"),
        From = new MessageAddress("helpdesk@contoso.test", "Dział IT"),
        To = [new MessageAddress("zofia.zak@contoso.test", "Zofia Żak")],
        Cc = [new MessageAddress("jan.kowalski@contoso.test")],
        ReplyTo = new MessageAddress("pomoc@contoso.test"),
        Subject = "Twoje hasło wygasa 5 listopada 2026",
        TextBody = "Dzień dobry, Zofio!",
        HtmlBody = "<p>Dzień dobry, Zofio!</p>",
        Language = MessageLanguage.English,
    };

    [Fact]
    public async Task Status_ReportsWhyTheWatchdogCannotBeUsed()
    {
        var client = new FakeWatchdogClient(sampleData: false);
        var transport = new WatchdogUserMessageTransport(client);

        Assert.Equal(UserMessageTransportStatus.Disabled, (await transport.GetStatusAsync(Ct)).Reason);
        client.UserMessagingStatus = client.UserMessagingStatus with { Enabled = true };
        Assert.True((await transport.GetStatusAsync(Ct)).IsAvailable);
        client.UserMessagingStatus = client.UserMessagingStatus with { SmtpConfigured = false };
        Assert.Equal(UserMessageTransportStatus.Disabled, (await transport.GetStatusAsync(Ct)).Reason);

        client.FailWith(WatchdogClientErrorKind.NotFound, nameof(IWatchdogClient.GetUserMessagingStatusAsync));
        Assert.Equal(UserMessageTransportStatus.NotSupported, (await transport.GetStatusAsync(Ct)).Reason);
        client.FailWith(WatchdogClientErrorKind.Forbidden, nameof(IWatchdogClient.GetUserMessagingStatusAsync));
        Assert.Equal(UserMessageTransportStatus.Forbidden, (await transport.GetStatusAsync(Ct)).Reason);
        client.FailWith(WatchdogClientErrorKind.Unreachable, nameof(IWatchdogClient.GetUserMessagingStatusAsync));
        Assert.Equal(UserMessageTransportStatus.Unreachable, (await transport.GetStatusAsync(Ct)).Reason);
        client.Configure(null);
        Assert.Equal(UserMessageTransportStatus.NotConfigured, (await transport.GetStatusAsync(Ct)).Reason);
        Assert.Equal(UserMessageTransportStatus.NotConfigured, (await new WatchdogUserMessageTransport().GetStatusAsync(Ct)).Reason);

        Assert.Equal(UserMessageTransportChoiceResult.WatchdogDisabled, UserMessageTransportChoiceResult.ForWatchdog(new UserMessageTransportStatus(false, UserMessageTransportStatus.Disabled)));
        Assert.Equal(UserMessageTransportChoiceResult.WatchdogForbidden, UserMessageTransportChoiceResult.ForWatchdog(new UserMessageTransportStatus(false, UserMessageTransportStatus.Forbidden)));
        Assert.Equal(UserMessageTransportChoiceResult.WatchdogUnreachable, UserMessageTransportChoiceResult.ForWatchdog(new UserMessageTransportStatus(false, UserMessageTransportStatus.Unreachable)));
    }

    [Fact]
    public async Task Send_PassesTheRenderedMessageUnchanged_AndMapsDeliveredWithRejectedCopies()
    {
        var client = Enabled();
        client.UserMessageHandler = r => new UserMessageResult
        {
            Id = r.Id, State = UserMessageState.Delivered, Code = "ok", SmtpStatus = 250, RejectedCopies = ["jan.kowalski@contoso.test"],
        };
        var transport = new WatchdogUserMessageTransport(client);
        await using var connection = await transport.ConnectAsync(Ct);

        var delivery = await connection.SendAsync(Message(), Ct);

        Assert.Equal((UserMessageDeliveryOutcome.Accepted, "ok", 250), (delivery.Outcome, delivery.Code, delivery.SmtpStatus));
        Assert.Equal(["jan.kowalski@contoso.test"], delivery.RejectedCopies);
        var request = Assert.Single(client.UserMessages);
        Assert.Equal(Message().Id, request.Id);
        Assert.Equal(("helpdesk@contoso.test", "Dział IT"), (request.From!.Address, request.From.DisplayName));
        Assert.Equal(("zofia.zak@contoso.test", "Zofia Żak"), (request.To[0].Address, request.To[0].DisplayName));
        Assert.Equal(("jan.kowalski@contoso.test", null), (request.Cc[0].Address, request.Cc[0].DisplayName));
        Assert.Equal(("pomoc@contoso.test", "en"), (request.ReplyTo!.Address, request.Language));
        Assert.Equal((Message().Subject, Message().TextBody, Message().HtmlBody), (request.Subject, request.TextBody, request.HtmlBody));
        Assert.Equal("AdMng Watchdog (https://admng-wd.contoso.com:8443)", transport.Description);
    }

    [Theory]
    [InlineData(UserMessageState.Queued, "smtp.451.4.7.1", 451, UserMessageDeliveryOutcome.Accepted, "watchdog.queued")]
    [InlineData(UserMessageState.Rejected, "smtp.550.5.1.1", 550, UserMessageDeliveryOutcome.Rejected, "smtp.550.5.1.1")]
    [InlineData(UserMessageState.Failed, "auth", null, UserMessageDeliveryOutcome.AuthenticationFailed, "auth")]
    [InlineData(UserMessageState.Failed, "smtp.535.5.7.139", 535, UserMessageDeliveryOutcome.AuthenticationFailed, "smtp.535.5.7.139")]
    [InlineData(UserMessageState.Failed, "tls", null, UserMessageDeliveryOutcome.TransportFailed, "tls")]
    [InlineData(UserMessageState.Failed, "userMessages.disabled", null, UserMessageDeliveryOutcome.TransportFailed, "userMessages.disabled")]
    public void Answers_MapToTheDispatchersOutcomes(UserMessageState state, string code, int? status, UserMessageDeliveryOutcome outcome, string expectedCode)
    {
        var delivery = WatchdogUserMessageTransport.FromResult(new UserMessageResult { Id = Guid.NewGuid(), State = state, Code = code, SmtpStatus = status });

        Assert.Equal((outcome, expectedCode), (delivery.Outcome, delivery.Code));
    }

    [Theory]
    [InlineData(WatchdogClientErrorKind.Validation, 409, ApiProblem.UserMessagesUnavailable, null, UserMessageDeliveryOutcome.TransportFailed, "watchdog.disabled")]
    [InlineData(WatchdogClientErrorKind.ServerError, 429, ApiProblem.RateLimited, null, UserMessageDeliveryOutcome.TransportFailed, "watchdog.limit")]
    [InlineData(WatchdogClientErrorKind.Validation, 409, ApiProblem.IdempotencyConflict, null, UserMessageDeliveryOutcome.Rejected, "watchdog.conflict")]
    [InlineData(WatchdogClientErrorKind.Validation, 400, ApiProblem.Validation, "senderNotAllowed", UserMessageDeliveryOutcome.TransportFailed, "watchdog.senderNotAllowed")]
    [InlineData(WatchdogClientErrorKind.Validation, 400, ApiProblem.Validation, "forbiddenHtml", UserMessageDeliveryOutcome.Rejected, "watchdog.invalid")]
    [InlineData(WatchdogClientErrorKind.Forbidden, 403, null, null, UserMessageDeliveryOutcome.AuthenticationFailed, "watchdog.forbidden")]
    [InlineData(WatchdogClientErrorKind.Unauthorized, 401, null, null, UserMessageDeliveryOutcome.AuthenticationFailed, "watchdog.forbidden")]
    [InlineData(WatchdogClientErrorKind.NotFound, 404, null, null, UserMessageDeliveryOutcome.TransportFailed, "watchdog.notSupported")]
    [InlineData(WatchdogClientErrorKind.CertificateUntrusted, null, null, null, UserMessageDeliveryOutcome.TransportFailed, "tls")]
    [InlineData(WatchdogClientErrorKind.Timeout, null, null, null, UserMessageDeliveryOutcome.Transient, "timeout")]
    [InlineData(WatchdogClientErrorKind.Unreachable, null, null, null, UserMessageDeliveryOutcome.Transient, "watchdog.unreachable")]
    [InlineData(WatchdogClientErrorKind.ServerError, 500, null, null, UserMessageDeliveryOutcome.Transient, "watchdog.serverError")]
    public async Task Failures_AreClassified_AndNeverThrown(
        WatchdogClientErrorKind kind, int? status, string? problem, string? issue, UserMessageDeliveryOutcome outcome, string code)
    {
        var client = Enabled();
        var transport = new WatchdogUserMessageTransport(client);
        await using var connection = await transport.ConnectAsync(Ct);
        client.FailNext(nameof(IWatchdogClient.SendUserMessageAsync), new WatchdogClientException(kind, "scripted")
        {
            StatusCode = status,
            ProblemType = problem,
            Issues = issue is null ? [] : [new ConfigIssue { Path = "from", Code = issue, Severity = IssueSeverity.Error, Message = "x" }],
        });

        var delivery = await connection.SendAsync(Message(), Ct);

        Assert.Equal((outcome, code), (delivery.Outcome, delivery.Code));
        Assert.Equal(outcome is UserMessageDeliveryOutcome.AuthenticationFailed or UserMessageDeliveryOutcome.TransportFailed, delivery.StopsRun);
    }

    [Fact]
    public async Task Connect_FailsClassified_WhenTheServiceCannotTakeMessages()
    {
        var client = new FakeWatchdogClient(sampleData: false);
        var transport = new WatchdogUserMessageTransport(client);

        var disabled = await Assert.ThrowsAsync<UserMessageTransportException>(() => transport.ConnectAsync(Ct));
        client.FailWith(WatchdogClientErrorKind.Unreachable, nameof(IWatchdogClient.GetUserMessagingStatusAsync));
        var unreachable = await Assert.ThrowsAsync<UserMessageTransportException>(() => transport.ConnectAsync(Ct));

        Assert.Equal((UserMessageDeliveryOutcome.TransportFailed, "watchdog.disabled"), (disabled.Delivery.Outcome, disabled.Delivery.Code));
        Assert.Equal((UserMessageDeliveryOutcome.Transient, "watchdog.unreachable"), (unreachable.Delivery.Outcome, unreachable.Delivery.Code));
    }

    [Fact]
    public async Task Cancellation_IsAnOperationCanceledException()
    {
        var client = Enabled();
        var transport = new WatchdogUserMessageTransport(client);
        await using var connection = await transport.ConnectAsync(Ct);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.SendAsync(Message(), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.GetStatusAsync(cancelled.Token));
        Assert.Empty(client.UserMessages);
    }

    private static FakeWatchdogClient Enabled()
    {
        var client = new FakeWatchdogClient(sampleData: false);
        client.UserMessagingStatus = client.UserMessagingStatus with { Enabled = true };
        return client;
    }
}
