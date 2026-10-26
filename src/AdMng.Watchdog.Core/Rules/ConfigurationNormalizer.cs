using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Core.Rules;

/// <summary>
/// Makes a configuration document safe to evaluate: a section stored as <c>null</c> takes the record defaults, a list
/// stored as <c>null</c> counts as an empty list (as in <see cref="Configuration.ConfigChangeAuditor"/>), nested
/// records stored as <c>null</c> take their defaults. Null entries of the top-level lists (channels, recipient groups,
/// rules) are kept – the validator reports them (<c>required</c>) and the provider drops them.
/// </summary>
/// <remarks>Properties missing from the JSON already keep their record defaults (<c>WatchdogJson.Options</c>); this
/// only repairs explicit nulls. The input is not modified.</remarks>
public static class ConfigurationNormalizer
{
    /// <summary>The normalized copy of <paramref name="configuration"/>.</summary>
    public static WatchdogConfiguration Normalize(WatchdogConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration with
        {
            General = configuration.General ?? new GeneralSettings(),
            Directory = Directory(configuration.Directory ?? new DirectorySettings()),
            Attribution = Attribution(configuration.Attribution ?? new AttributionSettings()),
            Scans = Scans(configuration.Scans ?? new ScanSettings()),
            PrivilegedGroups = PrivilegedGroups(configuration.PrivilegedGroups ?? new PrivilegedGroupSettings()),
            BusinessHours = BusinessHours(configuration.BusinessHours ?? new BusinessHoursSettings()),
            Notifications = configuration.Notifications ?? new NotificationSettings(),
            Smtp = Smtp(configuration.Smtp ?? new SmtpSettings()),
            Channels = [.. (configuration.Channels ?? []).Select(c => c is null ? null! : Channel(c))],
            RecipientGroups = [.. (configuration.RecipientGroups ?? []).Select(g => g is null ? null! : g with { Addresses = List(g.Addresses) })],
            Digest = Digest(configuration.Digest ?? new DigestSettings()),
            Rules = [.. (configuration.Rules ?? []).Select(r => r is null ? null! : Rule(r))],
            Retention = configuration.Retention ?? new RetentionSettings(),
            UserMessages = configuration.UserMessages is { } userMessages
                ? userMessages with { AllowedSenders = List(userMessages.AllowedSenders) }
                : new UserMessagingSettings(),
        };
    }

    /// <summary>A normalized rule (null nested records and lists repaired).</summary>
    public static RuleSettings Rule(RuleSettings rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var scope = rule.Scope ?? new RuleScope();
        var actors = rule.Actors ?? new ActorFilter();
        var delivery = rule.Delivery ?? new RuleDelivery();
        return rule with
        {
            Kinds = List(rule.Kinds),
            ObjectKinds = List(rule.ObjectKinds),
            Scope = scope with { IncludeSubtrees = List(scope.IncludeSubtrees), ExcludeSubtrees = List(scope.ExcludeSubtrees) },
            Attributes = List(rule.Attributes),
            RequiredTags = List(rule.RequiredTags),
            AnyTags = List(rule.AnyTags),
            GroupDns = List(rule.GroupDns),
            Actors = actors with { ExcludeActors = List(actors.ExcludeActors) },
            Delivery = delivery with { RecipientGroupIds = List(delivery.RecipientGroupIds), ChannelIds = List(delivery.ChannelIds) },
            Exclusions = List(rule.Exclusions),
        };
    }

    private static DirectorySettings Directory(DirectorySettings d) => d with
    {
        WakeUpContainers = List(d.WakeUpContainers),
        ExcludedSubtrees = List(d.ExcludedSubtrees),
        CriticalObjects = List(d.CriticalObjects),
        ServiceAccountSubtrees = List(d.ServiceAccountSubtrees),
        DisabledAccountsSubtrees = List(d.DisabledAccountsSubtrees),
        AdditionalWatchedAttributes = List(d.AdditionalWatchedAttributes),
        IgnoredAttributes = List(d.IgnoredAttributes),
        EnabledOptionalKinds = List(d.EnabledOptionalKinds),
        DisabledKinds = List(d.DisabledKinds),
    };

    private static AttributionSettings Attribution(AttributionSettings a) => a with
    {
        DomainControllers = List(a.DomainControllers),
        PendingRetries = List(a.PendingRetries),
    };

    private static ScanSettings Scans(ScanSettings s)
    {
        var accountExpiry = s.AccountExpiry ?? new AccountExpiryScan();
        var passwordExpiry = s.PasswordExpiry ?? new PasswordExpiryScan();
        return s with
        {
            AccountExpiry = accountExpiry with { ThresholdsDays = List(accountExpiry.ThresholdsDays) },
            PasswordExpiry = passwordExpiry with { ThresholdsDays = List(passwordExpiry.ThresholdsDays) },
            InactiveAccounts = s.InactiveAccounts ?? new InactiveAccountsScan(),
            MassLockout = s.MassLockout ?? new ScanSettings().MassLockout,
            MassDeletion = s.MassDeletion ?? new ScanSettings().MassDeletion,
            MassComputerDeletion = s.MassComputerDeletion ?? new ScanSettings().MassComputerDeletion,
            PasswordSpray = s.PasswordSpray ?? new PasswordSprayDetection(),
        };
    }

    private static PrivilegedGroupSettings PrivilegedGroups(PrivilegedGroupSettings p) => p with
    {
        CriticalSids = List(p.CriticalSids),
        HighSids = List(p.HighSids),
        HighSamAccountNames = List(p.HighSamAccountNames),
        AdditionalCriticalDns = List(p.AdditionalCriticalDns),
        AdditionalHighDns = List(p.AdditionalHighDns),
        ProtectRemovalSids = List(p.ProtectRemovalSids),
        WatchListDns = List(p.WatchListDns),
    };

    private static BusinessHoursSettings BusinessHours(BusinessHoursSettings b) => b with
    {
        Days = List(b.Days),
        ExtraHolidays = List(b.ExtraHolidays),
    };

    private static SmtpSettings Smtp(SmtpSettings s)
    {
        var oauth = s.OAuth ?? new SmtpOAuthSettings();
        return s with
        {
            Password = s.Password ?? new SecretField(),
            OAuth = oauth with { ClientSecret = oauth.ClientSecret ?? new SecretField() },
        };
    }

    private static ChannelSettings Channel(ChannelSettings c) => c with
    {
        Url = c.Url ?? new SecretField(),
        RateLimit = c.RateLimit ?? new RateLimitSettings(),
        Teams = c.Teams is { } teams ? teams with { ClientSecret = teams.ClientSecret ?? new SecretField() } : null,
        Webhook = c.Webhook is { } webhook
            ? webhook with
            {
                SigningSecret = webhook.SigningSecret ?? new SecretField(),
                PreviousSigningSecret = webhook.PreviousSigningSecret ?? new SecretField(),
                AllowedHosts = List(webhook.AllowedHosts),
            }
            : null,
    };

    private static DigestSettings Digest(DigestSettings d) => d with
    {
        Days = List(d.Days),
        RecipientGroupIds = List(d.RecipientGroupIds),
        ChannelIds = List(d.ChannelIds),
    };

    private static IReadOnlyList<T> List<T>(IReadOnlyList<T>? list) => list ?? [];
}
