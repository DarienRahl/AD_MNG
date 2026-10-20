using System.Text.Encodings.Web;
using System.Text.Json;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Configuration;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Watchdog.Core.Tests.Configuration;

public class ConfigChangeAuditorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 12, 3, TimeSpan.Zero);

    // Relaxed encoder: Polish values must be searchable as written (the default encoder escapes non-ASCII).
    // Reflection-based so the audit records (not part of the API context) serialize too.
    private static readonly JsonSerializerOptions Readable = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly ConfigChangeAuditor _auditor = new(new FakeTimeProvider(Now));
    private readonly WatchdogConfiguration _old = SampleConfiguration.Create();

    [Fact]
    public void IdenticalConfigurations_AreMedium_WithoutChanges()
    {
        var audit = _auditor.Compare(_old, SampleConfiguration.Create());

        Assert.Equal(Severity.Medium, audit.Severity);
        Assert.False(audit.HasChanges);
        Assert.Equal("no changes", audit.Summary);
    }

    [Fact]
    public void JsonRoundTrip_ProducesNoChanges()
    {
        var json = JsonSerializer.Serialize(_old, WatchdogJson.Options);
        var copy = JsonSerializer.Deserialize<WatchdogConfiguration>(json, WatchdogJson.Options)!;

        Assert.False(_auditor.Compare(_old, copy).HasChanges);
        Assert.False(_auditor.Compare(new WatchdogConfiguration(), new WatchdogConfiguration()).HasChanges);
    }

    [Fact]
    public void OrdinarySettings_AreMedium()
    {
        var updated = _old with
        {
            General = _old.General with { InstanceName = "Nadzór AD – Gdańsk", RolloutEnded = true },
            Retention = _old.Retention with { EventsDays = 1500 },
            BusinessHours = _old.BusinessHours with { ExtraHolidays = [new DateOnly(2026, 11, 2)] },
        };

        var audit = _auditor.Compare(_old, updated);

        Assert.Equal(Severity.Medium, audit.Severity);
        Assert.Equal(
            ["general.instanceName changed", "general.rolloutEnded changed", "businessHours.extraHolidays +1 -0", "retention.eventsDays changed"],
            audit.Items.Select(i => $"{i.Path} {i.Description}"));
        Assert.DoesNotContain("Gdańsk", audit.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("180", audit.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void SmtpChanges_AreHigh_AndDisablingSmtpIsCritical()
    {
        var host = _auditor.Compare(_old, _old with { Smtp = _old.Smtp with { Host = "smtp2.contoso.test" } });
        Assert.Equal(Severity.High, host.Severity);
        Assert.Equal("smtp.host changed", host.Summary);
        Assert.DoesNotContain("smtp2", host.Summary, StringComparison.Ordinal);

        var off = _auditor.Compare(_old, _old with { Smtp = _old.Smtp with { Enabled = false } });
        Assert.Equal(Severity.Critical, off.Severity);
        Assert.Equal("smtp.enabled changed", off.Summary);

        var on = _auditor.Compare(_old with { Smtp = _old.Smtp with { Enabled = false } }, _old);
        Assert.Equal(Severity.High, on.Severity);
    }

    [Fact]
    public void SecretFields_AreReportedAsSetReplacedOrCleared_NeverWithTheirValue()
    {
        const string NewPassword = "Nowe!Hasło#Żółw2026";
        var replaced = _old with { Smtp = _old.Smtp with { Password = new SecretField { IsSet = true, NewValue = NewPassword } } };
        var cleared = _old with { Smtp = _old.Smtp with { Password = new SecretField { IsSet = true, Clear = true } } };
        var strippedCleared = _old with { Smtp = _old.Smtp with { Password = new SecretField() } };
        var firstSet = _old with { Smtp = _old.Smtp with { Password = new SecretField() } };

        var audit = _auditor.Compare(_old, replaced);
        Assert.Equal("smtp.password replaced", audit.Summary);
        Assert.Equal(ConfigChangeKind.SecretReplaced, Assert.Single(audit.Items).Kind);
        Assert.Equal(Severity.High, audit.Severity);
        Assert.DoesNotContain(NewPassword, JsonSerializer.Serialize(audit, Readable), StringComparison.Ordinal);

        Assert.Equal("smtp.password cleared", _auditor.Compare(_old, cleared).Summary);
        Assert.Equal("smtp.password cleared", _auditor.Compare(_old, strippedCleared).Summary);
        Assert.Equal("smtp.password set", _auditor.Compare(firstSet, replaced).Summary);
        Assert.Equal("smtp.password set", _auditor.Compare(firstSet, _old).Summary);
        Assert.False(_auditor.Compare(_old, _old with { Smtp = _old.Smtp with { Password = new SecretField { IsSet = true, Hint = "inna" } } }).HasChanges);
    }

    [Fact]
    public void ChannelUrlReplaced_IsHigh_AndNamedById()
    {
        var teams = _old.Channels[0];
        var updated = _old with
        {
            Channels = [teams with { Url = teams.Url with { NewValue = "https://evil.example/powerautomate?sig=Tajne" } }, _old.Channels[1]],
        };

        var audit = _auditor.Compare(_old, updated);

        Assert.Equal(Severity.High, audit.Severity);
        Assert.Equal("channels[teams-it].url replaced", audit.Summary);
        Assert.DoesNotContain("evil", JsonSerializer.Serialize(audit, Readable), StringComparison.Ordinal);
    }

    [Fact]
    public void ChannelAdded_IsHigh_ButRemovingOrDisablingAChannelOfTheHealthRuleIsCritical()
    {
        var added = _old with
        {
            Channels = [.. _old.Channels, new ChannelSettings { Id = "teams-helpdesk", Type = ChannelType.Teams, Name = "Teams – Helpdesk" }],
        };
        Assert.Equal(Severity.High, _auditor.Compare(_old, added).Severity);
        Assert.Equal("channels[teams-helpdesk] added", _auditor.Compare(_old, added).Summary);

        var removed = _old with { Channels = [_old.Channels[0]] };
        Assert.Equal(Severity.Critical, _auditor.Compare(_old, removed).Severity);
        Assert.Equal("channels[siem] removed", _auditor.Compare(_old, removed).Summary);

        var disabled = _old with { Channels = [_old.Channels[0], _old.Channels[1] with { Enabled = false }] };
        Assert.Equal(Severity.Critical, _auditor.Compare(_old, disabled).Severity);
    }

    [Fact]
    public void RemovingAChannelNoProtectedRuleUses_IsOnlyHigh()
    {
        var health = _old.Rules[2];
        var baseline = _old with { Rules = [_old.Rules[0], _old.Rules[1], health with { Delivery = health.Delivery with { AllEnabledChannels = false } }] };

        Assert.Equal(Severity.High, _auditor.Compare(baseline, baseline with { Channels = [baseline.Channels[0]] }).Severity);
    }

    [Fact]
    public void RecipientChanges_AreHigh_AndListOnlyCounts()
    {
        var helpdesk = _old.RecipientGroups[1];
        var updated = _old with
        {
            RecipientGroups = [_old.RecipientGroups[0], helpdesk with { Addresses = ["nowy.pracownik.żółć@contoso.test"] }],
        };

        var audit = _auditor.Compare(_old, updated);

        Assert.Equal(Severity.High, audit.Severity);
        Assert.Equal("recipientGroups[helpdesk].addresses +1 -1", audit.Summary);
        var serialized = JsonSerializer.Serialize(audit, Readable);
        Assert.DoesNotContain("@", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("nowy.pracownik", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovingRecipientsOfATier0OrHealthRule_IsCritical()
    {
        var admins = _old.RecipientGroups[0];
        var fewer = _old with { RecipientGroups = [admins with { Addresses = [admins.Addresses[1]] }, _old.RecipientGroups[1]] };
        var more = _old with { RecipientGroups = [admins with { Addresses = [.. admins.Addresses, "nowy.admin@contoso.test"] }, _old.RecipientGroups[1]] };
        var groupRemoved = _old with { RecipientGroups = [_old.RecipientGroups[1]] };

        Assert.Equal(Severity.Critical, _auditor.Compare(_old, fewer).Severity);
        Assert.Equal("recipientGroups[it-admins].addresses +0 -1", _auditor.Compare(_old, fewer).Summary);
        Assert.Equal(Severity.High, _auditor.Compare(_old, more).Severity);
        Assert.Equal(Severity.Critical, _auditor.Compare(_old, groupRemoved).Severity);
    }

    [Fact]
    public void Exclusions_AreHigh_CriticalOnTier0Rules_AndOnlyCounted()
    {
        var exclusion = _old.Rules[1].Exclusions[0] with { Id = "nowy-wyjatek" };
        var onLockouts = WithRule(1, r => r with { Exclusions = [.. r.Exclusions, exclusion] });
        var onTier0 = WithRule(0, r => r with { Exclusions = [exclusion] });
        var changed = WithRule(1, r => r with { Exclusions = [r.Exclusions[0] with { Reason = "Inny powód – Łódź" }] });
        var removedFromTier0 = _old with { Rules = [onTier0.Rules[0] with { Exclusions = [] }, .. onTier0.Rules.Skip(1)] };

        var lockouts = _auditor.Compare(_old, onLockouts);
        Assert.Equal(Severity.High, lockouts.Severity);
        Assert.Equal("rules[lockouts].exclusions +1 -0", lockouts.Summary);

        var tier0 = _auditor.Compare(_old, onTier0);
        Assert.Equal(Severity.Critical, tier0.Severity);
        Assert.Equal("rules[tier0-security].exclusions +1 -0", tier0.Summary);

        Assert.Equal("rules[lockouts].exclusions +0 -0 ~1", _auditor.Compare(_old, changed).Summary);
        Assert.Equal(Severity.High, _auditor.Compare(onTier0, removedFromTier0).Severity);

        var serialized = JsonSerializer.Serialize(new[] { lockouts, tier0, _auditor.Compare(_old, changed) }, Readable);
        Assert.DoesNotContain("nowy-wyjatek", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(SampleConfiguration.ExclusionActorSid, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Serwery", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Łódź", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void ExclusionsWithoutUsableIds_AreNeverUnderReportedOnATier0Rule()
    {
        var exclusion = _old.Rules[1].Exclusions[0] with { Id = string.Empty };
        var before = WithRule(0, r => r with { Exclusions = [exclusion] });
        var after = WithRule(0, r => r with { Exclusions = [exclusion with { SubtreeDn = "DC=contoso,DC=test" }] });

        var audit = _auditor.Compare(before, after);

        Assert.Equal(Severity.Critical, audit.Severity);
        Assert.Equal("rules[tier0-security].exclusions +0 -0 ~1", audit.Summary);
    }

    [Theory]
    [MemberData(nameof(Tier0Weakenings))]
    public void WeakeningATier0Rule_IsCritical(string expectedPath, Func<RuleSettings, RuleSettings> change)
    {
        var audit = _auditor.Compare(_old, WithRule(0, change));

        Assert.Equal(Severity.Critical, audit.Severity);
        Assert.Equal(expectedPath, audit.Items[0].Path);
        Assert.Equal(Severity.Critical, audit.Items[0].Severity);
    }

    public static TheoryData<string, Func<RuleSettings, RuleSettings>> Tier0Weakenings() => new()
    {
        { "rules[tier0-security].enabled", r => r with { Enabled = false } },
        { "rules[tier0-security].kinds", r => r with { Kinds = [WatchEventKind.AdminCountSet] } },
        { "rules[tier0-security].objectKinds", r => r with { ObjectKinds = [WatchObjectKind.User] } },
        { "rules[tier0-security].scope.includeSubtrees", r => r with { Scope = new RuleScope { IncludeSubtrees = ["OU=Kraków,DC=contoso,DC=test"] } } },
        { "rules[tier0-security].scope.excludeSubtrees", r => r with { Scope = new RuleScope { ExcludeSubtrees = [SampleConfiguration.ExclusionSubtree] } } },
        { "rules[tier0-security].attributes", r => r with { Attributes = ["adminCount"] } },
        { "rules[tier0-security].requiredTags", r => r with { RequiredTags = [EventTags.Privileged] } },
        { "rules[tier0-security].anyTags", r => r with { AnyTags = [EventTags.ServiceAccount] } },
        { "rules[tier0-security].groups", r => r with { Groups = GroupFilter.WatchList } },
        { "rules[tier0-security].minSeverity", r => r with { MinSeverity = Severity.Critical } },
        { "rules[tier0-security].severity", r => r with { Severity = Severity.Low } },
        { "rules[tier0-security].actors.excludeActors", r => r with { Actors = r.Actors with { ExcludeActors = [SampleConfiguration.ExclusionActorSid] } } },
        { "rules[tier0-security].delivery.immediate", r => r with { Delivery = r.Delivery with { Immediate = false } } },
        { "rules[tier0-security].delivery.recipientGroupIds", r => r with { Delivery = r.Delivery with { RecipientGroupIds = [] } } },
        { "rules[tier0-security].delivery.includeInDigest", r => r with { Delivery = r.Delivery with { IncludeInDigest = false } } },
        { "rules[tier0-security].delivery.hours", r => r with { Delivery = r.Delivery with { Hours = DeliveryHours.BusinessHours } } },
        { "rules[tier0-security].delivery.aggregationWindow", r => r with { Delivery = r.Delivery with { AggregationWindow = TimeSpan.FromHours(1) } } },
        { "rules[tier0-security].delivery.maxMessagesPerHour", r => r with { Delivery = r.Delivery with { MaxMessagesPerHour = 1 } } },
        { "rules[tier0-security].delivery.repeatSuppression", r => r with { Delivery = r.Delivery with { RepeatSuppression = TimeSpan.FromHours(24) } } },
    };

    [Theory]
    [MemberData(nameof(Tier0Strengthenings))]
    public void StrengtheningOrRenamingATier0Rule_IsNotCritical(Func<RuleSettings, RuleSettings> change, Severity expected)
    {
        Assert.Equal(expected, _auditor.Compare(_old, WithRule(0, change)).Severity);
    }

    public static TheoryData<Func<RuleSettings, RuleSettings>, Severity> Tier0Strengthenings() => new()
    {
        { r => r with { Name = "Atrybuty krytyczne – Łódź" }, Severity.Medium },
        { r => r with { Kinds = [.. r.Kinds, WatchEventKind.DelegationChanged] }, Severity.Medium },
        { r => r with { Kinds = [] }, Severity.Medium },
        { r => r with { MinSeverity = Severity.Medium }, Severity.Medium },
        { r => r with { MinSeverity = null }, Severity.Medium },
        { r => r with { Severity = Severity.Critical }, Severity.Medium },
        { r => r with { Actors = r.Actors with { ExcludeSelfChanges = false } }, Severity.Medium },
        { r => r with { Delivery = r.Delivery with { RecipientGroupIds = [.. r.Delivery.RecipientGroupIds, "helpdesk"] } }, Severity.High },
        { r => r with { Delivery = r.Delivery with { ChannelIds = ["teams-it"] } }, Severity.High },
    };

    [Fact]
    public void TheSameChangeOnAnOrdinaryRule_IsMedium()
    {
        Assert.Equal(Severity.Medium, _auditor.Compare(_old, WithRule(1, r => r with { Enabled = false })).Severity);
        Assert.Equal(Severity.Medium, _auditor.Compare(_old, WithRule(1, r => r with { Delivery = r.Delivery with { Immediate = false } })).Severity);
        Assert.Equal(Severity.High, _auditor.Compare(_old, WithRule(1, r => r with { Delivery = r.Delivery with { RecipientGroupIds = ["it-admins"] } })).Severity);
    }

    [Fact]
    public void HealthRule_DisablingDeliveryToAllChannelsOrUnlocking_IsCritical()
    {
        Assert.Equal(Severity.Critical, _auditor.Compare(_old, WithRule(2, r => r with { Delivery = r.Delivery with { AllEnabledChannels = false } })).Severity);
        Assert.Equal(Severity.Critical, _auditor.Compare(_old, WithRule(2, r => r with { Locked = false })).Severity);
        Assert.Equal(Severity.Critical, _auditor.Compare(_old, WithRule(2, r => r with { Enabled = false })).Severity);
    }

    [Fact]
    public void RemovingATier0Rule_IsCritical_AddingARuleIsMedium()
    {
        var removed = _auditor.Compare(_old, _old with { Rules = [.. _old.Rules.Skip(1)] });
        Assert.Equal(Severity.Critical, removed.Severity);
        Assert.Equal("rules[tier0-security] removed", removed.Summary);

        // "lockouts" sends to the helpdesk group and carries an exclusion: removing it changes recipients (High).
        var removedOrdinary = _auditor.Compare(_old, _old with { Rules = [_old.Rules[0], _old.Rules[2]] });
        Assert.Equal(Severity.High, removedOrdinary.Severity);

        var added = _auditor.Compare(_old, _old with { Rules = [.. _old.Rules, new RuleSettings { Id = "tier0-nowa", Name = "Nowa reguła – Łódź" }] });
        Assert.Equal(Severity.Medium, added.Severity);
        Assert.Equal("rules[tier0-nowa] added", added.Summary);
    }

    [Fact]
    public void AddingOrRemovingARuleWithRecipientsChannelsOrExclusions_IsHigh()
    {
        // 25.6: High for any change of recipients (rule references) and of exclusions – also through a whole rule.
        var exclusion = _old.Rules[1].Exclusions[0] with { Id = "nowy-wyjatek" };
        RuleSettings[] withTargets =
        [
            new() { Id = "kopie-do-helpdesku", Name = "Kopie – Łódź", Delivery = new RuleDelivery { Immediate = true, RecipientGroupIds = ["helpdesk"] } },
            new() { Id = "kopie-do-siem", Name = "SIEM", Delivery = new RuleDelivery { ChannelIds = ["siem"] } },
            new() { Id = "kopie-wszedzie", Name = "Wszędzie", Delivery = new RuleDelivery { AllEnabledChannels = true } },
            new() { Id = "kierownicy", Name = "Kierownicy", Delivery = new RuleDelivery { NotifyManager = true } },
            new() { Id = "z-wyjatkiem", Name = "Z wyjątkiem", Exclusions = [exclusion] },
        ];

        foreach (var rule in withTargets)
        {
            var added = _auditor.Compare(_old, _old with { Rules = [.. _old.Rules, rule] });
            Assert.Equal(Severity.High, added.Severity);
            Assert.Equal($"rules[{rule.Id}] added", added.Summary);

            var withRule = _old with { Rules = [.. _old.Rules, rule] };
            Assert.Equal(Severity.High, _auditor.Compare(withRule, _old).Severity);
        }

        Assert.Equal(Severity.High, _auditor.Compare(_old, WithRule(1, r => r with { Delivery = r.Delivery with { NotifyManager = true } })).Severity);
        Assert.DoesNotContain("nowy-wyjatek", _auditor.Compare(_old, _old with { Rules = [.. _old.Rules, withTargets[^1]] }).Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void NullLists_CountAsEmpty_SoAWeakeningIsNeverHidden()
    {
        // A stored document can carry "exclusions": null (System.Text.Json keeps an explicit null); null means "none".
        var exclusion = _old.Rules[1].Exclusions[0] with { Id = "nowy-wyjatek" };
        var nullExclusions = WithRule(0, r => r with { Exclusions = null! });
        var tier0Exclusion = _auditor.Compare(nullExclusions, WithRule(0, r => r with { Exclusions = [exclusion] }));
        Assert.Equal(Severity.Critical, tier0Exclusion.Severity);
        Assert.Equal("rules[tier0-security].exclusions +1 -0", tier0Exclusion.Summary);

        var nullActors = WithRule(0, r => r with { Actors = r.Actors with { ExcludeActors = null! } });
        var actorExcluded = _auditor.Compare(nullActors, WithRule(0, r => r with { Actors = r.Actors with { ExcludeActors = [SampleConfiguration.ExclusionActorSid] } }));
        Assert.Equal(Severity.Critical, actorExcluded.Severity);
        Assert.Equal("rules[tier0-security].actors.excludeActors +1 -0", actorExcluded.Summary);

        var admins = _old.RecipientGroups[0];
        var emptied = _auditor.Compare(_old, _old with { RecipientGroups = [admins with { Addresses = null! }, _old.RecipientGroups[1]] });
        Assert.Equal(Severity.Critical, emptied.Severity);
        Assert.Equal("recipientGroups[it-admins].addresses +0 -2", emptied.Summary);
        Assert.DoesNotContain("@", emptied.Summary, StringComparison.Ordinal);

        var nullIgnored = _old with { Directory = _old.Directory with { IgnoredAttributes = null! } };
        Assert.False(_auditor.Compare(_old, nullIgnored).HasChanges);
        Assert.Equal(Severity.Critical, _auditor.Compare(nullIgnored, _old with { Directory = _old.Directory with { IgnoredAttributes = ["adminCount"] } }).Severity);
    }

    [Fact]
    public void SwitchingOffATier0DetectionSource_IsCritical()
    {
        // Attribution reads the DC Security logs: without it SecurityLogCleared / AuditPolicyChanged / DsrmPasswordChanged
        // (AuditLog, Tier-0) are never detected – the same effect as putting them in disabledKinds.
        var attributionOff = _auditor.Compare(_old, _old with { Attribution = _old.Attribution with { Enabled = false } });
        Assert.Equal(Severity.Critical, attributionOff.Severity);
        Assert.Equal("attribution.enabled changed", attributionOff.Summary);
        Assert.Equal(Severity.High, _auditor.Compare(_old with { Attribution = _old.Attribution with { Enabled = false } }, _old).Severity);
        var noSource = _old with { Attribution = _old.Attribution with { Source = AttributionSourceKind.None } };
        Assert.Equal(Severity.Critical, _auditor.Compare(_old, noSource).Severity);
        Assert.Equal(Severity.High, _auditor.Compare(noSource, _old).Severity);
        Assert.Equal(Severity.High, _auditor.Compare(_old, _old with { Attribution = _old.Attribution with { Source = AttributionSourceKind.ForwardedEvents } }).Severity);

        // The deleted-objects pass finds deleted trusts and PSOs (Domain, Tier-0) and feeds MassDeletion (Security).
        Assert.Equal(Severity.Critical, _auditor.Compare(_old, _old with { Directory = _old.Directory with { TrackDeletedObjects = false } }).Severity);
        Assert.Equal(Severity.Medium, _auditor.Compare(_old with { Directory = _old.Directory with { TrackDeletedObjects = false } }, _old).Severity);

        var sprayOn = _old with { Scans = _old.Scans with { PasswordSpray = _old.Scans.PasswordSpray with { Enabled = true } } };
        Assert.Equal(Severity.Critical, _auditor.Compare(sprayOn, _old).Severity);
        Assert.Equal(Severity.Medium, _auditor.Compare(_old, sprayOn).Severity);
    }

    [Theory]
    [MemberData(nameof(BurstDetectorWeakenings))]
    public void MakingABurstDetectorLessSensitive_IsCritical(string expectedPath, Func<ScanSettings, ScanSettings> weaken, Func<ScanSettings, ScanSettings> strengthen)
    {
        var weaker = _auditor.Compare(_old, _old with { Scans = weaken(_old.Scans) });
        Assert.Equal(Severity.Critical, weaker.Severity);
        Assert.Equal(expectedPath, weaker.Items[0].Path);

        Assert.Equal(Severity.Medium, _auditor.Compare(_old, _old with { Scans = strengthen(_old.Scans) }).Severity);
    }

    public static TheoryData<string, Func<ScanSettings, ScanSettings>, Func<ScanSettings, ScanSettings>> BurstDetectorWeakenings() => new()
    {
        { "scans.massDeletion.threshold", s => s with { MassDeletion = s.MassDeletion with { Threshold = 1000 } }, s => s with { MassDeletion = s.MassDeletion with { Threshold = 5 } } },
        { "scans.massDeletion.window", s => s with { MassDeletion = s.MassDeletion with { Window = TimeSpan.FromMinutes(1) } }, s => s with { MassDeletion = s.MassDeletion with { Window = TimeSpan.FromMinutes(30) } } },
        { "scans.massComputerDeletion.threshold", s => s with { MassComputerDeletion = s.MassComputerDeletion with { Threshold = 500 } }, s => s with { MassComputerDeletion = s.MassComputerDeletion with { Threshold = 20 } } },
        { "scans.massLockout.percentOfEnabledUsers", s => s with { MassLockout = s.MassLockout with { PercentOfEnabledUsers = 50 } }, s => s with { MassLockout = s.MassLockout with { PercentOfEnabledUsers = 0.1 } } },
        { "scans.passwordSpray.distinctAccounts", s => s with { PasswordSpray = s.PasswordSpray with { DistinctAccounts = 500 } }, s => s with { PasswordSpray = s.PasswordSpray with { DistinctAccounts = 10 } } },
        { "scans.passwordSpray.window", s => s with { PasswordSpray = s.PasswordSpray with { Window = TimeSpan.FromMinutes(1) } }, s => s with { PasswordSpray = s.PasswordSpray with { Window = TimeSpan.FromMinutes(30) } } },
    };

    [Theory]
    [MemberData(nameof(GlobalDeliveryWeakenings))]
    public void DelayingOrThrottlingImmediateDeliveryGlobally_IsCritical(string expectedPath, Func<NotificationSettings, NotificationSettings> weaken, Func<NotificationSettings, NotificationSettings> strengthen)
    {
        // The global knobs apply to tier0-*/health rules without their own value – the same weakening as the per-rule ones.
        var weaker = _auditor.Compare(_old, _old with { Notifications = weaken(_old.Notifications) });
        Assert.Equal(Severity.Critical, weaker.Severity);
        Assert.Equal(expectedPath, weaker.Items[0].Path);

        Assert.Equal(Severity.Medium, _auditor.Compare(_old, _old with { Notifications = strengthen(_old.Notifications) }).Severity);
    }

    public static TheoryData<string, Func<NotificationSettings, NotificationSettings>, Func<NotificationSettings, NotificationSettings>> GlobalDeliveryWeakenings() => new()
    {
        { "notifications.highSeverityFlushDelay", n => n with { HighSeverityFlushDelay = TimeSpan.FromDays(1) }, n => n with { HighSeverityFlushDelay = TimeSpan.Zero } },
        { "notifications.aggregationWindow", n => n with { AggregationWindow = TimeSpan.FromMinutes(60) }, n => n with { AggregationWindow = TimeSpan.FromMinutes(1) } },
        { "notifications.maxMessagesPerRulePerHour", n => n with { MaxMessagesPerRulePerHour = 1 }, n => n with { MaxMessagesPerRulePerHour = 100 } },
    };

    [Theory]
    [InlineData("ignoredAttributes")]
    [InlineData("disabledKinds")]
    [InlineData("excludedSubtrees")]
    public void AddingToDetectionSuppressionLists_IsCritical_RemovingIsMedium(string list)
    {
        var suppressed = _old with { Directory = Suppress(_old.Directory, list) };

        var added = _auditor.Compare(_old, suppressed);
        Assert.Equal(Severity.Critical, added.Severity);
        Assert.Equal($"directory.{list} +1 -0", added.Summary);
        Assert.Equal(Severity.Medium, _auditor.Compare(suppressed, _old).Severity);
    }

    [Fact]
    public void WeakeningPrivilegedGroupsCriticalObjectsOrPartitions_IsCritical()
    {
        var groups = _old.PrivilegedGroups;
        Assert.Equal(Severity.Critical, _auditor.Compare(_old, _old with { PrivilegedGroups = groups with { CriticalSids = groups.CriticalSids.Skip(1).ToArray() } }).Severity);
        Assert.Equal(Severity.Critical, _auditor.Compare(_old, _old with { PrivilegedGroups = groups with { HighSamAccountNames = [] } }).Severity);
        Assert.Equal(Severity.Medium, _auditor.Compare(_old, _old with { PrivilegedGroups = groups with { AdditionalHighDns = ["CN=Operatorzy,OU=Grupy,DC=contoso,DC=test"] } }).Severity);
        Assert.Equal(Severity.Medium, _auditor.Compare(
            _old with { PrivilegedGroups = groups with { WatchListDns = ["CN=Księgowość,OU=Grupy,DC=contoso,DC=test"] } }, _old).Severity);

        var directory = _old.Directory;
        Assert.Equal(Severity.Critical, _auditor.Compare(_old, _old with { Directory = directory with { CriticalObjects = directory.CriticalObjects.Take(1).ToArray() } }).Severity);
        Assert.Equal(Severity.Critical, _auditor.Compare(_old, _old with { Directory = directory with { WatchConfigurationPartition = false } }).Severity);
        Assert.Equal(Severity.Critical, _auditor.Compare(_old, _old with { Directory = directory with { WatchSchemaPartition = false } }).Severity);
        Assert.Equal(Severity.Medium, _auditor.Compare(_old, _old with { Directory = directory with { PollInterval = TimeSpan.FromSeconds(30) } }).Severity);
    }

    [Fact]
    public void CaseOrOrderOnlyChangesOfLists_AreReportedButNotEscalated()
    {
        var before = _old with { Directory = _old.Directory with { ExcludedSubtrees = ["OU=Testy,OU=Łódź,DC=contoso,DC=test", "OU=Laby,DC=contoso,DC=test"] } };
        var after = _old with { Directory = _old.Directory with { ExcludedSubtrees = ["OU=Laby,DC=contoso,DC=test", "ou=testy,ou=łódź,dc=contoso,dc=test"] } };

        var audit = _auditor.Compare(before, after);

        Assert.Equal(Severity.Medium, audit.Severity);
        Assert.Equal("directory.excludedSubtrees reordered", audit.Summary);
    }

    [Fact]
    public void AttributionAndDigestRecipients_AreHigh()
    {
        // (attribution.enabled switched off is Critical – SwitchingOffATier0DetectionSource_IsCritical)
        Assert.Equal(Severity.High, _auditor.Compare(_old, _old with { Attribution = _old.Attribution with { PollInterval = TimeSpan.FromMinutes(2) } }).Severity);
        Assert.Equal(Severity.High, _auditor.Compare(_old, _old with { Attribution = _old.Attribution with { DomainControllers = ["dc01.contoso.test"] } }).Severity);
        Assert.Equal(Severity.High, _auditor.Compare(_old, _old with { Digest = _old.Digest with { RecipientGroupIds = ["helpdesk"] } }).Severity);
        Assert.Equal(Severity.High, _auditor.Compare(_old, _old with { Digest = _old.Digest with { Enabled = false } }).Severity);
        Assert.Equal(Severity.Medium, _auditor.Compare(_old, _old with { Digest = _old.Digest with { Time = new TimeOnly(6, 45) } }).Severity);
    }

    [Fact]
    public void Items_AreOrderedMostSevereFirst_AndTheSummaryIsCapped()
    {
        var many = _old with
        {
            General = _old.General with { InstanceName = "Inny" },
            Smtp = _old.Smtp with { Port = 465 },
            Directory = _old.Directory with { DisabledKinds = [WatchEventKind.GroupCreated] },
        };
        var audit = _auditor.Compare(_old, many);
        Assert.Equal(["directory.disabledKinds", "smtp.port", "general.instanceName"], audit.Items.Select(i => i.Path));
        Assert.Equal([Severity.Critical, Severity.High, Severity.Medium], audit.Items.Select(i => i.Severity));

        var rules = Enumerable.Range(1, ConfigChangeAuditor.MaxSummaryItems + 5)
            .Select(i => new RuleSettings { Id = $"reguła-{i}", Name = $"Reguła {i}" }).ToArray();
        var big = _auditor.Compare(_old, _old with { Rules = [.. _old.Rules, .. rules] });
        Assert.Equal(ConfigChangeAuditor.MaxSummaryItems + 5, big.Items.Count);
        Assert.EndsWith("; (+5 more)", big.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateEvent_BuildsAResolvedHealthEvent_WithPathsOnly()
    {
        var updated = _old with
        {
            Smtp = _old.Smtp with { Host = "smtp.atakujący.example", Password = new SecretField { IsSet = true, NewValue = "Tajne!Hasło" } },
            Rules = [_old.Rules[0] with { Enabled = false }, .. _old.Rules.Skip(1)],
        };
        var audit = _auditor.Compare(_old, updated);
        var by = new PrincipalRef { Sid = "S-1-5-21-1004336348-1177238915-682003330-1104", Account = "CONTOSO\\adm.jkowalska", DisplayName = "Jadwiga Kowalska" };

        var watchEvent = _auditor.CreateEvent(audit, by, "contoso.test", "10.0.12.34");

        Assert.Equal(WatchEventKind.WatchdogConfigurationChanged, watchEvent.Kind);
        Assert.Equal(Severity.Critical, watchEvent.Severity);
        Assert.Equal(DetectionSource.Health, watchEvent.Source);
        Assert.Equal(WatchObjectKind.Watchdog, watchEvent.Object.Kind);
        Assert.Equal(string.Empty, watchEvent.Object.Dn);
        Assert.Equal(0, watchEvent.Id);
        Assert.Equal(Now, watchEvent.DetectedAt);
        Assert.Equal(7, watchEvent.EventId.Version);
        Assert.Equal(watchEvent.EventId, watchEvent.ChangeSetId);
        Assert.Equal("contoso.test", watchEvent.Domain);
        Assert.Equal(AttributionStatus.Resolved, watchEvent.Attribution.Status);
        Assert.Equal(AttributionConfidence.High, watchEvent.Attribution.Confidence);
        Assert.Same(by, watchEvent.Attribution.Actor);
        Assert.Equal("10.0.12.34", watchEvent.Attribution.IpAddress);
        Assert.Equal("local", watchEvent.Attribution.SourceMethod);
        Assert.Equal(Now, watchEvent.Attribution.ResolvedAt);
        Assert.Equal(
            ["rules[tier0-security].enabled|changed", "smtp.host|changed", "smtp.password|replaced"],
            watchEvent.Changes.Select(c => $"{c.Attribute}|{string.Join(',', c.NewValues)}"));
        Assert.All(watchEvent.Changes, c => Assert.Empty(c.OldValues));

        var json = JsonSerializer.Serialize(watchEvent, Readable);
        Assert.DoesNotContain("Tajne", json, StringComparison.Ordinal);
        Assert.DoesNotContain("atakujący", json, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateEvent_WithoutASourceAddress_HasNoSource()
    {
        var watchEvent = _auditor.CreateEvent(_auditor.Compare(_old, _old), new PrincipalRef { Account = "CONTOSO\\adm.jkowalska" });

        Assert.Equal(Severity.Medium, watchEvent.Severity);
        Assert.Empty(watchEvent.Changes);
        Assert.Null(watchEvent.Attribution.IpAddress);
        Assert.Null(watchEvent.Attribution.SourceMethod);
        Assert.Equal(AttributionConfidence.None, watchEvent.Attribution.SourceConfidence);
    }

    private WatchdogConfiguration WithRule(int index, Func<RuleSettings, RuleSettings> change) =>
        _old with { Rules = [.. _old.Rules.Select((r, i) => i == index ? change(r) : r)] };

    private static DirectorySettings Suppress(DirectorySettings directory, string list) => list switch
    {
        "ignoredAttributes" => directory with { IgnoredAttributes = ["department"] },
        "disabledKinds" => directory with { DisabledKinds = [WatchEventKind.SidHistoryChanged] },
        _ => directory with { ExcludedSubtrees = ["OU=Testy,OU=Łódź,DC=contoso,DC=test"] },
    };
}
