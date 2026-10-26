using System.Net;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Notifications;
using AdMng.Watchdog.Service.Storage;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using AdMng.Watchdog.Service.Tests.Notifications;
using AdMng.Watchdog.Service.UserMessages;
using AdMng.Watchdog.Testing.Smtp;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace AdMng.Watchdog.Service.Tests.Api;

/// <summary>
/// <c>/api/v1/user-messages</c> in process (README U1b): roles (Admin, Viewer, none, denied), the service's own
/// validation, idempotency by message id, the hourly limit, the switch, delivery through the outbox job to an SMTP sink
/// with the service's envelope sender, a permanent rejection and a queued answer – and nothing of the content in the
/// delivery log, the audit row or the diagnostic log.
/// </summary>
public sealed class UserMessageApiTests : IAsyncLifetime
{
    private const string Zofia = "zofia.zak@contoso.test";
    private const string Manager = "jan.kowalski@contoso.test";
    private const string Subject = "Twoje hasło wygasa 5 listopada 2026 – Zażółć gęślą jaźń";

    private readonly SmtpSink _sink = new();
    private readonly WatchdogFactory _factory;

    public UserMessageApiTests()
    {
        // The outbox job is hosted (the factory removes the service's jobs): it delivers what the endpoint enqueues.
        _factory = new WatchdogFactory(services: s => s.AddSingleton<IWatchdogJob>(p => p.GetRequiredService<OutboxSenderJob>()));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _sink.StartAsync(Ct);

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _sink.DisposeAsync();
    }

