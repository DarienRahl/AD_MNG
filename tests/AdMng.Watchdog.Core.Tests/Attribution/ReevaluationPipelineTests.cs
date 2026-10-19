using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Notifications;
using AdMng.Watchdog.Core.Rules;
using AdMng.Watchdog.Core.Tests.Attribution.Fakes;
using AdMng.Watchdog.Core.Tests.Rules.Fakes;
using AdMng.Watchdog.Testing.Attribution;
using AdMng.Watchdog.Testing.Stores;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using static AdMng.Watchdog.Core.Tests.Attribution.Records;
using static AdMng.Watchdog.Core.Tests.Rules.TestEvents;

namespace AdMng.Watchdog.Core.Tests.Attribution;

/// <summary>
/// The re-evaluation after attribution (11.4 #9) end to end over one in-memory store: the real rules job, attribution
/// engine, notification planner, follow-up planner and dispatch job – the pending rows of the rules job and of the
/// re-evaluation share the store's one-row-per-(event, target) key, and the dispatch re-matches held rows. Shadow
/// credentials written on an ordinary user by another account: Medium while the actor is pending, High once it is known
/// (10.6.4), so <c>tier0-security</c> matches only in the re-evaluation.
/// </summary>
public sealed class ReevaluationPipelineTests : IDisposable
{
    private const string Mailbox = "zespol.it@contoso.test";
    private static readonly DateTimeOffset T = Now.AddMinutes(-2);

    private readonly Pipeline _p = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _p.Dispose();

    /// <summary>A custom immediate rule for the same recipients as <c>tier0-security</c> that excludes the actor.</summary>
    private static RuleSettings VipRule(Severity? severity = null) => new()
    {
        Id = "konta-vip",
        Name = "Konta VIP – poświadczenia",
        Kinds = [WatchEventKind.KeyCredentialChanged],
        Actors = new ActorFilter { ExcludeActors = [AdminSid] },
        Severity = severity,
        Delivery = new RuleDelivery { Immediate = true, RecipientGroupIds = [DefaultRules.ItAdminsGroup], Hours = DeliveryHours.Always },
    };

    [Fact]
    public async Task ARuleThatNoLongerMatches_DoesNotSwallowTheRuleMatchedByTheReevaluation()
    {
        // konta-vip matched provisionally while the actor was pending (its held row takes the it-admins target – the store
        // keeps one row per event and target); the actor is excluded there, so the dispatch drops that row. tier0-security,
        // matched by the re-evaluation for the same target, must still alert.
        _p.Config.Set(Pipeline.Configuration(VipRule()));
        var e = await _p.AppendShadowCredentialsAsync();
        await _p.RulesJob.RunOnceAsync(Ct);
        Assert.True(Assert.Single(await _p.Store.GetAllAsync(Ct)).HoldForAttributionUntil is not null);

        _p.SecurityStore.Add(Modified(T, Admin, "msDS-KeyCredentialLink", null, added: true));
        await _p.Engine.RunOnceAsync(Ct);
        await _p.DispatchAsync();
        await _p.DispatchAsync(TimeSpan.FromMinutes(6));

        var stored = await _p.Store.GetAsync(e.Id, Ct);
        Assert.Equal(Severity.High, stored!.Severity);
        Assert.Contains(DefaultRules.Tier0Security, stored.MatchedRules);
        var alert = Assert.Single(await _p.Store.GetMessagesForEventAsync(e.Id, Ct));
        Assert.Equal((DeliveryKind.Alert, DefaultRules.Tier0Security, Severity.High), (alert.Kind, alert.RuleId, alert.Severity));
        Assert.Equal([Mailbox], alert.Recipients);
        Assert.False(alert.SentWithPendingAttribution);
        Assert.Empty(await _p.Store.GetAllAsync(Ct));
    }

