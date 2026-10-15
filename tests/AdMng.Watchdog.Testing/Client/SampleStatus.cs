using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using static AdMng.Watchdog.Testing.Client.SamplePrincipals;

namespace AdMng.Watchdog.Testing.Client;

/// <summary>
/// Service status, callers, digest preview, delivery log and configuration audit of the Polish sample data.
/// </summary>
public static class SampleStatus
{
    private static DateTimeOffset Now => SampleEvents.Now;

    /// <summary>An administrator (Admin role, full detail level).</summary>
    public static MeDto Admin { get; } = new()
    {
        Account = JanKowalski.Account!,
        Sid = JanKowalski.Sid,
        AuthenticationType = "Kerberos",
        Roles = [WatchdogRole.Viewer, WatchdogRole.Admin],
        DetailLevel = DetailLevel.Full,
    };

    /// <summary>A helpdesk member (Viewer role, standard detail level).</summary>
    public static MeDto Viewer { get; } = new()
    {
        Account = AnnaNowak.Account!,
        Sid = AnnaNowak.Sid,
        AuthenticationType = "Kerberos",
        Roles = [WatchdogRole.Viewer],
        DetailLevel = DetailLevel.Standard,
    };

    /// <summary>The status with a Teams channel in trouble, one unreadable DC log, an exclusion suggestion.</summary>
    /// <param name="rolloutUntil">End of rollout mode, or null when it is not active.</param>
    public static StatusDto Create(DateTimeOffset? rolloutUntil = null) => new()
    {
        ServiceVersion = "1.0.0",
        ApiVersion = WatchdogRoutes.ApiVersion,
        InstanceName = "AD Watchdog – Contoso",
        Domain = Domain,
        StartedAt = Now.AddDays(-3).AddHours(-2),
        Now = Now,
        Directory = new DirectoryStatusDto
        {
            State = HealthState.Ok,
            DcHostName = "dc01.contoso.test",
            IsPdc = true,
            InvocationId = Guid.Parse("7d1c2f4e-5b6a-4c3d-8e9f-0a1b2c3d4e5f"),
            HighestUsn = 1_204_877,
            LastPollAt = Now.AddSeconds(-9),
            LastChangeAt = Now.AddMinutes(-10),
            LastFullSyncAt = new DateTimeOffset(2026, 10, 14, 0, 30, 0, TimeSpan.Zero),
            BaselineCompletedAt = Now.AddDays(-3),
            SnapshotObjects = 4_812,
        },
        Attribution = new AttributionStatusDto
        {
            Enabled = true,
            DomainControllers =
            [
                new DcLogStatusDto
                {
                    Host = "dc01.contoso.test",
                    State = HealthState.Ok,
                    LastReadAt = Now.AddSeconds(-20),
                    LastRecordId = 1_885_010,
                    Coverage24h = 0.96,
                    SourceLookups24h = 212,
                    SourceLookupAverage = TimeSpan.FromMilliseconds(180),
                },
                new DcLogStatusDto
                {
                    Host = "dc02.contoso.test",
                    State = HealthState.Error,
                    LastReadAt = Now.AddHours(-5),
                    LastRecordId = 2_201_004,
                    Coverage24h = 0.41,
                    ErrorCode = "1722",
                    SourceLookups24h = 38,
                    SourceLookupAverage = TimeSpan.FromMilliseconds(950),
                    SourceLookupTimeouts24h = 6,
                },
            ],
        },
        Checks =
        [
            new HealthCheckDto { Code = "directory.connection", State = HealthState.Ok, CheckedAt = Now.AddSeconds(-9) },
            new HealthCheckDto { Code = "permission.uSNChanged", State = HealthState.Ok, CheckedAt = Now.AddHours(-2) },
            new HealthCheckDto
            {
                Code = "permission.deletedObjects",
                State = HealthState.Warning,
                Detail = "CN=Deleted Objects cannot be listed by the service account.",
                CheckedAt = Now.AddMinutes(-20),
            },
            new HealthCheckDto
            {
                Code = "eventlog.dc02.contoso.test",
                State = HealthState.Error,
                Detail = "RPC server unavailable.",
                Args = ["dc02.contoso.test", "1722"],
                CheckedAt = Now.AddMinutes(-1),
            },
            new HealthCheckDto
            {
                Code = "attribution.actorConditionsUnreliable",
                State = HealthState.Warning,
                Args = ["tier0-security", nameof(WatchEventKind.KeyCredentialChanged)],
                CheckedAt = Now.AddMinutes(-5),
            },
            new HealthCheckDto { Code = "storage.free", State = HealthState.Ok, Args = ["182"], CheckedAt = Now.AddMinutes(-15) },
        ],
        Channels =
        [
            new ChannelHealthDto { ChannelId = "email", ChannelType = DeliveryChannelType.Email, State = HealthState.Ok, LastSuccessAt = Now.AddMinutes(-9) },
            new ChannelHealthDto
            {
                ChannelId = "teams-it",
                ChannelType = DeliveryChannelType.Teams,
                State = HealthState.Error,
                LastSuccessAt = Now.AddHours(-14),
                LastFailureAt = Now.AddMinutes(-12),
                LastErrorCode = "http.401",
                ConsecutiveFailures = 4,
                Pending = 3,
            },
            new ChannelHealthDto { ChannelId = "siem", ChannelType = DeliveryChannelType.Webhook, State = HealthState.Ok, LastSuccessAt = Now.AddMinutes(-2) },
        ],
        Storage = new StorageStatusDto
        {
            DataDirectory = @"C:\ProgramData\AdMng\Watchdog\data",
            DatabaseBytes = 48_234_496,
            FreeBytes = 195_421_446_144,
            EventCount = 18_420,
            OldestEventAt = Now.AddDays(-3),
            LastBackupAt = new DateTimeOffset(2026, 10, 14, 1, 15, 0, TimeSpan.Zero),
        },
        Last24Hours = new EventCountsDto
        {
            Total = 27,
            BySeverity = new Dictionary<Severity, int>
            {
                [Severity.Info] = 12,
                [Severity.Low] = 6,
                [Severity.Medium] = 3,
                [Severity.High] = 4,
                [Severity.Critical] = 2,
            },
            Unacknowledged = 24,
            UnacknowledgedHighOrAbove = 5,
        },
        NextDigestAt = new DateTimeOffset(2026, 10, 15, 5, 30, 0, TimeSpan.Zero),
        RolloutModeUntil = rolloutUntil,
        Suggestions =
        [
            new ExclusionSuggestionDto
            {
                RuleId = "tier0-security",
                ActorSid = DomainSid + "-1181",
                ActorAccount = @"CONTOSO\MSOL_4f2a9c1b7e30",
                ReasonCode = "entraConnectSync",
            },
            new ExclusionSuggestionDto
            {
                RuleId = "bursts",
                ActorSid = DomainSid + "-1182",
                ActorAccount = @"CONTOSO\svc-porzadki",
                ReasonCode = "computerCleanupAccount",
            },
        ],
    };