    [Fact]
    public async Task Viewer_SendsAMessage_ThroughTheOutbox_WithTheServicesEnvelopeSender_AndNoContentIsKept()
    {
        await ConfigureAsync(_sink.Port);
        using var viewer = _factory.Viewer();

        using var response = await viewer.PostAsync(WatchdogRoutes.UserMessages, ApiJson.Content(Request()), Ct);
        var result = await ApiJson.ReadAsync<UserMessageResult>(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((UserMessageState.Delivered, "ok", 250, false), (result.State, result.Code, result.SmtpStatus, result.Duplicate));
        var received = Assert.Single(_sink.Messages);
        Assert.Equal("watchdog@contoso.test", received.MailFrom);                       // envelope: the channel's sender
        Assert.Equal([Manager, Zofia], received.Recipients.Order(StringComparer.Ordinal));
        var mime = MimeMessage.Load(new MemoryStream(received.Data), Ct);
        Assert.Equal(Subject, mime.Subject);
        Assert.Equal("helpdesk@contoso.test", mime.From.Mailboxes.Single().Address);     // allowed through @contoso.test
        Assert.Equal("Dział IT", mime.From.Mailboxes.Single().Name);
        Assert.Equal(Zofia, mime.To.Mailboxes.Single().Address);
        Assert.Equal(Manager, mime.Cc.Mailboxes.Single().Address);
        Assert.Equal("user-notification", mime.Headers["X-AdMng-Message"]);
        Assert.Equal("auto-generated", mime.Headers["Auto-Submitted"]);
        Assert.Null(mime.Headers["X-AdMng-Severity"]);
        Assert.Equal($"{Request().Id:N}@watchdog.contoso.test", mime.MessageId);
        Assert.Contains("Zażółć", mime.TextBody, StringComparison.Ordinal);
        Assert.Empty(mime.Attachments);

        // The delivery log keeps neither subject nor addresses; the payload is gone once sent.
        var item = Assert.Single((await _factory.Store.QueryAsync(new OutboxQuery(), Ct)).Items);
        Assert.Equal((DeliveryKind.UserMessage, OutboxStatus.Sent, null, 2, result.OutboxId), (item.Kind, item.Status, item.Subject, item.RecipientCount, (long?)item.Id));
        var (payloads, recipients) = _factory.Store.Database.Read(c => (
            SqliteValues.Scalar<long>(c, null, "SELECT count(*) FROM outbox WHERE payload IS NOT NULL;"),
            SqliteValues.Scalar<long>(c, null, "SELECT count(*) FROM outbox WHERE recipients IS NOT NULL;")));
        Assert.Equal((0, 0), (payloads, recipients));

        // The audit row: who, when, how many and a hash of the recipients – never an address.
        var audit = _factory.Store.Database.Read(c => SqliteValues.Query(c, null,
            "SELECT by_account, recipient_count, recipients_hash, submitted_at FROM user_messages;",
            r => (Account: r.GetString(0), Count: r.GetInt32(1), Hash: r.GetString(2), At: r.GetInt64(3))));
        var row = Assert.Single(audit);
        Assert.Equal((TestRoles.ViewerAccount, 2), (row.Account, row.Count));
        Assert.Equal(64, row.Hash.Length);
        Assert.DoesNotContain("contoso", row.Hash, StringComparison.Ordinal);
        Assert.Equal(_factory.Clock.GetUtcNow().ToUnixTimeMilliseconds(), row.At);

        var log = string.Join('\n', _factory.Log.Entries.Select(e => e.Message));
        Assert.Contains($"Message to a user {Request().Id:N} submitted by {TestRoles.ViewerAccount}: 2 recipient(s)", log, StringComparison.Ordinal);
        Assert.DoesNotContain(Zofia, log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wygasa", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(row.Hash, log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedId_IsIdempotent_AndAnotherCallerOrOtherContentConflicts()
    {
        await ConfigureAsync(_sink.Port);
        using var viewer = _factory.Viewer();
        using var admin = _factory.Admin();
        using var first = await viewer.PostAsync(WatchdogRoutes.UserMessages, ApiJson.Content(Request()), Ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var again = await viewer.PostAsync(WatchdogRoutes.UserMessages, ApiJson.Content(Request()), Ct);
        using var changed = await viewer.PostAsync(WatchdogRoutes.UserMessages, ApiJson.Content(Request() with { Subject = "Inny temat" }), Ct);
        using var other = await admin.PostAsync(WatchdogRoutes.UserMessages, ApiJson.Content(Request()), Ct);

        var repeated = await ApiJson.ReadAsync<UserMessageResult>(again);
        Assert.Equal((HttpStatusCode.OK, UserMessageState.Delivered, true), (again.StatusCode, repeated.State, repeated.Duplicate));
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal(ApiProblem.IdempotencyConflict, (await ApiJson.ProblemAsync(changed)).GetProperty("type").GetString());
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
        Assert.Single(_sink.Messages);
        Assert.Single((await _factory.Store.QueryAsync(new OutboxQuery(), Ct)).Items);
    }

    [Fact]
    public async Task AdminsAndViewers_MaySend_NobodyElse_AndDeniedSidsWin()
    {
        await ConfigureAsync(_sink.Port);
        using var admin = _factory.Admin();
        using var anonymous = _factory.Client(null);
        using var other = _factory.Client(@"CONTOSO\obcy", TestRoles.OtherGroup);
        using var denied = _factory.Client(@"CONTOSO\odwołany", TestRoles.ViewerGroup, TestRoles.DeniedUser);
        using var viewer = _factory.Viewer();

        using var byAdmin = await admin.PostAsync(WatchdogRoutes.UserMessages, ApiJson.Content(Request()), Ct);
        using var byAnonymous = await anonymous.PostAsync(WatchdogRoutes.UserMessages, ApiJson.Content(Request(Guid.NewGuid())), Ct);
        using var byOther = await other.PostAsync(WatchdogRoutes.UserMessages, ApiJson.Content(Request(Guid.NewGuid())), Ct);
        using var byDenied = await denied.PostAsync(WatchdogRoutes.UserMessages, ApiJson.Content(Request(Guid.NewGuid())), Ct);
        using var statusOther = await other.GetAsync(WatchdogRoutes.UserMessagesStatus, Ct);
        var status = await ApiJson.ReadAsync<UserMessagingStatusDto>(await viewer.GetAsync(WatchdogRoutes.UserMessagesStatus, Ct));

        Assert.Equal(HttpStatusCode.OK, byAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, byAnonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, byOther.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, byDenied.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, statusOther.StatusCode);
        Assert.Single(_sink.Messages);
        Assert.True(status.Available);
        Assert.Equal(($"127.0.0.1:{_sink.Port}", "watchdog@contoso.test", 1, 1), (status.Channel, status.Sender, status.SubmittedLastHour, status.SubmittedLastDay));
        Assert.Equal(["@contoso.test"], status.AllowedSenders);
    }

    [Fact]
    public async Task SwitchedOff_Is409_AndTheStatusSaysSo_AndNothingIsQueued()
    {
        await ConfigureAsync(_sink.Port, settings => settings with { Enabled = false });
        using var viewer = _factory.Viewer();

        var status = await ApiJson.ReadAsync<UserMessagingStatusDto>(await viewer.GetAsync(WatchdogRoutes.UserMessagesStatus, Ct));
        using var response = await viewer.PostAsync(WatchdogRoutes.UserMessages, ApiJson.Content(Request()), Ct);

        Assert.Equal((false, true, false), (status.Enabled, status.SmtpConfigured, status.Available));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ApiProblem.UserMessagesUnavailable, (await ApiJson.ProblemAsync(response)).GetProperty("type").GetString());
        Assert.Equal([UserMessageIssueCodes.Disabled], await ApiJson.IssueCodesAsync(response));
        Assert.Empty((await _factory.Store.QueryAsync(new OutboxQuery(), Ct)).Items);
    }

    [Theory]
    [InlineData("subjectNewline", "headerInjection")]
    [InlineData("displayNameNewline", "headerInjection")]
    [InlineData("threeTo", "range")]
    [InlineData("tooManyRecipients", "range")]
    [InlineData("script", "forbiddenHtml")]
    [InlineData("javascriptLink", "forbiddenHtml")]
    [InlineData("foreignSender", "senderNotAllowed")]
    [InlineData("foreignReplyTo", "senderNotAllowed")]
    [InlineData("addressList", "invalidAddress")]
    [InlineData("longSubject", "tooLong")]
    [InlineData("language", "invalidLanguage")]
    [InlineData("emptyId", "required")]
    public async Task InvalidMessages_Are400_WithTheirIssue_AndNothingIsQueued(string variant, string code)
    {
        await ConfigureAsync(_sink.Port);
        using var viewer = _factory.Viewer();
        var request = Request();
        request = variant switch
        {
            "subjectNewline" => request with { Subject = "Hasło\r\nBcc: wszyscy@contoso.test" },
            "displayNameNewline" => request with { To = [new UserMessageAddressDto { Address = Zofia, DisplayName = "Zofia\nBcc: x@contoso.test" }] },
            "threeTo" => request with { To = [Address(Zofia), Address(Manager), Address("a.nowak@contoso.test")] },
            "tooManyRecipients" => request with { Cc = [.. Enumerable.Range(1, 10).Select(i => Address($"kopia{i}@contoso.test"))] },
            "script" => request with { HtmlBody = "<p>Cześć</p><script>alert(1)</script>" },
            "javascriptLink" => request with { HtmlBody = "<a href=\"javascript:alert(1)\">zmień hasło</a>" },
            "foreignSender" => request with { From = Address("it@fabrikam.test") },
            "foreignReplyTo" => request with { ReplyTo = Address("phish@fabrikam.test") },
            "addressList" => request with { To = [Address("zofia.zak@contoso.test,jan@contoso.test")] },
            "longSubject" => request with { Subject = new string('ż', 251) },
            "language" => request with { Language = "de" },
            _ => request with { Id = Guid.Empty },
        };

        using var response = await viewer.PostAsync(WatchdogRoutes.UserMessages, ApiJson.Content(request), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(code, await ApiJson.IssueCodesAsync(response));
        Assert.Empty((await _factory.Store.QueryAsync(new OutboxQuery(), Ct)).Items);
        Assert.Empty(_sink.Messages);
    }

    [Fact]
    public async Task Attachments_CannotBeSent()
    {
        await ConfigureAsync(_sink.Port);
        using var viewer = _factory.Viewer();
        var json = System.Text.Json.JsonSerializer.Serialize(Request(), WatchdogJson.Options);
        using var content = new StringContent(json[..^1] + ",\"attachments\":[{\"name\":\"faktura.pdf.exe\",\"content\":\"TVqQ\"}]}", System.Text.Encoding.UTF8, "application/json");

        using var response = await viewer.PostAsync(WatchdogRoutes.UserMessages, content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["invalidJson"], await ApiJson.IssueCodesAsync(response));
        Assert.Empty(_sink.Messages);
    }

    [Fact]
    public async Task HourlyLimit_Is429_WithRetryAfter_AndCountsEveryCaller()
    {
        await ConfigureAsync(_sink.Port, settings => settings with { MaxMessagesPerHour = 2 });
        using var viewer = _factory.Viewer();
        using var admin = _factory.Admin();

        using var first = await viewer.PostAsync(WatchdogRoutes.UserMessages, ApiJson.Content(Request(Guid.NewGuid())), Ct);
        _factory.Clock.Advance(TimeSpan.FromMinutes(10));
        using var second = await admin.PostAsync(WatchdogRoutes.UserMessages, ApiJson.Content(Request(Guid.NewGuid())), Ct);
        using var third = await viewer.PostAsync(WatchdogRoutes.UserMessages, ApiJson.Content(Request(Guid.NewGuid())), Ct);

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (first.StatusCode, second.StatusCode));
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal(TimeSpan.FromMinutes(50), third.Headers.RetryAfter?.Delta);
        Assert.Equal([UserMessageIssueCodes.HourlyLimit], await ApiJson.IssueCodesAsync(third));

        _factory.Clock.Advance(TimeSpan.FromMinutes(51));
        using var later = await viewer.PostAsync(WatchdogRoutes.UserMessages, ApiJson.Content(Request(Guid.NewGuid())), Ct);
        Assert.Equal(HttpStatusCode.OK, later.StatusCode);
        Assert.Equal(3, _sink.Messages.Count);
    }

    [Fact]
    public async Task RejectedRecipient_IsRejected_AndARejectedCopyIsReported()
    {
        await using var server = new ScriptedSmtpServer
        {
            Script = line => line.StartsWith("RCPT TO:<a.nowak@", StringComparison.OrdinalIgnoreCase) ? "550 5.1.1 User unknown"
                : line.StartsWith("RCPT TO:<" + Manager, StringComparison.OrdinalIgnoreCase) ? "550 5.1.1 User unknown"
                : null,
        };
        await server.StartAsync();
        await ConfigureAsync(server.Port);
        using var viewer = _factory.Viewer();

        using var rejected = await viewer.PostAsync(WatchdogRoutes.UserMessages, ApiJson.Content(Request() with { To = [Address("a.nowak@contoso.test")], Cc = [] }), Ct);
        using var copy = await viewer.PostAsync(WatchdogRoutes.UserMessages, ApiJson.Content(Request(Guid.NewGuid())), Ct);

        var rejectedResult = await ApiJson.ReadAsync<UserMessageResult>(rejected);
        var copyResult = await ApiJson.ReadAsync<UserMessageResult>(copy);
        Assert.Equal((UserMessageState.Rejected, "smtp.550.5.1.1", 550), (rejectedResult.State, rejectedResult.Code, rejectedResult.SmtpStatus));
        Assert.Equal(UserMessageState.Delivered, copyResult.State);
        Assert.Equal([Manager], copyResult.RejectedCopies);
        var items = (await _factory.Store.QueryAsync(new OutboxQuery(), Ct)).Items;
        Assert.Equal(OutboxStatus.Dead, items.Single(i => i.Id == rejectedResult.OutboxId).Status);
        Assert.Equal(OutboxStatus.Sent, items.Single(i => i.Id == copyResult.OutboxId).Status);
        Assert.Null(_factory.Services.GetRequiredService<IHealthRegistry>().Channels.FirstOrDefault(c => c.ChannelId == "email")?.LastFailureAt);
    }

    [Fact]
    public async Task NoAttemptInTime_IsQueued_202_AndARepeatAnswersTheSameState()
    {
        await ConfigureAsync(port: 1);                    // nothing listens – but nobody waits for the job here
        _factory.Services.GetRequiredService<UserMessageService>().Wait = TimeSpan.Zero;
        using var viewer = _factory.Viewer();

        using var response = await viewer.PostAsync(WatchdogRoutes.UserMessages, ApiJson.Content(Request()), Ct);
        var result = await ApiJson.ReadAsync<UserMessageResult>(response);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(UserMessageState.Queued, result.State);
        Assert.NotNull(result.OutboxId);

        using var again = await viewer.PostAsync(WatchdogRoutes.UserMessages, ApiJson.Content(Request()), Ct);
        var repeated = await ApiJson.ReadAsync<UserMessageResult>(again);
        Assert.Equal((HttpStatusCode.Accepted, UserMessageState.Queued, true, result.OutboxId), (again.StatusCode, repeated.State, repeated.Duplicate, repeated.OutboxId));
        Assert.Single((await _factory.Store.QueryAsync(new OutboxQuery(), Ct)).Items);
    }

    private async Task ConfigureAsync(int port, Func<UserMessagingSettings, UserMessagingSettings>? change = null)
    {
        var settings = new UserMessagingSettings { Enabled = true, AllowedSenders = ["@contoso.test"] };
        var configuration = SmtpEmailTransportTests.Configuration(port, SmtpSecurity.None) with { UserMessages = change?.Invoke(settings) ?? settings };
        using var admin = _factory.Admin();
        using var response = await admin.PutAsync(WatchdogRoutes.Config, ApiJson.Content(new ConfigEnvelope { Version = 0, Configuration = configuration }), Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
    }

    private static UserMessageAddressDto Address(string address) => new() { Address = address };

    private static UserMessageRequest Request(Guid? id = null) => new()
    {
        Id = id ?? Guid.Parse("0192f3a4-5b6c-7d8e-9f00-112233445566"),
        From = new UserMessageAddressDto { Address = "helpdesk@contoso.test", DisplayName = "Dział IT" },
        To = [new UserMessageAddressDto { Address = Zofia, DisplayName = "Zofia Żak" }],
        Cc = [new UserMessageAddressDto { Address = Manager, DisplayName = "Jan Kowalski" }],
        Subject = Subject,
        TextBody = "Dzień dobry, Zofio!\nTwoje hasło wygasa 5 listopada 2026. Zażółć gęślą jaźń.",
        HtmlBody = "<p>Dzień dobry, Zofio!</p><p>Twoje hasło wygasa <b>5 listopada 2026</b>.</p>",
        Language = "pl",
    };
}