    [Fact]
    public async Task AnAlertAlreadySentToTheTarget_IsNotRepeatedByARuleMatchedInTheReevaluation()
    {
        // privileged-accounts went out when the attribution grace ended, High with the actor pending – its follow-up brings
        // the actor. A custom rule for password resets, matched by the re-evaluation (tag passwordReset) for the same
        // recipients, would repeat the alert next to the follow-up.
        var resets = new RuleSettings
        {
            Id = "resety-hasel",
            Name = "Resety haseł",
            Kinds = [WatchEventKind.PasswordChanged],
            RequiredTags = [EventTags.PasswordReset],
            Delivery = new RuleDelivery { Immediate = true, RecipientGroupIds = [DefaultRules.ItAdminsGroup], Hours = DeliveryHours.Always },
        };
        _p.Config.Set(Pipeline.Configuration(resets));
        var e = await _p.AppendAsync(Create(WatchEventKind.PasswordChanged, severity: Severity.High, tags: [EventTags.Privileged]) with
        {
            OriginatingTime = T,
            OriginatingDc = Dc1,
        });
        await _p.RulesJob.RunOnceAsync(Ct);
        await _p.DispatchAsync(TimeSpan.FromSeconds(91));
        var first = Assert.Single(await _p.Store.GetMessagesForEventAsync(e.Id, Ct));
        Assert.Equal((DefaultRules.PrivilegedAccounts, true), (first.RuleId, first.SentWithPendingAttribution));
        await _p.Store.LeaseDueAsync(_p.Time.GetUtcNow(), 10, TimeSpan.FromMinutes(1), Ct);
        await _p.Store.MarkSentAsync(first.Id, _p.Time.GetUtcNow(), Ct);

        _p.SecurityStore.Add(Account(4724, T, Admin));
        await _p.Engine.RunOnceAsync(Ct);
        await _p.DispatchAsync();
        await _p.DispatchAsync(TimeSpan.FromMinutes(6));

        Assert.Contains("resety-hasel", (await _p.Store.GetAsync(e.Id, Ct))!.MatchedRules);
        var messages = (await _p.Store.GetMessagesForEventAsync(e.Id, Ct)).OrderBy(m => m.Id).ToList();
        Assert.Equal([DeliveryKind.Alert, DeliveryKind.FollowUp], messages.Select(m => m.Kind));
        Assert.Equal(first.MessageId, messages[1].Email!.InReplyTo);
    }

    [Fact]
    public async Task AnEscalation_StillAlertsAfterALowerAlertWithoutFollowUp()
    {
        // konta-vip went out as Medium at the end of the grace (no follow-up below High); the final High is new
        // information for the recipients: tier0-security alerts.
        _p.Config.Set(Pipeline.Configuration(VipRule() with { Actors = new ActorFilter() }));
        var e = await _p.AppendShadowCredentialsAsync();
        await _p.RulesJob.RunOnceAsync(Ct);
        await _p.DispatchAsync(TimeSpan.FromMinutes(5));
        var first = Assert.Single(await _p.Store.GetMessagesForEventAsync(e.Id, Ct));
        Assert.Equal((Severity.Medium, true), (first.Severity, first.SentWithPendingAttribution));

        _p.SecurityStore.Add(Modified(T, Admin, "msDS-KeyCredentialLink", null, added: true));
        await _p.Engine.RunOnceAsync(Ct);
        await _p.DispatchAsync();

        var messages = (await _p.Store.GetMessagesForEventAsync(e.Id, Ct)).OrderBy(m => m.Id).ToList();
        Assert.Equal(["konta-vip", DefaultRules.Tier0Security], messages.Select(m => m.RuleId));
        Assert.Equal(Severity.High, messages[1].Severity);
    }

    [Fact]
    public async Task ARuleOverrideEqualToTheFinalSeverity_DoesNotHideTheReevaluation()
    {
        // konta-vip (override High, to the helpdesk) made the stored severity High while the event's own severity was
        // Medium; the attribution re-resolves it to High – equal to the stored value, so the update reports no change.
        // tier0-security (MinSeverity High) matches only now and must still reach it-admins.
        var vip = VipRule(Severity.High) with
        {
            Actors = new ActorFilter(),
            Delivery = new RuleDelivery { Immediate = true, RecipientGroupIds = [DefaultRules.HelpdeskGroup], Hours = DeliveryHours.Always },
        };
        var configuration = Pipeline.Configuration(vip);
        _p.Config.Set(configuration with
        {
            RecipientGroups = [.. configuration.RecipientGroups.Select(g => g.Id == DefaultRules.HelpdeskGroup ? g with { Addresses = ["pomoc@contoso.test"] } : g)],
        });
        var e = await _p.AppendShadowCredentialsAsync();
        await _p.RulesJob.RunOnceAsync(Ct);
        Assert.Equal(Severity.High, (await _p.Store.GetAsync(e.Id, Ct))!.Severity);

        _p.SecurityStore.Add(Modified(T, Admin, "msDS-KeyCredentialLink", null, added: true));
        await _p.Engine.RunOnceAsync(Ct);
        await _p.DispatchAsync();

        var messages = (await _p.Store.GetMessagesForEventAsync(e.Id, Ct)).OrderBy(m => m.RuleId, StringComparer.Ordinal).ToList();
        Assert.Equal([("konta-vip", "pomoc@contoso.test"), (DefaultRules.Tier0Security, Mailbox)], messages.Select(m => (m.RuleId, Assert.Single(m.Recipients))));
    }

