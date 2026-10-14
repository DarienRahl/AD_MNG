using AdMng.Core.ActiveDirectory;
using AdMng.Core.Schema;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// The attributes the watchdog snapshots and diffs per object kind (section 9.2). RDN attributes (cn, ou) are never
/// diffed – a rename is reported from the DN. <see cref="DirectorySettings.AdditionalWatchedAttributes"/> are appended
/// to the user, service account and computer sets, <see cref="DirectorySettings.IgnoredAttributes"/> removed; secret
/// attributes (<see cref="SecretAttributes.IsSecret"/>) and noise (<see cref="NoiseAttributes"/>) can never be watched.
/// </summary>
public static class WatchedAttributes
{
    /// <summary>Always read with every object (identity, not diffed).</summary>
    public static IReadOnlyList<string> Identity { get; } =
    [
        Attr.ObjectGuid, Attr.ObjectSid, Attr.ObjectClass, Attr.UsnCreated, Attr.UsnChanged, Attr.WhenCreated,
        Attr.WhenChanged, Attr.Name, Attr.IsDeleted,
    ];

    /// <summary>Users and service accounts.</summary>
    public static IReadOnlyList<string> User { get; } =
    [
        Attr.SamAccountName, Attr.UserPrincipalName, Attr.DisplayName, Attr.GivenName, Attr.Sn, Attr.Initials,
        Attr.Description, Attr.Mail, Attr.ProxyAddresses, Attr.Title, Attr.Department, Attr.Company, Attr.Manager,
        Attr.Office, Attr.TelephoneNumber, Attr.Mobile, Attr.EmployeeId, Attr.EmployeeNumber, Attr.EmployeeType, Attr.Info,
        Attr.UserAccountControl, Attr.AccountExpires, Attr.PwdLastSet, Attr.LockoutTime, Attr.PrimaryGroupId,
        Attr.HomeDirectory, Attr.HomeDrive, Attr.ScriptPath, Attr.ProfilePath, Attr.LogonHours, Attr.UserWorkstations,
        Attr.ServicePrincipalName, Attr.AllowedToDelegateTo, WatchdogAttributes.AllowedToActOnBehalfOfOtherIdentity,
        Attr.SidHistory, Attr.AdminCount, WatchdogAttributes.AltSecurityIdentities, WatchdogAttributes.KeyCredentialLink,
        Attr.SupportedEncryptionTypes, WatchdogAttributes.UserCertificate, WatchdogAttributes.SupersededManagedAccountLink,
        WatchdogAttributes.SupersededServiceAccountState,
    ];

    /// <summary>Added for gMSA / dMSA (managed password readers, dMSA migration links).</summary>
    public static IReadOnlyList<string> ServiceAccountExtra { get; } =
    [
        WatchdogAttributes.GroupMsaMembership, WatchdogAttributes.ManagedAccountPrecededByLink, WatchdogAttributes.DelegatedMsaState,
    ];

    /// <summary>Personal fields removed from the user set for computers.</summary>
    private static readonly HashSet<string> PersonalFields = new(StringComparer.OrdinalIgnoreCase)
    {
        Attr.GivenName, Attr.Sn, Attr.Initials, Attr.Mail, Attr.ProxyAddresses, Attr.Title, Attr.Department, Attr.Company,
        Attr.Manager, Attr.Office, Attr.TelephoneNumber, Attr.Mobile, Attr.EmployeeId, Attr.EmployeeNumber,
        Attr.EmployeeType, Attr.HomeDirectory, Attr.HomeDrive, Attr.ScriptPath, Attr.ProfilePath, Attr.LogonHours,
        Attr.UserWorkstations,
    };

    /// <summary>Computers: the user set minus personal fields plus host attributes.</summary>
    public static IReadOnlyList<string> Computer { get; } =
    [
        .. User.Where(a => !PersonalFields.Contains(a)),
        Attr.DnsHostName, Attr.OperatingSystem, Attr.OperatingSystemVersion, Attr.Location, Attr.ManagedBy,
        WatchdogAttributes.CreatorSid,
    ];

