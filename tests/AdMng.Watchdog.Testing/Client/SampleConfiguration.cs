using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using static AdMng.Watchdog.Testing.Client.SamplePrincipals;

namespace AdMng.Watchdog.Testing.Client;

/// <summary>
/// A configured watchdog (Polish sample data): the built-in rules of section 12.4 plus one custom rule, SMTP with a stored
/// password, a Teams and a webhook channel with stored secrets, two recipient groups and the daily digest.
/// </summary>
public static class SampleConfiguration
{
    public const string TeamsUrlHint = "https://default1a2b3c4d.12.environment.api.powerplatform.com/powerautomate/…";
    public const string WebhookUrlHint = "https://siem.contoso.com/hooks/…";

    /// <summary>The configuration as an Admin sees it (secrets as IsSet/Hint only).</summary>
    public static WatchdogConfiguration Create() => new()
    {
        General = new GeneralSettings { InstanceName = "AD Watchdog – Contoso", PublicBaseUrl = "https://admng-wd.contoso.com:8443" },
        Smtp = new SmtpSettings
        {
            Enabled = true,
            Host = "poczta.contoso.com",
            Port = 587,
            Security = SmtpSecurity.StartTls,
            Authentication = SmtpAuthentication.Basic,
            UserName = "watchdog@contoso.com",
            Password = new SecretField { IsSet = true },
            From = "AD Watchdog <watchdog@contoso.com>",
            ReplyTo = "helpdesk@contoso.com",
        },
        Channels =
        [
            new ChannelSettings
            {
                Id = "teams-it",
                Type = ChannelType.Teams,
                Name = "Teams – Administratorzy IT",
                Url = new SecretField { IsSet = true, Hint = TeamsUrlHint },
                DetailLevel = DetailLevel.Standard,
                Teams = new TeamsChannelOptions(),
            },
            new ChannelSettings
            {
                Id = "siem",
                Type = ChannelType.Webhook,
                Name = "SIEM (Splunk)",
                Url = new SecretField { IsSet = true, Hint = WebhookUrlHint },
                DetailLevel = DetailLevel.Minimal,
                Webhook = new WebhookChannelOptions
                {
                    SigningSecret = new SecretField { IsSet = true, Hint = "whsec_…Xw==" },
                    AllowedHosts = ["siem.contoso.com"],
                    HeartbeatInterval = TimeSpan.FromMinutes(15),
                },
                RateLimit = new RateLimitSettings { PerSecond = 10, PerMinute = 300, PerHour = 5000 },
            },
        ],
        RecipientGroups =
        [
            new RecipientGroup { Id = "it-admins", Name = "Administratorzy IT", Addresses = ["jan.kowalski@contoso.com", "zespol-it@contoso.com"] },
            new RecipientGroup { Id = "helpdesk", Name = "Helpdesk", Addresses = ["helpdesk@contoso.com"], DetailLevel = DetailLevel.Standard },
        ],
        Digest = new DigestSettings { RecipientGroupIds = ["it-admins"], ChannelIds = ["teams-it"] },
        Rules = CreateRules(),
    };

    /// <summary>The built-in rules (section 12.4) as shipped – the defaults of <c>GET /config/defaults</c>.</summary>
    public static WatchdogConfiguration CreateDefaults() => new()
    {
        RecipientGroups =
        [
            new RecipientGroup { Id = "it-admins", Name = "Administratorzy IT" },
            new RecipientGroup { Id = "helpdesk", Name = "Helpdesk", DetailLevel = DetailLevel.Standard },
        ],
        Rules = [.. CreateRules().Where(r => r.BuiltIn).Select(r => r with { Exclusions = [] })],
    };

