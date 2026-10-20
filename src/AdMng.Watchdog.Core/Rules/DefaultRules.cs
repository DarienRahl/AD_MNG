using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Rules;

/// <summary>
/// The built-in rules shipped with the service (section 12.4) and the merge of built-in rules a newer service version
/// adds (8.2: missing ids are appended in memory, existing rules are never overwritten).
/// </summary>
public static class DefaultRules
{
    public const string Tier0Privilege = "tier0-privilege";
    public const string Tier0Security = "tier0-security";
    public const string SecurityReview = "security-review";
    public const string Tier0Domain = "tier0-domain";
    public const string Tier0EventLog = "tier0-eventlog";
    public const string Bursts = "bursts";
    public const string PrivilegedAccounts = "privileged-accounts";
    public const string NewAccounts = "new-accounts";
    public const string NewComputers = "new-computers";
    public const string ComputersUnusual = "computers-unusual";
    public const string Deletions = "deletions";
    public const string AccountStatus = "account-status";
    public const string Lockouts = "lockouts";
    public const string Unlocks = "unlocks";
    public const string Passwords = "passwords";
    public const string AccountFlags = "account-flags";
    public const string AccountNames = "account-names";
    public const string LogonSettings = "logon-settings";
    public const string AccountAttributes = "account-attributes";
    public const string GroupChanges = "group-changes";
    public const string WatchListGroups = "watchlist-groups";
    public const string OuGpo = "ou-gpo";
    public const string Expiry = "expiry";
    public const string Health = "health";

    // Built-in rules of stage B (docs/dev/watchdog-linux-design.md 9.1–9.3, 8.6, 14.2), shipped after the stage A rules
    // (an existing document gets them appended by MergeMissing in the same order). watchdog-self has no "tier0-" prefix
    // but is a Tier-0 rule (Tier0Catalog.IsTier0Rule: immediate in rollout mode); all four can be disabled or weakened
    // only with AllowTier0Suppression (Tier0Catalog.SuppressionProtectedRules).
    public const string Tier0BadSuccessor = "tier0-badsuccessor";
    public const string Tier0MachineQuota = "tier0-machine-quota";
    public const string WatchdogSelf = "watchdog-self";
    public const string Tier0Gpo = "tier0-gpo";

    /// <summary>Recipient group of the administrators (default document, 8.1).</summary>
    public const string ItAdminsGroup = "it-admins";

    /// <summary>Recipient group of the helpdesk (default document, 8.1).</summary>
    public const string HelpdeskGroup = "helpdesk";

    /// <summary>The kinds of <see cref="Tier0Security"/> (also matched by <see cref="SecurityReview"/>).</summary>
    public static IReadOnlyList<WatchEventKind> SecurityKinds { get; } =
    [
        WatchEventKind.AdminCountSet, WatchEventKind.SidHistoryChanged, WatchEventKind.DelegationChanged,
        WatchEventKind.ServicePrincipalNameAdded, WatchEventKind.KeyCredentialChanged, WatchEventKind.PrimaryGroupChanged,
        WatchEventKind.SecurityDescriptorChanged, WatchEventKind.ComputerNameSpoofing, WatchEventKind.ManagedAccountLinkChanged,
        WatchEventKind.CertificateMappingChanged, WatchEventKind.ManagedPasswordReadersChanged, WatchEventKind.ServiceAccountCreated,
    ];

    /// <summary>The GPO content kinds of <see cref="Tier0Gpo"/> (design 8.6).</summary>
    public static IReadOnlyList<WatchEventKind> GpoContentKinds { get; } =
        [WatchEventKind.GroupPolicySettingsChanged, WatchEventKind.GroupPolicyPathRedirected, WatchEventKind.GroupPolicyContentOutOfBand];

    /// <summary>The logon-settings attributes (<see cref="LogonSettings"/>, 12.4).</summary>
    public static IReadOnlyList<string> LogonSettingsAttributes { get; } =
        ["scriptPath", "profilePath", "homeDirectory", "homeDrive", "userWorkstations", "logonHours"];

