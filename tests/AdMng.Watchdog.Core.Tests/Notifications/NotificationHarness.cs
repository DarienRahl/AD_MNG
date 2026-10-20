using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Notifications;
using AdMng.Watchdog.Core.Rules;
using AdMng.Watchdog.Core.Tests.Notifications.Fakes;
using AdMng.Watchdog.Testing.Stores;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Watchdog.Core.Tests.Notifications;

/// <summary>The notification pipeline wired against the in-memory store (real Core classes, fake time).</summary>
internal sealed class NotificationHarness
{
    /// <summary>Wednesday 14 Oct 2026, 10:00 in Warsaw – business time.</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    public const string AdminsGroup = "it-admins";
    public const string SecurityGroup = "security";
    public const string TeamsChannel = "teams-it";
    public const string SiemChannel = "siem";

    public NotificationHarness(WatchdogConfiguration? configuration = null)
    {
        Clock = new FakeTimeProvider(Now);
        Store = new InMemoryWatchdogStore(Clock);
        Health = new HealthRegistry();
        Signals = new WatchdogSignals(Clock);
        Config = new FakeConfigProvider(configuration ?? ConfigWith());
        Calendars = new BusinessCalendarProvider();
        Rollout = new RolloutGate(Health, Store);
        Targets = new NotificationTargetResolver(Store);
        Planner = new NotificationPlanner(Store, Targets, Calendars, Rollout);
        Rules = new RuleEngine(Config, new AdMng.Watchdog.Core.Tests.Rules.Fakes.FixedTokenSource(ConfigTokens.Unknown), Clock);
        Matcher = new RuleEngineHeldMatcher(Rules);
        Dispatch = new NotificationDispatchJob(Store, Store, Store, Config, Matcher, Planner, Rules, Rollout, new ThrottlePolicy(Store), Signals, Clock,
            NullLogger<NotificationDispatchJob>.Instance);
        FollowUps = new FollowUpPlanner(Store, Config, Clock);
    }

    public FakeTimeProvider Clock { get; }

    public InMemoryWatchdogStore Store { get; }

    public HealthRegistry Health { get; }

    public WatchdogSignals Signals { get; }

    public FakeConfigProvider Config { get; }

    public BusinessCalendarProvider Calendars { get; }

    public RolloutGate Rollout { get; }

    public NotificationTargetResolver Targets { get; }

    public NotificationPlanner Planner { get; }

    public RuleEngine Rules { get; }

    public IHeldNotificationMatcher Matcher { get; }

    public NotificationDispatchJob Dispatch { get; }

    public FollowUpPlanner FollowUps { get; }

    public WatchdogConfiguration Current => Config.Current.Configuration;

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static WatchdogConfiguration ConfigWith(params RuleSettings[] rules) => new()
    {
        General = new GeneralSettings
        {
            InstanceName = "AD Watchdog – Kraków",
            Language = "pl",
            TimeZone = "Europe/Warsaw",
            PublicBaseUrl = "https://admng-wd.contoso.com:8443",
        },
        Smtp = new SmtpSettings { Enabled = true, Host = "smtp.contoso.test", From = "AD Watchdog <watchdog@contoso.test>" },
        RecipientGroups =
        [
            new RecipientGroup { Id = AdminsGroup, Name = "Administratorzy IT", Addresses = ["łucja.nowak@contoso.test", "zbigniew.żak@contoso.test"] },
            new RecipientGroup { Id = SecurityGroup, Name = "Bezpieczeństwo", Addresses = ["soc@contoso.test"], DetailLevel = DetailLevel.Minimal },
        ],
        Channels =
        [
            new ChannelSettings { Id = TeamsChannel, Type = ChannelType.Teams, Name = "Teams – dział IT" },
            new ChannelSettings
            {
                Id = SiemChannel, Type = ChannelType.Webhook, Name = "SIEM", DetailLevel = DetailLevel.Minimal,
                Webhook = new WebhookChannelOptions { HeartbeatInterval = TimeSpan.FromMinutes(15) },
            },
        ],
        Digest = new DigestSettings { RecipientGroupIds = [SecurityGroup] },
        Rules = rules,
    };

