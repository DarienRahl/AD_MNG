using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Notifications.Digest;

/// <summary>Which section an event kind is reported in (section 13.4). Kinds without a section (expiry notices –
/// covered by "Nadchodzące terminy" –, lockout expiry, test) are only counted.</summary>
public static class DigestSections
{
    private static readonly Dictionary<WatchEventKind, DigestSectionKind> Map = Build();

    /// <summary>The section of <paramref name="kind"/>, or null.</summary>
    public static DigestSectionKind? For(WatchEventKind kind) => Map.TryGetValue(kind, out var section) ? section : null;

    /// <summary>Health kinds (always reported, independent of rules).</summary>
    public static bool IsHealth(WatchEventKind kind) => (int)kind is >= 900 and < 908;

    private static Dictionary<WatchEventKind, DigestSectionKind> Build()
    {
        var map = new Dictionary<WatchEventKind, DigestSectionKind>();
        void Add(DigestSectionKind section, params WatchEventKind[] kinds)
        {
            foreach (var kind in kinds)
            {
                map[kind] = section;
            }
        }

        Add(DigestSectionKind.NewAccounts, WatchEventKind.UserCreated, WatchEventKind.ServiceAccountCreated, WatchEventKind.ContactCreated);
        Add(DigestSectionKind.NewComputers, WatchEventKind.ComputerCreated);
        Add(DigestSectionKind.DeletedRestored, WatchEventKind.ObjectDeleted, WatchEventKind.ObjectRestored, WatchEventKind.MassDeletion);
        Add(DigestSectionKind.DisabledEnabled, WatchEventKind.AccountDisabled, WatchEventKind.AccountEnabled, WatchEventKind.AccountExpired);
        Add(DigestSectionKind.MovedRenamed, WatchEventKind.ObjectMoved, WatchEventKind.ObjectRenamed, WatchEventKind.ObjectAppeared,
            WatchEventKind.ObjectDisappeared, WatchEventKind.AccountNameChanged, WatchEventKind.OrganizationalUnitCreated);
        Add(DigestSectionKind.Privileged, WatchEventKind.PrivilegeGranted, WatchEventKind.PrivilegeRevoked);
        Add(DigestSectionKind.GroupMembership, WatchEventKind.GroupMemberAdded, WatchEventKind.GroupMemberRemoved, WatchEventKind.GroupCreated,
            WatchEventKind.GroupTypeChanged);
        Add(DigestSectionKind.AccountParameters, WatchEventKind.AttributesChanged, WatchEventKind.AccountFlagsChanged, WatchEventKind.AccountExpirationChanged,
            WatchEventKind.PasswordMustChangeSet);
        Add(DigestSectionKind.Lockouts, WatchEventKind.AccountLockedOut, WatchEventKind.AccountUnlocked, WatchEventKind.MassLockout);
        Add(DigestSectionKind.Passwords, WatchEventKind.PasswordChanged, WatchEventKind.PasswordAgeReset);
        Add(DigestSectionKind.DomainPolicy, WatchEventKind.DomainPolicyChanged, WatchEventKind.PasswordSettingsChanged, WatchEventKind.GroupPolicyLinkChanged,
            WatchEventKind.GroupPolicyChanged, WatchEventKind.TrustChanged, WatchEventKind.DomainControllerAdded, WatchEventKind.DomainControllerRemoved,
            WatchEventKind.SchemaChanged, WatchEventKind.FsmoRoleChanged, WatchEventKind.CertificateTemplateChanged, WatchEventKind.SecurityLogCleared,
            WatchEventKind.AuditPolicyChanged, WatchEventKind.DsrmPasswordChanged);
        Add(DigestSectionKind.SecurityReview, WatchEventKind.AdminCountSet, WatchEventKind.SidHistoryChanged, WatchEventKind.DelegationChanged,
            WatchEventKind.ServicePrincipalNameAdded, WatchEventKind.KeyCredentialChanged, WatchEventKind.PrimaryGroupChanged,
            WatchEventKind.SecurityDescriptorChanged, WatchEventKind.ComputerNameSpoofing, WatchEventKind.ManagedAccountLinkChanged,
            WatchEventKind.CertificateMappingChanged, WatchEventKind.ManagedPasswordReadersChanged, WatchEventKind.PasswordSprayingSuspected);
        Add(DigestSectionKind.WatchdogConfiguration, WatchEventKind.WatchdogConfigurationChanged);
        foreach (var kind in Enum.GetValues<WatchEventKind>().Where(IsHealth))
        {
            map[kind] = DigestSectionKind.Health;
        }

        return map;
    }
}
