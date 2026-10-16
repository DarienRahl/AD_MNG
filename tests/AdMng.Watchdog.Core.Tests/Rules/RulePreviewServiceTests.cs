using System.Runtime.CompilerServices;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Rules;
using AdMng.Watchdog.Core.Tests.Rules.Fakes;
using Microsoft.Extensions.Time.Testing;
using static AdMng.Watchdog.Core.Tests.Rules.TestEvents;

namespace AdMng.Watchdog.Core.Tests.Rules;

public class RulePreviewServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);   // Tuesday 11:00 in Warsaw

    private readonly FakeEventStore _store = new();
    private readonly FakeConfigProvider _config;
    private readonly FakeTimeProvider _clock = new(Now);

    public RulePreviewServiceTests()
    {
        var defaults = new DefaultConfigurationFactory().Create();
        _config = new FakeConfigProvider(defaults with
        {
            Smtp = defaults.Smtp with { Enabled = true, Host = "smtp.contoso.test", From = "nadzor@contoso.test" },
            RecipientGroups =
            [
                defaults.RecipientGroups[0] with { Addresses = ["łukasz.wiśniewski@contoso.test"] },
                defaults.RecipientGroups[1] with { Addresses = ["helpdesk.łódź@contoso.test"] },
            ],
            Channels =
            [
                DefaultConfigurationFactory.CreateChannel("siem", ChannelType.Webhook, "SIEM"),
                DefaultConfigurationFactory.CreateChannel("teams-it", ChannelType.Teams, "Teams IT") with { Enabled = false },
            ],
        });
    }

    private RulePreviewService CreateService(IBusinessHoursCalendar? calendar = null, IEventStore? store = null) =>
        new(store ?? _store, _config, new RuleEngine(_config, new FixedTokenSource(Tokens), _clock), _clock, calendar);

    private static RuleSettings Lockouts() => DefaultRules.Find(DefaultRules.Lockouts)!;

    private void Lockout(DateTimeOffset at, Guid? user = null, Severity severity = Severity.Low) =>
        _store.Add(TestEvents.Create(WatchEventKind.AccountLockedOut, $"CN=Użytkownik {at:HHmm},OU=Kraków,DC=contoso,DC=test", severity: severity,
            detectedAt: at, objectGuid: user ?? Guid.NewGuid()));

    private Task<RulePreviewResult> Preview(RuleSettings rule, int days = 7, IBusinessHoursCalendar? calendar = null) =>
        CreateService(calendar).PreviewAsync(new RulePreviewRequest { Rule = rule, Days = days }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task CountsMatchedEvents_PerLocalDay_WithNewestSamplesFirst()
    {
        Lockout(T0);
        Lockout(T0.AddHours(2));
        Lockout(Now.AddDays(-3));
        _store.Add(TestEvents.Create(WatchEventKind.UserCreated, detectedAt: T0));
        _store.Add(TestEvents.Create(WatchEventKind.AccountLockedOut, detectedAt: Now.AddDays(-30)));

        var result = await Preview(Lockouts());

        Assert.Equal(3, result.MatchedEvents);
        Assert.Equal(7, result.PerDay.Count);
        Assert.Equal(new DateOnly(2026, 10, 8), result.PerDay[0].Day);
        Assert.Equal(new DateOnly(2026, 10, 14), result.PerDay[^1].Day);
        Assert.Equal(2, result.PerDay.Single(d => d.Day == new DateOnly(2026, 10, 13)).Events);
        Assert.Equal(1, result.PerDay.Single(d => d.Day == new DateOnly(2026, 10, 11)).Events);
        Assert.Equal(result.Samples.OrderByDescending(e => e.DetectedAt), result.Samples);
        Assert.All(result.Samples, e => Assert.Equal(WatchEventKind.AccountLockedOut, e.Kind));
        Assert.False(result.ScanLimitReached);
    }

    [Fact]
    public async Task Aggregation_EventsWithinTheWindowShareOneMessage()
    {
        Lockout(T0);
        Lockout(T0.AddMinutes(2));
        Lockout(T0.AddMinutes(4));
        Lockout(T0.AddMinutes(20));

        var result = await Preview(Lockouts());

        Assert.Equal(4, result.MatchedEvents);
        Assert.Equal(2, result.EstimatedImmediateMessages);
        Assert.Equal(2, result.PerDay.Single(d => d.Day == new DateOnly(2026, 10, 13)).Messages);
    }

    [Fact]
    public async Task RepeatSuppression_OneImmediateMessagePerAccountAndHour()
    {
        var user = Guid.NewGuid();
        Lockout(T0, user);
        Lockout(T0.AddMinutes(20), user);
        Lockout(T0.AddMinutes(50), user);
        Lockout(T0.AddMinutes(70), user);

        var result = await Preview(Lockouts());

        Assert.Equal(4, result.MatchedEvents);
        Assert.Equal(2, result.EstimatedImmediateMessages);
    }

    [Fact]
    public async Task Throttle_HoldsMessagesBeyondTheHourlyCap_CriticalIsExempt()
    {
        var rule = new RuleSettings
        {
            Id = "wszystko",
            Name = "Wszystko",
            Kinds = [WatchEventKind.AccountLockedOut],
            Delivery = new RuleDelivery { Immediate = true, RecipientGroupIds = ["helpdesk"], Hours = DeliveryHours.Always, AggregationWindow = TimeSpan.Zero, MaxMessagesPerHour = 2 },
        };
        for (var i = 0; i < 5; i++)
        {
            Lockout(T0.AddMinutes(i));
        }

        Assert.Equal(3, (await Preview(rule)).EstimatedImmediateMessages);

        var critical = rule with { Severity = Severity.Critical };
        Assert.Equal(5, (await Preview(critical)).EstimatedImmediateMessages);
    }

    [Fact]
    public async Task DigestOnlyRules_SendNoImmediateMessages()
    {
        Lockout(T0);

        var result = await Preview(DefaultRules.Find(DefaultRules.Unlocks)! with { Kinds = [WatchEventKind.AccountLockedOut] });

        Assert.Equal(1, result.MatchedEvents);
        Assert.Equal(0, result.EstimatedImmediateMessages);
    }

    [Fact]
    public async Task BusinessHours_AreTakenFromTheCalendar_HighSeverityIsAlwaysSentWithDefaultHours()
    {
        Lockout(T0);
        _store.Add(TestEvents.Create(WatchEventKind.UserCreated, detectedAt: T0, severity: Severity.Low));
        _store.Add(TestEvents.Create(WatchEventKind.UserCreated, detectedAt: T0.AddHours(3), severity: Severity.High));
        var closed = new FixedCalendar(false);

        Assert.Equal(0, (await Preview(Lockouts(), calendar: closed)).EstimatedImmediateMessages);
        Assert.Equal(1, (await Preview(DefaultRules.Find(DefaultRules.NewAccounts)!, calendar: closed)).EstimatedImmediateMessages);
        Assert.Equal(2, (await Preview(DefaultRules.Find(DefaultRules.NewAccounts)!, calendar: new FixedCalendar(true))).EstimatedImmediateMessages);
    }

    [Fact]
    public async Task Webhooks_GetOneMessagePerEvent_DisabledChannelsNone()
    {
        Lockout(T0);
        Lockout(T0.AddMinutes(1));
        var rule = new RuleSettings
        {
            Id = "siem",
            Name = "SIEM",
            Kinds = [WatchEventKind.AccountLockedOut],
            Delivery = new RuleDelivery { Immediate = true, ChannelIds = ["siem", "teams-it"], Hours = DeliveryHours.Always },
        };

        Assert.Equal(2, (await Preview(rule)).EstimatedImmediateMessages);
        Assert.Equal(2, (await Preview(rule with { Delivery = rule.Delivery with { ChannelIds = [], AllEnabledChannels = true } })).EstimatedImmediateMessages);
        Assert.Equal(0, (await Preview(rule with { Delivery = rule.Delivery with { ChannelIds = ["teams-it"] } })).EstimatedImmediateMessages);
    }

    [Fact]
    public async Task PendingActors_CountAsUnresolved()
    {
        Lockout(T0);
        var rule = Lockouts() with { Actors = new ActorFilter { ExcludeActors = [SyncSid] } };

        Assert.Equal(1, (await Preview(rule)).MatchedEvents);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(90, 90)]
    [InlineData(400, 90)]
    public async Task Days_AreClampedTo1Through90(int requested, int expected) =>
        Assert.Equal(expected, (await Preview(Lockouts(), requested)).PerDay.Count);

    [Fact]
    public async Task Samples_AreCappedAt20()
    {
        for (var i = 0; i < 30; i++)
        {
            Lockout(T0.AddMinutes(i));
        }

        var result = await Preview(Lockouts());

        Assert.Equal(30, result.MatchedEvents);
        Assert.Equal(RulePreviewService.MaxSamples, result.Samples.Count);
        Assert.Equal(T0.AddMinutes(29), result.Samples[0].DetectedAt);
    }

    [Fact]
    public async Task ScanLimit_StopsAfter100000Events()
    {
        var service = CreateService(store: new GeneratedEventStore(RulePreviewService.ScanLimit + 5));

        var result = await service.PreviewAsync(new RulePreviewRequest { Rule = Lockouts() }, TestContext.Current.CancellationToken);

        Assert.True(result.ScanLimitReached);
        Assert.Equal(RulePreviewService.ScanLimit, result.MatchedEvents);
    }

    [Fact]
    public async Task PreviewAsync_RejectsAMissingRule() =>
        await Assert.ThrowsAsync<ArgumentNullException>(() => CreateService().PreviewAsync(new RulePreviewRequest { Rule = null! }, TestContext.Current.CancellationToken));

    private sealed class FixedCalendar(bool open) : IBusinessHoursCalendar
    {
        public bool IsBusinessTime(DateTimeOffset instant, WatchdogConfiguration configuration) => open;
    }

    private sealed class GeneratedEventStore(int count) : DelegatingEventStore
    {
        public override async IAsyncEnumerable<WatchEvent> GetRangeAsync(DateTimeOffset from, DateTimeOffset to, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var template = TestEvents.Create(WatchEventKind.AccountLockedOut, detectedAt: T0);
            for (var i = 0; i < count; i++)
            {
                yield return template with { Id = i + 1 };
            }

            await Task.CompletedTask;
        }
    }
}