    public static RuleSettings Rule(string id, string name = "Reguła testowa", IReadOnlyList<string>? groups = null, IReadOnlyList<string>? channels = null,
        bool immediate = true, DeliveryHours hours = DeliveryHours.Default) => new()
        {
            Id = id,
            Name = name,
            Delivery = new RuleDelivery
            {
                Immediate = immediate,
                RecipientGroupIds = groups ?? [AdminsGroup],
                ChannelIds = channels ?? [],
                Hours = hours,
            },
        };

    public static NotificationTarget AdminsTarget(WatchdogConfiguration configuration) =>
        NotificationTargetResolver.EmailTarget(configuration, AdminsGroup)!;

    public static EventAttribution Resolved(string sid = "S-1-5-21-1004336348-1177238915-682003330-1104") => new()
    {
        Status = AttributionStatus.Resolved,
        Confidence = AttributionConfidence.High,
        Actor = new PrincipalRef { Account = @"CONTOSO\adm.wójcik", DisplayName = "Paweł Wójcik", Sid = sid },
        Workstation = "WS-ADM-02",
        IpAddress = "10.0.12.35",
        ResolvedAt = Now,
    };

    public static WatchEvent Event(WatchEventKind kind = WatchEventKind.UserCreated, Severity severity = Severity.Medium, EventAttribution? attribution = null,
        string name = "Grzegorz Brzęczyszczykiewicz", Guid? objectGuid = null, DateTimeOffset? detectedAt = null, IReadOnlyList<string>? tags = null) => new()
        {
            EventId = Guid.NewGuid(),
            Kind = kind,
            Severity = severity,
            Object = new ObjectRef
            {
                ObjectGuid = objectGuid ?? Guid.NewGuid(),
                Dn = $"CN={name},OU=Księgowość,OU=Firma,DC=contoso,DC=com",
                Name = name,
                SamAccountName = "g.brzeczyszczykiewicz",
                Sid = "S-1-5-21-1004336348-1177238915-682003330-2201",
                Kind = WatchObjectKind.User,
            },
            Domain = "contoso.com",
            DetectedAt = detectedAt ?? Now,
            Source = DetectionSource.IncrementalPoll,
            Attribution = attribution ?? Resolved(),
            Tags = tags ?? [],
        };

    /// <summary>Stores the event and returns it with its id.</summary>
    public async Task<WatchEvent> StoreAsync(WatchEvent watchEvent) =>
        watchEvent with { Id = await Store.AppendAsync(watchEvent, Ct) };

    /// <summary>Stores the event, plans it with the given matches and adds the pending rows.</summary>
    public async Task<(WatchEvent Event, IReadOnlyList<PendingNotification> Planned)> PlanAsync(WatchEvent watchEvent, params PlannedMatch[] matches)
    {
        var stored = await StoreAsync(watchEvent);
        var planned = await Planner.PlanAsync(stored, matches, Current, Clock.GetUtcNow(), Ct);
        await Store.AddAsync(planned, Ct);
        return (stored, planned);
    }

    /// <summary>Every outbox message containing any of the events (distinct by message id).</summary>
    public async Task<IReadOnlyList<OutboxMessage>> MessagesAsync(params WatchEvent[] events)
    {
        var messages = new List<OutboxMessage>();
        foreach (var e in events)
        {
            messages.AddRange(await Store.GetMessagesForEventAsync(e.Id, Ct));
        }

        return [.. messages.DistinctBy(m => m.MessageId).OrderBy(m => m.Id)];
    }

    public async Task<OutboxItemDto> OutboxItemAsync(long id) =>
        (await Store.QueryAsync(new OutboxQuery { Limit = 1000 }, Ct)).Items.Single(i => i.Id == id);

    /// <summary>Lets the store mark one message Sent at <paramref name="sentAt"/>.</summary>
    public async Task MarkSentAsync(long id, DateTimeOffset sentAt)
    {
        await Store.LeaseDueAsync(Clock.GetUtcNow(), 1000, TimeSpan.FromMinutes(5), Ct);
        await Store.MarkSentAsync(id, sentAt, Ct);
    }
}
