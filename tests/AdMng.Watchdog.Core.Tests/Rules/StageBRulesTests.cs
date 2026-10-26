using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Configuration;
using AdMng.Watchdog.Core.Notifications;
using AdMng.Watchdog.Core.Rules;
using Microsoft.Extensions.Time.Testing;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Watchdog.Core.Tests.Rules;

/// <summary>
/// The built-in rules of stage B (docs/dev/watchdog-linux-design.md 9.1–9.3, 8.6, 14.2): their shape, matching, rollout
/// mode, the <c>tier0Suppression</c> protection, the retention floor and the configuration audit.
/// </summary>
public class StageBRulesTests
{
    private static readonly string[] StageBRules =
        [DefaultRules.Tier0BadSuccessor, DefaultRules.Tier0MachineQuota, DefaultRules.WatchdogSelf, DefaultRules.Tier0Gpo];

    private static readonly ConfigValidationContext Production = new();
    private static readonly ConfigValidationContext Allowed = new() { AllowTier0Suppression = true };

    // ------------------------------------------------------------------ shape

    [Fact]
    public void StageBRules_AreShippedLast_ImmediateAlwaysToItAdmins_WithoutAggregation()
    {
        var rules = DefaultRules.Create();

        Assert.Equal(StageBRules, rules.TakeLast(4).Select(r => r.Id));
        Assert.All(rules.TakeLast(4), r =>
        {
            Assert.True(r.BuiltIn);
            Assert.True(r.Enabled);
            Assert.False(r.Locked);
            Assert.True(r.Delivery.Immediate);
            Assert.True(r.Delivery.IncludeInDigest);
            Assert.Equal(DeliveryHours.Always, r.Delivery.Hours);
            Assert.Equal([DefaultRules.ItAdminsGroup], r.Delivery.RecipientGroupIds);
            Assert.Equal(TimeSpan.Zero, r.Delivery.AggregationWindow);
            Assert.Empty(r.Exclusions);
            Assert.True(Tier0Catalog.IsTier0Rule(r.Id));
            Assert.True(Tier0Catalog.IsSuppressionProtectedRule(r.Id));
        });
    }

    [Fact]
    public void StageBRules_FollowSection9()
    {
        var rules = DefaultRules.Create().ToDictionary(r => r.Id);

        var badSuccessor = rules[DefaultRules.Tier0BadSuccessor];
        Assert.Equal([WatchEventKind.ServiceAccountCreated, WatchEventKind.ManagedAccountLinkChanged], badSuccessor.Kinds);
        Assert.Equal([EventTags.BadSuccessor], badSuccessor.RequiredTags);
        Assert.Equal(Severity.Critical, badSuccessor.MinSeverity);

        var quota = rules[DefaultRules.Tier0MachineQuota];
        Assert.Equal([WatchEventKind.DomainPolicyChanged], quota.Kinds);
        Assert.Equal([EventTags.MachineAccountQuota], quota.RequiredTags);
        Assert.Equal(Severity.High, quota.MinSeverity);

        var self = rules[DefaultRules.WatchdogSelf];
        Assert.Empty(self.Kinds);
        Assert.Equal([EventTags.WatchdogAccount], self.RequiredTags);
        Assert.Equal(Severity.Info, self.MinSeverity);
        Assert.Equal(Severity.Critical, self.Severity);
        Assert.False(self.Actors.ExcludeSelfChanges);

        var gpo = rules[DefaultRules.Tier0Gpo];
        Assert.Equal(DefaultRules.GpoContentKinds, gpo.Kinds);
        Assert.Equal(Severity.High, gpo.MinSeverity);
    }

    [Fact]
    public void MergeMissing_AppendsTheStageBRulesToAStageADocument()
    {
        var stageA = DefaultRules.Create().Where(r => !StageBRules.Contains(r.Id)).ToList();

        var merged = DefaultRules.MergeMissing(stageA);

        Assert.Equal(StageBRules, merged.TakeLast(4).Select(r => r.Id));
        var configuration = new DefaultConfigurationFactory().Create() with { Rules = merged };
        Assert.DoesNotContain(Validate(configuration).Issues, i => i.Code == ConfigValidationCodes.UnknownReference);
    }

