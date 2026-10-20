using System.Collections.Frozen;

namespace AdMng.Watchdog.Contracts.Events;

/// <summary>Catalog defaults of one <see cref="WatchEventKind"/> (section 7 of the watchdog specification).</summary>
public sealed record EventKindInfo
{
    public required WatchEventKind Kind { get; init; }
    public required EventCategory Category { get; init; }
    public required Severity DefaultSeverity { get; init; }
    /// <summary>False: the kind is not recorded unless enabled in Directory.EnabledOptionalKinds.</summary>
    public required bool RecordedByDefault { get; init; }
    /// <summary>Object kinds the event is raised for; empty = any kind.</summary>
    public IReadOnlyList<WatchObjectKind> ObjectKinds { get; init; } = [];
    public bool NeedsSecurityLog { get; init; }       // event-log-only kinds
    public bool IsHealth { get; init; }
}

/// <summary>The defaults table of every <see cref="WatchEventKind"/> (section 7). Tag bumps, attribution-dependent
/// severities and explicit overrides are applied on top of it by the SeverityResolver (section 12.2).</summary>
public static class EventKindCatalog
{
    private static readonly WatchObjectKind[] Accounts = [WatchObjectKind.User, WatchObjectKind.Computer, WatchObjectKind.ServiceAccount];

    private static readonly WatchObjectKind[] Principals =
        [WatchObjectKind.User, WatchObjectKind.Computer, WatchObjectKind.Group, WatchObjectKind.ServiceAccount];

    private static readonly WatchObjectKind[] Snapshotted =
    [
        WatchObjectKind.User, WatchObjectKind.Computer, WatchObjectKind.Group, WatchObjectKind.OrganizationalUnit,
        WatchObjectKind.Contact, WatchObjectKind.ServiceAccount,
    ];

    private static readonly WatchObjectKind[] Deletable =
    [
        .. Snapshotted, WatchObjectKind.GroupPolicy, WatchObjectKind.Trust, WatchObjectKind.PasswordSettings,
    ];

    private static readonly WatchObjectKind[] Movable = [.. Snapshotted, WatchObjectKind.Container];

    private static readonly WatchObjectKind[] WatchdogOnly = [WatchObjectKind.Watchdog];

    public static IReadOnlyList<EventKindInfo> All { get; } = Build();

    private static readonly FrozenDictionary<WatchEventKind, EventKindInfo> ByKind = All.ToFrozenDictionary(i => i.Kind);

    /// <summary>The catalog entry of <paramref name="kind"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined kind.</exception>
    public static EventKindInfo Get(WatchEventKind kind) =>
        ByKind.TryGetValue(kind, out var info)
            ? info
            : throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown watch event kind.");

    /// <summary>Default severity of ObjectDeleted etc. depends on the object kind (section 7).</summary>
    /// <remarks>ObjectDeleted: users, groups, service accounts, GPOs, trusts and PSOs Medium, computers Low, contacts
    /// Info, OUs High. MassDeletion: computers/contacts High, otherwise Critical. GroupPolicyLinkChanged: High on the
    /// domain head, otherwise Medium (the Domain Controllers OU bump is the SeverityResolver's). Every other kind:
    /// <see cref="EventKindInfo.DefaultSeverity"/>.</remarks>
    public static Severity DefaultSeverity(WatchEventKind kind, WatchObjectKind objectKind) => kind switch
    {
        WatchEventKind.ObjectDeleted => objectKind switch
        {
            WatchObjectKind.Computer => Severity.Low,
            WatchObjectKind.Contact => Severity.Info,
            WatchObjectKind.OrganizationalUnit => Severity.High,
            _ => Get(kind).DefaultSeverity,
        },
        WatchEventKind.MassDeletion when objectKind is WatchObjectKind.Computer or WatchObjectKind.Contact => Severity.High,
        WatchEventKind.GroupPolicyLinkChanged when objectKind is WatchObjectKind.Domain => Severity.High,
        _ => Get(kind).DefaultSeverity,
    };