    /// <summary>The digest as it would be sent now.</summary>
    public static DigestPreviewDto Digest() => new()
    {
        PeriodStart = new DateTimeOffset(2026, 10, 13, 5, 30, 0, TimeSpan.Zero),
        PeriodEnd = Now,
        Subject = "[AD Watchdog] Raport dzienny 14.10.2026 – 27 zdarzeń (2 krytyczne)",
        Text = string.Join(
            Environment.NewLine,
            "Raport dzienny AD Watchdog – contoso.test",
            "Okres: 13.10.2026 07:30 – 14.10.2026 12:30",
            string.Empty,
            "KRYTYCZNE (2)",
            "  12:42  Dostęp uprzywilejowany: Łukasz Wiśniewski → Domain Admins (przez GG_Administratorzy_Serwerów), wykonawca: CONTOSO\\jan.kowalski",
            "  18:30  Wyczyszczono dziennik zabezpieczeń na DC02, wykonawca: CONTOSO\\Administrator",
            string.Empty,
            "BLOKADY KONT",
            "  Żaneta Szymańska – zablokowane 5× dziś, ostatnio 14:30 (WRO-LT014, 10.20.4.17)",
            "  Paweł Lewandowski – zablokowane 1× dziś, ostatnio 12:20",
            string.Empty,
            "AKTYWNE WYJĄTKI",
            "  tier0-security: konto synchronizacji Entra Connect (msDS-KeyCredentialLink)",
            "  lockouts: kiosk w recepcji (do 31.12.2026)"),
        Html = "<html><body><h1>Raport dzienny AD Watchdog</h1></body></html>",
        EventCount = 27,
        NextScheduledAt = new DateTimeOffset(2026, 10, 15, 5, 30, 0, TimeSpan.Zero),
        LastSentAt = new DateTimeOffset(2026, 10, 14, 5, 30, 4, TimeSpan.Zero),
    };