    public static IReadOnlyList<string> Group { get; } =
    [
        Attr.SamAccountName, Attr.GroupType, Attr.Description, Attr.Info, Attr.Mail, Attr.ManagedBy, Attr.AdminCount,
        Attr.SidHistory,
    ];

    public static IReadOnlyList<string> Contact { get; } = [Attr.DisplayName, Attr.GivenName, Attr.Sn, Attr.Mail, Attr.Description];

    public static IReadOnlyList<string> OrganizationalUnit { get; } =
        [Attr.Description, Attr.GpLink, WatchdogAttributes.GpOptions, Attr.ManagedBy];

    /// <summary>Domain head: policy, GPO links and the PDC role (wellKnownObjects is read by WellKnownContainers).</summary>
    public static IReadOnlyList<string> DomainHead { get; } =
    [
        WatchdogAttributes.LockoutThreshold, WatchdogAttributes.LockoutDuration, WatchdogAttributes.LockoutObservationWindow,
        WatchdogAttributes.MaxPwdAge, WatchdogAttributes.MinPwdAge, WatchdogAttributes.MinPwdLength,
        WatchdogAttributes.PwdHistoryLength, WatchdogAttributes.PwdProperties, WatchdogAttributes.MachineAccountQuota,
        Attr.GpLink, WatchdogAttributes.GpOptions, WatchdogAttributes.BehaviorVersion, WatchdogAttributes.FsmoRoleOwner,
    ];

    /// <summary>Domain policy attributes of the domain head (DomainPolicyChanged).</summary>
    public static IReadOnlyList<string> DomainPolicy { get; } =
    [
        WatchdogAttributes.LockoutThreshold, WatchdogAttributes.LockoutDuration, WatchdogAttributes.LockoutObservationWindow,
        WatchdogAttributes.MaxPwdAge, WatchdogAttributes.MinPwdAge, WatchdogAttributes.MinPwdLength,
        WatchdogAttributes.PwdHistoryLength, WatchdogAttributes.PwdProperties, WatchdogAttributes.MachineAccountQuota,
        WatchdogAttributes.BehaviorVersion,
    ];

    /// <summary>FSMO holder objects and forest singletons (per object only the relevant ones are present).</summary>
    public static IReadOnlyList<string> Configuration { get; } =
    [
        WatchdogAttributes.FsmoRoleOwner, WatchdogAttributes.EnabledFeature, WatchdogAttributes.DsHeuristics, Attr.TombstoneLifetime,
    ];

    /// <summary>Certificate templates, enrollment services, CAs and NTAuth.</summary>
    public static IReadOnlyList<string> PublicKeyInfrastructure { get; } =
    [
        Attr.DisplayName, WatchdogAttributes.Flags, WatchdogAttributes.CertificateNameFlag, WatchdogAttributes.EnrollmentFlag,
        WatchdogAttributes.RaSignature, WatchdogAttributes.ExtendedKeyUsage, WatchdogAttributes.ApplicationPolicy,
        Attr.NtSecurityDescriptor, WatchdogAttributes.CertificateTemplates, Attr.DnsHostName, WatchdogAttributes.CaCertificate,
    ];

    public static IReadOnlyList<string> GroupPolicy { get; } =
        [Attr.DisplayName, WatchdogAttributes.VersionNumber, WatchdogAttributes.Flags, WatchdogAttributes.GpcFileSysPath];

    public static IReadOnlyList<string> Trust { get; } =
    [
        WatchdogAttributes.TrustDirection, WatchdogAttributes.TrustType, WatchdogAttributes.TrustAttributes,
        WatchdogAttributes.FlatName, WatchdogAttributes.TrustPartner,
    ];

    public static IReadOnlyList<string> PasswordSettings { get; } =
    [
        WatchdogAttributes.PsoPrecedence, WatchdogAttributes.PsoMinimumPasswordLength, WatchdogAttributes.PsoPasswordHistoryLength,
        WatchdogAttributes.PsoComplexityEnabled, WatchdogAttributes.PsoReversibleEncryptionEnabled,
        WatchdogAttributes.PsoMinimumPasswordAge, WatchdogAttributes.PsoMaximumPasswordAge, WatchdogAttributes.PsoLockoutThreshold,
        WatchdogAttributes.PsoLockoutObservationWindow, WatchdogAttributes.PsoLockoutDuration, WatchdogAttributes.PsoAppliesTo,
    ];