    private static readonly WatchEventKind[] AccountKinds = [.. EventKindCatalog.All
        .Where(k => k.Category == EventCategory.Account)
        .Select(k => k.Kind)];

    private static readonly WatchEventKind[] HealthKinds = [.. EventKindCatalog.All
        .Where(k => k.IsHealth && k.Kind != WatchEventKind.Test)
        .Select(k => k.Kind)];

    /// <summary>A fresh list of every built-in rule in shipping order (new instances on every call).</summary>
    public static IReadOnlyList<RuleSettings> Create() =>
    [
        Rule(Tier0Privilege, "Dostęp uprzywilejowany", [WatchEventKind.PrivilegeGranted, WatchEventKind.PrivilegeRevoked]) with
        {
            Delivery = Immediate(ItAdminsGroup, DeliveryHours.Always),
        },
        Rule(Tier0Security, "Atrybuty krytyczne dla bezpieczeństwa", SecurityKinds) with
        {
            MinSeverity = Severity.High,
            Actors = new ActorFilter { ExcludeSelfChanges = true },
            Delivery = Immediate(ItAdminsGroup, DeliveryHours.Always),
        },
        Rule(SecurityReview, "Zmiany bezpieczeństwa do przeglądu", SecurityKinds) with { Delivery = DigestOnly() },
        Rule(Tier0Domain, "Domena, zaufania, schemat, PKI, zasady",
        [
            WatchEventKind.DomainPolicyChanged, WatchEventKind.PasswordSettingsChanged, WatchEventKind.TrustChanged,
            WatchEventKind.DomainControllerAdded, WatchEventKind.DomainControllerRemoved, WatchEventKind.FsmoRoleChanged,
            WatchEventKind.CertificateTemplateChanged, WatchEventKind.SchemaChanged, WatchEventKind.GroupPolicyLinkChanged,
        ]) with
        {
            MinSeverity = Severity.High,
            Delivery = Immediate(ItAdminsGroup, DeliveryHours.Always),
        },
        Rule(Tier0EventLog, "Dzienniki zabezpieczeń DC",
            [WatchEventKind.SecurityLogCleared, WatchEventKind.AuditPolicyChanged, WatchEventKind.DsrmPasswordChanged]) with
        {
            Delivery = Immediate(ItAdminsGroup, DeliveryHours.Always),
        },
        Rule(Bursts, "Zdarzenia masowe",
            [WatchEventKind.MassLockout, WatchEventKind.MassDeletion, WatchEventKind.PasswordSprayingSuspected]) with
        {
            Delivery = Immediate(ItAdminsGroup, DeliveryHours.Default),
        },
        Rule(PrivilegedAccounts, "Konta uprzywilejowane i serwisowe",
            [.. AccountKinds, WatchEventKind.PasswordExpiring, WatchEventKind.ObjectDeleted, WatchEventKind.ObjectMoved, WatchEventKind.ObjectRenamed]) with
        {
            AnyTags = [EventTags.Privileged, EventTags.ServiceAccount],
            MinSeverity = Severity.High,
            Delivery = Immediate(ItAdminsGroup, DeliveryHours.Always),
        },
        Rule(NewAccounts, "Nowe konta", [WatchEventKind.UserCreated, WatchEventKind.ServiceAccountCreated]) with
        {
            Delivery = Immediate(ItAdminsGroup, DeliveryHours.Default) with { AggregationWindow = TimeSpan.FromMinutes(5) },
        },
        Rule(NewComputers, "Nowe komputery", [WatchEventKind.ComputerCreated]) with { Delivery = DigestOnly() },
        Rule(ComputersUnusual, "Komputery spoza procesu", [WatchEventKind.ComputerCreated]) with
        {
            AnyTags = [EventTags.CreatedByNonAdmin, EventTags.UnexpectedContainer, EventTags.Server],
            Delivery = Immediate(ItAdminsGroup, DeliveryHours.Default),
        },
        Rule(Deletions, "Usunięcia i przywrócenia",
            [WatchEventKind.ObjectDeleted, WatchEventKind.ObjectRestored, WatchEventKind.ObjectDisappeared]) with
        {
            Delivery = Immediate(ItAdminsGroup, DeliveryHours.Default),
        },
        Rule(AccountStatus, "Włączenia i wyłączenia kont", [WatchEventKind.AccountDisabled, WatchEventKind.AccountEnabled]) with
        {
            Delivery = DigestOnly(),
        },
        Rule(Lockouts, "Blokady kont", [WatchEventKind.AccountLockedOut]) with
        {
            Delivery = Immediate(HelpdeskGroup, DeliveryHours.BusinessHours) with
            {
                AggregationWindow = TimeSpan.FromMinutes(5),
                MaxMessagesPerHour = 10,
                RepeatSuppression = TimeSpan.FromHours(1),
            },
        },
        Rule(Unlocks, "Odblokowania", [WatchEventKind.AccountUnlocked, WatchEventKind.LockoutExpired]) with { Delivery = DigestOnly() },
        Rule(Passwords, "Hasła", [WatchEventKind.PasswordChanged, WatchEventKind.PasswordMustChangeSet]) with { Delivery = DigestOnly() },
        Rule(AccountFlags, "Flagi konta", [WatchEventKind.AccountFlagsChanged, WatchEventKind.PasswordAgeReset]) with
        {
            MinSeverity = Severity.Medium,
            Delivery = Immediate(ItAdminsGroup, DeliveryHours.Default),
        },
        Rule(AccountNames, "Zmiany nazw kont", [WatchEventKind.AccountNameChanged]) with
        {
            Delivery = Immediate(ItAdminsGroup, DeliveryHours.Default),
        },
        Rule(LogonSettings, "Skrypty, profile, ograniczenia logowania", [WatchEventKind.AttributesChanged]) with
        {
            Attributes = [.. LogonSettingsAttributes],
            Severity = Severity.Medium,
            Delivery = Immediate(ItAdminsGroup, DeliveryHours.Default),
        },
        Rule(AccountAttributes, "Zmiany parametrów kont",
        [
            WatchEventKind.AttributesChanged, WatchEventKind.AccountExpirationChanged, WatchEventKind.ObjectMoved,
            WatchEventKind.ObjectRenamed, WatchEventKind.ObjectAppeared,
        ]) with
        {
            Delivery = DigestOnly(),
        },
        Rule(GroupChanges, "Grupy i członkostwo",
        [
            WatchEventKind.GroupMemberAdded, WatchEventKind.GroupMemberRemoved, WatchEventKind.GroupCreated,
            WatchEventKind.GroupTypeChanged,
        ]) with
        {
            Delivery = DigestOnly(),
        },
        Rule(WatchListGroups, "Grupy obserwowane", [WatchEventKind.GroupMemberAdded, WatchEventKind.GroupMemberRemoved]) with
        {
            Groups = GroupFilter.WatchList,
            Severity = Severity.Medium,
            Delivery = Immediate(ItAdminsGroup, DeliveryHours.Default),
        },
        Rule(OuGpo, "OU i GPO",
        [
            WatchEventKind.OrganizationalUnitCreated, WatchEventKind.GroupPolicyLinkChanged, WatchEventKind.GroupPolicyChanged,
            WatchEventKind.GroupPolicySettingsChanged, WatchEventKind.GroupPolicyPathRedirected, WatchEventKind.GroupPolicyContentOutOfBand,
        ]) with
        {
            Delivery = DigestOnly(),
        },
        Rule(Expiry, "Terminy ważności",
            [WatchEventKind.AccountExpiring, WatchEventKind.AccountExpired, WatchEventKind.PasswordExpiring]) with
        {
            Delivery = Immediate(HelpdeskGroup, DeliveryHours.BusinessHours) with
            {
                AggregationWindow = TimeSpan.FromMinutes(60),
                NotifyManager = false,
            },
        },
        Rule(Health, "Stan usługi Watchdog", HealthKinds) with
        {
            Locked = true,
            Delivery = Immediate(ItAdminsGroup, DeliveryHours.Always) with { AllEnabledChannels = true },
        },

        // Stage B (design 9.1–9.3, 8.6): immediate around the clock, no aggregation of their own.
        Rule(Tier0BadSuccessor, "BadSuccessor – konta dMSA", [WatchEventKind.ServiceAccountCreated, WatchEventKind.ManagedAccountLinkChanged]) with
        {
            RequiredTags = [EventTags.BadSuccessor],
            MinSeverity = Severity.Critical,
            Delivery = Tier0Immediate(),
        },
        Rule(Tier0MachineQuota, "Limit kont komputerów (MachineAccountQuota)", [WatchEventKind.DomainPolicyChanged]) with
        {
            RequiredTags = [EventTags.MachineAccountQuota],
            MinSeverity = Severity.High,
            Delivery = Tier0Immediate(),
        },
        Rule(WatchdogSelf, "Konto usługi Watchdog", []) with
        {
            RequiredTags = [EventTags.WatchdogAccount],
            MinSeverity = Severity.Info,
            Severity = Severity.Critical,
            Delivery = Tier0Immediate(),
        },
        Rule(Tier0Gpo, "Treść GPO", GpoContentKinds) with
        {
            MinSeverity = Severity.High,
            Delivery = Tier0Immediate(),
        },
    ];