    [Fact]
    public async Task ARuleSeverityOverride_SurvivesTheFinalAttribution()
    {
        // The rules job stored max(event severity, rule overrides) = Critical; the attribution re-resolves the event's own
        // severity (High) – the stored severity must stay the evaluated one (GUI, digest "Wymaga uwagi").
        var critical = VipRule(Severity.Critical) with { Actors = new ActorFilter(), Delivery = new RuleDelivery { Immediate = false } };
        _p.Config.Set(Pipeline.Configuration(critical));
        var e = await _p.AppendShadowCredentialsAsync();
        await _p.RulesJob.RunOnceAsync(Ct);
        Assert.Equal(Severity.Critical, (await _p.Store.GetAsync(e.Id, Ct))!.Severity);

        _p.SecurityStore.Add(Modified(T, Admin, "msDS-KeyCredentialLink", null, added: true));
        await _p.Engine.RunOnceAsync(Ct);

        var stored = (await _p.Store.GetAsync(e.Id, Ct))!;
        Assert.Equal(AttributionStatus.Resolved, stored.Attribution.Status);
        Assert.Equal(Severity.Critical, stored.Severity);
    }

    [Fact]
    public async Task TheRowsOfTheReevaluation_AreAnnouncedOnlyOnceTheAttributionIsStored()
    {
        // With HighSeverityFlushDelay 0 the dispatch job, woken by NotificationsQueued, renders the new row at once: it
        // must read the final attribution, not "ustalanie…".
        var configuration = Pipeline.Configuration();
        _p.Config.Set(configuration with { Notifications = (configuration.Notifications ?? new NotificationSettings()) with { HighSeverityFlushDelay = TimeSpan.Zero } });
        var e = await _p.AppendShadowCredentialsAsync();
        await _p.RulesJob.RunOnceAsync(Ct);
        var statusWhenQueued = new List<AttributionStatus>();
        _p.Signals.OnRaise = signal =>
        {
            if (signal == WatchdogSignal.NotificationsQueued)
            {
                statusWhenQueued.Add(_p.Store.GetAsync(e.Id, CancellationToken.None).GetAwaiter().GetResult()!.Attribution.Status);
            }
        };

        _p.SecurityStore.Add(Modified(T, Admin, "msDS-KeyCredentialLink", null, added: true));
        await _p.Engine.RunOnceAsync(Ct);

        Assert.NotEmpty(statusWhenQueued);
        Assert.All(statusWhenQueued, s => Assert.Equal(AttributionStatus.Resolved, s));
    }

    // ---- Severity model: the event's own severity is the input of every rule match ------------------------------------

