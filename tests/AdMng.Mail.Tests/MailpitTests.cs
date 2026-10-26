using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using AdMng.Core.Messaging;
using static AdMng.Mail.Tests.MailTestData;

namespace AdMng.Mail.Tests;

/// <summary>
/// The direct SMTP transport against Mailpit (tools/testlab/watchdog/start-mailpit.sh: STARTTLS with a self-signed
/// certificate, any AUTH accepted, chaos API enabled). Skipped unless ADMNG_TEST_SMTP_HOST and ADMNG_TEST_MAILPIT_API are
/// set, e.g. <c>ADMNG_TEST_SMTP_HOST=127.0.0.1 ADMNG_TEST_SMTP_PORT=1025 ADMNG_TEST_MAILPIT_API=http://127.0.0.1:8025</c>.
/// One class: the tests share Mailpit's mailbox and chaos triggers and run one after another.
/// </summary>
public sealed class MailpitTests : IAsyncLifetime
{
    private const string SkipReason = "ADMNG_TEST_SMTP_HOST / ADMNG_TEST_MAILPIT_API are not set - start Mailpit with tools/testlab/watchdog/start-mailpit.sh.";

    private static readonly string? Host = Environment.GetEnvironmentVariable("ADMNG_TEST_SMTP_HOST") is { Length: > 0 } h ? h : null;
    private static readonly int Port = int.TryParse(Environment.GetEnvironmentVariable("ADMNG_TEST_SMTP_PORT"), out var p) ? p : 1025;
    private static readonly Uri? Api = Environment.GetEnvironmentVariable("ADMNG_TEST_MAILPIT_API") is { Length: > 0 } a ? new Uri(a) : null;

    private readonly HttpClient _http = new();
    private DirectSmtpOptions _options = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        if (Host is null || Api is null)
        {
            return;
        }

        _http.BaseAddress = Api;
        await SetChaosAsync(null, 0);

        // Mailpit's certificate is generated at start: learn its SHA-256 from a test with system trust, then pin it.
        var probe = await new SmtpUserMessageTransport(new DirectSmtpOptions { Host = Host, Port = Port, LocalDomain = "admng.contoso.test" }, null)
            .TestConnectionAsync(Token);
        _options = new DirectSmtpOptions
        {
            Host = Host,
            Port = Port,
            Certificate = SmtpCertificateCheck.PinnedSha256,
            PinnedSha256 = probe.CertificateSha256,
            LocalDomain = "admng.contoso.test",
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (Api is not null)
        {
            await SetChaosAsync(null, 0);
        }

        _http.Dispose();
    }

    [Fact]
    public async Task PolishSubjectAndBody_ArriveIntact_WithTheAutomaticHeaders()
    {
        Assert.SkipWhen(Host is null || Api is null, SkipReason);
        var transport = new SmtpUserMessageTransport(_options, null);
        var message = Message(cc: "pomoc@contoso.test") with { Subject = PolishSubject + " " + Guid.NewGuid().ToString("N")[..8] };

        var test = await transport.TestConnectionAsync(Token);
        var results = await Dispatcher().SendAsync(transport, [Planned(message)], Limiter(600), null, null, Token);

        Assert.True(test.Success, test.Failure?.Code);
        Assert.True(test.IsEncrypted);
        Assert.Equal(UserMessageSendStatus.Accepted, results.Single().Status);
        var id = await FindAsync(message.Subject);
        using var details = await GetJsonAsync($"api/v1/message/{id}");
        var root = details.RootElement;
        Assert.Equal(message.Subject, root.GetProperty("Subject").GetString());
        Assert.Contains("ZAŻÓŁĆ GĘŚLĄ JAŹŃ", root.GetProperty("Text").GetString(), StringComparison.Ordinal);
        Assert.Contains("ZAŻÓŁĆ GĘŚLĄ JAŹŃ", root.GetProperty("HTML").GetString(), StringComparison.Ordinal);
        Assert.Equal("Łukasz Wiśniewski", root.GetProperty("To")[0].GetProperty("Name").GetString());
        Assert.Equal("pomoc@contoso.test", root.GetProperty("Cc")[0].GetProperty("Address").GetString());
        Assert.Equal(0, root.GetProperty("Attachments").GetArrayLength());
        using var headers = await GetJsonAsync($"api/v1/message/{id}/headers");
        Assert.Equal("auto-generated", headers.RootElement.GetProperty("Auto-Submitted")[0].GetString());
        Assert.Equal("All", headers.RootElement.GetProperty("X-Auto-Response-Suppress")[0].GetString());
    }