    // ------------------------------------------------------------------ matching

    [Theory]
    [InlineData(WatchEventKind.ServiceAccountCreated, Severity.Critical, true, true)]
    [InlineData(WatchEventKind.ManagedAccountLinkChanged, Severity.Critical, true, true)]
    [InlineData(WatchEventKind.ServiceAccountCreated, Severity.Medium, false, false)]      // a gMSA
    [InlineData(WatchEventKind.ServiceAccountCreated, Severity.High, true, false)]         // below MinSeverity Critical
    public void Tier0BadSuccessor_NeedsTheTagAndCritical(WatchEventKind kind, Severity severity, bool tagged, bool expected)
    {
        var e = TestEvents.Create(kind, TestEvents.ComputerDn, WatchObjectKind.ServiceAccount, severity, tags: tagged ? [EventTags.BadSuccessor] : []);

        Assert.Equal(expected, Matches(DefaultRules.Tier0BadSuccessor, e));
    }

    [Theory]
    [InlineData(Severity.Critical, true, true)]
    [InlineData(Severity.High, true, true)]
    [InlineData(Severity.High, false, false)]       // another domain policy attribute
    public void Tier0MachineQuota_NeedsTheTag(Severity severity, bool tagged, bool expected)
    {
        var e = TestEvents.Create(WatchEventKind.DomainPolicyChanged, TestEvents.DomainDn, WatchObjectKind.Domain, severity,
            tags: tagged ? [EventTags.MachineAccountQuota] : [], changedAttributes: ["ms-DS-MachineAccountQuota"]);

        Assert.Equal(expected, Matches(DefaultRules.Tier0MachineQuota, e));
    }

    [Theory]
    [InlineData(WatchEventKind.GroupPolicySettingsChanged, Severity.High, true)]
    [InlineData(WatchEventKind.GroupPolicySettingsChanged, Severity.Low, false)]      // unlinked GPO
    [InlineData(WatchEventKind.GroupPolicyPathRedirected, Severity.Critical, true)]
    [InlineData(WatchEventKind.GroupPolicyContentOutOfBand, Severity.High, true)]
    [InlineData(WatchEventKind.GroupPolicyChanged, Severity.High, false)]
    public void Tier0Gpo_MatchesTheContentKindsFromHigh(WatchEventKind kind, Severity severity, bool expected)
    {
        var e = TestEvents.Create(kind, "CN={31B2F340-016D-11D2-945F-00C04FB984F9},CN=Policies,CN=System,DC=contoso,DC=test", WatchObjectKind.GroupPolicy, severity);

        Assert.Equal(expected, Matches(DefaultRules.Tier0Gpo, e));
    }

    [Theory]
    [InlineData(WatchEventKind.AttributesChanged, Severity.Info)]
    [InlineData(WatchEventKind.PasswordChanged, Severity.Info)]
    [InlineData(WatchEventKind.ObjectMoved, Severity.Low)]
    [InlineData(WatchEventKind.PrivilegeGranted, Severity.Critical)]
    public void WatchdogSelf_MatchesEveryKindOfTheAccount(WatchEventKind kind, Severity severity)
    {
        var tagged = TestEvents.Create(kind, TestEvents.ComputerDn, WatchObjectKind.Computer, severity, tags: [EventTags.WatchdogAccount]);
        var other = TestEvents.Create(kind, TestEvents.ComputerDn, WatchObjectKind.Computer, severity);

        Assert.True(Matches(DefaultRules.WatchdogSelf, tagged));
        Assert.False(Matches(DefaultRules.WatchdogSelf, other));
    }