    /// <summary>The delivery log, newest first (ids 120 down to 111).</summary>
    public static List<OutboxItemDto> Outbox() =>
    [
        Item(120, DeliveryChannelType.Teams, "teams-it", "Teams – Administratorzy IT", DeliveryKind.Health, OutboxStatus.Retrying, Now.AddMinutes(-12),
            "[AD Watchdog] Kanał Teams nie działa", attempts: 4, error: "http.401", errorText: "HTTP 401 Unauthorized"),
        Item(119, DeliveryChannelType.Email, "email", null, DeliveryKind.Alert, OutboxStatus.Sent, Now.AddMinutes(-30),
            "[AD Watchdog] Zmiana konfiguracji usługi Watchdog", recipients: 2),
        Item(118, DeliveryChannelType.Webhook, "siem", "SIEM (Splunk)", DeliveryKind.Alert, OutboxStatus.Sent, Now.AddMinutes(-40), "ad.user.keyCredentialChanged"),
        Item(117, DeliveryChannelType.Teams, "teams-it", "Teams – Administratorzy IT", DeliveryKind.Alert, OutboxStatus.Dead, Now.AddHours(-9),
            "Dostęp uprzywilejowany: Łukasz Wiśniewski", attempts: 8, error: "http.401", errorText: "HTTP 401 Unauthorized (SAS signature invalid)"),
        Item(116, DeliveryChannelType.Email, "email", null, DeliveryKind.FollowUp, OutboxStatus.Sent, Now.AddHours(-10),
            "Re: [AD Watchdog] SPN dodany do svc-sql – wykonawca niejednoznaczny", recipients: 2),
        Item(115, DeliveryChannelType.Email, "email", null, DeliveryKind.Digest, OutboxStatus.Sent, Now.AddHours(-5),
            "[AD Watchdog] Raport dzienny 14.10.2026", recipients: 2, events: 31),
        Item(114, DeliveryChannelType.Email, "email", null, DeliveryKind.Alert, OutboxStatus.Dead, Now.AddHours(-20),
            "[AD Watchdog] Blokady kont (3)", attempts: 6, error: "smtp.550", errorText: "550 5.1.1 recipient rejected", recipients: 1),
        Item(113, DeliveryChannelType.Email, "email", null, DeliveryKind.Alert, OutboxStatus.Pending, Now.AddMinutes(-1),
            "[AD Watchdog] Blokada konta: Paweł Lewandowski", recipients: 1),
        Item(112, DeliveryChannelType.Webhook, "siem", "SIEM (Splunk)", DeliveryKind.Health, OutboxStatus.Sent, Now.AddMinutes(-15), "watchdog.heartbeat", events: 0),
        Item(111, DeliveryChannelType.Email, "email", null, DeliveryKind.Test, OutboxStatus.Cancelled, Now.AddDays(-1), "Wiadomość testowa AD Watchdog"),
    ];

