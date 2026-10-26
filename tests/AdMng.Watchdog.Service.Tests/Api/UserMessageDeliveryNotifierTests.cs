using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Service.Tests.Fakes;
using AdMng.Watchdog.Service.UserMessages;

namespace AdMng.Watchdog.Service.Tests.Api;

/// <summary>
/// The hand-over of SMTP attempts to waiting <c>POST /api/v1/user-messages</c> requests – above all a repeated request
/// that registers after the attempt was reported but before the outbox row shows it (it must not wait for an attempt
/// that already happened).
/// </summary>
public sealed class UserMessageDeliveryNotifierTests
{
    private const string MessageId = "user-0192f3a45b6c7d8e9f00112233445566";

    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static UserMessageAttempt Delivered() =>
        new(DeliveryOutcome.Delivered, "ok", 250) { RejectedCopies = ["jan.kowalski@contoso.test"] };

    [Fact]
    public async Task AWaitingRequest_GetsTheAttempt_WithTheRejectedCopies()
    {
        var notifier = new UserMessageDeliveryNotifier(_clock);
        using var registration = notifier.Register(MessageId);

        notifier.Report(MessageId, Delivered());

        var attempt = await registration.WaitAsync(TimeSpan.Zero, _clock, Ct);
        Assert.Equal((DeliveryOutcome.Delivered, 250), (attempt?.Outcome, attempt?.SmtpStatus));
        Assert.Equal(["jan.kowalski@contoso.test"], attempt!.RejectedCopies);
    }

    [Fact]
    public async Task ARegistrationAfterTheAttempt_IsAnsweredAtOnce_WithoutAddresses()
    {
        var notifier = new UserMessageDeliveryNotifier(_clock);
        using (notifier.Register(MessageId))
        {
            notifier.Report(MessageId, Delivered());
        }

        _clock.Advance(UserMessageDeliveryNotifier.RecentAttemptLifetime - TimeSpan.FromSeconds(1));
        using var repeated = notifier.Register(MessageId);

        var attempt = await repeated.WaitAsync(TimeSpan.Zero, _clock, Ct);
        Assert.Equal(DeliveryOutcome.Delivered, attempt?.Outcome);
        Assert.Empty(attempt!.RejectedCopies);
    }

    [Fact]
    public async Task AnOldAttempt_OrADeferral_DoesNotAnswer()
    {
        var notifier = new UserMessageDeliveryNotifier(_clock);
        notifier.Report(MessageId, Delivered());
        notifier.Report("user-other", new UserMessageAttempt(DeliveryOutcome.Deferred, "rateLimit"));
        _clock.Advance(UserMessageDeliveryNotifier.RecentAttemptLifetime);

        using var late = notifier.Register(MessageId);
        using var deferred = notifier.Register("user-other");

        Assert.Null(await late.WaitAsync(TimeSpan.Zero, _clock, Ct));
        Assert.Null(await deferred.WaitAsync(TimeSpan.Zero, _clock, Ct));
    }

    [Fact]
    public async Task TheLatestAttempt_Wins()
    {
        var notifier = new UserMessageDeliveryNotifier(_clock);
        notifier.Report(MessageId, new UserMessageAttempt(DeliveryOutcome.Transient, "smtp.451", 451));
        notifier.Report(MessageId, Delivered());

        using var repeated = notifier.Register(MessageId);

        Assert.Equal(DeliveryOutcome.Delivered, (await repeated.WaitAsync(TimeSpan.Zero, _clock, Ct))?.Outcome);
    }
}
