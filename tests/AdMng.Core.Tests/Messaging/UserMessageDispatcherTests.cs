using AdMng.Core.Csv;
using AdMng.Core.Messaging;
using AdMng.Testing;

namespace AdMng.Core.Tests.Messaging;

/// <summary>Sending with a fake transport and a manual clock: spacing, daily budget, retries, stop, cancellation, report.</summary>
public class UserMessageDispatcherTests
{
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 26, 10, 0, 0, TimeSpan.Zero));
    private readonly List<TimeSpan> _delays = [];
    private readonly FakeUserMessageTransport _transport = new();

    private Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _delays.Add(delay);
        _time.Advance(delay);
        return Task.CompletedTask;
    }

    private MessageRateLimiter Limiter(int perMinute = 30, int perDay = 10_000) =>
        new(_time, Delay) { MessagesPerMinute = perMinute, RecipientsPerDay = perDay };

    private UserMessageDispatcher Dispatcher() => new(_time, Delay);

    private static PlannedUserMessage Planned(int index, string address, int cc = 0) => new()
    {
        Index = index,
        DistinguishedName = $"CN=Użytkownik {index},DC=contoso,DC=test",
        Name = $"Użytkownik {index}",
        Login = $"u{index}",
        Status = PlannedMessageStatus.Ready,
        Message = new OutgoingUserMessage
        {
            Id = Guid.NewGuid(),
            From = new MessageAddress("noreply@contoso.test"),
            To = [new MessageAddress(address)],
            Cc = [.. Enumerable.Range(0, cc).Select(i => new MessageAddress($"cc{i}@contoso.test"))],
            Subject = "Hasło wygasa",
            TextBody = "Treść",
            HtmlBody = "<p>Treść</p>",
        },
    };

    private static List<PlannedUserMessage> Messages(int count) => [.. Enumerable.Range(0, count).Select(i => Planned(i, $"u{i}@contoso.test"))];

    [Fact]
    public async Task Messages_AreSpacedByTheRate_OverOneConnection()
    {
        var progress = new List<UserMessageProgress>();

        var results = await Dispatcher().SendAsync(_transport, Messages(4), Limiter(perMinute: 30), null, new SyncProgress(progress.Add), CancellationToken.None);

        Assert.All(results, r => Assert.Equal(UserMessageSendStatus.Accepted, r.Status));
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)], _delays);
        Assert.Equal(4, _transport.Accepted.Count);
        Assert.Equal(1, _transport.Connections);
        Assert.Equal(1, _transport.DisposedConnections);
        Assert.Equal(new UserMessageProgress(4, 4, 4, 0, false), progress[^1]);
        Assert.Equal(_time.GetUtcNow(), results[^1].CompletedAt);
    }

    [Fact]
    public async Task TransientFailures_AreRetriedOnANewConnection_ThenSucceedOrFail()
    {
        _transport.Script("u0@contoso.test", FakeUserMessageTransport.Transient(451), FakeUserMessageTransport.Transient(421));
        _transport.Script("u1@contoso.test", FakeUserMessageTransport.Transient(), FakeUserMessageTransport.Transient(), FakeUserMessageTransport.Transient());
        var progress = new List<UserMessageProgress>();

        var results = await Dispatcher().SendAsync(_transport, Messages(3), Limiter(perMinute: 600), null, new SyncProgress(progress.Add), CancellationToken.None);

        Assert.Equal(UserMessageSendStatus.Accepted, results[0].Status);
        Assert.Equal(3, results[0].Attempts);
        Assert.Equal(UserMessageSendStatus.Failed, results[1].Status);
        Assert.Equal(3, results[1].Attempts);
        Assert.Equal("smtp.451", results[1].Delivery!.Code);
        Assert.Equal(UserMessageSendStatus.Accepted, results[2].Status);
        Assert.Contains(TimeSpan.FromSeconds(10), _delays);
        Assert.Contains(TimeSpan.FromSeconds(30), _delays);
        Assert.Equal(6, _transport.Connections);
        Assert.Contains(progress, p => p.IsWaitingForRetry);
    }

    [Fact]
    public async Task PermanentRejection_FailsOnlyThatMessage()
    {
        _transport.Script("u1@contoso.test", FakeUserMessageTransport.Rejected(550, "5.1.1"));

        var results = await Dispatcher().SendAsync(_transport, Messages(3), Limiter(), null, null, CancellationToken.None);

        Assert.Equal([UserMessageSendStatus.Accepted, UserMessageSendStatus.Failed, UserMessageSendStatus.Accepted], results.Select(r => r.Status));
        Assert.Equal(1, results[1].Attempts);
        Assert.Equal(550, results[1].Delivery!.SmtpStatus);
    }

    [Fact]
    public async Task AuthenticationFailure_StopsTheRun_TheRestIsNotSent()
    {
        _transport.Script("u1@contoso.test", FakeUserMessageTransport.AuthenticationFailed());

        var results = await Dispatcher().SendAsync(_transport, Messages(4), Limiter(), null, null, CancellationToken.None);

        Assert.Equal(UserMessageSendStatus.Failed, results[1].Status);
        Assert.Equal("smtp.basicAuthDisabled", results[1].Delivery!.Hint);
        Assert.All(results.Skip(2), r => Assert.Equal((UserMessageSendStatus.NotSent, UserMessageNotSentReason.TransportStopped), (r.Status, r.NotSentReason)));
        Assert.Single(_transport.Accepted);
    }

    [Fact]
    public async Task ConnectFailure_IsClassified_AndStopsTheRun()
    {
        _transport.ConnectFailure = new UserMessageTransportException(new UserMessageDelivery(UserMessageDeliveryOutcome.TransportFailed, "tls", Hint: "tls.certificateUntrusted"));

        var results = await Dispatcher().SendAsync(_transport, Messages(2), Limiter(), null, null, CancellationToken.None);

        Assert.Equal(UserMessageSendStatus.Failed, results[0].Status);
        Assert.Equal("tls", results[0].Delivery!.Code);
        Assert.Equal(UserMessageNotSentReason.TransportStopped, results[1].NotSentReason);
    }

    [Fact]
    public async Task Cancellation_LeavesTheRestNotSent()
    {
        using var cts = new CancellationTokenSource();
        var sent = 0;
        _transport.BeforeSend = _ =>
        {
            if (++sent == 2)
            {
                cts.Cancel();
            }
        };

        var results = await Dispatcher().SendAsync(_transport, Messages(5), Limiter(perMinute: 600), null, null, cts.Token);

        // The second message was already on its way when the operator cancelled: it is accepted, the rest is not sent.
        Assert.Equal([UserMessageSendStatus.Accepted, UserMessageSendStatus.Accepted], results.Take(2).Select(r => r.Status));
        Assert.All(results.Skip(2), r => Assert.Equal((UserMessageSendStatus.NotSent, UserMessageNotSentReason.Cancelled), (r.Status, r.NotSentReason)));
        Assert.Equal(5, results.Count);
    }

    [Fact]
    public async Task DailyBudget_CountsRecipients_AndStopsBeforeItIsExceeded()
    {
        var limiter = Limiter(perDay: 5);
        List<PlannedUserMessage> messages = [Planned(0, "a@contoso.test", cc: 1), Planned(1, "b@contoso.test", cc: 1), Planned(2, "c@contoso.test", cc: 1)];

        var results = await Dispatcher().SendAsync(_transport, messages, limiter, null, null, CancellationToken.None);

        Assert.Equal([UserMessageSendStatus.Accepted, UserMessageSendStatus.Accepted, UserMessageSendStatus.NotSent], results.Select(r => r.Status));
        Assert.Equal(UserMessageNotSentReason.DailyLimit, results[2].NotSentReason);
        Assert.Equal(4, limiter.RecipientsInLastDay);

        _time.Advance(TimeSpan.FromHours(24));
        Assert.Equal(0, limiter.RecipientsInLastDay);
        Assert.True(await limiter.WaitAsync(5, CancellationToken.None));
    }

    [Fact]
    public async Task Limiter_IsSharedBetweenRuns_SoASecondRunWaitsForTheNextSlot()
    {
        var limiter = Limiter(perMinute: 6);
        await Dispatcher().SendAsync(_transport, Messages(1), limiter, null, null, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(4));

        await Dispatcher().SendAsync(_transport, Messages(1), limiter, null, null, CancellationToken.None);

        Assert.Equal([TimeSpan.FromSeconds(6)], _delays);
    }

    [Fact]
    public void Report_CsvHasOneRowPerAccount_WithoutContent()
    {
        var accepted = Planned(0, "jan.kowalski@contoso.test");
        var failed = Planned(1, "grazyna.dabrowska@contoso.test");
        var skipped = Planned(2, "zbigniew@contoso.test") with { Status = PlannedMessageStatus.Skipped };
        var excluded = Planned(3, "pawel@contoso.test");
        UserMessageReportEntry[] entries =
        [
            new(accepted, new UserMessageSendResult(accepted, UserMessageSendStatus.Accepted, UserMessageDelivery.Accepted(), 1, _time.GetUtcNow())),
            new(failed, new UserMessageSendResult(failed, UserMessageSendStatus.Failed, FakeUserMessageTransport.Rejected(550, "5.1.1"), 1, _time.GetUtcNow())),
            new(skipped, null),
            new(excluded, null, Excluded: true),
        ];
        var writer = new StringWriter();

        new UserMessageReportExporter(new CsvCodec()).Write(writer, entries);

        var lines = writer.ToString().ReplaceLineEndings("\n").TrimEnd().Split('\n');
        Assert.Equal("Status;Name;Login;DN;To;CC;Code;SMTP status;Hint;Attempts;Time (UTC)", lines[0]);
        Assert.Equal("Accepted;Użytkownik 0;u0;CN=Użytkownik 0,DC=contoso,DC=test;jan.kowalski@contoso.test;;ok;250;;1;2026-10-26T10:00:00Z", lines[1]);
        Assert.StartsWith("Failed;Użytkownik 1;u1;", lines[2], StringComparison.Ordinal);
        Assert.Contains(";smtp.550.5.1.1;550;", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("Skipped;", lines[3], StringComparison.Ordinal);
        Assert.StartsWith("Excluded;", lines[4], StringComparison.Ordinal);
        Assert.DoesNotContain("Hasło wygasa", writer.ToString(), StringComparison.Ordinal);
        Assert.True(entries[1].CanRetry);
        Assert.False(entries[0].CanRetry);
        Assert.False(entries[2].CanRetry);
    }

    /// <summary>Reports synchronously (Progress&lt;T&gt; would post to the thread pool).</summary>
    private sealed class SyncProgress(Action<UserMessageProgress> report) : IProgress<UserMessageProgress>
    {
        public void Report(UserMessageProgress value) => report(value);
    }
}