    [Fact]
    public async Task ARuleOverride_DoesNotLetTheDispatchReleaseAnAlertTheOwnSeverityNoLongerQualifiesFor()
    {
        // A review rule raises privileged password changes to High (evaluated severity). The user's own change at expiry
        // (4723 by the user) makes the event's own severity Info: privileged-accounts (MinSeverity High) no longer matches.
        // The dispatch re-matches its held row – against the own severity, as the rules job does, not the raised one.
        var review = new RuleSettings
        {
            Id = "hasla-uprzywilejowane",
            Name = "Hasła kont uprzywilejowanych – przegląd",
            Kinds = [WatchEventKind.PasswordChanged],
            AnyTags = [EventTags.Privileged],
            Severity = Severity.High,
            Delivery = new RuleDelivery { Immediate = false },
        };
        _p.Config.Set(Pipeline.Configuration(review));
        var e = await _p.AppendAsync(Create(WatchEventKind.PasswordChanged, severity: Severity.High, tags: [EventTags.Privileged]) with
        {
            OriginatingTime = T,
            OriginatingDc = Dc1,
        });
        await _p.RulesJob.RunOnceAsync(Ct);
        Assert.Equal(DefaultRules.PrivilegedAccounts, Assert.Single(await _p.Store.GetAllAsync(Ct)).RuleId);

        _p.SecurityStore.Add(Account(4723, T, User));
        await _p.Engine.RunOnceAsync(Ct);
        await _p.DispatchAsync();
        await _p.DispatchAsync(TimeSpan.FromMinutes(6));

        var stored = (await _p.Store.GetAsync(e.Id, Ct))!;
        Assert.Contains(EventTags.SelfChange, stored.Tags);
        Assert.Equal((Severity.Info, Severity.High), (stored.OwnSeverity, stored.Severity));
        Assert.Empty(await _p.Store.GetMessagesForEventAsync(e.Id, Ct));
        Assert.Empty(await _p.Store.GetAllAsync(Ct));
    }

    [Fact]
    public async Task ATagAddedByTheAttribution_IsMatchedWithTheOwnSeverity_NotWithARuleOverride()
    {
        // A review rule raises description changes to High; the event's own severity is Info. The attribution finds the sync
        // account (tag syncAccount): a rule for sync-account changes from High up must not match – the rules job would not
        // match it with the same tags either.
        var review = new RuleSettings
        {
            Id = "opisy-kont",
            Name = "Opisy kont – przegląd",
            Kinds = [WatchEventKind.AttributesChanged],
            Severity = Severity.High,
            Delivery = new RuleDelivery { Immediate = false },
        };
        var sync = new RuleSettings
        {
            Id = "konta-synchronizacji",
            Name = "Zmiany wykonane przez konto synchronizacji",
            Kinds = [WatchEventKind.AttributesChanged],
            AnyTags = [EventTags.SyncAccount],
            MinSeverity = Severity.High,
            Delivery = new RuleDelivery { Immediate = true, RecipientGroupIds = [DefaultRules.ItAdminsGroup], Hours = DeliveryHours.Always },
        };
        _p.Config.Set(Pipeline.Configuration(review, sync));
        var e = await _p.AppendAsync(Create(WatchEventKind.AttributesChanged, severity: Severity.Info, changedAttributes: ["description"]) with
        {
            OriginatingTime = T,
            OriginatingDc = Dc1,
        });
        await _p.RulesJob.RunOnceAsync(Ct);
        Assert.Equal(Severity.High, (await _p.Store.GetAsync(e.Id, Ct))!.Severity);

        _p.SecurityStore.Add(Modified(T, Sync, "description", "Księgowość – Kraków", added: true));
        await _p.Engine.RunOnceAsync(Ct);
        await _p.DispatchAsync();

        var stored = (await _p.Store.GetAsync(e.Id, Ct))!;
        Assert.Contains(EventTags.SyncAccount, stored.Tags);
        Assert.DoesNotContain("konta-synchronizacji", stored.MatchedRules);
        Assert.Equal((Severity.Info, Severity.High), (stored.OwnSeverity, stored.Severity));
        Assert.Empty(await _p.Store.GetMessagesForEventAsync(e.Id, Ct));
    }

    // ---- Coverage of an earlier alert: per event, decided where the alert is completed ------------------------------------

    private const string Helpdesk = "pomoc@contoso.test";