    private static List<RuleSettings> CreateRules()
    {
        WatchEventKind[] tier0Security =
        [
            WatchEventKind.AdminCountSet, WatchEventKind.SidHistoryChanged, WatchEventKind.DelegationChanged,
            WatchEventKind.ServicePrincipalNameAdded, WatchEventKind.KeyCredentialChanged, WatchEventKind.PrimaryGroupChanged,
            WatchEventKind.SecurityDescriptorChanged, WatchEventKind.ComputerNameSpoofing, WatchEventKind.ManagedAccountLinkChanged,
            WatchEventKind.CertificateMappingChanged, WatchEventKind.ManagedPasswordReadersChanged, WatchEventKind.ServiceAccountCreated,
        ];
        WatchEventKind[] accountKinds = [.. EventKindCatalog.All.Where(i => i.Category == EventCategory.Account).Select(i => i.Kind)];
        WatchEventKind[] healthKinds = [.. EventKindCatalog.All.Where(i => i.IsHealth && i.Kind != WatchEventKind.Test).Select(i => i.Kind)];
        var itAdminsAlways = Immediate(["it-admins"], DeliveryHours.Always);
        var itAdmins = Immediate(["it-admins"], DeliveryHours.Default);

        return
        [
            BuiltIn("tier0-privilege", "Dostęp uprzywilejowany", [WatchEventKind.PrivilegeGranted, WatchEventKind.PrivilegeRevoked], itAdminsAlways),
            BuiltIn("tier0-security", "Atrybuty krytyczne dla bezpieczeństwa", tier0Security, itAdminsAlways) with
            {
                MinSeverity = Severity.High,
                Actors = new ActorFilter { ExcludeSelfChanges = true },
                Exclusions =
                [
                    new RuleExclusion
                    {
                        Id = "entra-connect",
                        Actor = DomainSid + "-1180",
                        Attribute = "msDS-KeyCredentialLink",
                        Reason = "Konto synchronizacji Entra Connect (Windows Hello for Business).",
                        CreatedBy = JanKowalski.Account!,
                        CreatedAt = new DateTimeOffset(2026, 9, 30, 9, 0, 0, TimeSpan.Zero),
                    },
                ],
            },
            BuiltIn("security-review", "Zmiany bezpieczeństwa do przeglądu", tier0Security, DigestOnly()),
            BuiltIn("tier0-domain", "Domena, zaufania, schemat, PKI, zasady",
            [
                WatchEventKind.DomainPolicyChanged, WatchEventKind.PasswordSettingsChanged, WatchEventKind.TrustChanged,
                WatchEventKind.DomainControllerAdded, WatchEventKind.DomainControllerRemoved, WatchEventKind.FsmoRoleChanged,
                WatchEventKind.CertificateTemplateChanged, WatchEventKind.SchemaChanged, WatchEventKind.GroupPolicyLinkChanged,
            ], itAdminsAlways) with { MinSeverity = Severity.High },
            BuiltIn("tier0-eventlog", "Dzienniki zabezpieczeń DC",
                [WatchEventKind.SecurityLogCleared, WatchEventKind.AuditPolicyChanged, WatchEventKind.DsrmPasswordChanged], itAdminsAlways),
            BuiltIn("bursts", "Zdarzenia masowe",
                [WatchEventKind.MassLockout, WatchEventKind.MassDeletion, WatchEventKind.PasswordSprayingSuspected], itAdmins),
            BuiltIn("privileged-accounts", "Konta uprzywilejowane i serwisowe",
                [.. accountKinds, WatchEventKind.PasswordExpiring, WatchEventKind.ObjectDeleted, WatchEventKind.ObjectMoved, WatchEventKind.ObjectRenamed],
                itAdminsAlways) with
            {
                AnyTags = [EventTags.Privileged, EventTags.ServiceAccount],
                MinSeverity = Severity.High,
            },
            BuiltIn("new-accounts", "Nowe konta", [WatchEventKind.UserCreated, WatchEventKind.ServiceAccountCreated],
                itAdmins with { AggregationWindow = TimeSpan.FromMinutes(5) }),
            BuiltIn("new-computers", "Nowe komputery", [WatchEventKind.ComputerCreated], DigestOnly()),
            BuiltIn("computers-unusual", "Komputery spoza procesu", [WatchEventKind.ComputerCreated], itAdmins) with
            {
                AnyTags = [EventTags.CreatedByNonAdmin, EventTags.UnexpectedContainer, EventTags.Server],
            },
            BuiltIn("deletions", "Usunięcia i przywrócenia",
                [WatchEventKind.ObjectDeleted, WatchEventKind.ObjectRestored, WatchEventKind.ObjectDisappeared], itAdmins),
            BuiltIn("account-status", "Włączenia i wyłączenia kont", [WatchEventKind.AccountDisabled, WatchEventKind.AccountEnabled], DigestOnly()),
            BuiltIn("lockouts", "Blokady kont", [WatchEventKind.AccountLockedOut], new RuleDelivery
            {
                Immediate = true,
                RecipientGroupIds = ["helpdesk"],
                Hours = DeliveryHours.BusinessHours,
                AggregationWindow = TimeSpan.FromMinutes(5),
                MaxMessagesPerHour = 10,
                RepeatSuppression = TimeSpan.FromHours(1),
            }) with
            {
                Exclusions =
                [
                    new RuleExclusion
                    {
                        Id = "kiosk-recepcja",
                        ObjectGuid = Guid.Parse("4a8e1f42-3c51-4f0e-9b7a-2d1c6e5f8f01"),
                        Reason = "Konto kiosku w recepcji – blokady zgłasza ochrona.",
                        ExpiresAt = new DateTimeOffset(2026, 12, 31, 23, 0, 0, TimeSpan.Zero),
                        CreatedBy = AnnaNowak.Account!,
                        CreatedAt = new DateTimeOffset(2026, 10, 14, 10, 0, 0, TimeSpan.Zero),
                    },
                ],
            },
            BuiltIn("unlocks", "Odblokowania", [WatchEventKind.AccountUnlocked, WatchEventKind.LockoutExpired], DigestOnly()),
            BuiltIn("passwords", "Hasła", [WatchEventKind.PasswordChanged, WatchEventKind.PasswordMustChangeSet], DigestOnly()),
            BuiltIn("account-flags", "Flagi konta", [WatchEventKind.AccountFlagsChanged, WatchEventKind.PasswordAgeReset], itAdmins) with
            {
                MinSeverity = Severity.Medium,
            },
            BuiltIn("account-names", "Zmiany nazw kont", [WatchEventKind.AccountNameChanged], itAdmins),
            BuiltIn("logon-settings", "Skrypty, profile, ograniczenia logowania", [WatchEventKind.AttributesChanged], itAdmins) with
            {
                Attributes = ["scriptPath", "profilePath", "homeDirectory", "homeDrive", "userWorkstations", "logonHours"],
                Severity = Severity.Medium,
            },
            BuiltIn("account-attributes", "Zmiany parametrów kont",
            [
                WatchEventKind.AttributesChanged, WatchEventKind.AccountExpirationChanged, WatchEventKind.ObjectMoved,
                WatchEventKind.ObjectRenamed, WatchEventKind.ObjectAppeared,
            ], DigestOnly()),
            BuiltIn("group-changes", "Grupy i członkostwo",
            [
                WatchEventKind.GroupMemberAdded, WatchEventKind.GroupMemberRemoved, WatchEventKind.GroupCreated, WatchEventKind.GroupTypeChanged,
            ], DigestOnly()),
            BuiltIn("watchlist-groups", "Grupy obserwowane", [WatchEventKind.GroupMemberAdded, WatchEventKind.GroupMemberRemoved], itAdmins) with
            {
                Groups = GroupFilter.WatchList,
                Severity = Severity.Medium,
            },
            BuiltIn("ou-gpo", "OU i GPO",
                [WatchEventKind.OrganizationalUnitCreated, WatchEventKind.GroupPolicyLinkChanged, WatchEventKind.GroupPolicyChanged], DigestOnly()),
            BuiltIn("expiry", "Terminy ważności",
                [WatchEventKind.AccountExpiring, WatchEventKind.AccountExpired, WatchEventKind.PasswordExpiring],
                Immediate(["helpdesk"], DeliveryHours.BusinessHours) with { AggregationWindow = TimeSpan.FromMinutes(60) }),
            BuiltIn("health", "Stan usługi Watchdog", healthKinds, itAdminsAlways with { AllEnabledChannels = true }) with { Locked = true },
            new RuleSettings
            {
                Id = "oddzial-krakow",
                Name = "Oddział Kraków – zmiany kont",
                Description = "Zmiany kont i grup w jednostkach oddziału krakowskiego (dla kierownika oddziału).",
                Kinds = [WatchEventKind.UserCreated, WatchEventKind.AccountDisabled, WatchEventKind.GroupMemberAdded, WatchEventKind.GroupMemberRemoved],
                ObjectKinds = [WatchObjectKind.User, WatchObjectKind.Group],
                Scope = new RuleScope { IncludeSubtrees = ["OU=Kraków,OU=Firma," + DomainDn] },
                Delivery = new RuleDelivery { Immediate = true, RecipientGroupIds = ["helpdesk"], ChannelIds = ["teams-it"], Hours = DeliveryHours.BusinessHours },
            },
        ];
    }

    private static RuleSettings BuiltIn(string id, string name, IReadOnlyList<WatchEventKind> kinds, RuleDelivery delivery) => new()
    {
        Id = id,
        Name = name,
        BuiltIn = true,
        Kinds = kinds,
        Delivery = delivery,
    };

    private static RuleDelivery Immediate(IReadOnlyList<string> groups, DeliveryHours hours) =>
        new() { Immediate = true, RecipientGroupIds = groups, Hours = hours };

    private static RuleDelivery DigestOnly() => new() { Immediate = false, IncludeInDigest = true };
}
