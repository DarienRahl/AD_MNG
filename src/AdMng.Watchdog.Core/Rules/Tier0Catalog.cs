using System.Collections.Frozen;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Rules;

/// <summary>
/// Tier-0 detections that cannot be suppressed without the bootstrap flag <c>Authorization.AllowTier0Suppression</c>
/// (sections 8.3 <c>tier0Suppression</c> and 12.6): the kinds <c>Directory.DisabledKinds</c> may not contain, the
/// attributes <c>Directory.IgnoredAttributes</c> may not contain, the subtrees <c>Directory.ExcludedSubtrees</c> may not
/// cover, and the rule id prefix of the Tier-0 rules.
/// </summary>
public static class Tier0Catalog
{
    /// <summary>Id prefix of the Tier-0 rules (<c>tier0-privilege</c>, <c>tier0-security</c>, …): exclusions on them are
    /// warned about (<c>tier0Exclusion</c>) and they notify immediately even in rollout mode (12.5).</summary>
    public const string RuleIdPrefix = "tier0-";

    /// <summary>
    /// The built-in rules of stage B (design 9.1–9.3, 8.6): disabling or weakening them (<c>enabled</c> false, kinds,
    /// tags, scope or minimum severity narrowed, exclusions, delivery weakened) is a <c>tier0Suppression</c> error without
    /// the bootstrap flag <c>Authorization.AllowTier0Suppression</c>, and every such change is a Critical
    /// WatchdogConfigurationChanged.
    /// </summary>
    public static IReadOnlySet<string> SuppressionProtectedRules { get; } = new[]
    {
        DefaultRules.Tier0BadSuccessor, DefaultRules.Tier0MachineQuota, DefaultRules.WatchdogSelf, DefaultRules.Tier0Gpo,
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every kind of the categories Security, Domain and AuditLog, plus PrivilegeGranted/PrivilegeRevoked,
    /// AccountFlagsChanged, DelegationChanged and ServiceAccountCreated (dMSA creation, BadSuccessor – design 9.1).</summary>
    public static IReadOnlySet<WatchEventKind> Kinds { get; } = EventKindCatalog.All
        .Where(k => k.Category is EventCategory.Security or EventCategory.Domain or EventCategory.AuditLog)
        .Select(k => k.Kind)
        .Concat([
            WatchEventKind.PrivilegeGranted, WatchEventKind.PrivilegeRevoked, WatchEventKind.AccountFlagsChanged, WatchEventKind.DelegationChanged,
            WatchEventKind.ServiceAccountCreated,
        ])
        .ToFrozenSet();

    /// <summary>The attributes the ChangeClassifier consumes for the Tier-0 kinds (10.6, 9.2), case-insensitive.</summary>
    public static IReadOnlySet<string> Attributes { get; } = new[]
    {
        // accounts and groups
        "userAccountControl", "member", "primaryGroupID", "sIDHistory", "adminCount", "servicePrincipalName",
        "msDS-KeyCredentialLink", "msDS-AllowedToDelegateTo", "msDS-AllowedToActOnBehalfOfOtherIdentity",
        "msDS-ManagedAccountPrecededByLink", "msDS-DelegatedMSAState", "msDS-SupersededManagedAccountLink",
        "msDS-SupersededServiceAccountState", "altSecurityIdentities", "msDS-GroupMSAMembership", "nTSecurityDescriptor",
        "sAMAccountName",

        // the service's own account (watchdog-self, design 9.3): RC4 re-enabled by a self-write
        "msDS-SupportedEncryptionTypes",

        // computer name spoofing (dNSHostName conflict, 9.3: always evaluated)
        "dNSHostName",

        // GPO links, GPO content (GroupPolicyChanged: version and file system path) and FSMO
        "gPLink", "gPOptions", "versionNumber", "gPCFileSysPath", "fSMORoleOwner",

        // domain and forest policy
        "lockoutThreshold", "lockoutDuration", "lockOutObservationWindow", "maxPwdAge", "minPwdAge", "minPwdLength",
        "pwdHistoryLength", "pwdProperties", "ms-DS-MachineAccountQuota", "msDS-Behavior-Version", "msDS-EnabledFeature",
        "dSHeuristics", "tombstoneLifetime",

        // password settings objects
        "msDS-PasswordSettingsPrecedence", "msDS-MinimumPasswordLength", "msDS-PasswordHistoryLength",
        "msDS-PasswordComplexityEnabled", "msDS-PasswordReversibleEncryptionEnabled", "msDS-MinimumPasswordAge",
        "msDS-MaximumPasswordAge", "msDS-LockoutThreshold", "msDS-LockoutObservationWindow", "msDS-LockoutDuration",
        "msDS-PSOAppliesTo",

        // trusts
        "trustDirection", "trustType", "trustAttributes", "flatName", "trustPartner",

        // PKI
        "flags", "msPKI-Certificate-Name-Flag", "msPKI-Enrollment-Flag", "msPKI-RA-Signature", "pKIExtendedKeyUsage",
        "msPKI-Certificate-Application-Policy", "certificateTemplates", "cACertificate",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Subtrees <c>Directory.ExcludedSubtrees</c> may neither equal nor contain (besides every
    /// <c>Directory.CriticalObjects</c> entry and the additional privileged group DNs); tokens are expanded by
    /// <see cref="ConfigTokens"/>. <c>CN=Users</c> holds Domain/Enterprise/Schema Admins, krbtgt and the built-in
    /// Administrator, <c>CN=Builtin</c> the Administrators and operator groups – excluding either container would
    /// silence Tier-0 privilege detection.</summary>
    public static IReadOnlyList<string> ProtectedSubtrees { get; } =
    [
        ConfigTokens.DomainToken, "OU=Domain Controllers," + ConfigTokens.DomainToken, "CN=System," + ConfigTokens.DomainToken,
        "CN=Users," + ConfigTokens.DomainToken, "CN=Builtin," + ConfigTokens.DomainToken,
    ];

    /// <summary>True for a Tier-0 kind.</summary>
    public static bool IsTier0Kind(WatchEventKind kind) => Kinds.Contains(kind);

    /// <summary>True for a Tier-0 attribute (attribute options such as <c>;binary</c> are ignored).</summary>
    public static bool IsTier0Attribute(string? attribute)
    {
        if (string.IsNullOrWhiteSpace(attribute))
        {
            return false;
        }

        var name = attribute.Trim();
        var separator = name.IndexOf(';', StringComparison.Ordinal);
        return Attributes.Contains(separator < 0 ? name : name[..separator]);
    }

    /// <summary>True for a rule id starting with <see cref="RuleIdPrefix"/> and for <c>watchdog-self</c> (design 9.3: a
    /// Tier-0 rule without the prefix), case-insensitive.</summary>
    public static bool IsTier0Rule(string? ruleId) =>
        ruleId is not null
        && (ruleId.StartsWith(RuleIdPrefix, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ruleId, DefaultRules.WatchdogSelf, StringComparison.OrdinalIgnoreCase));

    /// <summary>True for one of <see cref="SuppressionProtectedRules"/>.</summary>
    public static bool IsSuppressionProtectedRule(string? ruleId) => ruleId is not null && SuppressionProtectedRules.Contains(ruleId);
}
