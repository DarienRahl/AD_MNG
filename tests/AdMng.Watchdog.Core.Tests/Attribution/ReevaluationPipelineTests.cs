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
    public async Task AnEscalation_AfterALowerAlertWithoutFollowUp_RepliesInItsThread()
    {
        // konta-vip went out as Medium at the end of the grace (no follow-up below High); the final High is new
        // information for the recipients: tier0-security, matched by the re-evaluation for the same recipients, escalates
        // in the thread of that alert – one message with the actor and the new severity, not a second alert.
        _p.Config.Set(Pipeline.Configuration(VipRule() with { Actors = new ActorFilter() }));
        var e = await _p.AppendShadowCredentialsAsync();
        await _p.RulesJob.RunOnceAsync(Ct);
        await _p.DispatchAsync(TimeSpan.FromMinutes(5));
        var first = Assert.Single(await _p.Store.GetMessagesForEventAsync(e.Id, Ct));
        Assert.Equal((Severity.Medium, true), (first.Severity, first.SentWithPendingAttribution));
        await _p.MarkSentAsync(e);

        _p.SecurityStore.Add(Modified(T, Admin, "msDS-KeyCredentialLink", null, added: true));
        await _p.Engine.RunOnceAsync(Ct);
        await _p.DispatchAsync();

        var messages = (await _p.Store.GetMessagesForEventAsync(e.Id, Ct)).OrderBy(m => m.Id).ToList();
        Assert.Equal([(DeliveryKind.Alert, "konta-vip"), (DeliveryKind.FollowUp, DefaultRules.Tier0Security)], messages.Select(m => (m.Kind, m.RuleId!)));
        Assert.Equal(Severity.High, messages[1].Severity);
        Assert.Equal(first.MessageId, messages[1].Email!.InReplyTo);
        Assert.Empty(await _p.Store.GetAllAsync(Ct));
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

    // ---- One alert per (event, target) with the strongest treatment of every rule that matches it now -----------------

    /// <summary>A custom rule for the it-admins mailbox that lowers its notifications to Low (Default hours: business hours
    /// only below High).</summary>
    private static RuleSettings LoweringRule() => new()
    {
        Id = "konta-vip",
        Name = "Konta VIP – poświadczenia (niski)",
        Kinds = [WatchEventKind.KeyCredentialChanged],
        Severity = Severity.Low,
        Delivery = new RuleDelivery { Immediate = true, RecipientGroupIds = [DefaultRules.ItAdminsGroup], Hours = DeliveryHours.Default },
    };

    [Fact]
    public async Task ARuleAddedByTheReevaluation_TakesOverTheHeldTarget_WithItsSeverityAndName()
    {
        // konta-vip planned the it-admins row (Low, held for the actor). The attribution makes the shadow credentials High:
        // tier0-security – earlier in the configuration – now matches the same recipients. The alert must be tier0-security's,
        // High, not konta-vip's Low.
        _p.Config.Set(Pipeline.Configuration(LoweringRule()));
        var e = await _p.AppendShadowCredentialsAsync();
        await _p.RulesJob.RunOnceAsync(Ct);
        Assert.Equal(("konta-vip", Severity.Low), (Assert.Single(await _p.Store.GetAllAsync(Ct)).RuleId, Assert.Single(await _p.Store.GetAllAsync(Ct)).Severity));

        _p.SecurityStore.Add(Modified(T, Admin, "msDS-KeyCredentialLink", null, added: true));
        await _p.Engine.RunOnceAsync(Ct);
        await _p.DispatchAsync();
        await _p.DispatchAsync(TimeSpan.FromMinutes(6));

        var alert = Assert.Single(await _p.Store.GetMessagesForEventAsync(e.Id, Ct));
        Assert.Equal((DeliveryKind.Alert, DefaultRules.Tier0Security, Severity.High), (alert.Kind, alert.RuleId, alert.Severity));
        Assert.Equal([Mailbox], alert.Recipients);
        Assert.StartsWith("[AD Watchdog][WYSOKI]", alert.Email!.Subject, StringComparison.Ordinal);
        Assert.Empty(await _p.Store.GetAllAsync(Ct));
    }

    [Fact]
    public async Task ARuleAddedByTheReevaluation_IsNotDeferredToBusinessHoursByTheHeldRowOfALowerRule()
    {
        // 16:58 in Warsaw: konta-vip (Low, Default hours) still plans within business hours. The actor is known at 17:01 –
        // after hours. tier0-security (High, Always) must alert at once instead of konta-vip's row being dropped.
        _p.Time.Advance(TimeSpan.FromHours(6) + TimeSpan.FromMinutes(58));
        var detected = _p.Time.GetUtcNow();
        _p.Config.Set(Pipeline.Configuration(LoweringRule()));
        var e = await _p.AppendShadowCredentialsAsync(detected);
        await _p.RulesJob.RunOnceAsync(Ct);
        Assert.Single(await _p.Store.GetAllAsync(Ct));

        _p.Time.Advance(TimeSpan.FromMinutes(3));
        _p.SecurityStore.Add(Modified(detected.AddMinutes(-2), Admin, "msDS-KeyCredentialLink", null, added: true));
        await _p.Engine.RunOnceAsync(Ct);
        await _p.DispatchAsync();

        var alert = Assert.Single(await _p.Store.GetMessagesForEventAsync(e.Id, Ct));
        Assert.Equal((DeliveryKind.Alert, DefaultRules.Tier0Security, Severity.High), (alert.Kind, alert.RuleId, alert.Severity));
        Assert.Empty(await _p.Store.GetAllAsync(Ct));
    }

    [Fact]
    public async Task AFailedRematchAtRelease_PassesTheTargetToTheNextRuleThatStillMatches()
    {
        // Two custom rules for the it-admins mailbox matched a description change while the actor was pending: konta-vip
        // (first, excludes the administrator) took the target. The actor is that administrator – konta-vip no longer
        // matches, opisy-kont still does and must alert.
        var vip = new RuleSettings
        {
            Id = "konta-vip",
            Name = "Konta VIP – zmiany opisu",
            Kinds = [WatchEventKind.AttributesChanged],
            Actors = new ActorFilter { ExcludeActors = [AdminSid] },
            Delivery = new RuleDelivery { Immediate = true, RecipientGroupIds = [DefaultRules.ItAdminsGroup], Hours = DeliveryHours.Always },
        };
        var descriptions = new RuleSettings
        {
            Id = "opisy-kont",
            Name = "Zmiany opisów kont",
            Kinds = [WatchEventKind.AttributesChanged],
            Delivery = new RuleDelivery { Immediate = true, RecipientGroupIds = [DefaultRules.ItAdminsGroup], Hours = DeliveryHours.Always },
        };
        _p.Config.Set(Pipeline.Configuration(vip, descriptions));
        var e = await _p.AppendAsync(Create(WatchEventKind.AttributesChanged, severity: Severity.Medium, changedAttributes: ["description"]) with
        {
            OriginatingTime = T,
            OriginatingDc = Dc1,
        });
        await _p.RulesJob.RunOnceAsync(Ct);
        Assert.Equal("konta-vip", Assert.Single(await _p.Store.GetAllAsync(Ct)).RuleId);

        _p.SecurityStore.Add(Modified(T, Admin, "description", "Księgowość – Gdańsk", added: true));
        await _p.Engine.RunOnceAsync(Ct);
        await _p.DispatchAsync();
        await _p.DispatchAsync(TimeSpan.FromMinutes(6));

        var alert = Assert.Single(await _p.Store.GetMessagesForEventAsync(e.Id, Ct));
        Assert.Equal((DeliveryKind.Alert, "opisy-kont", Severity.Medium, false), (alert.Kind, alert.RuleId, alert.Severity, alert.SentWithPendingAttribution));
        Assert.Equal([Mailbox], alert.Recipients);
        Assert.Empty(await _p.Store.GetAllAsync(Ct));
    }

    [Fact]
    public async Task TwoRulesForOneTarget_AlertOnce_WithTheHighestSeverityAndTheEarliestDelivery()
    {
        // The rules job: konta-vip (earlier, lowers to Low, Default hours) and opisy-kont (High, Always) both notify the
        // it-admins mailbox about a High change whose actor is already known. One alert, High, after the high-severity flush
        // delay – not konta-vip's Low after its 5-minute window.
        var vip = new RuleSettings
        {
            Id = "konta-vip",
            Name = "Konta VIP – zmiany opisu",
            Kinds = [WatchEventKind.AttributesChanged],
            Severity = Severity.Low,
            Delivery = new RuleDelivery { Immediate = true, RecipientGroupIds = [DefaultRules.ItAdminsGroup], Hours = DeliveryHours.Default },
        };
        var descriptions = new RuleSettings
        {
            Id = "opisy-kont",
            Name = "Zmiany opisów kont",
            Kinds = [WatchEventKind.AttributesChanged],
            Delivery = new RuleDelivery { Immediate = true, RecipientGroupIds = [DefaultRules.ItAdminsGroup], Hours = DeliveryHours.Always },
        };
        _p.Config.Set(Pipeline.Configuration(vip, descriptions));
        var e = await _p.AppendAsync(Create(WatchEventKind.AttributesChanged, severity: Severity.High, changedAttributes: ["description"],
            attribution: Resolved(AdminSid)));
        await _p.RulesJob.RunOnceAsync(Ct);

        await _p.DispatchAsync();

        var alert = Assert.Single(await _p.Store.GetMessagesForEventAsync(e.Id, Ct));
        Assert.Equal((DeliveryKind.Alert, "konta-vip", Severity.High), (alert.Kind, alert.RuleId, alert.Severity));
        Assert.Empty(await _p.Store.GetAllAsync(Ct));
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
    public async Task AnEventShownLowerInsideAHighAggregatedAlert_IsEscalated_NotCoveredByTheAggregatesSeverity()
    {
        // konta-vip collects a privileged password change (High) and shadow credentials (Medium while the actor is pending)
        // into one aggregated helpdesk alert – High as a whole, but the shadow credentials are shown as Medium. Once the actor
        // is known they are High and a rule for the same recipients matches: the helpdesk saw them only as Medium, so that is
        // an escalation, not a repeat – one follow-up in the thread with the actor and the new severity of that event only
        // (the password change is still waiting for its actor).
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

        var helpdesk = (await _p.Store.GetMessagesForEventAsync(e.Id, Ct))
            .Where(m => m.Recipients.SequenceEqual([Helpdesk]))
            .OrderBy(m => m.Id)
            .ToList();
        Assert.Equal([DeliveryKind.Alert, DeliveryKind.FollowUp], helpdesk.Select(m => m.Kind));
        var escalation = helpdesk[1];
        Assert.Equal([e.Id], escalation.EventIds);
        Assert.Equal([new OutboxEventState(e.Id, Severity.High, false)], escalation.EventStates);
        Assert.Equal(aggregated.MessageId, escalation.Email!.InReplyTo);
        Assert.Contains("Średni → Wysoki", escalation.Email.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain(await _p.Store.GetMessagesForEventAsync(password.Id, Ct), m => m.Kind == DeliveryKind.FollowUp);
    }

    [Fact]
    public async Task ADispatchRunningWhileTheReevaluationStores_WaitsForIt_AndSendsOneAlertWithTheFinalState()
    {
        // privileged-accounts is due (the grace ended with the actor pending) at the very moment the re-evaluation stores the
        // row of resety-hasel for the same recipients. The dispatch run waits for the re-evaluation and the attribution
        // update (one evaluation section) and sends one alert with the actor – never a second alert.
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
        Task? dispatch = null;
        p.Racing.BeforeWrite = async () =>
        {
            dispatch = Task.Run(() => p.DispatchAsync(TimeSpan.Zero), Ct);
            await Task.WhenAny(dispatch, Task.Delay(TimeSpan.FromMilliseconds(500), Ct));
        };

        await p.Engine.RunOnceAsync(Ct);
        Assert.Null(p.Racing.BeforeWrite);
        Assert.NotNull(dispatch);
        await dispatch;
        var first = Assert.Single(await p.Store.GetMessagesForEventAsync(e.Id, Ct));
        Assert.Equal((DeliveryKind.Alert, DefaultRules.PrivilegedAccounts, false), (first.Kind, first.RuleId, first.SentWithPendingAttribution));
        await p.Store.LeaseDueAsync(p.Time.GetUtcNow(), 10, TimeSpan.FromMinutes(1), Ct);
        await p.Store.MarkSentAsync(first.Id, p.Time.GetUtcNow(), Ct);
        await p.DispatchAsync();
        await p.DispatchAsync(TimeSpan.FromMinutes(6));

        Assert.Contains("resety-hasel", (await p.Store.GetAsync(e.Id, Ct))!.MatchedRules);
        Assert.Single(await p.Store.GetMessagesForEventAsync(e.Id, Ct));
        Assert.Empty(await p.Store.GetAllAsync(Ct));
    }

    [Fact]
    public async Task AReevaluationWhileTheDispatchCompletesTheHeldRow_DoesNotLoseTheStrongerTreatment()
    {
        // konta-vip (Low) holds the it-admins row; its window ends and the dispatch releases it at the very moment the actor
        // becomes known – the re-evaluation re-plans that row for tier0-security (High) while the dispatch is completing it
        // with konta-vip's treatment. The recipients must still learn that the shadow credentials are High.
        using var p = new Pipeline(hookedDispatch: true);
        p.Config.Set(Pipeline.Configuration(LoweringRule()));
        var e = await p.AppendShadowCredentialsAsync();
        await p.RulesJob.RunOnceAsync(Ct);
        Assert.Equal("konta-vip", Assert.Single(await p.Store.GetAllAsync(Ct)).RuleId);
        p.SecurityStore.Add(Modified(T, Admin, "msDS-KeyCredentialLink", null, added: true));
        Task<JobResult>? engine = null;
        p.DispatchQueue.BeforeComplete = async () =>
        {
            // The attribution job runs concurrently; it may have to wait for the dispatch.
            engine = Task.Run(() => p.Engine.RunOnceAsync(Ct), Ct);
            await Task.WhenAny(engine, Task.Delay(TimeSpan.FromMilliseconds(500), Ct));
        };

        await p.DispatchAsync(TimeSpan.FromMinutes(5));
        Assert.NotNull(engine);
        Assert.False((await engine).Failed);
        await p.MarkSentAsync(e);
        await p.DispatchAsync();
        await p.DispatchAsync(TimeSpan.FromMinutes(6));

        // konta-vip's Low alert went out first: tier0-security escalates in its thread (13.2 #7).
        var messages = (await p.Store.GetMessagesForEventAsync(e.Id, Ct)).OrderBy(m => m.Id).ToList();
        var last = messages[^1];
        Assert.Equal((DeliveryKind.FollowUp, DefaultRules.Tier0Security, Severity.High, false), (last.Kind, last.RuleId, last.Severity, last.SentWithPendingAttribution));
        Assert.Equal([Mailbox], last.Recipients);
        Assert.Empty(await p.Store.GetAllAsync(Ct));
    }

    [Theory]
    [InlineData("commit", false)]
    [InlineData("commit", true)]
    [InlineData("update", false)]
    [InlineData("update", true)]
    public async Task APassInterruptedAfterTheTakeover_AndARestart_AlertOnceUnderTheRuleThatTookOver(string failingWrite, bool dispatchInBetween)
    {
        // tier0-security takes over the held row of konta-vip, then the pass fails (before or after it stored the
        // evaluation, always before the attribution) and the service restarts – possibly after a dispatch sent the taken-over
        // row with the actor still pending. Repeated cycles afterwards: one tier0-security alert (with its follow-up when it
        // went out before the actor was stored), never konta-vip's Low alert, never a second alert.
        using var p = new Pipeline(racing: true);
        p.Config.Set(Pipeline.Configuration(LoweringRule()));
        var e = await p.AppendShadowCredentialsAsync();
        await p.RulesJob.RunOnceAsync(Ct);
        p.SecurityStore.Add(Modified(T, Admin, "msDS-KeyCredentialLink", null, added: true));
        p.Racing.FailingWrite = failingWrite;

        Assert.True((await p.Engine.RunOnceAsync(Ct)).Failed);
        Assert.Equal(AttributionStatus.Pending, (await p.Store.GetAsync(e.Id, Ct))!.Attribution.Status);
        Assert.Equal((DefaultRules.Tier0Security, Severity.High), (Assert.Single(await p.Store.GetAllAsync(Ct)).RuleId, (await p.Store.GetAllAsync(Ct))[0].Severity));
        if (dispatchInBetween)
        {
            await p.DispatchAsync();
        }

        p.Racing.FailingWrite = null;
        p.Restart();
        for (var cycle = 0; cycle < 3; cycle++)
        {
            p.Time.Advance(TimeSpan.FromSeconds(30));
            Assert.False((await p.Engine.RunOnceAsync(Ct)).Failed);
            await p.RulesJob.RunOnceAsync(Ct);
            await p.DispatchAsync();
            foreach (var message in await p.Store.GetMessagesForEventAsync(e.Id, Ct))
            {
                await p.Store.LeaseDueAsync(p.Time.GetUtcNow(), 10, TimeSpan.FromMinutes(1), Ct);
                await p.Store.MarkSentAsync(message.Id, p.Time.GetUtcNow(), Ct);
            }

            await p.DispatchAsync(TimeSpan.FromMinutes(2));
        }

        var stored = (await p.Store.GetAsync(e.Id, Ct))!;
        Assert.Equal((AttributionStatus.Resolved, Severity.High), (stored.Attribution.Status, stored.Severity));
        Assert.Equal(1, stored.MatchedRules.Count(r => r == DefaultRules.Tier0Security));
        var messages = (await p.Store.GetMessagesForEventAsync(e.Id, Ct)).OrderBy(m => m.Id).ToList();
        Assert.Equal(dispatchInBetween ? [DeliveryKind.Alert, DeliveryKind.FollowUp] : [DeliveryKind.Alert], messages.Select(m => m.Kind));
        Assert.Equal((DefaultRules.Tier0Security, Severity.High, dispatchInBetween), (messages[0].RuleId, messages[0].Severity, messages[0].SentWithPendingAttribution));
        Assert.All(messages, m => Assert.Equal([Mailbox], m.Recipients));
        Assert.Empty(await p.Store.GetAllAsync(Ct));
    }

    // ---- Escalation of an alert that already went out: once per target, in its thread, merged with the actor ------------

    private const string Siem = "siem";

    /// <summary>A custom rule that sends shadow credentials to the helpdesk only (tier0-security goes to it-admins).</summary>
    private static RuleSettings HelpdeskRule(Severity? severity = null, params string[] channels) => new()
    {
        Id = "konta-vip",
        Name = "Konta VIP – poświadczenia",
        Kinds = [WatchEventKind.KeyCredentialChanged],
        Severity = severity,
        Delivery = new RuleDelivery
        {
            Immediate = true,
            RecipientGroupIds = [DefaultRules.HelpdeskGroup],
            ChannelIds = channels,
            Hours = DeliveryHours.Always,
        },
    };

    /// <summary>The configuration with the helpdesk mailbox and a SIEM webhook channel.</summary>
    private static WatchdogConfiguration WithHelpdeskAndSiem(params RuleSettings[] rules)
    {
        var configuration = WithHelpdesk(Pipeline.Configuration(rules));
        return configuration with
        {
            Channels =
            [
                .. configuration.Channels ?? [],
                new ChannelSettings { Id = Siem, Type = ChannelType.Webhook, Name = "SIEM – Kraków", DetailLevel = DetailLevel.Minimal, Webhook = new WebhookChannelOptions() },
            ],
        };
    }

    private static async Task<List<OutboxMessage>> ToAsync(Pipeline p, WatchEvent e, Func<OutboxMessage, bool> target) =>
        [.. (await p.Store.GetMessagesForEventAsync(e.Id, Ct)).Where(target).OrderBy(m => m.Id)];

    private static bool ToHelpdesk(OutboxMessage m) => m.ChannelType == DeliveryChannelType.Email && m.Recipients.SequenceEqual([Helpdesk]);

    private static bool ToSiem(OutboxMessage m) => m.ChannelId == Siem;

    [Fact]
    public async Task AMediumAlertSentWithTheActorPending_IsEscalatedOnce_WhenTheAttributionMakesItHigh()
    {
        // konta-vip told the helpdesk and the SIEM about the shadow credentials as Medium at the end of the grace, with the
        // actor pending (no follow-up below High). The actor is another account: the event is High now. Recipients notified
        // only by that stored rule get exactly one escalation in the thread of the alert, with the actor and the new
        // severity – also over repeated cycles.
        _p.Config.Set(WithHelpdeskAndSiem(HelpdeskRule(channels: Siem)));
        var e = await _p.AppendShadowCredentialsAsync();
        await _p.RulesJob.RunOnceAsync(Ct);
        await _p.DispatchAsync(TimeSpan.FromMinutes(5));
        var alerts = await _p.Store.GetMessagesForEventAsync(e.Id, Ct);
        Assert.Equal(2, alerts.Count);
        Assert.All(alerts, a => Assert.Equal((DeliveryKind.Alert, Severity.Medium, true), (a.Kind, a.Severity, a.SentWithPendingAttribution)));
        await _p.MarkSentAsync(e);

        _p.SecurityStore.Add(Modified(T, Admin, "msDS-KeyCredentialLink", null, added: true));
        await _p.Engine.RunOnceAsync(Ct);
        await _p.DispatchAsync();

        var helpdesk = await ToAsync(_p, e, ToHelpdesk);
        Assert.Equal([DeliveryKind.Alert, DeliveryKind.FollowUp], helpdesk.Select(m => m.Kind));
        var escalation = helpdesk[1];
        Assert.Equal((Severity.High, "konta-vip", false), (escalation.Severity, escalation.RuleId, escalation.SentWithPendingAttribution));
        Assert.Equal([new OutboxEventState(e.Id, Severity.High, false)], escalation.EventStates);
        Assert.Equal(helpdesk[0].MessageId, escalation.Email!.InReplyTo);
        Assert.StartsWith("Re: [AD Watchdog][ŚREDNI]", escalation.Email.Subject, StringComparison.Ordinal);
        Assert.Equal("High", escalation.Email.Headers["X-AdMng-Severity"]);
        Assert.Contains("Uzupełnienie: wykonawca ustalony, priorytet podniesiony", escalation.Email.TextBody, StringComparison.Ordinal);
        Assert.Contains("Średni → Wysoki", escalation.Email.TextBody, StringComparison.Ordinal);
        Assert.Contains("adm.jkowalska", escalation.Email.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ustalanie", escalation.Email.TextBody, StringComparison.Ordinal);

        var siem = await ToAsync(_p, e, ToSiem);
        Assert.Equal([DeliveryKind.Alert, DeliveryKind.FollowUp], siem.Select(m => m.Kind));
        Assert.Equal("ad.severity.escalated", siem[1].Http!.WebhookEventType);
        using (var payload = System.Text.Json.JsonDocument.Parse(siem[1].Http!.Body))
        {
            var data = payload.RootElement.GetProperty("data");
            Assert.Equal(("high", "medium"), (data.GetProperty("severity").GetString(), data.GetProperty("previousSeverity").GetString()));
            Assert.Equal("Resolved", data.GetProperty("actor").GetProperty("status").GetString());
        }

        // tier0-security, matched only now, alerts the it-admins mailbox that had no alert yet.
        var admins = await ToAsync(_p, e, m => m.Recipients.SequenceEqual([Mailbox]));
        Assert.Equal((DeliveryKind.Alert, DefaultRules.Tier0Security, Severity.High), (Assert.Single(admins).Kind, admins[0].RuleId, admins[0].Severity));

        for (var cycle = 0; cycle < 3; cycle++)
        {
            await _p.MarkSentAsync(e);
            _p.Time.Advance(TimeSpan.FromMinutes(1));
            await _p.Engine.RunOnceAsync(Ct);
            await _p.RulesJob.RunOnceAsync(Ct);
            await _p.DispatchAsync(TimeSpan.FromMinutes(6));
        }

        Assert.Equal(5, (await _p.Store.GetMessagesForEventAsync(e.Id, Ct)).Count);
        Assert.Empty(await _p.Store.GetAllAsync(Ct));
    }

    [Fact]
    public async Task AnAlertThatWasHighAlready_GetsTheUsualFollowUp_WithoutAnEscalation()
    {
        // konta-vip raises shadow credentials to High: the helpdesk alert went out High with the actor pending and the
        // follow-up brings the actor. The final High is no news – no escalation, no second message.
        _p.Config.Set(WithHelpdeskAndSiem(HelpdeskRule(Severity.High, Siem)));
        var e = await _p.AppendShadowCredentialsAsync();
        await _p.RulesJob.RunOnceAsync(Ct);
        await _p.DispatchAsync(TimeSpan.FromSeconds(91));
        Assert.All(await _p.Store.GetMessagesForEventAsync(e.Id, Ct), a => Assert.Equal((Severity.High, true), (a.Severity, a.SentWithPendingAttribution)));
        await _p.MarkSentAsync(e);

        _p.SecurityStore.Add(Modified(T, Admin, "msDS-KeyCredentialLink", null, added: true));
        await _p.Engine.RunOnceAsync(Ct);
        await _p.DispatchAsync();

        var helpdesk = await ToAsync(_p, e, ToHelpdesk);
        Assert.Equal([DeliveryKind.Alert, DeliveryKind.FollowUp], helpdesk.Select(m => m.Kind));
        Assert.Equal([new OutboxEventState(e.Id, Severity.High, false)], helpdesk[1].EventStates);
        Assert.Contains("Uzupełnienie: wykonawca ustalony ·", helpdesk[1].Email!.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Priorytet", helpdesk[1].Email!.TextBody, StringComparison.Ordinal);
        var siem = await ToAsync(_p, e, ToSiem);
        Assert.Equal("ad.attribution.updated", siem[1].Http!.WebhookEventType);
        using (var payload = System.Text.Json.JsonDocument.Parse(siem[1].Http!.Body))
        {
            Assert.False(payload.RootElement.GetProperty("data").TryGetProperty("previousSeverity", out _));
        }

        await _p.MarkSentAsync(e);
        await _p.Engine.RunOnceAsync(Ct);
        await _p.DispatchAsync(TimeSpan.FromMinutes(6));
        Assert.Equal(2, (await ToAsync(_p, e, ToHelpdesk)).Count);
    }

    [Fact]
    public async Task AHighAlertRaisedToCritical_GetsOneFollowUp_WithTheActorAndTheNewSeverity()
    {
        // privileged-accounts alerted it-admins about a privileged password change as High with the actor pending – the
        // follow-up will bring the actor. The actor turns out to be an administrator's reset: resety-hasel (Critical) now
        // matches for the same recipients. One follow-up carries both the actor and Critical – no Critical alert next to it.
        _p.Config.Set(Pipeline.Configuration(ResetsRule() with { Severity = Severity.Critical }));
        var e = await _p.AppendAsync(Create(WatchEventKind.PasswordChanged, severity: Severity.High, tags: [EventTags.Privileged]) with
        {
            OriginatingTime = T,
            OriginatingDc = Dc1,
        });
        await _p.RulesJob.RunOnceAsync(Ct);
        await _p.DispatchAsync(TimeSpan.FromSeconds(91));
        var alert = Assert.Single(await _p.Store.GetMessagesForEventAsync(e.Id, Ct));
        Assert.Equal((DefaultRules.PrivilegedAccounts, Severity.High, true), (alert.RuleId, alert.Severity, alert.SentWithPendingAttribution));
        await _p.MarkSentAsync(e);

        _p.SecurityStore.Add(Account(4724, T, Admin));
        await _p.Engine.RunOnceAsync(Ct);
        await _p.DispatchAsync();
        await _p.DispatchAsync(TimeSpan.FromMinutes(6));

        var messages = (await _p.Store.GetMessagesForEventAsync(e.Id, Ct)).OrderBy(m => m.Id).ToList();
        Assert.Equal([DeliveryKind.Alert, DeliveryKind.FollowUp], messages.Select(m => m.Kind));
        var followUp = messages[1];
        Assert.Equal(Severity.Critical, followUp.Severity);
        Assert.Equal(alert.MessageId, followUp.Email!.InReplyTo);
        Assert.Contains("Wysoki → Krytyczny", followUp.Email.TextBody, StringComparison.Ordinal);
        Assert.Contains("adm.jkowalska", followUp.Email.TextBody, StringComparison.Ordinal);
        Assert.Empty(await _p.Store.GetAllAsync(Ct));
    }

    [Fact]
    public async Task ADispatchSendingTheEscalationBeforeTheActorFollowUpIsQueued_SendsNoSecondFollowUp()
    {
        // The dispatch wakes on NotificationsQueued right after the attribution update and sends the Critical escalation
        // (with the actor) before the pass queues the attribution follow-up of the High alert: that follow-up arrives after
        // the escalation was completed – no duplicate in the queue any more – and must not go out as a second message.
        _p.Config.Set(Pipeline.Configuration(ResetsRule() with { Severity = Severity.Critical }));
        var e = await _p.AppendAsync(Create(WatchEventKind.PasswordChanged, severity: Severity.High, tags: [EventTags.Privileged]) with
        {
            OriginatingTime = T,
            OriginatingDc = Dc1,
        });
        await _p.RulesJob.RunOnceAsync(Ct);
        await _p.DispatchAsync(TimeSpan.FromSeconds(91));
        var alert = Assert.Single(await _p.Store.GetMessagesForEventAsync(e.Id, Ct));
        await _p.MarkSentAsync(e);
        OutboxMessage? escalation = null;
        _p.Signals.OnRaise = signal =>
        {
            if (signal == WatchdogSignal.NotificationsQueued && escalation is null)
            {
                _p.DispatchAsync(TimeSpan.Zero).GetAwaiter().GetResult();
                escalation = _p.Store.GetMessagesForEventAsync(e.Id, Ct).GetAwaiter().GetResult().MaxBy(m => m.Id);
            }
        };

        _p.SecurityStore.Add(Account(4724, T, Admin));
        await _p.Engine.RunOnceAsync(Ct);
        Assert.NotNull(escalation);
        Assert.Equal((DeliveryKind.FollowUp, Severity.Critical, alert.MessageId), (escalation.Kind, escalation.Severity, escalation.Email!.InReplyTo));
        Assert.Contains("adm.jkowalska", escalation.Email.TextBody, StringComparison.Ordinal);
        await _p.MarkSentAsync(e);
        await _p.DispatchAsync();
        await _p.DispatchAsync(TimeSpan.FromMinutes(6));

        Assert.Equal([DeliveryKind.Alert, DeliveryKind.FollowUp], (await _p.Store.GetMessagesForEventAsync(e.Id, Ct)).OrderBy(m => m.Id).Select(m => m.Kind));
        Assert.Empty(await _p.Store.GetAllAsync(Ct));
    }

    [Fact]
    public async Task ALoweredSeverity_SendsNothingMore_AndTheFollowUpShowsTheFinalSeverity()
    {
        // privileged-accounts alerted High with the actor pending; the actor is the user changing their own password at
        // expiry (4723) – the event's own severity is Info now. Nothing new is sent for the lower severity; the follow-up
        // that brings the actor shows Wysoki → Info.
        _p.Config.Set(Pipeline.Configuration());
        var e = await _p.AppendAsync(Create(WatchEventKind.PasswordChanged, severity: Severity.High, tags: [EventTags.Privileged]) with
        {
            OriginatingTime = T,
            OriginatingDc = Dc1,
        });
        await _p.RulesJob.RunOnceAsync(Ct);
        await _p.DispatchAsync(TimeSpan.FromSeconds(91));
        await _p.MarkSentAsync(e);

        _p.SecurityStore.Add(Account(4723, T, User));
        await _p.Engine.RunOnceAsync(Ct);
        await _p.DispatchAsync();
        await _p.DispatchAsync(TimeSpan.FromMinutes(6));

        var messages = (await _p.Store.GetMessagesForEventAsync(e.Id, Ct)).OrderBy(m => m.Id).ToList();
        Assert.Equal([DeliveryKind.Alert, DeliveryKind.FollowUp], messages.Select(m => m.Kind));
        Assert.Equal([new OutboxEventState(e.Id, Severity.Info, false)], messages[1].EventStates);
        Assert.Contains("Wysoki → Info", messages[1].Email!.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("priorytet podniesiony", messages[1].Email!.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADispatchSendingTheLoweredFollowUpBeforeTheActorFollowUpIsQueued_SendsNoSecondFollowUp()
    {
        // privileged-accounts alerted it-admins High with the actor pending. The actor is the user at expiry (4723): the own
        // severity is Info, and only hasla-kont (no override) still tells it-admins – Info, in the thread of the alert. The
        // digest-only hasla-raport keeps the event's evaluated severity High, so the attribution follow-up of the alert would
        // say High. The dispatch wakes right after the attribution update and sends the Info follow-up before the pass queues
        // the attribution follow-up: that one is no news for it-admins and must not go out as a second message.
        var passwords = new RuleSettings
        {
            Id = "hasla-kont",
            Name = "Hasła kont uprzywilejowanych",
            Kinds = [WatchEventKind.PasswordChanged],
            AnyTags = [EventTags.Privileged],
            Delivery = new RuleDelivery { Immediate = true, RecipientGroupIds = [DefaultRules.ItAdminsGroup], Hours = DeliveryHours.Always },
        };
        var report = new RuleSettings
        {
            Id = "hasla-raport",
            Name = "Hasła – raport",
            Kinds = [WatchEventKind.PasswordChanged],
            AnyTags = [EventTags.Privileged],
            Severity = Severity.High,
            Delivery = new RuleDelivery { Immediate = false, IncludeInDigest = true },
        };
        _p.Config.Set(Pipeline.Configuration(passwords, report));
        var e = await _p.AppendAsync(Create(WatchEventKind.PasswordChanged, severity: Severity.High, tags: [EventTags.Privileged]) with
        {
            OriginatingTime = T,
            OriginatingDc = Dc1,
        });
        await _p.RulesJob.RunOnceAsync(Ct);
        await _p.DispatchAsync(TimeSpan.FromSeconds(91));
        var alert = Assert.Single(await _p.Store.GetMessagesForEventAsync(e.Id, Ct));
        Assert.Equal((DefaultRules.PrivilegedAccounts, Severity.High, true), (alert.RuleId, alert.Severity, alert.SentWithPendingAttribution));
        await _p.MarkSentAsync(e);
        OutboxMessage? lowered = null;
        _p.Signals.OnRaise = signal =>
        {
            if (signal == WatchdogSignal.NotificationsQueued && lowered is null)
            {
                _p.DispatchAsync(TimeSpan.Zero).GetAwaiter().GetResult();
                lowered = _p.Store.GetMessagesForEventAsync(e.Id, Ct).GetAwaiter().GetResult().MaxBy(m => m.Id);
            }
        };

        _p.SecurityStore.Add(Account(4723, T, User));
        await _p.Engine.RunOnceAsync(Ct);
        Assert.NotNull(lowered);
        Assert.Equal((DeliveryKind.FollowUp, Severity.Info, alert.MessageId), (lowered.Kind, lowered.Severity, lowered.Email!.InReplyTo));
        Assert.Contains("Wysoki → Info", lowered.Email.TextBody, StringComparison.Ordinal);
        Assert.Equal(Severity.High, (await _p.Store.GetAsync(e.Id, Ct))!.Severity);
        await _p.MarkSentAsync(e);
        await _p.DispatchAsync();
        await _p.DispatchAsync(TimeSpan.FromMinutes(6));

        Assert.Equal([DeliveryKind.Alert, DeliveryKind.FollowUp], (await _p.Store.GetMessagesForEventAsync(e.Id, Ct)).OrderBy(m => m.Id).Select(m => m.Kind));
        Assert.Empty(await _p.Store.GetAllAsync(Ct));
    }

    [Fact]
    public async Task AnAggregatedAlert_EscalatesOnlyTheEventThatBecameHigh()
    {
        // konta-vip collected shadow credentials and a description change of another account into one Medium helpdesk alert,
        // both actors pending. Both actors become known in one pass; only the shadow credentials become High: one escalation
        // with that event alone.
        var vip = HelpdeskRule(channels: Siem) with { Kinds = [WatchEventKind.KeyCredentialChanged, WatchEventKind.AttributesChanged] };
        _p.Config.Set(WithHelpdeskAndSiem(vip));
        var annaGuid = Guid.Parse("0b6d5c8e-2f1a-4c3e-9d7b-5a4e3f2d1c0b");
        const string AnnaDn = "CN=Anna Zając,OU=Kraków,DC=contoso,DC=test";
        var description = await _p.AppendAsync(Create(WatchEventKind.AttributesChanged, dn: AnnaDn, severity: Severity.Medium, changedAttributes: ["description"],
            objectGuid: annaGuid, sid: DomainSid + "-7702") with { OriginatingTime = T, OriginatingDc = Dc1 });
        var e = await _p.AppendShadowCredentialsAsync();
        await _p.RulesJob.RunOnceAsync(Ct);
        await _p.DispatchAsync(TimeSpan.FromMinutes(5));
        var aggregated = Assert.Single(await ToAsync(_p, e, ToHelpdesk));
        Assert.Equal([description.Id, e.Id], aggregated.EventIds.Order());
        Assert.Equal(Severity.Medium, aggregated.Severity);
        await _p.MarkSentAsync(e);

        _p.SecurityStore.Add(Modified(T, Admin, "description", "Księgowość – Gdańsk", added: true, objectGuid: annaGuid, dn: AnnaDn));
        _p.SecurityStore.Add(Modified(T, Admin, "msDS-KeyCredentialLink", null, added: true));
        await _p.Engine.RunOnceAsync(Ct);
        await _p.DispatchAsync();
        await _p.DispatchAsync(TimeSpan.FromMinutes(6));

        Assert.Equal(AttributionStatus.Resolved, (await _p.Store.GetAsync(description.Id, Ct))!.Attribution.Status);
        var helpdesk = await ToAsync(_p, e, ToHelpdesk);
        Assert.Equal([DeliveryKind.Alert, DeliveryKind.FollowUp], helpdesk.Select(m => m.Kind));
        Assert.Equal([e.Id], helpdesk[1].EventIds);
        Assert.Equal([new OutboxEventState(e.Id, Severity.High, false)], helpdesk[1].EventStates);
        Assert.Equal(aggregated.MessageId, helpdesk[1].Email!.InReplyTo);
        Assert.Equal([DeliveryKind.Alert], (await ToAsync(_p, description, ToHelpdesk)).Select(m => m.Kind));
        Assert.Equal([DeliveryKind.Alert], (await ToAsync(_p, description, ToSiem)).Select(m => m.Kind));
        Assert.Equal([DeliveryKind.Alert, DeliveryKind.FollowUp], (await ToAsync(_p, e, ToSiem)).Select(m => m.Kind));
    }

    [Fact]
    public async Task AnAttributionArrivingWhileTheDispatchCompletesTheMediumAlert_StillEscalates()
    {
        // The grace ends and the dispatch completes the Medium helpdesk alert at the very moment the actor becomes known.
        // The re-evaluation waits for the dispatch run (one evaluation section) and must see that alert: one escalation.
        using var p = new Pipeline(hookedDispatch: true);
        p.Config.Set(WithHelpdeskAndSiem(HelpdeskRule()));
        var e = await p.AppendShadowCredentialsAsync();
        await p.RulesJob.RunOnceAsync(Ct);
        p.SecurityStore.Add(Modified(T, Admin, "msDS-KeyCredentialLink", null, added: true));
        Task<JobResult>? engine = null;
        p.DispatchQueue.BeforeComplete = async () =>
        {
            engine = Task.Run(() => p.Engine.RunOnceAsync(Ct), Ct);
            await Task.WhenAny(engine, Task.Delay(TimeSpan.FromMilliseconds(500), Ct));
        };

        await p.DispatchAsync(TimeSpan.FromMinutes(5));
        Assert.NotNull(engine);
        Assert.False((await engine).Failed);
        var alert = Assert.Single(await ToAsync(p, e, ToHelpdesk));
        Assert.Equal((Severity.Medium, true), (alert.Severity, alert.SentWithPendingAttribution));
        await p.DispatchAsync();
        Assert.Single(await ToAsync(p, e, ToHelpdesk));       // the escalation waits until its alert was sent
        await p.MarkSentAsync(e);
        await p.DispatchAsync(NotificationDispatchJob.FollowUpWait);

        var helpdesk = await ToAsync(p, e, ToHelpdesk);
        Assert.Equal([DeliveryKind.Alert, DeliveryKind.FollowUp], helpdesk.Select(m => m.Kind));
        Assert.Equal((Severity.High, alert.MessageId), (helpdesk[1].Severity, helpdesk[1].Email!.InReplyTo));
        Assert.Empty(await p.Store.GetAllAsync(Ct));
    }

    [Fact]
    public async Task ADispatchStartingWhileTheReevaluationStoresTheEscalation_SendsItWithTheFinalActor()
    {
        // The dispatch job wakes at the very moment the re-evaluation writes the escalation – before the attribution is
        // stored. It waits for the re-evaluation and the attribution update and renders the actor, not "ustalanie…".
        using var p = new Pipeline(racing: true);
        p.Config.Set(WithHelpdeskAndSiem(HelpdeskRule()));
        var e = await p.AppendShadowCredentialsAsync();
        await p.RulesJob.RunOnceAsync(Ct);
        await p.DispatchAsync(TimeSpan.FromMinutes(5));
        await p.MarkSentAsync(e);
        p.SecurityStore.Add(Modified(T, Admin, "msDS-KeyCredentialLink", null, added: true));
        Task? dispatch = null;
        p.Racing.BeforeWrite = async () =>
        {
            dispatch = Task.Run(() => p.DispatchAsync(TimeSpan.Zero), Ct);
            await Task.WhenAny(dispatch, Task.Delay(TimeSpan.FromMilliseconds(500), Ct));
        };

        await p.Engine.RunOnceAsync(Ct);
        Assert.NotNull(dispatch);
        await dispatch;
        await p.DispatchAsync();

        var helpdesk = await ToAsync(p, e, ToHelpdesk);
        Assert.Equal([DeliveryKind.Alert, DeliveryKind.FollowUp], helpdesk.Select(m => m.Kind));
        Assert.Contains("adm.jkowalska", helpdesk[1].Email!.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ustalanie", helpdesk[1].Email!.TextBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("commit", false)]
    [InlineData("commit", true)]
    [InlineData("update", false)]
    [InlineData("update", true)]
    public async Task ARestartBetweenTheAlertAndTheEscalation_EscalatesOnce_WithTheFinalActor(string? failingWrite, bool dispatchInBetween)
    {
        // The Medium alert went out, then the service restarted – possibly in the middle of the pass that stored the
        // escalation (before the attribution), possibly with a dispatch run in between. Repeated cycles afterwards: one
        // escalation, rendered with the final actor, never one with "ustalanie…" and never a second one.
        using var p = new Pipeline(racing: true);
        p.Config.Set(WithHelpdeskAndSiem(HelpdeskRule(channels: Siem)));
        var e = await p.AppendShadowCredentialsAsync();
        await p.RulesJob.RunOnceAsync(Ct);
        await p.DispatchAsync(TimeSpan.FromMinutes(5));
        await p.MarkSentAsync(e);
        p.SecurityStore.Add(Modified(T, Admin, "msDS-KeyCredentialLink", null, added: true));
        if (failingWrite is not null)
        {
            p.Racing.FailingWrite = failingWrite;
            Assert.True((await p.Engine.RunOnceAsync(Ct)).Failed);
            Assert.Equal(AttributionStatus.Pending, (await p.Store.GetAsync(e.Id, Ct))!.Attribution.Status);
            if (dispatchInBetween)
            {
                await p.DispatchAsync();
                Assert.Empty(await ToAsync(p, e, m => m.Kind == DeliveryKind.FollowUp));
            }

            p.Racing.FailingWrite = null;
        }

        p.Restart();
        for (var cycle = 0; cycle < 3; cycle++)
        {
            p.Time.Advance(TimeSpan.FromSeconds(30));
            Assert.False((await p.Engine.RunOnceAsync(Ct)).Failed);
            await p.RulesJob.RunOnceAsync(Ct);
            await p.DispatchAsync();
            await p.DispatchAsync(TimeSpan.FromMinutes(2));
            await p.MarkSentAsync(e, DeliveryKind.Alert);
        }

        Assert.Equal(AttributionStatus.Resolved, (await p.Store.GetAsync(e.Id, Ct))!.Attribution.Status);
        var helpdesk = await ToAsync(p, e, ToHelpdesk);
        Assert.Equal([DeliveryKind.Alert, DeliveryKind.FollowUp], helpdesk.Select(m => m.Kind));
        Assert.Equal(Severity.High, helpdesk[1].Severity);
        Assert.Contains("adm.jkowalska", helpdesk[1].Email!.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ustalanie", helpdesk[1].Email!.TextBody, StringComparison.Ordinal);
        Assert.Equal([DeliveryKind.Alert, DeliveryKind.FollowUp], (await ToAsync(p, e, ToSiem)).Select(m => m.Kind));
        Assert.Empty(await p.Store.GetAllAsync(Ct));
    }

    [Fact]
    public async Task AStoredRuleSuppressedAsARepeat_DoesNotEscalate_WhenTheAttributionChangesNothing()
    {
        // vip-powtorki (High, repeat suppression 1 h) told the helpdesk about shadow credentials on this user 30 minutes
        // ago. For the second change it is a repeat, so konta-vip alone told the helpdesk – Medium, with the actor pending.
        // The actor is the user itself: the event stays Medium. The repeat-suppressed rule's High is no news from the
        // attribution – nothing more goes out.
        var repeat = HelpdeskRule(Severity.High) with { Id = "vip-powtorki", Name = "VIP – powtórki" };
        repeat = repeat with { Delivery = repeat.Delivery! with { RepeatSuppression = TimeSpan.FromHours(1) } };
        _p.Config.Set(WithHelpdeskAndSiem(repeat, HelpdeskRule()));
        var earlier = await _p.AppendAsync(Create(WatchEventKind.KeyCredentialChanged, severity: Severity.High, attribution: Resolved(AdminSid)));
        await _p.RulesJob.RunOnceAsync(Ct);
        await _p.DispatchAsync();
        Assert.Equal("vip-powtorki", Assert.Single(await ToAsync(_p, earlier, ToHelpdesk)).RuleId);
        await _p.MarkSentAsync(earlier);

        _p.Time.Advance(TimeSpan.FromMinutes(30));
        var detected = _p.Time.GetUtcNow();
        var e = await _p.AppendShadowCredentialsAsync(detected);
        await _p.RulesJob.RunOnceAsync(Ct);
        await _p.DispatchAsync(TimeSpan.FromMinutes(5));
        var alert = Assert.Single(await ToAsync(_p, e, ToHelpdesk));
        Assert.Equal(("konta-vip", Severity.Medium, true), (alert.RuleId, alert.Severity, alert.SentWithPendingAttribution));
        await _p.MarkSentAsync(e);

        _p.SecurityStore.Add(Modified(detected.AddMinutes(-2), User, "msDS-KeyCredentialLink", null, added: true));
        await _p.Engine.RunOnceAsync(Ct);
        await _p.DispatchAsync();
        await _p.DispatchAsync(TimeSpan.FromMinutes(6));

        Assert.Equal(AttributionStatus.Resolved, (await _p.Store.GetAsync(e.Id, Ct))!.Attribution.Status);
        Assert.Equal([DeliveryKind.Alert], (await ToAsync(_p, e, ToHelpdesk)).Select(m => m.Kind));
        Assert.Empty(await _p.Store.GetAllAsync(Ct));
    }

    [Fact]
    public async Task ABusinessHoursRule_DoesNotEscalateOutsideItsHours()
    {
        // konta-vip delivers in business hours only: its Medium alert went out at 10:00, the actor became known at 20:00 and
        // made the event High – no escalation at night (the digest reports the event); the actor alone is no news.
        var vip = HelpdeskRule();
        _p.Config.Set(WithHelpdeskAndSiem(vip with { Delivery = vip.Delivery! with { Hours = DeliveryHours.BusinessHours } }));
        var e = await _p.AppendShadowCredentialsAsync();
        await _p.RulesJob.RunOnceAsync(Ct);
        await _p.DispatchAsync(TimeSpan.FromMinutes(5));
        var alert = Assert.Single(await ToAsync(_p, e, ToHelpdesk));
        Assert.Equal((Severity.Medium, true), (alert.Severity, alert.SentWithPendingAttribution));
        await _p.MarkSentAsync(e);

        _p.Time.Advance(TimeSpan.FromHours(10));
        _p.SecurityStore.Add(Modified(T, Admin, "msDS-KeyCredentialLink", null, added: true));
        await _p.Engine.RunOnceAsync(Ct);
        await _p.DispatchAsync();
        await _p.DispatchAsync(TimeSpan.FromMinutes(6));

        var stored = (await _p.Store.GetAsync(e.Id, Ct))!;
        Assert.Equal((AttributionStatus.Resolved, Severity.High), (stored.Attribution.Status, stored.Severity));
        Assert.Equal([DeliveryKind.Alert], (await ToAsync(_p, e, ToHelpdesk)).Select(m => m.Kind));
        Assert.DoesNotContain(await _p.Store.GetAllAsync(Ct), r => r.EventId == e.Id && r.IsFollowUp);
    }

    [Fact]
    public async Task AnEscalationOfAnInterruptedPass_IsDropped_WhenNoRuleNotifiesTheTargetInTheNextPass()
    {
        // The pass that stored the escalation of the Medium alert failed before the attribution update; before the next pass
        // the operator removed konta-vip. No rule notifies the helpdesk or the SIEM any more: the escalation stored by the
        // failed pass must not go out with the next pass's attribution.
        using var p = new Pipeline(racing: true);
        p.Config.Set(WithHelpdeskAndSiem(HelpdeskRule(channels: Siem)));
        var e = await p.AppendShadowCredentialsAsync();
        await p.RulesJob.RunOnceAsync(Ct);
        await p.DispatchAsync(TimeSpan.FromMinutes(5));
        await p.MarkSentAsync(e);
        p.SecurityStore.Add(Modified(T, Admin, "msDS-KeyCredentialLink", null, added: true));
        p.Racing.FailingWrite = "update";
        Assert.True((await p.Engine.RunOnceAsync(Ct)).Failed);
        Assert.Equal(2, (await p.Store.GetAllAsync(Ct)).Count(r => r.IsFollowUp && r.EventId == e.Id));
        p.Racing.FailingWrite = null;

        p.Config.Set(WithHelpdeskAndSiem());
        p.Restart();
        p.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.False((await p.Engine.RunOnceAsync(Ct)).Failed);
        await p.DispatchAsync();
        await p.DispatchAsync(TimeSpan.FromMinutes(2));

        Assert.Equal(AttributionStatus.Resolved, (await p.Store.GetAsync(e.Id, Ct))!.Attribution.Status);
        Assert.Equal([DeliveryKind.Alert], (await ToAsync(p, e, ToHelpdesk)).Select(m => m.Kind));
        Assert.Equal([DeliveryKind.Alert], (await ToAsync(p, e, ToSiem)).Select(m => m.Kind));
        Assert.DoesNotContain(await p.Store.GetAllAsync(Ct), r => r.IsFollowUp);
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
        /// <param name="hookedDispatch">The dispatch job uses <see cref="DispatchQueue"/> (a hook runs right before it
        /// completes a bucket).</param>
        public Pipeline(bool racing = false, bool hookedDispatch = false)
        {
            Store = new InMemoryWatchdogStore(Time);
            Racing = new RacingStore(Store);
            DispatchQueue = new HookedQueue(Store);
            Config = new FakeConfigProvider(Configuration());
            Tracker = new DcLogHealthTracker(Time);
            Gate = new SourceQueryGate(Tracker, Time);
            _racing = racing;
            _hookedDispatch = hookedDispatch;
            Start();
        }

        private readonly bool _racing;
        private readonly bool _hookedDispatch;

        /// <summary>A service restart: new jobs, rule engine, planner and throttle over the same store, configuration and
        /// Security log (nothing kept in memory survives).</summary>
        public void Restart() => Start();

        private void Start()
        {
            var racing = _racing;
            var hookedDispatch = _hookedDispatch;
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
            Dispatch = new NotificationDispatchJob(hookedDispatch ? DispatchQueue : Store, Store, Store, Config, new RuleEngineHeldMatcher(Rules), Planner, Rules,
                rollout, new ThrottlePolicy(Store), Signals, Time, NullLogger<NotificationDispatchJob>.Instance);
        }

        /// <summary>The dispatch job's queue in a pipeline with a hooked dispatch.</summary>
        public HookedQueue DispatchQueue { get; }

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

        public RuleEngine Rules { get; private set; } = null!;

        public NotificationPlanner Planner { get; private set; } = null!;

        public RuleEvaluationJob RulesJob { get; private set; } = null!;

        public AttributionEngine Engine { get; private set; } = null!;

        public NotificationDispatchJob Dispatch { get; private set; } = null!;

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
        public Task<WatchEvent> AppendShadowCredentialsAsync(DateTimeOffset? detectedAt = null) =>
            AppendAsync(Create(WatchEventKind.KeyCredentialChanged, severity: Severity.Medium, detectedAt: detectedAt ?? Now) with
            {
                OriginatingTime = (detectedAt ?? Now).AddMinutes(-2),
                OriginatingDc = Dc1,
                Changes = [new AttributeChange { Attribute = "msDS-KeyCredentialLink", Redacted = true }],
            });

        public async Task<WatchEvent> AppendAsync(WatchEvent e) => e with { Id = await Store.AppendAsync(e, Ct) };

        /// <summary>Delivers every waiting message of <paramref name="e"/> (only those of <paramref name="kind"/> when given):
        /// leased and marked Sent now – the payload is cleared, as after a real send.</summary>
        public async Task MarkSentAsync(WatchEvent e, DeliveryKind? kind = null)
        {
            var delivered = (await Store.GetDeliveriesForEventAsync(e.Id, Ct)).Where(d => d.Status == OutboxStatus.Sent).Select(d => d.OutboxId).ToHashSet();
            foreach (var message in await Store.GetMessagesForEventAsync(e.Id, Ct))
            {
                if ((kind is null || message.Kind == kind) && !delivered.Contains(message.Id))
                {
                    await Store.LeaseDueAsync(Time.GetUtcNow(), 100, TimeSpan.FromMinutes(1), Ct);
                    await Store.MarkSentAsync(message.Id, Time.GetUtcNow(), Ct);
                }
            }
        }

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
    /// right before the first write that stores pending rows – a dispatch job starting at that instant.
    /// </summary>
    private sealed class RacingStore(InMemoryWatchdogStore inner) : IEventStore, INotificationQueueStore
    {
        public Func<Task>? BeforeWrite { get; set; }

        /// <summary>The write that fails (a store error, the service stopping): "commit" (the evaluation of the
        /// re-evaluation) or "update" (the attribution update); null = none.</summary>
        public string? FailingWrite { get; set; }

        public async Task CommitEvaluationAsync(IReadOnlyList<EventEvaluation> evaluations, IReadOnlyList<PendingNotification> notifications,
            CancellationToken cancellationToken)
        {
            if (notifications.Count > 0)
            {
                await RaceAsync();
            }

            ThrowIfFailing("commit");
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

        public Task UpdateAttributionAsync(long id, AttributionUpdate update, CancellationToken cancellationToken)
        {
            ThrowIfFailing("update");
            return inner.UpdateAttributionAsync(id, update, cancellationToken);
        }

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

        public async Task ReplanAsync(IReadOnlyList<PendingNotification> notifications, bool insertMissing, CancellationToken cancellationToken)
        {
            if (notifications.Count > 0)
            {
                await RaceAsync();
            }

            await inner.ReplanAsync(notifications, insertMissing, cancellationToken);
        }

        private void ThrowIfFailing(string write)
        {
            if (string.Equals(FailingWrite, write, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("SQLite Error 5: database is locked");
            }
        }

        private async Task RaceAsync()
        {
            if (BeforeWrite is { } race)
            {
                BeforeWrite = null;
                await race();
            }
        }
    }

    /// <summary>The dispatch job's view of the queue: runs <see cref="BeforeComplete"/> once, right before it completes a bucket.</summary>
    private sealed class HookedQueue(InMemoryWatchdogStore inner) : INotificationQueueStore
    {
        public Func<Task>? BeforeComplete { get; set; }

        public Task<IReadOnlyList<PendingNotification>> GetAllAsync(CancellationToken cancellationToken) => inner.GetAllAsync(cancellationToken);

        public Task AddAsync(IReadOnlyList<PendingNotification> notifications, CancellationToken cancellationToken) => inner.AddAsync(notifications, cancellationToken);

        public async Task CompleteAsync(IReadOnlyList<long> pendingIds, OutboxMessage? message, CancellationToken cancellationToken)
        {
            if (BeforeComplete is { } hook)
            {
                BeforeComplete = null;
                await hook();
            }

            await inner.CompleteAsync(pendingIds, message, cancellationToken);
        }

        public Task DropAsync(IReadOnlyList<long> pendingIds, CancellationToken cancellationToken) => inner.DropAsync(pendingIds, cancellationToken);

        public Task RescheduleAsync(IReadOnlyList<long> pendingIds, DateTimeOffset dueAt, CancellationToken cancellationToken) =>
            inner.RescheduleAsync(pendingIds, dueAt, cancellationToken);

        public Task ReplanAsync(IReadOnlyList<PendingNotification> notifications, bool insertMissing, CancellationToken cancellationToken) =>
            inner.ReplanAsync(notifications, insertMissing, cancellationToken);
    }

    /// <summary>Records signals and lets a test look at the store at the moment one is raised.</summary>
    private sealed class HookedSignals : IWatchdogSignals
    {
        public Action<WatchdogSignal>? OnRaise { get; set; }

        public void Raise(WatchdogSignal signal) => OnRaise?.Invoke(signal);

        public ISignalSubscription Subscribe(IReadOnlyList<WatchdogSignal> signals) => new FakeSignals().Subscribe(signals);
    }
}
