using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Configuration;
using AdMng.Watchdog.Core.Formatting.Webhooks;
using AdMng.Watchdog.Core.Rules;
using AdMng.Watchdog.Core.Tests.Rules.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Watchdog.Core.Tests.Rules;

public class ConfigurationValidatorTests
{
    private const string TenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47";
    private const string ClientId = "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0";
    private const string TeamsUrl = "https://default72f988bf86f141af91ab2d7cd011db.47.environment.api.powerplatform.com:443/powerautomate/automations/direct/workflows/abc/triggers/manual/paths/invoke?sig=x";
    private const string SiemUrl = "https://siem.contoso.test/hooks/admng";

    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);
    private static readonly ConfigValidationContext Production = new();

    /// <summary>A configuration without any issue: defaults, SMTP relay with STARTTLS, recipient addresses.</summary>
    private static WatchdogConfiguration Valid()
    {
        var defaults = new DefaultConfigurationFactory().Create();
        return defaults with
        {
            Smtp = defaults.Smtp with { Enabled = true, Host = "smtp.contoso.test", From = "Nadzór AD <nadzor@contoso.test>" },
            RecipientGroups =
            [
                defaults.RecipientGroups[0] with { Addresses = ["łukasz.wiśniewski@contoso.test", "Zofia Żak <zofia.żak@contoso.test>"] },
                defaults.RecipientGroups[1] with { Addresses = ["helpdesk.łódź@contoso.test"] },
            ],
        };
    }

    private static ConfigValidationResult Validate(WatchdogConfiguration configuration, ConfigValidationContext? context = null, IHealthRegistry? registry = null) =>
        new ConfigurationValidator(new FakeTimeProvider(Now), registry).Validate(configuration, context ?? Production);

    private static ConfigIssue AssertIssue(ConfigValidationResult result, string code, string path, IssueSeverity severity = IssueSeverity.Error)
    {
        var issue = Assert.Single(result.Issues, i => i.Code == code && i.Path == path);
        Assert.Equal(severity, issue.Severity);
        Assert.False(string.IsNullOrWhiteSpace(issue.Message));
        return issue;
    }

    private static void AssertNoIssue(ConfigValidationResult result, string code) =>
        Assert.DoesNotContain(result.Issues, i => i.Code == code);

    private static WatchdogConfiguration WithRule(WatchdogConfiguration c, string id, Func<RuleSettings, RuleSettings> change) =>
        c with { Rules = [.. c.Rules.Select(r => r.Id == id ? change(r) : r)] };

    private static ChannelSettings Teams(string url, TeamsAuthMode mode = TeamsAuthMode.Sas) =>
        DefaultConfigurationFactory.CreateChannel("teams-it", ChannelType.Teams, "Teams – Administratorzy IT") with
        {
            Url = new SecretField { NewValue = url },
            Teams = new TeamsChannelOptions
            {
                AuthMode = mode,
                TenantId = mode == TeamsAuthMode.EntraClientCredentials ? TenantId : null,
                ClientId = mode == TeamsAuthMode.EntraClientCredentials ? ClientId : null,
                CertificateThumbprint = mode == TeamsAuthMode.EntraClientCredentials ? "A1B2C3D4E5F60718293A4B5C6D7E8F9012345678" : null,
            },
        };

    private static ChannelSettings Webhook(string? url = SiemUrl, params string[] allowedHosts) =>
        DefaultConfigurationFactory.CreateChannel("siem", ChannelType.Webhook, "SIEM – Łódź") with
        {
            Url = new SecretField { NewValue = url },
            Webhook = new WebhookChannelOptions
            {
                SigningSecret = new SecretField { NewValue = StandardWebhookSigner.GenerateSecret() },
                AllowedHosts = allowedHosts,
            },
        };

    [Fact]
    public void ValidConfiguration_HasNoIssues() => Assert.Empty(Validate(Valid()).Issues);

    [Fact]
    public void DefaultConfiguration_IsValid_WithSmtpAndRecipientWarningsOnly()
    {
        var result = Validate(new DefaultConfigurationFactory().Create());

        Assert.True(result.IsValid);
        Assert.All(result.Issues, i => Assert.Contains(i.Code, new[] { ConfigValidationCodes.SmtpDisabledButUsed, ConfigValidationCodes.NoRecipients }));
        AssertIssue(result, ConfigValidationCodes.SmtpDisabledButUsed, "smtp.enabled", IssueSeverity.Warning);
    }

    public static TheoryData<string, Func<WatchdogConfiguration, WatchdogConfiguration>> RangeCases => new()
    {
        { "directory.pollInterval", c => c with { Directory = c.Directory with { PollInterval = TimeSpan.FromSeconds(4) } } },
        { "attribution.pollInterval", c => c with { Attribution = c.Attribution with { PollInterval = TimeSpan.FromSeconds(9) } } },
        { "attribution.matchWindow", c => c with { Attribution = c.Attribution with { MatchWindow = TimeSpan.FromSeconds(31) } } },
        { "attribution.fallbackWindow", c => c with { Attribution = c.Attribution with { FallbackWindow = TimeSpan.FromSeconds(29) } } },
        { "attribution.logonLookback", c => c with { Attribution = c.Attribution with { LogonLookback = TimeSpan.FromHours(73) } } },
        { "attribution.sourceQueryTimeout", c => c with { Attribution = c.Attribution with { SourceQueryTimeout = TimeSpan.FromSeconds(1) } } },
        { "attribution.maxRecordsPerPoll", c => c with { Attribution = c.Attribution with { MaxRecordsPerPoll = 999 } } },
        { "attribution.pendingRetries[1]", c => c with { Attribution = c.Attribution with { PendingRetries = [TimeSpan.FromSeconds(30), TimeSpan.Zero] } } },
        { "directory.creationSettleWindow", c => c with { Directory = c.Directory with { CreationSettleWindow = TimeSpan.FromMinutes(11) } } },
        { "directory.maxValuesPerAttribute", c => c with { Directory = c.Directory with { MaxValuesPerAttribute = 1001 } } },
        { "notifications.aggregationWindow", c => c with { Notifications = c.Notifications with { AggregationWindow = TimeSpan.FromMinutes(61) } } },
        { "notifications.attributionGrace", c => c with { Notifications = c.Notifications with { AttributionGrace = TimeSpan.FromMinutes(11) } } },
        { "notifications.maxItemsPerMessage", c => c with { Notifications = c.Notifications with { MaxItemsPerMessage = 0 } } },
        { "notifications.maxMessagesPerRulePerHour", c => c with { Notifications = c.Notifications with { MaxMessagesPerRulePerHour = 1001 } } },
        { "scans.accountExpiry.thresholdsDays[1]", c => c with { Scans = c.Scans with { AccountExpiry = c.Scans.AccountExpiry with { ThresholdsDays = [14, 366] } } } },
        { "scans.passwordExpiry.thresholdsDays[1]", c => c with { Scans = c.Scans with { PasswordExpiry = c.Scans.PasswordExpiry with { ThresholdsDays = [7, 7] } } } },
        { "scans.inactiveAccounts.days", c => c with { Scans = c.Scans with { InactiveAccounts = c.Scans.InactiveAccounts with { Days = 29 } } } },
        { "retention.eventsDays", c => c with { Retention = c.Retention with { EventsDays = 6 } } },
        { "retention.securityEventsDays", c => c with { Retention = c.Retention with { SecurityEventsDays = 3651 } } },
        { "retention.maxDatabaseMb", c => c with { Retention = c.Retention with { MaxDatabaseMb = 511 } } },
        { "smtp.port", c => c with { Smtp = c.Smtp with { Port = 0 } } },
        { "smtp.maxRecipientsPerMessage", c => c with { Smtp = c.Smtp with { MaxRecipientsPerMessage = 501 } } },
        { "general.rolloutDays", c => c with { General = c.General with { RolloutDays = 61 } } },
        { "channels[0].rateLimit.perMinute", c => c with { Channels = [Webhook() with { RateLimit = new RateLimitSettings { PerMinute = 0 } }] } },
        { "channels[0].webhook.heartbeatInterval", c => c with { Channels = [Webhook() with { Webhook = Webhook().Webhook! with { HeartbeatInterval = TimeSpan.FromMinutes(4) } }] } },
        { "rules[12].delivery.repeatSuppression", c => WithRule(c, DefaultRules.Lockouts, r => r with { Delivery = r.Delivery with { RepeatSuppression = TimeSpan.FromHours(25) } }) },
        { "rules[7].delivery.aggregationWindow", c => WithRule(c, DefaultRules.NewAccounts, r => r with { Delivery = r.Delivery with { AggregationWindow = TimeSpan.FromMinutes(-1) } }) },
        { "rules[12].delivery.maxMessagesPerHour", c => WithRule(c, DefaultRules.Lockouts, r => r with { Delivery = r.Delivery with { MaxMessagesPerHour = 0 } }) },
    };

    [Theory]
    [MemberData(nameof(RangeCases))]
    public void Range(string path, Func<WatchdogConfiguration, WatchdogConfiguration> change)
    {
        var result = Validate(change(Valid()));

        var issue = AssertIssue(result, ConfigValidationCodes.Range, path);
        Assert.Equal(2, issue.Args.Count);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Required_SmtpHostAndSenderWhenEnabled()
    {
        var result = Validate(Valid() with { Smtp = Valid().Smtp with { Host = null, From = " " } });

        AssertIssue(result, ConfigValidationCodes.Required, "smtp.host");
        AssertIssue(result, ConfigValidationCodes.Required, "smtp.from");
        Assert.True(Validate(Valid() with { Smtp = Valid().Smtp with { Enabled = false, Host = null, From = null } }).IsValid);
    }

    [Fact]
    public void Required_ChannelUrlWhenEnabled_UsesStoredSecretsNotIsSet()
    {
        var claimed = Webhook() with { Url = new SecretField { IsSet = true } };
        var configuration = Valid() with { Channels = [claimed] };

        AssertIssue(Validate(configuration), ConfigValidationCodes.Required, "channels[0].url");

        var stored = new ConfigValidationContext
        {
            StoredSecrets = new Dictionary<string, string> { [SecretNames.ChannelUrl("siem")] = SecretBindings.ForChannelUrl(claimed) },
        };
        AssertNoIssue(Validate(configuration, stored), ConfigValidationCodes.Required);
        AssertNoIssue(Validate(configuration with { Channels = [claimed with { Enabled = false }] }), ConfigValidationCodes.Required);
        var cleared = configuration with { Channels = [claimed with { Url = new SecretField { IsSet = true, Clear = true } }] };
        AssertIssue(Validate(cleared, stored), ConfigValidationCodes.Required, "channels[0].url");
    }

    [Fact]
    public void Required_WebhookSigningSecret_AndItsFormat()
    {
        var unsigned = Webhook() with { Webhook = new WebhookChannelOptions() };
        AssertIssue(Validate(Valid() with { Channels = [unsigned] }), ConfigValidationCodes.Required, "channels[0].webhook.signingSecret");

        var malformed = Webhook() with { Webhook = new WebhookChannelOptions { SigningSecret = new SecretField { NewValue = "tajne-hasło" } } };
        AssertIssue(Validate(Valid() with { Channels = [malformed] }), ConfigValidationCodes.Required, "channels[0].webhook.signingSecret");
    }

    [Fact]
    public void Required_OAuthIdentity_ForSmtpAndTeams()
    {
        var smtp = Valid().Smtp with
        {
            Host = "smtp.office365.com",
            UserName = "nadzor@contoso.test",
            Authentication = SmtpAuthentication.OAuth2ClientCredentials,
            OAuth = new SmtpOAuthSettings { TenantId = "nie-guid", ClientId = null },
        };
        var result = Validate(Valid() with { Smtp = smtp });

        AssertIssue(result, ConfigValidationCodes.Required, "smtp.oAuth.tenantId");
        AssertIssue(result, ConfigValidationCodes.Required, "smtp.oAuth.clientId");
        AssertIssue(result, ConfigValidationCodes.Required, "smtp.oAuth.clientSecret");

        var withCertificate = smtp with { OAuth = new SmtpOAuthSettings { TenantId = TenantId, ClientId = ClientId, CertificateThumbprint = "ABCDEF" } };
        Assert.True(Validate(Valid() with { Smtp = withCertificate }).IsValid);

        var teams = Teams(TeamsUrl, TeamsAuthMode.EntraClientCredentials) with { Teams = new TeamsChannelOptions { AuthMode = TeamsAuthMode.EntraClientCredentials } };
        var teamsResult = Validate(Valid() with { Channels = [teams] });
        AssertIssue(teamsResult, ConfigValidationCodes.Required, "channels[0].teams.tenantId");
        AssertIssue(teamsResult, ConfigValidationCodes.Required, "channels[0].teams.clientSecret");
    }

    [Fact]
    public void Required_BasicLoginWhenEnabled()
    {
        var smtp = Valid().Smtp with { Authentication = SmtpAuthentication.Basic };

        var result = Validate(Valid() with { Smtp = smtp });

        AssertIssue(result, ConfigValidationCodes.Required, "smtp.userName");
        AssertIssue(result, ConfigValidationCodes.Required, "smtp.password");
        var complete = smtp with { UserName = "svc-wd@contoso.test", Password = new SecretField { NewValue = "Zażółć-gęślą-jaźń-2026" } };
        Assert.True(Validate(Valid() with { Smtp = complete }).IsValid);
    }

    [Fact]
    public void SecretRebindRequired_WhenTheSmtpHostChangesWithoutANewPassword()
    {
        var original = Valid().Smtp with { Authentication = SmtpAuthentication.Basic, UserName = "svc-wd@contoso.test", Password = new SecretField { IsSet = true } };
        var context = new ConfigValidationContext
        {
            StoredSecrets = new Dictionary<string, string> { [SecretNames.SmtpPassword] = SecretBindings.ForSmtpPassword(original) },
        };

        Assert.True(Validate(Valid() with { Smtp = original }, context).IsValid);

        var moved = original with { Host = "smtp.atakujacy.example" };
        AssertIssue(Validate(Valid() with { Smtp = moved }, context), ConfigValidationCodes.SecretRebindRequired, "smtp.password");

        var reentered = moved with { Password = new SecretField { NewValue = "Nowe-hasło-2026" } };
        AssertNoIssue(Validate(Valid() with { Smtp = reentered }, context), ConfigValidationCodes.SecretRebindRequired);
    }

    [Fact]
    public void SecretRebindRequired_WhenAWebhookUrlHostChangesWithoutANewSigningSecret()
    {
        var channel = Webhook() with
        {
            Url = new SecretField { IsSet = true, Hint = "https://siem.contoso.test/hooks/…" },
            Webhook = new WebhookChannelOptions { SigningSecret = new SecretField { IsSet = true } },
        };
        var context = new ConfigValidationContext
        {
            StoredSecrets = new Dictionary<string, string>
            {
                [SecretNames.ChannelUrl("siem")] = SecretBindings.ForChannelUrl(channel),
                [SecretNames.WebhookSigningSecret("siem")] = SecretBindings.ForWebhookSigningSecret(channel, "siem.contoso.test"),
            },
        };
        Assert.True(Validate(Valid() with { Channels = [channel] }, context).IsValid);

        var redirected = channel with { Url = new SecretField { NewValue = "https://zbieracz.example/hook" } };

        AssertIssue(Validate(Valid() with { Channels = [redirected] }, context), ConfigValidationCodes.SecretRebindRequired, "channels[0].webhook.signingSecret");
    }

    [Fact]
    public void DuplicateId_IsCaseInsensitive_ForRulesChannelsGroupsAndExclusions()
    {
        var c = Valid();
        var exclusion = new RuleExclusion { Id = "wyjątek-1", Reason = "Test", CreatedBy = "CONTOSO\\adm.jkowalska", CreatedAt = Now, Attribute = "department" };
        var configuration = c with
        {
            Rules = [.. c.Rules, c.Rules[0] with { Id = c.Rules[0].Id }],
            Channels = [Webhook(), Webhook()],
            RecipientGroups = [.. c.RecipientGroups, c.RecipientGroups[0]],
        };
        configuration = WithRule(configuration, DefaultRules.Lockouts, r => r with { Exclusions = [exclusion, exclusion] });

        var result = Validate(configuration);

        AssertIssue(result, ConfigValidationCodes.DuplicateId, $"rules[{c.Rules.Count}].id");
        AssertIssue(result, ConfigValidationCodes.DuplicateId, "channels[1].id");
        AssertIssue(result, ConfigValidationCodes.DuplicateId, "recipientGroups[2].id");
        AssertIssue(result, ConfigValidationCodes.DuplicateId, "rules[12].exclusions[1].id");
    }

    [Fact]
    public void InvalidId_SyntaxLengthAndTheReservedEmailChannel()
    {
        var c = Valid();
        var configuration = c with
        {
            Rules = [.. c.Rules, c.Rules[1] with { Id = "Wielkie-Litery" }, c.Rules[1] with { Id = new string('a', 61) }],
            Channels = [Webhook() with { Id = "email" }, Webhook() with { Id = new string('k', 41) }],
            RecipientGroups = [.. c.RecipientGroups, c.RecipientGroups[0] with { Id = "zespół" }],
        };

        var result = Validate(configuration);

        AssertIssue(result, ConfigValidationCodes.InvalidId, $"rules[{c.Rules.Count}].id");
        AssertIssue(result, ConfigValidationCodes.InvalidId, $"rules[{c.Rules.Count + 1}].id");
        AssertIssue(result, ConfigValidationCodes.InvalidId, "channels[0].id");
        AssertIssue(result, ConfigValidationCodes.InvalidId, "channels[1].id");
        AssertIssue(result, ConfigValidationCodes.InvalidId, "recipientGroups[2].id");
        Assert.True(Validate(c with { Channels = [Webhook() with { Id = new string('k', 40) }] }).IsValid);
    }

    [Fact]
    public void UnknownReference_InRulesAndDigest()
    {
        var c = WithRule(Valid(), DefaultRules.Bursts, r => r with { Delivery = r.Delivery with { ChannelIds = ["nie-ma"], RecipientGroupIds = ["it-admins", "kierownicy"] } });
        c = c with { Digest = c.Digest with { RecipientGroupIds = ["IT-ADMINS", "zarząd"], ChannelIds = ["teams-x"] } };

        var result = Validate(c);

        AssertIssue(result, ConfigValidationCodes.UnknownReference, "rules[5].delivery.channelIds[0]");
        AssertIssue(result, ConfigValidationCodes.UnknownReference, "rules[5].delivery.recipientGroupIds[1]");
        AssertIssue(result, ConfigValidationCodes.UnknownReference, "digest.recipientGroupIds[1]");
        AssertIssue(result, ConfigValidationCodes.UnknownReference, "digest.channelIds[0]");
        Assert.DoesNotContain(result.Issues, i => i.Path == "digest.recipientGroupIds[0]");
    }

    [Fact]
    public void InvalidDn_AfterPlaceholderExpansion()
    {
        var c = Valid();
        c = c with
        {
            Directory = c.Directory with { ExcludedSubtrees = ["OU=Archiwum,{domain}", "to nie jest DN"], ServiceAccountSubtrees = [""] },
            PrivilegedGroups = c.PrivilegedGroups with { WatchListDns = ["CN=Księgowość,OU=Grupy,DC=contoso,DC=test", "CN=x,,DC=y"] },
        };
        c = WithRule(c, DefaultRules.Deletions, r => r with
        {
            Scope = new RuleScope { IncludeSubtrees = ["OU=Kraków,{domain}"], ExcludeSubtrees = ["=bez typu"] },
            Exclusions = [new RuleExclusion { Id = "zły-dn", SubtreeDn = "OU", Reason = "Test", CreatedBy = "CONTOSO\\jan", CreatedAt = Now }],
        });

        var result = Validate(c);

        AssertIssue(result, ConfigValidationCodes.InvalidDn, "directory.excludedSubtrees[1]");
        AssertIssue(result, ConfigValidationCodes.InvalidDn, "directory.serviceAccountSubtrees[0]");
        AssertIssue(result, ConfigValidationCodes.InvalidDn, "privilegedGroups.watchListDns[1]");
        AssertIssue(result, ConfigValidationCodes.InvalidDn, "rules[10].scope.excludeSubtrees[0]");
        AssertIssue(result, ConfigValidationCodes.InvalidDn, "rules[10].exclusions[0].subtreeDn");
        Assert.Equal(5, result.Issues.Count(i => i.Code == ConfigValidationCodes.InvalidDn));
    }

    [Fact]
    public void InvalidSid_ActorsAreSidsOnly()
    {
        var c = WithRule(Valid(), DefaultRules.Lockouts, r => r with
        {
            Actors = new ActorFilter { ExcludeActors = ["S-1-5-21-1004336348-1177238915-682003330-1601", "CONTOSO\\svc-sync", "{domainSid}-500"] },
            Exclusions = [new RuleExclusion { Id = "aktor", Actor = "Łukasz Wiśniewski", Reason = "Test", CreatedBy = "CONTOSO\\jan", CreatedAt = Now }],
        });
        c = c with { PrivilegedGroups = c.PrivilegedGroups with { HighSids = ["{rootDomainSid}-519", "S-1-5-32-x"] } };

        var result = Validate(c);

        AssertIssue(result, ConfigValidationCodes.InvalidSid, "rules[12].actors.excludeActors[1]");
        AssertIssue(result, ConfigValidationCodes.InvalidSid, "rules[12].exclusions[0].actor");
        AssertIssue(result, ConfigValidationCodes.InvalidSid, "privilegedGroups.highSids[1]");
        Assert.Equal(3, result.Issues.Count(i => i.Code == ConfigValidationCodes.InvalidSid));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("+")]
    [InlineData("1.1")]
    [InlineData("cn;binary")]
    [InlineData("unicodePwd")]
    [InlineData("ms-Mcs-AdmPwd")]
    [InlineData("lastLogonTimestamp")]
    [InlineData("memberOf")]
    [InlineData("msTPM-TpmInformationForComputer")]
    [InlineData("zażółć")]
    public void InvalidAttribute_InEveryAttributeList(string attribute)
    {
        var c = Valid();
        c = c with { Directory = c.Directory with { AdditionalWatchedAttributes = [attribute], IgnoredAttributes = [attribute] } };
        c = WithRule(c, DefaultRules.LogonSettings, r => r with
        {
            Attributes = [attribute],
            Exclusions = [new RuleExclusion { Id = "atrybut", Attribute = attribute, Reason = "Test", CreatedBy = "CONTOSO\\jan", CreatedAt = Now }],
        });

        var result = Validate(c);

        AssertIssue(result, ConfigValidationCodes.InvalidAttribute, "directory.additionalWatchedAttributes[0]");
        AssertIssue(result, ConfigValidationCodes.InvalidAttribute, "directory.ignoredAttributes[0]");
        AssertIssue(result, ConfigValidationCodes.InvalidAttribute, "rules[17].attributes[0]");
        AssertIssue(result, ConfigValidationCodes.InvalidAttribute, "rules[17].exclusions[0].attribute");
    }

    [Fact]
    public void InvalidAttribute_AtMostTwentyAdditionalAttributes()
    {
        var c = Valid();
        var twenty = Enumerable.Range(1, 20).Select(i => $"extensionAttribute{i}").ToArray();

        Assert.True(Validate(c with { Directory = c.Directory with { AdditionalWatchedAttributes = twenty } }).IsValid);
        AssertIssue(Validate(c with { Directory = c.Directory with { AdditionalWatchedAttributes = [.. twenty, "employeeType"] } }),
            ConfigValidationCodes.InvalidAttribute, "directory.additionalWatchedAttributes");
    }

    [Fact]
    public void Tier0Suppression_AttributesKindsAndSubtrees_UnlessTheBootstrapFlagAllowsIt()
    {
        var c = Valid();
        c = c with
        {
            Directory = c.Directory with
            {
                IgnoredAttributes = ["department", "userAccountControl"],
                DisabledKinds = [WatchEventKind.ObjectRenamed, WatchEventKind.SidHistoryChanged],
                ExcludedSubtrees = ["OU=Archiwum,{domain}", "CN=System,{domain}", "DC=contoso,DC=test", "OU=Domain Controllers,DC=contoso,DC=test", "CN=AdminSDHolder,CN=System,{domain}"],
            },
        };

        var result = Validate(c);

        AssertIssue(result, ConfigValidationCodes.Tier0Suppression, "directory.ignoredAttributes[1]");
        AssertIssue(result, ConfigValidationCodes.Tier0Suppression, "directory.disabledKinds[1]");
        AssertIssue(result, ConfigValidationCodes.Tier0Suppression, "directory.excludedSubtrees[1]");
        AssertIssue(result, ConfigValidationCodes.Tier0Suppression, "directory.excludedSubtrees[2]");
        AssertIssue(result, ConfigValidationCodes.Tier0Suppression, "directory.excludedSubtrees[3]");
        AssertIssue(result, ConfigValidationCodes.Tier0Suppression, "directory.excludedSubtrees[4]");
        Assert.Equal(6, result.Issues.Count(i => i.Code == ConfigValidationCodes.Tier0Suppression));

        AssertNoIssue(Validate(c, new ConfigValidationContext { AllowTier0Suppression = true }), ConfigValidationCodes.Tier0Suppression);
    }

    [Fact]
    public void Tier0Suppression_HealthKindsCanNeverBeDisabled()
    {
        var c = Valid();
        c = c with { Directory = c.Directory with { DisabledKinds = [WatchEventKind.HealthEventLogGap] } };

        AssertIssue(Validate(c, new ConfigValidationContext { AllowTier0Suppression = true }), ConfigValidationCodes.Tier0Suppression, "directory.disabledKinds[0]");
    }

    [Theory]
    [InlineData("jan.kowalski")]
    [InlineData("jan@contoso.test\r\nBcc: zbieracz@atakujacy.example")]
    [InlineData("a@b.test, c@d.test")]
    [InlineData("")]
    public void InvalidAddress_RecipientsAndSender(string address)
    {
        var c = Valid();
        c = c with
        {
            RecipientGroups = [c.RecipientGroups[0] with { Addresses = ["łukasz.wiśniewski@contoso.test", address] }, c.RecipientGroups[1]],
            Smtp = c.Smtp with { From = address, ReplyTo = address },
        };

        var result = Validate(c);

        AssertIssue(result, ConfigValidationCodes.InvalidAddress, "recipientGroups[0].addresses[1]");
        Assert.Contains(result.Issues, i => i.Path is "smtp.from" or "smtp.replyTo");
    }

    [Theory]
    [InlineData("smtp contoso")]
    [InlineData("smtp.contoso.test\r\n")]
    [InlineData("smtp_relay.contoso.test")]
    public void InvalidHostName_SmtpHostAndLocalDomain(string host)
    {
        var c = Valid();
        var result = Validate(c with { Smtp = c.Smtp with { Host = host, LocalDomain = host } });

        AssertIssue(result, ConfigValidationCodes.InvalidHostName, "smtp.host");
        AssertIssue(result, ConfigValidationCodes.InvalidHostName, "smtp.localDomain");
    }

    [Theory]
    [InlineData(SmtpSecurity.None)]
    [InlineData(SmtpSecurity.StartTlsWhenAvailable)]
    public void InsecureCredentials_CredentialsOnlyOverTls(SmtpSecurity security)
    {
        var c = Valid();
        var smtp = c.Smtp with { Security = security, Authentication = SmtpAuthentication.Basic, UserName = "svc", Password = new SecretField { NewValue = "x" } };

        var result = Validate(c with { Smtp = smtp });

        AssertIssue(result, ConfigValidationCodes.InsecureCredentials, "smtp.security");
        AssertIssue(result, ConfigValidationCodes.NoTls, "smtp.security", IssueSeverity.Warning);
        Assert.True(Validate(c with { Smtp = smtp with { Security = SmtpSecurity.SslOnConnect, Port = 465 } }).IsValid);
    }

    [Fact]
    public void OAuthHostNotAllowed_TokensOnlyToBootstrapHosts()
    {
        var c = Valid();
        var smtp = c.Smtp with
        {
            Authentication = SmtpAuthentication.OAuth2ClientCredentials,
            UserName = "nadzor@contoso.test",
            OAuth = new SmtpOAuthSettings { TenantId = TenantId, ClientId = ClientId, CertificateThumbprint = "ABCDEF" },
        };

        AssertIssue(Validate(c with { Smtp = smtp }), ConfigValidationCodes.OAuthHostNotAllowed, "smtp.host");
        Assert.True(Validate(c with { Smtp = smtp with { Host = "SMTP.Office365.com." } }).IsValid);
        var custom = new ConfigValidationContext { OAuthSmtpHosts = ["smtp.contoso.test"] };
        Assert.True(Validate(c with { Smtp = smtp }, custom).IsValid);
    }

    [Fact]
    public void InvalidScope_OnlyThePowerAutomateScopesOfTheCloud()
    {
        var channel = Teams(TeamsUrl) with { Teams = Teams(TeamsUrl).Teams! with { Scope = "https://graph.microsoft.com/.default" } };

        AssertIssue(Validate(Valid() with { Channels = [channel] }), ConfigValidationCodes.InvalidScope, "channels[0].teams.scope");

        var china = channel with { Teams = channel.Teams! with { Cloud = EntraCloud.China, Scope = "https://service.flow.microsoft.com//.default" } };
        AssertIssue(Validate(Valid() with { Channels = [china] }), ConfigValidationCodes.InvalidScope, "channels[0].teams.scope");
        Assert.True(Validate(Valid() with { Channels = [Teams(TeamsUrl)] }).IsValid);
    }

    [Fact]
    public void InsecureUrl_ChannelsNeedHttps_DevelopmentMayUseLoopbackHttp()
    {
        var plain = Webhook("http://siem.contoso.test/hooks/admng");
        var loopback = Webhook("http://127.0.0.1:5005/hook");

        AssertIssue(Validate(Valid() with { Channels = [plain] }), ConfigValidationCodes.InsecureUrl, "channels[0].url");
        AssertIssue(Validate(Valid() with { Channels = [loopback] }), ConfigValidationCodes.InsecureUrl, "channels[0].url");
        Assert.True(Validate(Valid() with { Channels = [loopback] }, new ConfigValidationContext { IsDevelopment = true }).IsValid);
        AssertIssue(Validate(Valid() with { Channels = [plain] }, new ConfigValidationContext { IsDevelopment = true }), ConfigValidationCodes.InsecureUrl, "channels[0].url");
        AssertIssue(Validate(Valid() with { Channels = [Teams(TeamsUrl.Replace("https://", "http://", StringComparison.Ordinal))] }),
            ConfigValidationCodes.InsecureUrl, "channels[0].url");
        AssertIssue(Validate(Valid() with { General = Valid().General with { PublicBaseUrl = "http://admng-wd.contoso.test:8443" } }),
            ConfigValidationCodes.InsecureUrl, "general.publicBaseUrl");
    }

    [Theory]
    [InlineData("https://contoso.webhook.office.com/webhookb2/abc")]
    [InlineData("https://prod-12.westeurope.logic.azure.com/workflows/abc")]
    public void RetiredTeamsUrl_NewValueAndStoredHint(string url)
    {
        AssertIssue(Validate(Valid() with { Channels = [Teams(url)] }), ConfigValidationCodes.RetiredTeamsUrl, "channels[0].url");

        var stored = Teams(url) with { Url = new SecretField { IsSet = true, Hint = url[..(url.IndexOf('/', 8) + 1)] + "…" } };
        var context = new ConfigValidationContext
        {
            StoredSecrets = new Dictionary<string, string> { [SecretNames.ChannelUrl("teams-it")] = SecretBindings.ForChannelUrl(stored) },
        };
        AssertIssue(Validate(Valid() with { Channels = [stored] }, context), ConfigValidationCodes.RetiredTeamsUrl, "channels[0].url");
    }

    [Fact]
    public void TeamsUrlFormat_WarningForSas_ErrorForEntraClientCredentials()
    {
        const string custom = "https://flows.contoso.test/powerautomate/automations/direct/workflows/abc";

        AssertIssue(Validate(Valid() with { Channels = [Teams(custom)] }), ConfigValidationCodes.TeamsUrlFormat, "channels[0].url", IssueSeverity.Warning);
        AssertIssue(Validate(Valid() with { Channels = [Teams(custom, TeamsAuthMode.EntraClientCredentials)] }),
            ConfigValidationCodes.TeamsUrlFormat, "channels[0].url");

        var stored = Teams(TeamsUrl, TeamsAuthMode.EntraClientCredentials) with { Url = new SecretField { IsSet = true, Hint = "https://flows.contoso.test/powerautomate/…" } };
        var context = new ConfigValidationContext
        {
            StoredSecrets = new Dictionary<string, string> { [SecretNames.ChannelUrl("teams-it")] = SecretBindings.ForChannelUrl(stored) },
        };
        AssertIssue(Validate(Valid() with { Channels = [stored] }, context), ConfigValidationCodes.TeamsUrlFormat, "channels[0].url");
    }

    [Fact]
    public void HostNotAllowed_WebhookHostMustBeInTheAllowList()
    {
        AssertIssue(Validate(Valid() with { Channels = [Webhook(SiemUrl, "siem2.contoso.test")] }), ConfigValidationCodes.HostNotAllowed, "channels[0].url");
        Assert.True(Validate(Valid() with { Channels = [Webhook(SiemUrl, "SIEM.contoso.test.")] }).IsValid);
        Assert.True(Validate(Valid() with { Channels = [Webhook(SiemUrl)] }).IsValid);
    }

    [Fact]
    public void LockedRule_TheHealthRuleCannotBeDisabledDeletedUnlockedOrNarrowed()
    {
        var c = Valid();

        AssertIssue(Validate(WithRule(c, DefaultRules.Health, r => r with { Enabled = false })), ConfigValidationCodes.LockedRule, "rules[23].enabled");
        AssertIssue(Validate(WithRule(c, DefaultRules.Health, r => r with { Locked = false })), ConfigValidationCodes.LockedRule, "rules[23].locked");
        AssertIssue(Validate(WithRule(c, DefaultRules.Health, r => r with { Kinds = [WatchEventKind.HealthStorage] })), ConfigValidationCodes.LockedRule, "rules[23].kinds");
        AssertIssue(Validate(c with { Rules = [.. c.Rules.Where(r => r.Id != DefaultRules.Health)] }), ConfigValidationCodes.LockedRule, "rules");
        var ownLocked = new RuleSettings { Id = "moja-zablokowana", Name = "Własna", Locked = true, Enabled = false };
        AssertIssue(Validate(c with { Rules = [.. c.Rules, ownLocked] }), ConfigValidationCodes.LockedRule, $"rules[{c.Rules.Count}].enabled");
        Assert.True(Validate(WithRule(c, DefaultRules.Health, r => r with { Kinds = [.. r.Kinds.Reverse()], Name = "Zdrowie usługi" })).IsValid);
    }

    [Fact]
    public void AcceptAnyNotAllowed_OutsideDevelopment()
    {
        var c = Valid();
        var accepting = c with { Smtp = c.Smtp with { CertificateValidation = CertificateValidationMode.AcceptAnyDevelopmentOnly } };

        AssertIssue(Validate(accepting), ConfigValidationCodes.AcceptAnyNotAllowed, "smtp.certificateValidation");
        AssertNoIssue(Validate(accepting, new ConfigValidationContext { IsDevelopment = true }), ConfigValidationCodes.AcceptAnyNotAllowed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("zz2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f80")]
    public void PinnedHash_Is64HexCharacters(string? hash)
    {
        var c = Valid();
        var pinned = c with { Smtp = c.Smtp with { CertificateValidation = CertificateValidationMode.PinnedSha256, PinnedSha256 = hash } };

        AssertIssue(Validate(pinned), ConfigValidationCodes.PinnedHash, "smtp.pinnedSha256");
        Assert.True(Validate(pinned with { Smtp = pinned.Smtp with { PinnedSha256 = new string('a', 32) + new string('F', 32) } }).IsValid);
    }

    [Theory]
    [InlineData("Europe/Warsaw", true)]
    [InlineData("Central European Standard Time", true)]
    [InlineData("UTC", true)]
    [InlineData("Mars/Olympus_Mons", false)]
    [InlineData("", false)]
    public void TimeZone_IanaAndWindowsIds(string timeZone, bool valid)
    {
        var result = Validate(Valid() with { General = Valid().General with { TimeZone = timeZone } });

        if (valid)
        {
            Assert.True(result.IsValid);
        }
        else
        {
            AssertIssue(result, ConfigValidationCodes.TimeZone, "general.timeZone");
        }
    }

    [Theory]
    [InlineData("de")]
    [InlineData("PL")]
    [InlineData("")]
    public void Language_PlOrEn(string language) =>
        AssertIssue(Validate(Valid() with { General = Valid().General with { Language = language } }), ConfigValidationCodes.Language, "general.language");

    [Fact]
    public void ExclusionReason_ReasonAndCreatorRequired()
    {
        RuleExclusion[] exclusions =
        [
            new() { Id = "bez-powodu", Attribute = "department", Reason = " ", CreatedBy = "CONTOSO\\jan", CreatedAt = Now },
            new() { Id = "za-długi", Attribute = "department", Reason = new string('ż', 501), CreatedBy = "CONTOSO\\jan", CreatedAt = Now },
            new() { Id = "bez-autora", Attribute = "department", Reason = "Synchronizacja HR", CreatedBy = "", CreatedAt = Now },
        ];

        var result = Validate(WithRule(Valid(), DefaultRules.AccountAttributes, r => r with { Exclusions = exclusions }));

        AssertIssue(result, ConfigValidationCodes.ExclusionReason, "rules[18].exclusions[0].reason");
        AssertIssue(result, ConfigValidationCodes.ExclusionReason, "rules[18].exclusions[1].reason");
        AssertIssue(result, ConfigValidationCodes.ExclusionReason, "rules[18].exclusions[2].createdBy");
    }

    [Fact]
    public void TooManyWakeUpContainers()
    {
        var c = Valid();
        string[] five = ["OU=A,{domain}", "OU=B,{domain}", "OU=C,{domain}", "OU=D,{domain}", "OU=E,{domain}"];

        AssertIssue(Validate(c with { Directory = c.Directory with { WakeUpContainers = five } }), ConfigValidationCodes.TooManyWakeUpContainers, "directory.wakeUpContainers");
        Assert.True(Validate(c with { Directory = c.Directory with { WakeUpContainers = five[..4] } }).IsValid);
    }

    [Fact]
    public void SecretWithoutChannel_NewValueAndClearTogether()
    {
        var c = Valid();
        var smtp = c.Smtp with { Password = new SecretField { NewValue = "hasło", Clear = true } };

        AssertIssue(Validate(c with { Smtp = smtp }), ConfigValidationCodes.SecretWithoutChannel, "smtp.password");
    }

    [Fact]
    public void RedactedConfig_AViewerEnvelopeIsNeverAccepted()
    {
        var validator = new ConfigurationValidator(new FakeTimeProvider(Now));
        var viewer = new ConfigRedactor().Redact(new ConfigEnvelope { Version = 3, Configuration = Valid() });

        var result = validator.Validate(viewer, Production);

        var issue = Assert.Single(result.Issues);
        Assert.Equal(ConfigValidationCodes.RedactedConfig, issue.Code);
        Assert.False(result.IsValid);
        Assert.True(validator.Validate(new ConfigEnvelope { Version = 3, Configuration = Valid() }, Production).IsValid);
    }

    [Fact]
    public void RedactedContent_FailsEvenWithoutTheFlag()
    {
        var c = WithRule(Valid(), DefaultRules.Lockouts, r => r with
        {
            Actors = new ActorFilter { ExcludeActors = ["S-1-5-21-1004336348-1177238915-682003330-1601"] },
            Exclusions = [new RuleExclusion { Id = "konto-testowe", Attribute = "department", Reason = "Test", CreatedBy = "CONTOSO\\jan", CreatedAt = Now }],
        });
        var stripped = new ConfigRedactor().Redact(c);

        var result = Validate(stripped);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Code == ConfigValidationCodes.ExclusionReason);
    }

    [Fact]
    public void NullEntries_AreRequiredErrors_NotExceptions()
    {
        var c = Valid();
        var broken = c with { Rules = [.. c.Rules, null!], Channels = [null!], RecipientGroups = [.. c.RecipientGroups, null!], Smtp = null!, Directory = null! };

        var result = Validate(broken);

        AssertIssue(result, ConfigValidationCodes.Required, $"rules[{c.Rules.Count}]");
        AssertIssue(result, ConfigValidationCodes.Required, "channels[0]");
        AssertIssue(result, ConfigValidationCodes.Required, "recipientGroups[2]");
    }

    [Fact]
    public void Warnings_NoRecipientsAndSmtpDisabledButUsed()
    {
        var c = Valid();
        var silent = c with { RecipientGroups = [c.RecipientGroups[0] with { Addresses = [] }, c.RecipientGroups[1]] };

        var result = Validate(silent);

        Assert.True(result.IsValid);
        AssertIssue(result, ConfigValidationCodes.NoRecipients, "rules[0].delivery", IssueSeverity.Warning);
        Assert.DoesNotContain(result.Issues, i => i.Code == ConfigValidationCodes.NoRecipients && i.Path == "rules[12].delivery");   // lockouts → helpdesk
        AssertIssue(result, ConfigValidationCodes.NoRecipients, "rules[23].delivery", IssueSeverity.Warning);   // health: no channel either
        AssertIssue(Validate(c with { Smtp = c.Smtp with { Enabled = false } }), ConfigValidationCodes.SmtpDisabledButUsed, "smtp.enabled", IssueSeverity.Warning);
    }

    [Fact]
    public void Warnings_HealthRuleWithoutAddressesIsSatisfiedByAnEnabledChannel()
    {
        var c = Valid();
        var silent = c with { RecipientGroups = [c.RecipientGroups[0] with { Addresses = [] }, c.RecipientGroups[1]], Channels = [Webhook()] };

        var result = Validate(silent);

        Assert.DoesNotContain(result.Issues, i => i.Code == ConfigValidationCodes.NoRecipients && i.Path == "rules[23].delivery");
        AssertIssue(result, ConfigValidationCodes.NoRecipients, "rules[0].delivery", IssueSeverity.Warning);
    }

    [Theory]
    [InlineData("smtp.office365.com")]
    [InlineData("smtp-legacy.office365.com")]
    public void Warnings_ExchangeOnlineBasic(string host)
    {
        var c = Valid();
        var smtp = c.Smtp with { Host = host, Authentication = SmtpAuthentication.Basic, UserName = "nadzor@contoso.test", Password = new SecretField { NewValue = "x" } };

        AssertIssue(Validate(c with { Smtp = smtp }), ConfigValidationCodes.ExchangeOnlineBasic, "smtp.authentication", IssueSeverity.Warning);
    }

    [Fact]
    public void Warnings_RevocationOffAndTier0Exclusion()
    {
        var c = Valid();
        c = c with { Smtp = c.Smtp with { CheckRevocation = false } };
        c = WithRule(c, DefaultRules.Tier0Security, r => r with { Actors = r.Actors with { ExcludeActors = ["S-1-5-21-1004336348-1177238915-682003330-1601"] } });

        var result = Validate(c);

        Assert.True(result.IsValid);
        AssertIssue(result, ConfigValidationCodes.RevocationOff, "smtp.checkRevocation", IssueSeverity.Warning);
        AssertIssue(result, ConfigValidationCodes.Tier0Exclusion, "rules[1].exclusions", IssueSeverity.Warning);
    }

    [Fact]
    public void Warnings_InactiveLowAndPollFast()
    {
        var c = Valid();
        c = c with
        {
            Scans = c.Scans with { InactiveAccounts = c.Scans.InactiveAccounts with { Days = 44 } },
            Directory = c.Directory with { PollInterval = TimeSpan.FromSeconds(5) },
        };

        var result = Validate(c);

        Assert.True(result.IsValid);
        AssertIssue(result, ConfigValidationCodes.InactiveLow, "scans.inactiveAccounts.days", IssueSeverity.Warning);
        AssertIssue(result, ConfigValidationCodes.PollFast, "directory.pollInterval", IssueSeverity.Warning);
    }

    [Fact]
    public void Warnings_RolloutOffDependsOnTheBaselineAge()
    {
        var c = Valid() with { General = Valid().General with { RolloutDays = 0 } };
        var registry = new FakeHealthRegistry();

        registry.ReportDirectory(new DirectoryStatusDto { State = HealthState.Ok, BaselineCompletedAt = Now.AddDays(-3) });
        AssertIssue(Validate(c, registry: registry), ConfigValidationCodes.RolloutOff, "general.rolloutDays", IssueSeverity.Warning);

        registry.ReportDirectory(new DirectoryStatusDto { State = HealthState.Ok, BaselineCompletedAt = Now.AddDays(-8) });
        AssertNoIssue(Validate(c, registry: registry), ConfigValidationCodes.RolloutOff);

        AssertIssue(Validate(c), ConfigValidationCodes.RolloutOff, "general.rolloutDays", IssueSeverity.Warning);
        AssertNoIssue(Validate(Valid()), ConfigValidationCodes.RolloutOff);
    }

    [Fact]
    public void Validate_DoesNotModifyTheInput()
    {
        var c = Valid() with { Rules = [null!], Smtp = null! };

        _ = Validate(c);

        Assert.Null(c.Smtp);
        Assert.Null(Assert.Single(c.Rules));
    }
}