    [Fact]
    public void WatchdogSelf_IsNotHiddenBySuppressComputerSelfChanges()
    {
        var configuration = Defaults() with { Attribution = new AttributionSettings { SuppressComputerSelfChanges = true } };
        var selfWrite = new EventAttribution
        {
            Status = AttributionStatus.Resolved,
            Confidence = AttributionConfidence.High,
            Actor = new PrincipalRef { Sid = TestEvents.DomainSid + "-6609" },
        };
        var own = TestEvents.Create(WatchEventKind.AttributesChanged, TestEvents.ComputerDn, WatchObjectKind.Computer, Severity.Info, selfWrite,
            tags: [EventTags.WatchdogAccount, EventTags.SelfChange], changedAttributes: ["msDS-SupportedEncryptionTypes"]);
        var ordinary = own with { Tags = [EventTags.SelfChange] };

        Assert.False(RuleMatcher.IsComputerNoiseChange(own));
        Assert.True(RuleMatcher.IsComputerNoiseChange(ordinary));
        var context = RuleMatchContext.Create(configuration, TestEvents.Tokens);
        var rule = configuration.Rules.Single(r => r.Id == DefaultRules.WatchdogSelf);
        Assert.Equal(RuleMatchOutcome.Match, RuleMatcher.Match(rule, own, context, TestEvents.Now));
        var accountAttributes = configuration.Rules.Single(r => r.Id == DefaultRules.AccountAttributes);
        Assert.Equal(RuleMatchOutcome.NoMatch, RuleMatcher.Match(accountAttributes, ordinary, context, TestEvents.Now));
    }

    // ------------------------------------------------------------------ rollout mode

    [Theory]
    [InlineData(DefaultRules.Tier0BadSuccessor)]
    [InlineData(DefaultRules.Tier0MachineQuota)]
    [InlineData(DefaultRules.WatchdogSelf)]
    [InlineData(DefaultRules.Tier0Gpo)]
    [InlineData("WATCHDOG-SELF")]
    public void StageBRules_NotifyImmediatelyInRolloutMode(string ruleId)
    {
        Assert.True(RolloutMode.AllowsImmediate(ruleId));
        Assert.True(RolloutGate.AllowsImmediate(ruleId));
    }

    [Fact]
    public void Tier0Catalog_StageB()
    {
        Assert.True(Tier0Catalog.IsTier0Rule("watchdog-self"));
        Assert.False(Tier0Catalog.IsTier0Rule("watchdog-selfie"));
        Assert.False(Tier0Catalog.IsSuppressionProtectedRule(DefaultRules.Tier0Security));
        Assert.False(Tier0Catalog.IsSuppressionProtectedRule(null));
        Assert.True(Tier0Catalog.IsTier0Kind(WatchEventKind.ServiceAccountCreated));
        Assert.True(Tier0Catalog.IsTier0Kind(WatchEventKind.GroupPolicySettingsChanged));
        Assert.True(Tier0Catalog.IsTier0Attribute("msDS-SupportedEncryptionTypes"));
        Assert.True(Tier0Catalog.IsTier0Attribute("ms-DS-MachineAccountQuota"));
        Assert.True(Tier0Catalog.IsTier0Attribute("msDS-DelegatedMSAState"));
    }

    // ------------------------------------------------------------------ tier0Suppression (B6 acceptance)

    [Theory]
    [InlineData(DefaultRules.Tier0BadSuccessor)]
    [InlineData(DefaultRules.Tier0MachineQuota)]
    [InlineData(DefaultRules.WatchdogSelf)]
    [InlineData(DefaultRules.Tier0Gpo)]
    public void DisablingAStageBRule_NeedsAllowTier0Suppression(string id)
    {
        var (configuration, index) = WithRule(Defaults(), id, r => r with { Enabled = false });

        var issue = AssertIssue(Validate(configuration), ConfigValidationCodes.Tier0Suppression, $"rules[{index}].enabled");
        Assert.Equal([id], issue.Args);
        Assert.DoesNotContain(Validate(configuration, Allowed).Issues, i => i.Code == ConfigValidationCodes.Tier0Suppression);
    }

    [Fact]
    public void DisablingAStageARule_StaysAllowed()
    {
        var (configuration, _) = WithRule(Defaults(), DefaultRules.Tier0Security, r => r with { Enabled = false });

        Assert.DoesNotContain(Validate(configuration).Issues, i => i.Code == ConfigValidationCodes.Tier0Suppression);
    }

    [Theory]
    [MemberData(nameof(Weakenings))]
    public void WeakeningAStageBRule_NeedsAllowTier0Suppression(string id, string property, Func<RuleSettings, RuleSettings> change)
    {
        var (configuration, index) = WithRule(Defaults(), id, change);

        AssertIssue(Validate(configuration), ConfigValidationCodes.Tier0Suppression, $"rules[{index}].{property}");
        Assert.DoesNotContain(Validate(configuration, Allowed).Issues, i => i.Code == ConfigValidationCodes.Tier0Suppression);
    }