    /// <summary>The default (unconfigured) diffed set of a kind; empty for kinds without attributes.</summary>
    public static IReadOnlyList<string> Defaults(WatchObjectKind kind) => kind switch
    {
        WatchObjectKind.User => User,
        WatchObjectKind.ServiceAccount => [.. User, .. ServiceAccountExtra],
        WatchObjectKind.Computer => Computer,
        WatchObjectKind.Group => Group,
        WatchObjectKind.Contact => Contact,
        WatchObjectKind.OrganizationalUnit => OrganizationalUnit,
        WatchObjectKind.Domain => DomainHead,
        WatchObjectKind.Configuration => Configuration,
        WatchObjectKind.PublicKeyInfrastructure => PublicKeyInfrastructure,
        WatchObjectKind.GroupPolicy => GroupPolicy,
        WatchObjectKind.Trust => Trust,
        WatchObjectKind.PasswordSettings => PasswordSettings,
        _ => [],
    };

    /// <summary>
    /// The effective diffed set of a kind under <paramref name="settings"/>: defaults (+ additional attributes for
    /// users, service accounts and computers) minus ignored, secret and noise attributes. Case-insensitive.
    /// </summary>
    public static IReadOnlySet<string> For(WatchObjectKind kind, DirectorySettings settings, ISchemaCatalog? schema = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var result = new HashSet<string>(Defaults(kind), StringComparer.OrdinalIgnoreCase);
        if (kind is WatchObjectKind.User or WatchObjectKind.ServiceAccount or WatchObjectKind.Computer)
        {
            result.UnionWith(settings.AdditionalWatchedAttributes.Where(IsValidName));
        }

        result.ExceptWith(settings.IgnoredAttributes);
        result.RemoveWhere(a => SecretAttributes.IsSecret(a, schema) || NoiseAttributes.IsNoise(a) || IsRdnAttribute(a));
        return result;
    }

    /// <summary>
    /// True when <paramref name="attribute"/> is in the effective watched set of any kind (the attribution ingestion
    /// keeps 5136 values only for these, section 11.1). Never true for secret or noise attributes.
    /// </summary>
    public static bool IsWatched(string attribute, DirectorySettings settings, ISchemaCatalog? schema = null)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(settings);
        return AllKinds.Any(k => For(k, settings, schema).Contains(attribute));
    }

    /// <summary>
    /// The attribute list of the change searches (section 10.4/10.5): identity, every kind's effective set and member.
    /// nTSecurityDescriptor is read separately for critical objects only.
    /// </summary>
    public static IReadOnlyList<string> RequestSet(DirectorySettings settings, ISchemaCatalog? schema = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var result = new List<string>(Identity) { Attr.Member };
        foreach (var kind in AllKinds)
        {
            foreach (var attribute in For(kind, settings, schema))
            {
                if (!string.Equals(attribute, Attr.NtSecurityDescriptor, StringComparison.OrdinalIgnoreCase)
                    && !result.Contains(attribute, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(attribute);
                }
            }
        }

        return result;
    }

    private static readonly WatchObjectKind[] AllKinds =
    [
        WatchObjectKind.User, WatchObjectKind.ServiceAccount, WatchObjectKind.Computer, WatchObjectKind.Group,
        WatchObjectKind.Contact, WatchObjectKind.OrganizationalUnit, WatchObjectKind.Domain, WatchObjectKind.Configuration,
        WatchObjectKind.GroupPolicy, WatchObjectKind.Trust, WatchObjectKind.PasswordSettings,
    ];

    private static bool IsRdnAttribute(string attribute) =>
        attribute.Equals(Attr.Cn, StringComparison.OrdinalIgnoreCase) || attribute.Equals(Attr.Ou, StringComparison.OrdinalIgnoreCase)
        || attribute.Equals(Attr.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>The reader re-filters configured names (validation is the first line, section 8.3 invalidAttribute).</summary>
    private static bool IsValidName(string attribute) =>
        attribute.Length is > 0 and <= 64 && char.IsAsciiLetter(attribute[0])
        && attribute.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
}