    private static EventKindInfo[] Build() =>
    [
        // Lifecycle
        Entry(WatchEventKind.UserCreated, EventCategory.Lifecycle, Severity.Low, [WatchObjectKind.User]),
        Entry(WatchEventKind.ComputerCreated, EventCategory.Lifecycle, Severity.Info, [WatchObjectKind.Computer]),
        Entry(WatchEventKind.GroupCreated, EventCategory.Lifecycle, Severity.Info, [WatchObjectKind.Group]),
        Entry(WatchEventKind.OrganizationalUnitCreated, EventCategory.Lifecycle, Severity.Info, [WatchObjectKind.OrganizationalUnit]),
        Entry(WatchEventKind.ContactCreated, EventCategory.Lifecycle, Severity.Info, [WatchObjectKind.Contact], recorded: false),
        Entry(WatchEventKind.ServiceAccountCreated, EventCategory.Lifecycle, Severity.Medium, [WatchObjectKind.ServiceAccount]),
        Entry(WatchEventKind.ObjectDeleted, EventCategory.Lifecycle, Severity.Medium, Deletable),
        Entry(WatchEventKind.ObjectRestored, EventCategory.Lifecycle, Severity.Medium, Deletable),
        Entry(WatchEventKind.ObjectMoved, EventCategory.Lifecycle, Severity.Info, Movable),
        Entry(WatchEventKind.ObjectRenamed, EventCategory.Lifecycle, Severity.Info, Movable),
        Entry(WatchEventKind.ObjectAppeared, EventCategory.Lifecycle, Severity.Info, Movable),
        Entry(WatchEventKind.ObjectDisappeared, EventCategory.Lifecycle, Severity.Low, Movable),

        // Account
        Entry(WatchEventKind.AccountDisabled, EventCategory.Account, Severity.Info, Accounts),
        Entry(WatchEventKind.AccountEnabled, EventCategory.Account, Severity.Low, Accounts),
        Entry(WatchEventKind.AccountLockedOut, EventCategory.Account, Severity.Low, [WatchObjectKind.User]),
        Entry(WatchEventKind.AccountUnlocked, EventCategory.Account, Severity.Info, [WatchObjectKind.User]),
        Entry(WatchEventKind.LockoutExpired, EventCategory.Account, Severity.Info, [WatchObjectKind.User], recorded: false),
        Entry(WatchEventKind.PasswordChanged, EventCategory.Account, Severity.Info, Accounts),
        Entry(WatchEventKind.PasswordAgeReset, EventCategory.Account, Severity.Medium, Accounts),
        Entry(WatchEventKind.PasswordMustChangeSet, EventCategory.Account, Severity.Info, [WatchObjectKind.User]),
        Entry(WatchEventKind.AccountFlagsChanged, EventCategory.Account, Severity.Low, Accounts),
        Entry(WatchEventKind.AccountExpirationChanged, EventCategory.Account, Severity.Info, Accounts),
        Entry(WatchEventKind.AccountNameChanged, EventCategory.Account, Severity.Medium, Principals),
        Entry(WatchEventKind.AttributesChanged, EventCategory.Account, Severity.Info, Snapshotted),

        // Membership
        Entry(WatchEventKind.GroupMemberAdded, EventCategory.Membership, Severity.Info, [WatchObjectKind.Group]),
        Entry(WatchEventKind.GroupMemberRemoved, EventCategory.Membership, Severity.Info, [WatchObjectKind.Group]),
        Entry(WatchEventKind.GroupTypeChanged, EventCategory.Membership, Severity.Medium, [WatchObjectKind.Group]),
        Entry(WatchEventKind.PrivilegeGranted, EventCategory.Membership, Severity.Critical, Principals),
        Entry(WatchEventKind.PrivilegeRevoked, EventCategory.Membership, Severity.Medium, Principals),

        // Security (Tier 0)
        Entry(WatchEventKind.AdminCountSet, EventCategory.Security, Severity.High, Principals),
        Entry(WatchEventKind.SidHistoryChanged, EventCategory.Security, Severity.Critical, Principals),
        Entry(WatchEventKind.DelegationChanged, EventCategory.Security, Severity.High, Accounts),
        Entry(WatchEventKind.ServicePrincipalNameAdded, EventCategory.Security, Severity.High, [WatchObjectKind.User]),
        Entry(WatchEventKind.KeyCredentialChanged, EventCategory.Security, Severity.Medium, [WatchObjectKind.User, WatchObjectKind.Computer]),
        Entry(WatchEventKind.PrimaryGroupChanged, EventCategory.Security, Severity.High, Accounts),
        Entry(WatchEventKind.SecurityDescriptorChanged, EventCategory.Security, Severity.Critical, []),
        Entry(WatchEventKind.ComputerNameSpoofing, EventCategory.Security, Severity.Critical, [WatchObjectKind.Computer]),
        // BadSuccessor (stage B design 9.1): every change of a dMSA migration link or state is Critical.
        Entry(WatchEventKind.ManagedAccountLinkChanged, EventCategory.Security, Severity.Critical, Accounts),
        Entry(WatchEventKind.CertificateMappingChanged, EventCategory.Security, Severity.High, [WatchObjectKind.User, WatchObjectKind.Computer]),
        Entry(WatchEventKind.ManagedPasswordReadersChanged, EventCategory.Security, Severity.High, [WatchObjectKind.ServiceAccount]),

        // Domain and forest
        Entry(WatchEventKind.DomainPolicyChanged, EventCategory.Domain, Severity.High, [WatchObjectKind.Domain, WatchObjectKind.Configuration]),
        Entry(WatchEventKind.PasswordSettingsChanged, EventCategory.Domain, Severity.High, [WatchObjectKind.PasswordSettings]),
        Entry(WatchEventKind.GroupPolicyLinkChanged, EventCategory.Domain, Severity.Medium, [WatchObjectKind.Domain, WatchObjectKind.OrganizationalUnit]),
        Entry(WatchEventKind.GroupPolicyChanged, EventCategory.Domain, Severity.Info, [WatchObjectKind.GroupPolicy]),
        Entry(WatchEventKind.TrustChanged, EventCategory.Domain, Severity.Critical, [WatchObjectKind.Trust]),
        Entry(WatchEventKind.DomainControllerAdded, EventCategory.Domain, Severity.Critical, [WatchObjectKind.DomainController, WatchObjectKind.Computer]),
        Entry(WatchEventKind.DomainControllerRemoved, EventCategory.Domain, Severity.High, [WatchObjectKind.DomainController]),
        Entry(WatchEventKind.FsmoRoleChanged, EventCategory.Domain, Severity.High, [WatchObjectKind.Domain, WatchObjectKind.Configuration]),
        Entry(WatchEventKind.CertificateTemplateChanged, EventCategory.Domain, Severity.High, [WatchObjectKind.PublicKeyInfrastructure]),

        // GPO content (stage B design 8.6): GroupPolicySettingsChanged starts at Low (an unlinked GPO) and is raised by the
        // GPO classifier (Medium linked, High sensitive category, Critical on the domain head / DC OU or cpassword);
        // GroupPolicyContentOutOfBand is Critical when linked to the domain head / DC OU.
        Entry(WatchEventKind.GroupPolicySettingsChanged, EventCategory.Domain, Severity.Low, [WatchObjectKind.GroupPolicy]),
        Entry(WatchEventKind.GroupPolicyPathRedirected, EventCategory.Domain, Severity.Critical, [WatchObjectKind.GroupPolicy]),
        Entry(WatchEventKind.GroupPolicyContentOutOfBand, EventCategory.Domain, Severity.High, [WatchObjectKind.GroupPolicy]),
        Entry(WatchEventKind.SchemaChanged, EventCategory.Domain, Severity.High, [WatchObjectKind.Schema]),

        // Expiry and bursts
        Entry(WatchEventKind.AccountExpiring, EventCategory.Expiry, Severity.Info, [WatchObjectKind.User]),
        Entry(WatchEventKind.AccountExpired, EventCategory.Expiry, Severity.Info, [WatchObjectKind.User]),
        Entry(WatchEventKind.PasswordExpiring, EventCategory.Expiry, Severity.Info, [WatchObjectKind.User]),
        Entry(WatchEventKind.MassLockout, EventCategory.Security, Severity.Medium, []),
        Entry(WatchEventKind.MassDeletion, EventCategory.Security, Severity.Critical, []),
        Entry(WatchEventKind.PasswordSprayingSuspected, EventCategory.Security, Severity.High, [], recorded: false, securityLog: true),

        // Security log only
        Entry(WatchEventKind.SecurityLogCleared, EventCategory.AuditLog, Severity.Critical, [WatchObjectKind.DomainController], securityLog: true),
        Entry(WatchEventKind.AuditPolicyChanged, EventCategory.AuditLog, Severity.Critical, [WatchObjectKind.DomainController], securityLog: true),
        Entry(WatchEventKind.DsrmPasswordChanged, EventCategory.AuditLog, Severity.High, [WatchObjectKind.DomainController], securityLog: true),

        // Health (always recorded)
        Entry(WatchEventKind.HealthDirectoryUnavailable, EventCategory.Health, Severity.High, WatchdogOnly),
        Entry(WatchEventKind.HealthResync, EventCategory.Health, Severity.Medium, WatchdogOnly),
        Entry(WatchEventKind.HealthPermissionGap, EventCategory.Health, Severity.High, WatchdogOnly),
        Entry(WatchEventKind.HealthAttributionDegraded, EventCategory.Health, Severity.Medium, WatchdogOnly),
        Entry(WatchEventKind.HealthEventLogGap, EventCategory.Health, Severity.High, WatchdogOnly),
        Entry(WatchEventKind.HealthDeliveryFailing, EventCategory.Health, Severity.High, WatchdogOnly),
        Entry(WatchEventKind.HealthStorage, EventCategory.Health, Severity.High, WatchdogOnly),
        Entry(WatchEventKind.HealthBaselineCompleted, EventCategory.Health, Severity.Info, WatchdogOnly),
        Entry(WatchEventKind.WatchdogConfigurationChanged, EventCategory.Health, Severity.Medium, WatchdogOnly),
        Entry(WatchEventKind.HealthServiceAccount, EventCategory.Health, Severity.High, WatchdogOnly),
        Entry(WatchEventKind.Test, EventCategory.Health, Severity.Info, WatchdogOnly),
    ];

    private static EventKindInfo Entry(
        WatchEventKind kind,
        EventCategory category,
        Severity severity,
        WatchObjectKind[] objectKinds,
        bool recorded = true,
        bool securityLog = false) => new()
        {
            Kind = kind,
            Category = category,
            DefaultSeverity = severity,
            RecordedByDefault = recorded,
            ObjectKinds = objectKinds,
            NeedsSecurityLog = securityLog,
            IsHealth = category == EventCategory.Health,
        };
}