    /// <summary>Saved configuration changes, newest first.</summary>
    public static List<ConfigAuditEntry> Audit() =>
    [
        new() { Id = 7, At = Now.AddMinutes(-30), BySid = JanKowalski.Sid, ByAccount = JanKowalski.Account!, Summary = "rules[lockouts].exclusions +1 -0; channels[teams-it].url replaced", Version = 7 },
        new() { Id = 6, At = Now.AddDays(-1), BySid = JanKowalski.Sid, ByAccount = JanKowalski.Account!, Summary = "recipientGroups[it-admins].addresses +1 -0", Version = 6 },
        new() { Id = 5, At = Now.AddDays(-2), BySid = JanKowalski.Sid, ByAccount = JanKowalski.Account!, Summary = "channels[siem] added", Version = 5 },
        new() { Id = 4, At = Now.AddDays(-2).AddHours(-1), BySid = JanKowalski.Sid, ByAccount = JanKowalski.Account!, Summary = "smtp.password set; smtp.host changed", Version = 4 },
    ];

    /// <summary>Deliveries of the sample events (event id → deliveries).</summary>
    public static Dictionary<long, List<EventDeliveryDto>> Deliveries() => new()
    {
        [SampleEvents.PrivilegeGrantedId] =
        [
            Delivery(119, DeliveryChannelType.Email, "email", null, DeliveryKind.Alert, OutboxStatus.Sent, Now.AddHours(-9)),
            Delivery(117, DeliveryChannelType.Teams, "teams-it", "Teams – Administratorzy IT", DeliveryKind.Alert, OutboxStatus.Dead, Now.AddHours(-9), "http.401"),
        ],
        [SampleEvents.LockoutId] =
        [
            Delivery(114, DeliveryChannelType.Email, "email", null, DeliveryKind.Alert, OutboxStatus.Dead, Now.AddHours(-20), "smtp.550"),
        ],
        [SampleEvents.AmbiguousId] =
        [
            Delivery(116, DeliveryChannelType.Email, "email", null, DeliveryKind.FollowUp, OutboxStatus.Sent, Now.AddHours(-10)),
        ],
    };

    private static OutboxItemDto Item(
        long id,
        DeliveryChannelType type,
        string channelId,
        string? channelName,
        DeliveryKind kind,
        OutboxStatus status,
        DateTimeOffset created,
        string subject,
        int attempts = 1,
        string? error = null,
        string? errorText = null,
        int recipients = 0,
        int events = 1) => new()
        {
            Id = id,
            MessageId = $"<{SampleEvents.EventGuid(id):D}@admng-wd.contoso.com>",
            ChannelType = type,
            ChannelId = channelId,
            ChannelName = channelName,
            Kind = kind,
            Status = status,
            Attempts = attempts,
            CreatedAt = created,
            NextAttemptAt = status is OutboxStatus.Retrying or OutboxStatus.Pending ? created.AddMinutes(15) : null,
            SentAt = status == OutboxStatus.Sent ? created.AddSeconds(3) : null,
            LastErrorCode = error,
            LastError = errorText,
            Subject = subject,
            EventCount = events,
            RecipientCount = recipients,
        };

    private static EventDeliveryDto Delivery(
        long outboxId,
        DeliveryChannelType type,
        string channelId,
        string? channelName,
        DeliveryKind kind,
        OutboxStatus status,
        DateTimeOffset created,
        string? error = null) => new()
        {
            OutboxId = outboxId,
            ChannelType = type,
            ChannelId = channelId,
            ChannelName = channelName,
            Kind = kind,
            Status = status,
            CreatedAt = created,
            SentAt = status == OutboxStatus.Sent ? created.AddSeconds(3) : null,
            LastErrorCode = error,
        };
}