    [Fact]
    public async Task Rejection_IsReportedPerRecipient_AndATemporaryOneIsRetried()
    {
        Assert.SkipWhen(Host is null || Api is null, SkipReason);
        var transport = new SmtpUserMessageTransport(_options, null);

        // Permanent: every RCPT TO is answered 550.
        await SetChaosAsync("Recipient", 550);
        var rejected = await Dispatcher().SendAsync(transport, [Planned(Message())], Limiter(600), null, null, Token);
        Assert.Equal((UserMessageSendStatus.Failed, UserMessageDeliveryOutcome.Rejected, 550), (rejected[0].Status, rejected[0].Delivery!.Outcome, rejected[0].Delivery!.SmtpStatus));

        // Temporary: 451 until the dispatcher waits for its retry, then the trigger is switched off.
        await SetChaosAsync("Recipient", 451);
        var subject = "Ponowienie " + Guid.NewGuid().ToString("N")[..8];
        var retried = await Dispatcher().SendAsync(
            transport,
            [Planned(Message(subject: subject))],
            Limiter(600),
            new UserMessageDispatchOptions { RetryDelays = [TimeSpan.FromMilliseconds(200)] },
            new SyncProgress(p =>
            {
                if (p.IsWaitingForRetry)
                {
                    SetChaosAsync(null, 0).GetAwaiter().GetResult();
                }
            }),
            Token);

        Assert.Equal((UserMessageSendStatus.Accepted, 2), (retried[0].Status, retried[0].Attempts));
        Assert.NotNull(await FindAsync(subject));
    }

    [Fact]
    public async Task RateLimit_SpacesTheMessages()
    {
        Assert.SkipWhen(Host is null || Api is null, SkipReason);
        var transport = new SmtpUserMessageTransport(_options, null);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var messages = Enumerable.Range(1, 3).Select(i => Planned(Message(subject: $"Limit {tag} {i}"), i)).ToList();
        var watch = Stopwatch.StartNew();

        // 120 per minute = one message every 500 ms.
        var results = await Dispatcher().SendAsync(transport, messages, Limiter(120), null, null, Token);

        watch.Stop();
        Assert.All(results, r => Assert.Equal(UserMessageSendStatus.Accepted, r.Status));
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(950), $"took {watch.Elapsed}");
        foreach (var i in Enumerable.Range(1, 3))
        {
            Assert.NotNull(await FindAsync($"Limit {tag} {i}"));
        }
    }

    private static UserMessageDispatcher Dispatcher() => new(TimeProvider.System);

    private static MessageRateLimiter Limiter(int perMinute) => new(TimeProvider.System) { MessagesPerMinute = perMinute };

    private static PlannedUserMessage Planned(OutgoingUserMessage message, int index = 0) => new()
    {
        Index = index,
        DistinguishedName = "CN=Łukasz Wiśniewski,DC=contoso,DC=test",
        Name = "Łukasz Wiśniewski",
        Status = PlannedMessageStatus.Ready,
        Message = message,
    };

    private async Task SetChaosAsync(string? trigger, int errorCode)
    {
        object body = trigger is null ? new { } : new Dictionary<string, object> { [trigger] = new { ErrorCode = errorCode, Probability = 100 } };
        using var response = await _http.PutAsJsonAsync("api/v1/chaos", body, CancellationToken.None);
        response.EnsureSuccessStatusCode();
    }

    private async Task<JsonDocument> GetJsonAsync(string path)
    {
        using var response = await _http.GetAsync(path, Token);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(Token), cancellationToken: Token);
    }

    /// <summary>The Mailpit id of the message with exactly this subject (waits up to 10 s).</summary>
    private async Task<string?> FindAsync(string subject)
    {
        for (var i = 0; i < 50; i++)
        {
            using var page = await GetJsonAsync("api/v1/messages?limit=200");
            foreach (var message in page.RootElement.GetProperty("messages").EnumerateArray())
            {
                if (message.GetProperty("Subject").GetString() == subject)
                {
                    return message.GetProperty("ID").GetString();
                }
            }

            await Task.Delay(200, Token);
        }

        return null;
    }

    private sealed class SyncProgress(Action<UserMessageProgress> report) : IProgress<UserMessageProgress>
    {
        public void Report(UserMessageProgress value) => report(value);
    }
}
