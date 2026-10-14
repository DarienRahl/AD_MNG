using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Tests.Configuration;

/// <summary>A filled-in configuration (Polish data) as an Admin would have it: SMTP with Basic auth, a Teams channel
/// with Entra client credentials, a signed webhook, tier0/health/lockout rules and exclusions.</summary>
internal static class SampleConfiguration
{
    public const string TenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47";
    public const string ClientId = "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0";
    public const string TeamsUrlHint = "https://default72f988bf86f141af91ab2d7cd011db.47.environment.api.powerplatform.com/powerautomate/…";
    public const string SiemUrlHint = "https://siem.contoso.test/hooks/…";
    public const string AdminAddress = "łukasz.wiśniewski@contoso.test";
    public const string HelpdeskAddress = "helpdesk.łódź@contoso.test";
    public const string ExclusionReason = "Synchronizacja Entra Connect – zatwierdził Łukasz Wiśniewski";
    public const string ExclusionActorSid = "S-1-5-21-1004336348-1177238915-682003330-1601";
    public const string ExclusionSubtree = "OU=Serwery,OU=Kraków,DC=contoso,DC=test";

    public static readonly DateTimeOffset Created = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    public static WatchdogConfiguration Create() => new()
    {
        General = new GeneralSettings { InstanceName = "Nadzór AD – Kraków", PublicBaseUrl = "https://admng-wd.contoso.test:8443" },
        Smtp = new SmtpSettings
        {
            Enabled = true,
            Host = "smtp.contoso.test",
            Port = 587,
            Authentication = SmtpAuthentication.Basic,
            UserName = "svc-wd@contoso.test",
            Password = new SecretField { IsSet = true },
            From = "Nadzór AD <nadzor@contoso.test>",
            ReplyTo = "zespół.it@contoso.test",
            LocalDomain = "admng-wd.contoso.test",
            OAuth = new SmtpOAuthSettings
            {
                TenantId = TenantId,
                ClientId = ClientId,
                CertificateThumbprint = "A1B2C3D4E5F60718293A4B5C6D7E8F9012345678",
                ClientSecret = new SecretField { IsSet = true, Hint = "…abcd" },
            },
        },
        Channels =
        [
            new ChannelSettings
            {
                Id = "teams-it",
                Type = ChannelType.Teams,
                Name = "Teams – Administratorzy IT",
                Url = new SecretField { IsSet = true, Hint = TeamsUrlHint },
                Teams = new TeamsChannelOptions
                {
                    AuthMode = TeamsAuthMode.EntraClientCredentials,
                    TenantId = TenantId,
                    ClientId = ClientId,
                    CertificateThumbprint = "00112233445566778899AABBCCDDEEFF00112233",
                    ClientSecret = new SecretField { IsSet = true, Hint = "…wxyz" },
                },
            },
            new ChannelSettings
            {
                Id = "siem",
                Type = ChannelType.Webhook,
                Name = "SIEM – Łódź",
                DetailLevel = DetailLevel.Minimal,
                Url = new SecretField { IsSet = true, Hint = SiemUrlHint },
                Webhook = new WebhookChannelOptions
                {
                    SigningSecret = new SecretField { IsSet = true, Hint = "whsec_…Xw==" },
                    AllowedHosts = ["siem.contoso.test"],
                },
            },
        ],
        RecipientGroups =
        [
            new RecipientGroup { Id = "it-admins", Name = "Administratorzy IT", Addresses = [AdminAddress, "zofia.żak@contoso.test"] },
            new RecipientGroup { Id = "helpdesk", Name = "Helpdesk Łódź", Addresses = [HelpdeskAddress], DetailLevel = DetailLevel.Standard },
        ],
        Rules =
        [
            new RuleSettings
            {
                Id = "tier0-security",
                Name = "Atrybuty krytyczne dla bezpieczeństwa",
                BuiltIn = true,
                Kinds = [WatchEventKind.AdminCountSet, WatchEventKind.SidHistoryChanged, WatchEventKind.KeyCredentialChanged],
                MinSeverity = Severity.High,
                Actors = new ActorFilter { ExcludeSelfChanges = true },
                Delivery = new RuleDelivery { Immediate = true, RecipientGroupIds = ["it-admins"], Hours = DeliveryHours.Always },
            },
            new RuleSettings
            {
                Id = "lockouts",
                Name = "Blokady kont",
                BuiltIn = true,
                Kinds = [WatchEventKind.AccountLockedOut],
                Delivery = new RuleDelivery
                {
                    Immediate = true,
                    RecipientGroupIds = ["helpdesk"],
                    Hours = DeliveryHours.BusinessHours,
                    RepeatSuppression = TimeSpan.FromHours(1),
                },
                Exclusions =
                [
                    new RuleExclusion
                    {
                        Id = "konto-testowe",
                        SubtreeDn = ExclusionSubtree,
                        Actor = ExclusionActorSid,
                        Reason = ExclusionReason,
                        CreatedBy = "CONTOSO\\adm.jkowalska",
                        CreatedAt = Created,
                    },
                ],
            },
            new RuleSettings
            {
                Id = "health",
                Name = "Stan usługi Watchdog",
                BuiltIn = true,
                Locked = true,
                Delivery = new RuleDelivery
                {
                    Immediate = true,
                    RecipientGroupIds = ["it-admins"],
                    AllEnabledChannels = true,
                    Hours = DeliveryHours.Always,
                },
            },
        ],
    };
}