    public static TheoryData<string, string, Func<RuleSettings, RuleSettings>> Weakenings() => new()
    {
        { DefaultRules.Tier0BadSuccessor, "kinds", r => r with { Kinds = [WatchEventKind.ServiceAccountCreated] } },
        { DefaultRules.WatchdogSelf, "kinds", r => r with { Kinds = [WatchEventKind.PasswordChanged] } },
        { DefaultRules.Tier0Gpo, "objectKinds", r => r with { ObjectKinds = [WatchObjectKind.GroupPolicy] } },
        { DefaultRules.Tier0Gpo, "scope", r => r with { Scope = new RuleScope { ExcludeSubtrees = ["OU=Kraków,{domain}"] } } },
        { DefaultRules.Tier0MachineQuota, "attributes", r => r with { Attributes = ["ms-DS-MachineAccountQuota"] } },
        { DefaultRules.Tier0MachineQuota, "requiredTags", r => r with { RequiredTags = [.. r.RequiredTags, EventTags.Privileged] } },
        { DefaultRules.WatchdogSelf, "requiredTags", r => r with { AnyTags = [EventTags.Privileged] } },
        { DefaultRules.Tier0BadSuccessor, "groups", r => r with { Groups = GroupFilter.Privileged } },
        { DefaultRules.Tier0Gpo, "minSeverity", r => r with { MinSeverity = Severity.Critical } },
        { DefaultRules.WatchdogSelf, "minSeverity", r => r with { MinSeverity = Severity.High } },
        { DefaultRules.WatchdogSelf, "severity", r => r with { Severity = null } },
        { DefaultRules.Tier0MachineQuota, "severity", r => r with { Severity = Severity.Medium } },
        { DefaultRules.WatchdogSelf, "actors", r => r with { Actors = new ActorFilter { ExcludeActors = [TestEvents.AdminSid] } } },
        { DefaultRules.WatchdogSelf, "actors", r => r with { Actors = new ActorFilter { ExcludeSelfChanges = true } } },
        {
            DefaultRules.Tier0Gpo, "exclusions", r => r with
            {
                Exclusions = [new RuleExclusion { Id = "druk", Reason = "GPO drukarek – Łódź", CreatedBy = "adm-jan", CreatedAt = TestEvents.Now, SubtreeDn = "OU=Łódź,{domain}" }],
            }
        },
        { DefaultRules.Tier0BadSuccessor, "delivery.immediate", r => r with { Delivery = r.Delivery with { Immediate = false } } },
        { DefaultRules.WatchdogSelf, "delivery.includeInDigest", r => r with { Delivery = r.Delivery with { IncludeInDigest = false } } },
        { DefaultRules.Tier0MachineQuota, "delivery.hours", r => r with { Delivery = r.Delivery with { Hours = DeliveryHours.BusinessHours } } },
        { DefaultRules.Tier0Gpo, "delivery.recipientGroupIds", r => r with { Delivery = r.Delivery with { RecipientGroupIds = [] } } },
        { DefaultRules.WatchdogSelf, "delivery.aggregationWindow", r => r with { Delivery = r.Delivery with { AggregationWindow = TimeSpan.FromMinutes(30) } } },
        { DefaultRules.Tier0BadSuccessor, "delivery.repeatSuppression", r => r with { Delivery = r.Delivery with { RepeatSuppression = TimeSpan.FromHours(1) } } },
        { DefaultRules.Tier0BadSuccessor, "delivery.maxMessagesPerHour", r => r with { Delivery = r.Delivery with { MaxMessagesPerHour = 1 } } },
    };

    [Theory]
    [MemberData(nameof(Strengthenings))]
    public void StrengtheningOrRetargetingAStageBRule_IsAllowed(string id, Func<RuleSettings, RuleSettings> change)
    {
        var (configuration, _) = WithRule(Defaults(), id, change);

        Assert.DoesNotContain(Validate(configuration).Issues, i => i.Code == ConfigValidationCodes.Tier0Suppression);
    }