    [Fact]
    public async Task AnEventShownLowerInsideAHighAggregatedAlert_IsNotCoveredByTheAggregatesSeverity()
    {
        // konta-vip collects a privileged password change (High) and shadow credentials (Medium while the actor is pending)
        // into one aggregated helpdesk alert – High as a whole, but the shadow credentials are shown as Medium. Once the actor
        // is known they are High and a rule for the same recipients matches: the helpdesk saw them only as Medium, so that is
        // an escalation, not a repeat.
        var vip = new RuleSettings
        {
            Id = "konta-vip",
            Name = "Konta VIP – hasła i poświadczenia",
            Kinds = [WatchEventKind.PasswordChanged, WatchEventKind.KeyCredentialChanged],
            Delivery = new RuleDelivery { Immediate = true, RecipientGroupIds = [DefaultRules.HelpdeskGroup], Hours = DeliveryHours.Always },
        };
        var shadow = new RuleSettings
        {
            Id = "poswiadczenia-pomoc",
            Name = "Shadow Credentials – helpdesk",
            Kinds = [WatchEventKind.KeyCredentialChanged],
            MinSeverity = Severity.High,
            Delivery = new RuleDelivery { Immediate = true, RecipientGroupIds = [DefaultRules.HelpdeskGroup], Hours = DeliveryHours.Always },
        };
        _p.Config.Set(WithHelpdesk(Pipeline.Configuration(vip, shadow)));
        var password = await _p.AppendAsync(Create(WatchEventKind.PasswordChanged, dn: "CN=Anna Zając,OU=Kraków,DC=contoso,DC=test",
            severity: Severity.High, tags: [EventTags.Privileged], objectGuid: Guid.Parse("0b6d5c8e-2f1a-4c3e-9d7b-5a4e3f2d1c0b"),
            sid: DomainSid + "-7702") with { OriginatingTime = T, OriginatingDc = Dc1 });
        var e = await _p.AppendShadowCredentialsAsync();
        await _p.RulesJob.RunOnceAsync(Ct);
        await _p.DispatchAsync(TimeSpan.FromSeconds(91));
        var aggregated = Assert.Single(await _p.Store.GetMessagesForEventAsync(e.Id, Ct));
        Assert.Equal([password.Id, e.Id], aggregated.EventIds.Order());
        Assert.Equal((Severity.High, true), (aggregated.Severity, aggregated.SentWithPendingAttribution));
        await _p.Store.LeaseDueAsync(_p.Time.GetUtcNow(), 10, TimeSpan.FromMinutes(1), Ct);
        await _p.Store.MarkSentAsync(aggregated.Id, _p.Time.GetUtcNow(), Ct);

        _p.SecurityStore.Add(Modified(T, Admin, "msDS-KeyCredentialLink", null, added: true));
        await _p.Engine.RunOnceAsync(Ct);
        await _p.DispatchAsync();

        var helpdeskAlerts = (await _p.Store.GetMessagesForEventAsync(e.Id, Ct))
            .Where(m => m.Kind == DeliveryKind.Alert && m.Recipients.SequenceEqual([Helpdesk]))
            .OrderBy(m => m.Id)
            .ToList();
        Assert.Equal(2, helpdeskAlerts.Count);
        Assert.Equal([e.Id], helpdeskAlerts[1].EventIds);
        Assert.Equal((Severity.High, false), (helpdeskAlerts[1].Severity, helpdeskAlerts[1].SentWithPendingAttribution));
    }

    [Fact]
    public async Task ADispatchCompletingTheEarlierAlertWhileTheReevaluationStores_DoesNotCauseADuplicate()
    {
        // privileged-accounts is due (the grace ended with the actor pending) at the very moment the re-evaluation stores the
        // row of resety-hasel for the same recipients: the dispatch completes the earlier alert first. The recipients get it
        // with the follow-up – never a second alert.
        using var p = new Pipeline(racing: true);
        p.Config.Set(Pipeline.Configuration(ResetsRule()));
        var e = await p.AppendAsync(Create(WatchEventKind.PasswordChanged, severity: Severity.High, tags: [EventTags.Privileged]) with
        {
            OriginatingTime = T,
            OriginatingDc = Dc1,
        });
        await p.RulesJob.RunOnceAsync(Ct);
        p.Time.Advance(TimeSpan.FromSeconds(91));
        p.SecurityStore.Add(Account(4724, T, Admin));
        p.Racing.BeforeWrite = () => p.DispatchAsync(TimeSpan.Zero);

        await p.Engine.RunOnceAsync(Ct);
        Assert.Null(p.Racing.BeforeWrite);
        var first = Assert.Single(await p.Store.GetMessagesForEventAsync(e.Id, Ct));
        Assert.Equal((DefaultRules.PrivilegedAccounts, true), (first.RuleId, first.SentWithPendingAttribution));
        await p.Store.LeaseDueAsync(p.Time.GetUtcNow(), 10, TimeSpan.FromMinutes(1), Ct);
        await p.Store.MarkSentAsync(first.Id, p.Time.GetUtcNow(), Ct);
        await p.DispatchAsync();
        await p.DispatchAsync(TimeSpan.FromMinutes(6));

        Assert.Contains("resety-hasel", (await p.Store.GetAsync(e.Id, Ct))!.MatchedRules);
        var messages = (await p.Store.GetMessagesForEventAsync(e.Id, Ct)).OrderBy(m => m.Id).ToList();
        Assert.Equal([DeliveryKind.Alert, DeliveryKind.FollowUp], messages.Select(m => m.Kind));
        Assert.Empty(await p.Store.GetAllAsync(Ct));
    }

