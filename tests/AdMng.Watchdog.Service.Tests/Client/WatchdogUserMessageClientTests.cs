using AdMng.Watchdog.Client;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Notifications;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using AdMng.Watchdog.Service.Tests.Notifications;
using AdMng.Watchdog.Testing.Smtp;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Watchdog.Service.Tests.Client;

/// <summary><see cref="WatchdogHttpClient"/>'s user-message calls against the real server over https (README U1b): status,
/// send, idempotent repeat, and the switched-off (409) and limit (429 with Retry-After) answers as classified exceptions.</summary>
public sealed class WatchdogUserMessageClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Status_Send_Repeat_Disabled_AndLimit()
    {
        await using var sink = new SmtpSink();
        await sink.StartAsync(Ct);
        await using var server = WatchdogClientServer.Start(
            s => s.AddSingleton<IWatchdogJob>(p => p.GetRequiredService<OutboxSenderJob>()), TestRoles.AdminAccount, TestRoles.AdminGroup);
        using var client = server.Client();
        var smtp = SmtpEmailTransportTests.Configuration(sink.Port, SmtpSecurity.None);
        var saved = await client.SaveConfigAsync(new ConfigEnvelope { Version = 0, Configuration = smtp }, Ct);

        var off = await client.GetUserMessagingStatusAsync(Ct);
        var disabled = await Assert.ThrowsAsync<WatchdogClientException>(() => client.SendUserMessageAsync(Request(Guid.NewGuid()), Ct));

        Assert.False(off.Available);
        Assert.Equal((WatchdogClientErrorKind.Validation, 409, ApiProblem.UserMessagesUnavailable), (disabled.Kind, disabled.StatusCode, disabled.ProblemType));
        Assert.Equal(UserMessageIssueCodes.Disabled, Assert.Single(disabled.Issues).Code);

        await client.SaveConfigAsync(saved with
        {
            Configuration = smtp with { UserMessages = new UserMessagingSettings { Enabled = true, MaxMessagesPerHour = 1 } },
        }, Ct);
        var request = Request(Guid.NewGuid());

        var on = await client.GetUserMessagingStatusAsync(Ct);
        var sent = await client.SendUserMessageAsync(request, Ct);
        var repeated = await client.SendUserMessageAsync(request, Ct);
        var limited = await Assert.ThrowsAsync<WatchdogClientException>(() => client.SendUserMessageAsync(Request(Guid.NewGuid()), Ct));

        Assert.True(on.Available);
        Assert.Equal(("watchdog@contoso.test", 1), (on.Sender, on.MaxMessagesPerHour));
        Assert.Equal((UserMessageState.Delivered, false), (sent.State, sent.Duplicate));
        Assert.Equal((UserMessageState.Delivered, true), (repeated.State, repeated.Duplicate));
        Assert.Equal((429, ApiProblem.RateLimited, TimeSpan.FromHours(1)), (limited.StatusCode, limited.ProblemType, limited.RetryAfter));
        Assert.Single(sink.Messages);
    }

    private static UserMessageRequest Request(Guid id) => new()
    {
        Id = id,
        To = [new UserMessageAddressDto { Address = "zofia.zak@contoso.test", DisplayName = "Zofia Żak" }],
        Subject = "Konto wygasa – Łódź",
        TextBody = "Dzień dobry, Twoje konto wygasa 30 listopada.",
        Language = "pl",
    };
}
