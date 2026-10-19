using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Testing.Smtp;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace AdMng.Watchdog.Service.Tests.Notifications;

/// <summary>
/// Partial SMTP deliveries (spec 15.4, 15.6): a recipient rejected temporarily is retried without resending to the
/// recipients that got the message, a failed recipient chunk does not resend the chunks already accepted, every SMTP
/// message takes a rate-limit token, and the chunks of a follow-up reply to the matching chunk of the original.
/// </summary>
public sealed class SmtpPartialDeliveryTests
{
    private const string Jan = "jan.kowalski@contoso.test";
    private const string Soc = "soc@contoso.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TemporarilyRejectedRecipient_IsRetried_WithoutResendingToTheOthers()
    {
        var greylisted = true;
        await using var server = await StartAsync(line => greylisted && line.StartsWith("RCPT TO:<" + Soc, StringComparison.OrdinalIgnoreCase) ? "450 4.2.0 Greylisted" : null);
        await using var fixture = Fixture(server.Port);
        var message = SmtpEmailTransportTests.Message() with { Recipients = [Jan, Soc] };

        var first = await fixture.Smtp.SendAsync(message, Ct);

        // Jan has it: exhausted retries would be message-specific, not a failing e-mail channel.
        Assert.Equal((DeliveryOutcome.Transient, "partial.smtp.450.4.2.0"), (first.Outcome, first.ErrorCode));
        Assert.Single(server.Messages);
        Assert.Equal((1, 1), (Rcpt(server, Jan), Rcpt(server, Soc)));
        Assert.Contains(fixture.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("1 recipient(s) rejected temporarily", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Log.Entries, e => e.Message.Contains(Soc, StringComparison.OrdinalIgnoreCase));

        greylisted = false;
        var second = await fixture.Smtp.SendAsync(message, Ct);

        Assert.Equal(DeliveryOutcome.Delivered, second.Outcome);
        Assert.Equal((1, 2), (Rcpt(server, Jan), Rcpt(server, Soc)));
        Assert.Equal([Soc], Addresses(Parsed(server)[1]));
        Assert.Contains(fixture.Log.Entries, e => e.EventId.Id == 2200 && e.Message.Contains("delivered to 1 recipient(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PermanentlyRejectedRecipient_IsDroppedWithAWarning_WhileTheOthersGetTheMessage()
    {
        await using var server = await StartAsync(line => line.StartsWith("RCPT TO:<" + Soc, StringComparison.OrdinalIgnoreCase) ? "550 5.1.1 User unknown" : null);
        await using var fixture = Fixture(server.Port);

        var result = await fixture.Smtp.SendAsync(SmtpEmailTransportTests.Message() with { Recipients = [Jan, Soc] }, Ct);

        Assert.Equal(DeliveryOutcome.Delivered, result.Outcome);
        Assert.Single(server.Messages);
        Assert.Equal((1, 1), (Rcpt(server, Jan), Rcpt(server, Soc)));
        Assert.Contains(fixture.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("1 recipient(s) rejected permanently", StringComparison.Ordinal));
        Assert.Contains(fixture.Log.Entries, e => e.EventId.Id == 2200 && e.Message.Contains("delivered to 1 recipient(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedLaterChunk_IsRetried_WithoutResendingTheChunksAlreadyAccepted()
    {
        var data = 0;
        await using var server = await StartAsync(line => line == ScriptedSmtpServer.EndOfData && Interlocked.Increment(ref data) == 2 ? "451 4.3.0 Try again later" : null);
        await using var fixture = Fixture(server.Port, maxRecipients: 1);
        var message = SmtpEmailTransportTests.Message() with { Recipients = ["a.nowak@contoso.test", "b.zak@contoso.test"] };

        var first = await fixture.Smtp.SendAsync(message, Ct);
        var second = await fixture.Smtp.SendAsync(message, Ct);

        Assert.Equal((DeliveryOutcome.Transient, "partial.smtp.451.4.3.0"), (first.Outcome, first.ErrorCode));
        Assert.Equal(DeliveryOutcome.Delivered, second.Outcome);
        Assert.Equal([["a.nowak@contoso.test"], ["b.zak@contoso.test"]], Recipients(server));
        Assert.Equal(
            ["0192f3a45b6c7d8e9f00112233445566@watchdog.contoso.test", "0192f3a45b6c7d8e9f00112233445566.2@watchdog.contoso.test"],
            Parsed(server).Select(m => m.MessageId));
    }

    [Fact]
    public async Task ChunkWhoseRecipientsAreAllRejected_DoesNotStopTheOtherChunks()
    {
        await using var server = await StartAsync(line => line.StartsWith("RCPT TO:<a.nowak@", StringComparison.OrdinalIgnoreCase) ? "550 5.1.1 User unknown" : null);
        await using var fixture = Fixture(server.Port, maxRecipients: 1);

        var result = await fixture.Smtp.SendAsync(SmtpEmailTransportTests.Message() with { Recipients = ["a.nowak@contoso.test", "b.zak@contoso.test"] }, Ct);

        Assert.Equal(DeliveryOutcome.Delivered, result.Outcome);
        Assert.Equal([["b.zak@contoso.test"]], Recipients(server));
    }

    [Fact]
    public async Task EverySmtpMessage_TakesARateLimitToken_AndDeferredChunksAreNotResent()
    {
        await using var server = await StartAsync(_ => null);
        await using var fixture = Fixture(server.Port, maxRecipients: 1, perMinute: 1);
        var message = SmtpEmailTransportTests.Message() with { Recipients = ["a.nowak@contoso.test", "b.zak@contoso.test", "c.wojcik@contoso.test"] };

        var first = await fixture.Smtp.SendAsync(message, Ct);
        Assert.Equal(DeliveryOutcome.Deferred, first.Outcome);
        Assert.True(first.RetryAfter > TimeSpan.Zero);
        Assert.Single(server.Messages);

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(DeliveryOutcome.Deferred, (await fixture.Smtp.SendAsync(message, Ct)).Outcome);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(DeliveryOutcome.Delivered, (await fixture.Smtp.SendAsync(message, Ct)).Outcome);

        Assert.Equal([["a.nowak@contoso.test"], ["b.zak@contoso.test"], ["c.wojcik@contoso.test"]], Recipients(server));
    }

    [Fact]
    public async Task FollowUpChunks_ReplyToTheMatchingChunkOfTheOriginal()
    {
        await using var server = await StartAsync(_ => null);
        await using var fixture = Fixture(server.Port, maxRecipients: 2);
        var recipients = new[] { "c.wojcik@contoso.test", "a.nowak@contoso.test", "b.zak@contoso.test" };
        var original = SmtpEmailTransportTests.Message() with { Recipients = recipients };
        var followUp = SmtpEmailTransportTests.Message() with
        {
            MessageId = "0192f3a45b6c7d8e9f00000000000099",
            Kind = DeliveryKind.FollowUp,
            Recipients = [.. recipients.Reverse()],
            Email = SmtpEmailTransportTests.Message().Email! with { InReplyTo = original.MessageId },
        };

        Assert.Equal(DeliveryOutcome.Delivered, (await fixture.Smtp.SendAsync(original, Ct)).Outcome);
        Assert.Equal(DeliveryOutcome.Delivered, (await fixture.Smtp.SendAsync(followUp, Ct)).Outcome);

        var messages = Parsed(server);
        var originals = messages.Take(2).ToList();
        var replies = messages.Skip(2).ToList();
        Assert.Equal(2, replies.Count);
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(Addresses(originals[i]), Addresses(replies[i]));
            Assert.Equal(originals[i].MessageId, replies[i].InReplyTo);
            Assert.Equal(originals[i].MessageId, Assert.Single(replies[i].References));
        }
    }

    private static async Task<ScriptedSmtpServer> StartAsync(Func<string, string?> script)
    {
        var server = new ScriptedSmtpServer { Script = script };
        await server.StartAsync();
        return server;
    }

    private static DeliveryFixture Fixture(int port, int maxRecipients = 50, int perMinute = 20)
    {
        var fixture = DeliveryFixture.Create();
        var configuration = SmtpEmailTransportTests.Configuration(port, SmtpSecurity.None);
        fixture.Configure(configuration with { Smtp = configuration.Smtp with { MaxRecipientsPerMessage = maxRecipients, MaxMessagesPerMinute = perMinute } });
        return fixture;
    }

    private static List<MimeMessage> Parsed(ScriptedSmtpServer server) =>
        [.. server.Messages.Select(data =>
        {
            using var stream = new MemoryStream(data);
            return MimeMessage.Load(stream);
        })];

    private static List<List<string>> Recipients(ScriptedSmtpServer server) => [.. Parsed(server).Select(Addresses)];

    private static int Rcpt(ScriptedSmtpServer server, string address) =>
        server.Commands.Count(c => c.StartsWith("RCPT TO:<" + address + ">", StringComparison.OrdinalIgnoreCase));

    private static List<string> Addresses(MimeMessage message) => [.. message.To.Mailboxes.Select(m => m.Address)];
}