    private static RuleSettings ResetsRule() => new()
    {
        Id = "resety-hasel",
        Name = "Resety haseł",
        Kinds = [WatchEventKind.PasswordChanged],
        RequiredTags = [EventTags.PasswordReset],
        Delivery = new RuleDelivery { Immediate = true, RecipientGroupIds = [DefaultRules.ItAdminsGroup], Hours = DeliveryHours.Always },
    };

    /// <summary>The configuration with the helpdesk mailbox.</summary>
    private static WatchdogConfiguration WithHelpdesk(WatchdogConfiguration configuration) => configuration with
    {
        RecipientGroups = [.. configuration.RecipientGroups.Select(g => g.Id == DefaultRules.HelpdeskGroup ? g with { Addresses = [Helpdesk] } : g)],
    };

    /// <summary>The rules job, attribution engine and dispatch job over one <see cref="InMemoryWatchdogStore"/>.</summary>
    private sealed class Pipeline : IDisposable
    {
        /// <param name="racing">The attribution engine writes through <see cref="Racing"/> (a hook runs right before its
        /// first write of pending rows).</param>
        public Pipeline(bool racing = false)
        {
            Store = new InMemoryWatchdogStore(Time);
            Racing = new RacingStore(Store);
            Config = new FakeConfigProvider(Configuration());
            Tracker = new DcLogHealthTracker(Time);
            Gate = new SourceQueryGate(Tracker, Time);
            var actors = new ActorResolver(new FakeDetectionStore(), new FakeSyncAccounts(SyncSid));
            var processor = new PendingAttributionProcessor(
                new AttributionCorrelator(SecurityStore),
                actors,
                new LogonSourceResolver(Source, Gate, new FakeReverseDns(), Time, Tracker),
                new LockoutSourceResolver(SecurityStore, Source, Gate, DomainControllers, new FixedLockoutPolicy(TimeSpan.FromMinutes(30))),
                new UnresolvedReasonResolver(SecurityStore, Tracker, Time),
                new PrivilegeAttributionInheritor(Store),
                Tracker,
                Time);
            var tokens = new FixedTokenSource(Tokens);
            Rules = new RuleEngine(Config, tokens, Time);
            var rollout = new RolloutGate(Health, Store);
            Planner = new NotificationPlanner(Store, new NotificationTargetResolver(Store), new BusinessCalendarProvider(), rollout);
            RulesJob = new RuleEvaluationJob(Store, Rules, [Planner], Config, tokens, Signals, Health, Time, NullLogger<RuleEvaluationJob>.Instance);
            Engine = new AttributionEngine(
                Config,
                Source,
                racing ? Racing : Store,
                racing ? Racing : Store,
                DomainControllers,
                new AttributionSourceResolver(DomainControllers, Health, Time),
                new SecurityEventReductionFactory(new FakeSessionProvider(new AdMng.Testing.FakeDirectorySession()), tokens,
                    new AdMng.Watchdog.Core.Detection.WatchedAttributeCatalog(Config)),
                new SecurityLogIngestor(Source, SecurityStore, Store, new SecurityEventParser(), new EventLogGapDetector(Time),
                    new EventLogOnlyDetector(actors, Time), Tracker, Health, Time, NullLogger<SecurityLogIngestor>.Instance),
                processor,
                Tracker,
                new AttributionDegradedMonitor(Tracker, Time),
                new ActorConditionHealthMonitor(Time),
                tokens,
                Health,
                Signals,
                [new FollowUpPlanner(Store, Config, Time)],
                Rules,
                [Planner],
                Time,
                NullLogger<AttributionEngine>.Instance);
            Dispatch = new NotificationDispatchJob(Store, Store, Store, Config, new RuleEngineHeldMatcher(Rules), Planner, rollout, new ThrottlePolicy(Store),
                Signals, Time, NullLogger<NotificationDispatchJob>.Instance);
        }