    /// <summary>The ids of every built-in rule in shipping order.</summary>
    public static IReadOnlyList<string> Ids { get; } = [.. Create().Select(r => r.Id)];

    /// <summary>A fresh copy of the built-in rule <paramref name="id"/> (case-insensitive), or null.</summary>
    public static RuleSettings? Find(string? id) =>
        id is null ? null : Create().FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Section 8.2: <paramref name="stored"/> unchanged (it is authoritative, also for disabled or edited built-in
    /// rules) followed by every built-in rule whose id is missing, with <c>Enabled</c> as shipped. Null entries of a
    /// broken stored document are dropped. Exception: the kinds of a stored locked built-in rule (<c>health</c>) are the
    /// shipped ones – they cannot be edited, and a document stored by an older version (stage A: no
    /// <c>HealthServiceAccount</c>) would otherwise neither match the new health kinds nor pass the validator's
    /// <c>lockedRule</c> check on its next save.
    /// </summary>
    public static IReadOnlyList<RuleSettings> MergeMissing(IReadOnlyList<RuleSettings>? stored)
    {
        var shipped = Create();
        var locked = shipped.Where(r => r.Locked).ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        var rules = (stored ?? []).Where(r => r is not null)
            .Select(r => r.Locked && locked.TryGetValue(r.Id ?? string.Empty, out var builtIn) && !(r.Kinds ?? []).ToHashSet().SetEquals(builtIn.Kinds)
                ? r with { Kinds = [.. builtIn.Kinds] }
                : r)
            .ToList();
        var ids = new HashSet<string>(rules.Select(r => r.Id ?? string.Empty), StringComparer.OrdinalIgnoreCase);
        rules.AddRange(shipped.Where(r => !ids.Contains(r.Id)));
        return rules;
    }

    private static RuleSettings Rule(string id, string name, IReadOnlyList<WatchEventKind> kinds) => new()
    {
        Id = id,
        Name = name,
        BuiltIn = true,
        Kinds = [.. kinds],
    };

    private static RuleDelivery Immediate(string recipientGroupId, DeliveryHours hours) => new()
    {
        Immediate = true,
        RecipientGroupIds = [recipientGroupId],
        Hours = hours,
        IncludeInDigest = true,
    };

    private static RuleDelivery DigestOnly() => new() { Immediate = false, IncludeInDigest = true };

    /// <summary>Immediate to it-admins, Always, without an aggregation window of the rule (stage B rules).</summary>
    private static RuleDelivery Tier0Immediate() => Immediate(ItAdminsGroup, DeliveryHours.Always) with { AggregationWindow = TimeSpan.Zero };
}