    public static TheoryData<string, Func<RuleSettings, RuleSettings>> Strengthenings() => new()
    {
        { DefaultRules.Tier0Gpo, r => r with { Name = "Treść GPO – Gdańsk" } },
        { DefaultRules.Tier0Gpo, r => r with { MinSeverity = Severity.Medium } },
        { DefaultRules.Tier0BadSuccessor, r => r with { Kinds = [] } },
        { DefaultRules.Tier0MachineQuota, r => r with { RequiredTags = [] } },
        { DefaultRules.WatchdogSelf, r => r with { Delivery = r.Delivery with { RecipientGroupIds = [DefaultRules.HelpdeskGroup] } } },
        { DefaultRules.WatchdogSelf, r => r with { Delivery = r.Delivery with { AllEnabledChannels = true } } },
        { DefaultRules.Tier0Gpo, r => r with { Severity = Severity.Critical } },
    };

    [Fact]
    public void DisablingTheBadSuccessorKinds_NeedsAllowTier0Suppression()
    {
        var c = Defaults();
        c = c with { Directory = c.Directory with { DisabledKinds = [WatchEventKind.ServiceAccountCreated, WatchEventKind.GroupPolicySettingsChanged] } };

        var result = Validate(c);

        AssertIssue(result, ConfigValidationCodes.Tier0Suppression, "directory.disabledKinds[0]");
        AssertIssue(result, ConfigValidationCodes.Tier0Suppression, "directory.disabledKinds[1]");
    }

    [Fact]
    public void IgnoringTheEncryptionTypes_NeedsAllowTier0Suppression()
    {
        var c = Defaults();
        c = c with { Directory = c.Directory with { IgnoredAttributes = ["msDS-SupportedEncryptionTypes"] } };

        AssertIssue(Validate(c), ConfigValidationCodes.Tier0Suppression, "directory.ignoredAttributes[0]");
    }

    // ------------------------------------------------------------------ retention floor (14.2)

    [Theory]
    [InlineData(1094, 1095, true)]
    [InlineData(1095, 1095, false)]
    [InlineData(3650, 1095, false)]
    [InlineData(7, 1095, true)]
    [InlineData(30, 0, false)]                     // no floor (Windows service, tests)
    [InlineData(400, 365, false)]                  // root lowered the floor
    public void RetentionBelowFloor(int eventsDays, int floor, bool expected)
    {
        var c = Defaults();
        c = c with { Retention = c.Retention with { EventsDays = eventsDays } };

        var result = Validate(c, new ConfigValidationContext { MinEventsDays = floor });

        if (expected)
        {
            var issue = AssertIssue(result, ConfigValidationCodes.RetentionBelowFloor, "retention.eventsDays");
            Assert.Equal([floor.ToString(System.Globalization.CultureInfo.InvariantCulture)], issue.Args);
        }
        else
        {
            Assert.DoesNotContain(result.Issues, i => i.Code == ConfigValidationCodes.RetentionBelowFloor);
        }
    }

    [Fact]
    public void ShippedDefaults_MeetTheShippedFloor()
    {
        var result = Validate(Defaults(), new ConfigValidationContext { MinEventsDays = 1095 });

        Assert.DoesNotContain(result.Issues, i => i.Code == ConfigValidationCodes.RetentionBelowFloor);
        Assert.Equal(1095, new RetentionSettings().EventsDays);
    }

    // ------------------------------------------------------------------ configuration audit

    [Theory]
    [InlineData(DefaultRules.Tier0BadSuccessor)]
    [InlineData(DefaultRules.Tier0MachineQuota)]
    [InlineData(DefaultRules.WatchdogSelf)]
    [InlineData(DefaultRules.Tier0Gpo)]
    public void DisablingOrRemovingAStageBRule_IsCritical(string id)
    {
        var auditor = new ConfigChangeAuditor(new FakeTimeProvider(TestEvents.Now));
        var before = Defaults();

        var disabled = auditor.Compare(before, WithRule(before, id, r => r with { Enabled = false }).Configuration);
        var removed = auditor.Compare(before, before with { Rules = [.. before.Rules.Where(r => r.Id != id)] });
        var weakened = auditor.Compare(before, WithRule(before, id, r => r with { Delivery = r.Delivery with { Immediate = false } }).Configuration);

        Assert.Equal(Severity.Critical, disabled.Severity);
        Assert.Equal($"rules[{id}].enabled", disabled.Items[0].Path);
        Assert.Equal(Severity.Critical, removed.Severity);
        Assert.Equal(Severity.Critical, weakened.Severity);
    }