        public FakeTimeProvider Time { get; } = new(Now);

        public InMemoryWatchdogStore Store { get; }

        /// <summary>The engine's store in a racing pipeline.</summary>
        public RacingStore Racing { get; }

        public FakeConfigProvider Config { get; }

        public FakeSecurityEventSource Source { get; } = new();

        public FakeSecurityEventStore SecurityStore { get; } = new();

        public FakeDomainControllers DomainControllers { get; } = new(Dc1, Dc2);

        public FakeHealthRegistry Health { get; } = new();

        public HookedSignals Signals { get; } = new();

        public DcLogHealthTracker Tracker { get; }

        public SourceQueryGate Gate { get; }

        public RuleEngine Rules { get; }

        public NotificationPlanner Planner { get; }

        public RuleEvaluationJob RulesJob { get; }

        public AttributionEngine Engine { get; }

        public NotificationDispatchJob Dispatch { get; }

        /// <summary>The default document with SMTP, the it-admins mailbox, rollout ended and <paramref name="extra"/> rules.</summary>
        public static WatchdogConfiguration Configuration(params RuleSettings[] extra)
        {
            var defaults = new DefaultConfigurationFactory().Create();
            return defaults with
            {
                General = (defaults.General ?? new GeneralSettings()) with { RolloutEnded = true, TimeZone = "Europe/Warsaw" },
                Smtp = (defaults.Smtp ?? new SmtpSettings()) with { Enabled = true, Host = "smtp.contoso.test", From = "AD Watchdog <watchdog@contoso.test>" },
                RecipientGroups = [.. (defaults.RecipientGroups ?? []).Select(g => g.Id == DefaultRules.ItAdminsGroup ? g with { Addresses = [Mailbox] } : g)],
                Rules = [.. defaults.Rules ?? [], .. extra],
            };
        }

        /// <summary>Shadow credentials on an ordinary user, detected with the actor pending (Medium).</summary>
        public Task<WatchEvent> AppendShadowCredentialsAsync() => AppendAsync(Create(WatchEventKind.KeyCredentialChanged, severity: Severity.Medium) with
        {
            OriginatingTime = T,
            OriginatingDc = Dc1,
            Changes = [new AttributeChange { Attribute = "msDS-KeyCredentialLink", Redacted = true }],
        });

        public async Task<WatchEvent> AppendAsync(WatchEvent e) => e with { Id = await Store.AppendAsync(e, Ct) };

        /// <summary>Runs the dispatch job after <paramref name="after"/> (default: the high-severity flush delay).</summary>
        public async Task DispatchAsync(TimeSpan? after = null)
        {
            Time.Advance(after ?? TimeSpan.FromSeconds(10));
            var result = await Dispatch.RunOnceAsync(Ct);
            Assert.False(result.Failed);
        }

        public void Dispose() => Gate.Dispose();
    }

    /// <summary>
    /// The engine's view of the store: delegates everything to the in-memory store and runs <see cref="BeforeWrite"/> once,
    /// right before the first write that stores pending rows – a dispatch job running at that instant.
    /// </summary>
    private sealed class RacingStore(InMemoryWatchdogStore inner) : IEventStore, INotificationQueueStore
    {
        public Func<Task>? BeforeWrite { get; set; }

        public async Task CommitEvaluationAsync(IReadOnlyList<EventEvaluation> evaluations, IReadOnlyList<PendingNotification> notifications,
            CancellationToken cancellationToken)
        {
            if (notifications.Count > 0)
            {
                await RaceAsync();
            }

            await inner.CommitEvaluationAsync(evaluations, notifications, cancellationToken);
        }

        public Task<long> AppendAsync(WatchEvent watchEvent, CancellationToken cancellationToken) => inner.AppendAsync(watchEvent, cancellationToken);

        public Task<EventPage> QueryAsync(EventQuery query, CancellationToken cancellationToken) => inner.QueryAsync(query, cancellationToken);

        public Task WaitForNewerAsync(long afterId, TimeSpan timeout, CancellationToken cancellationToken) =>
            inner.WaitForNewerAsync(afterId, timeout, cancellationToken);

        public Task<WatchEvent?> GetAsync(long id, CancellationToken cancellationToken) => inner.GetAsync(id, cancellationToken);

        public Task<WatchEvent?> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken) => inner.GetByEventIdAsync(eventId, cancellationToken);

        public Task<IReadOnlyList<WatchEvent>> GetChangeSetAsync(Guid changeSetId, CancellationToken cancellationToken) =>
            inner.GetChangeSetAsync(changeSetId, cancellationToken);

        public IAsyncEnumerable<WatchEvent> GetRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
            inner.GetRangeAsync(from, to, cancellationToken);

        public Task<IReadOnlyList<WatchEvent>> GetPendingAttributionAsync(DateTimeOffset dueBefore, int limit, CancellationToken cancellationToken) =>
            inner.GetPendingAttributionAsync(dueBefore, limit, cancellationToken);

        public Task UpdateAttributionAsync(long id, AttributionUpdate update, CancellationToken cancellationToken) =>
            inner.UpdateAttributionAsync(id, update, cancellationToken);

        public Task<IReadOnlyList<WatchEvent>> GetUnevaluatedAsync(int limit, CancellationToken cancellationToken) => inner.GetUnevaluatedAsync(limit, cancellationToken);

        public Task<bool> AcknowledgeAsync(long id, string by, string? comment, DateTimeOffset at, CancellationToken cancellationToken) =>
            inner.AcknowledgeAsync(id, by, comment, at, cancellationToken);

        public Task<int> AcknowledgeManyAsync(IReadOnlyList<long> ids, string by, string? comment, DateTimeOffset at, CancellationToken cancellationToken) =>
            inner.AcknowledgeManyAsync(ids, by, comment, at, cancellationToken);

        public Task<int> CountForObjectAsync(Guid objectGuid, WatchEventKind kind, DateTimeOffset since, CancellationToken cancellationToken) =>
            inner.CountForObjectAsync(objectGuid, kind, since, cancellationToken);

        public Task<EventCountsDto> CountAsync(DateTimeOffset since, CancellationToken cancellationToken) => inner.CountAsync(since, cancellationToken);

        public Task<long> GetLatestIdAsync(CancellationToken cancellationToken) => inner.GetLatestIdAsync(cancellationToken);

        public Task<IReadOnlyList<PendingNotification>> GetAllAsync(CancellationToken cancellationToken) => inner.GetAllAsync(cancellationToken);

        public Task AddAsync(IReadOnlyList<PendingNotification> notifications, CancellationToken cancellationToken) => inner.AddAsync(notifications, cancellationToken);

        public Task CompleteAsync(IReadOnlyList<long> pendingIds, OutboxMessage? message, CancellationToken cancellationToken) =>
            inner.CompleteAsync(pendingIds, message, cancellationToken);

        public Task DropAsync(IReadOnlyList<long> pendingIds, CancellationToken cancellationToken) => inner.DropAsync(pendingIds, cancellationToken);

        public Task RescheduleAsync(IReadOnlyList<long> pendingIds, DateTimeOffset dueAt, CancellationToken cancellationToken) =>
            inner.RescheduleAsync(pendingIds, dueAt, cancellationToken);

        private async Task RaceAsync()
        {
            if (BeforeWrite is { } race)
            {
                BeforeWrite = null;
                await race();
            }
        }
    }

    /// <summary>Records signals and lets a test look at the store at the moment one is raised.</summary>
    private sealed class HookedSignals : IWatchdogSignals
    {
        public Action<WatchdogSignal>? OnRaise { get; set; }

        public void Raise(WatchdogSignal signal) => OnRaise?.Invoke(signal);

        public ISignalSubscription Subscribe(IReadOnlyList<WatchdogSignal> signals) => new FakeSignals().Subscribe(signals);
    }
}