    [Theory]
    [MemberData(nameof(RetentionDecreases))]
    public void DecreasingARetentionValue_IsCritical(string path, Func<RetentionSettings, RetentionSettings> change)
    {
        var auditor = new ConfigChangeAuditor(new FakeTimeProvider(TestEvents.Now));
        var before = Defaults();

        var audit = auditor.Compare(before, before with { Retention = change(before.Retention) });

        Assert.Equal(Severity.Critical, audit.Severity);
        Assert.Equal(path, Assert.Single(audit.Items).Path);
    }

    public static TheoryData<string, Func<RetentionSettings, RetentionSettings>> RetentionDecreases() => new()
    {
        { "retention.eventsDays", r => r with { EventsDays = 1094 } },
        { "retention.securityEventsDays", r => r with { SecurityEventsDays = 7 } },
        { "retention.logonRecordsHours", r => r with { LogonRecordsHours = 24 } },
        { "retention.backupsToKeep", r => r with { BackupsToKeep = 0 } },
        { "retention.configAuditDays", r => r with { ConfigAuditDays = 365 } },
        { "retention.deletedSnapshotDays", r => r with { DeletedSnapshotDays = 30 } },
        { "retention.outboxSentDays", r => r with { OutboxSentDays = 1 } },
        { "retention.maxDatabaseMb", r => r with { MaxDatabaseMb = 1024 } },
    };

    [Fact]
    public void IncreasingRetentionOrMovingTheMaintenanceTime_IsMedium()
    {
        var auditor = new ConfigChangeAuditor(new FakeTimeProvider(TestEvents.Now));
        var before = Defaults();

        var longer = auditor.Compare(before, before with { Retention = before.Retention with { EventsDays = 2000, BackupsToKeep = 14 } });
        var moved = auditor.Compare(before, before with { Retention = before.Retention with { MaintenanceTime = new TimeOnly(1, 30) } });

        Assert.Equal(Severity.Medium, longer.Severity);
        Assert.Equal(Severity.Medium, moved.Severity);
    }

    // ------------------------------------------------------------------ helpers

    private static WatchdogConfiguration Defaults()
    {
        var defaults = new DefaultConfigurationFactory().Create();
        return defaults with
        {
            Smtp = defaults.Smtp with { Enabled = true, Host = "smtp.contoso.test", From = "Nadzór AD <nadzor@contoso.test>" },
            RecipientGroups = [defaults.RecipientGroups[0] with { Addresses = ["łukasz.wiśniewski@contoso.test"] }, defaults.RecipientGroups[1]],
        };
    }

    private static (WatchdogConfiguration Configuration, int Index) WithRule(WatchdogConfiguration c, string id, Func<RuleSettings, RuleSettings> change)
    {
        var index = c.Rules.ToList().FindIndex(r => r.Id == id);
        return (c with { Rules = [.. c.Rules.Select((r, i) => i == index ? change(r) : r)] }, index);
    }

    private static ConfigValidationResult Validate(WatchdogConfiguration configuration, ConfigValidationContext? context = null) =>
        new ConfigurationValidator(new FakeTimeProvider(TestEvents.Now)).Validate(configuration, context ?? Production);

    private static ConfigIssue AssertIssue(ConfigValidationResult result, string code, string path)
    {
        var issue = Assert.Single(result.Issues, i => i.Code == code && i.Path == path);
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.False(string.IsNullOrWhiteSpace(issue.Message));
        return issue;
    }

    private static bool Matches(string ruleId, WatchEvent e)
    {
        var configuration = Defaults();
        var rule = configuration.Rules.Single(r => r.Id == ruleId);
        return RuleMatcher.Match(rule, e, RuleMatchContext.Create(configuration, TestEvents.Tokens), TestEvents.Now, attributionGraceExpired: true)
            != RuleMatchOutcome.NoMatch;
    }
}
