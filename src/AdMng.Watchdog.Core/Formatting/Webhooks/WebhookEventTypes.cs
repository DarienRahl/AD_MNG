using System.Text;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Formatting.Webhooks;

/// <summary>
/// The <c>type</c> of generic webhook payloads (section 14.5): <c>ad.&lt;object&gt;.&lt;action&gt;</c> in lower snake case
/// for directory changes, <c>ad.privilege.granted|revoked</c>, <c>ad.burst.*</c>, <c>ad.dc.*</c> for event-log kinds,
/// <c>watchdog.health.*</c>, <c>watchdog.configuration_changed</c>, <c>watchdog.heartbeat</c>, <c>watchdog.digest</c>,
/// <c>watchdog.test</c>, <c>ad.attribution.updated</c> for follow-ups and <c>ad.severity.escalated</c> for follow-ups
/// that raise the severity (13.2 #7).
/// </summary>
public static class WebhookEventTypes
{
    public const string Heartbeat = "watchdog.heartbeat";
    public const string Digest = "watchdog.digest";
    public const string Test = "watchdog.test";
    public const string AttributionUpdated = "ad.attribution.updated";

    /// <summary>A follow-up whose event became more severe than the alert showed it (an escalation; it carries the final
    /// attribution as well, <c>data.previousSeverity</c> holds the earlier severity).</summary>
    public const string SeverityEscalated = "ad.severity.escalated";
    public const string ConfigurationChanged = "watchdog.configuration_changed";

    /// <summary>The event type of an event of <paramref name="kind"/> on an object of <paramref name="objectKind"/>.</summary>
    public static string For(WatchEventKind kind, WatchObjectKind objectKind)
    {
        switch (kind)
        {
            case WatchEventKind.PrivilegeGranted:
                return "ad.privilege.granted";
            case WatchEventKind.PrivilegeRevoked:
                return "ad.privilege.revoked";
            case WatchEventKind.MassLockout:
                return "ad.burst.mass_lockout";
            case WatchEventKind.MassDeletion:
                return "ad.burst.mass_deletion";
            case WatchEventKind.PasswordSprayingSuspected:
                return "ad.burst.password_spraying";
            case WatchEventKind.SecurityLogCleared:
                return "ad.dc.security_log_cleared";
            case WatchEventKind.AuditPolicyChanged:
                return "ad.dc.audit_policy_changed";
            case WatchEventKind.DsrmPasswordChanged:
                return "ad.dc.dsrm_password_changed";
            case WatchEventKind.DomainControllerAdded:
                return "ad.dc.added";
            case WatchEventKind.DomainControllerRemoved:
                return "ad.dc.removed";
            case WatchEventKind.WatchdogConfigurationChanged:
                return ConfigurationChanged;
            case WatchEventKind.Test:
                return Test;
        }

        var name = kind.ToString();
        if (name.StartsWith("Health", StringComparison.Ordinal))
        {
            return "watchdog.health." + Snake(name["Health".Length..]);
        }

        return "ad." + ObjectPart(kind, objectKind) + "." + Action(kind);
    }

    /// <summary>The object part: user, computer, group, ou, contact, service_account, domain, gpo, trust, dc, pso,
    /// schema, pki, forest (container for plain containers, object when unknown).</summary>
    public static string ObjectPart(WatchEventKind kind, WatchObjectKind objectKind) => kind switch
    {
        WatchEventKind.UserCreated => "user",
        WatchEventKind.ComputerCreated => "computer",
        WatchEventKind.GroupCreated => "group",
        WatchEventKind.OrganizationalUnitCreated => "ou",
        WatchEventKind.ContactCreated => "contact",
        WatchEventKind.ServiceAccountCreated => "service_account",
        WatchEventKind.SchemaChanged => "schema",
        WatchEventKind.CertificateTemplateChanged => "pki",
        WatchEventKind.PasswordSettingsChanged => "pso",
        WatchEventKind.TrustChanged => "trust",
        WatchEventKind.DomainPolicyChanged when objectKind == WatchObjectKind.Configuration => "forest",
        WatchEventKind.DomainPolicyChanged => "domain",
        _ => objectKind switch
        {
            WatchObjectKind.User => "user",
            WatchObjectKind.Computer => "computer",
            WatchObjectKind.Group => "group",
            WatchObjectKind.OrganizationalUnit => "ou",
            WatchObjectKind.Contact => "contact",
            WatchObjectKind.ServiceAccount => "service_account",
            WatchObjectKind.Domain => "domain",
            WatchObjectKind.GroupPolicy => "gpo",
            WatchObjectKind.Trust => "trust",
            WatchObjectKind.DomainController => "dc",
            WatchObjectKind.PasswordSettings => "pso",
            WatchObjectKind.Schema => "schema",
            WatchObjectKind.PublicKeyInfrastructure => "pki",
            WatchObjectKind.Configuration => "forest",
            WatchObjectKind.Container => "container",
            _ => "object",
        },
    };

    /// <summary>The action part of a directory kind.</summary>
    public static string Action(WatchEventKind kind) => kind switch
    {
        WatchEventKind.UserCreated or WatchEventKind.ComputerCreated or WatchEventKind.GroupCreated or WatchEventKind.OrganizationalUnitCreated
            or WatchEventKind.ContactCreated or WatchEventKind.ServiceAccountCreated => "created",
        WatchEventKind.ObjectDeleted => "deleted",
        WatchEventKind.ObjectRestored => "restored",
        WatchEventKind.ObjectMoved => "moved",
        WatchEventKind.ObjectRenamed => "renamed",
        WatchEventKind.ObjectAppeared => "appeared",
        WatchEventKind.ObjectDisappeared => "disappeared",
        WatchEventKind.AccountDisabled => "disabled",
        WatchEventKind.AccountEnabled => "enabled",
        WatchEventKind.AccountLockedOut => "locked_out",
        WatchEventKind.AccountUnlocked => "unlocked",
        WatchEventKind.LockoutExpired => "lockout_expired",
        WatchEventKind.PasswordChanged => "password_changed",
        WatchEventKind.PasswordAgeReset => "password_age_reset",
        WatchEventKind.PasswordMustChangeSet => "password_must_change",
        WatchEventKind.AccountFlagsChanged => "flags_changed",
        WatchEventKind.AccountExpirationChanged => "expiration_changed",
        WatchEventKind.AccountNameChanged => "name_changed",
        WatchEventKind.AttributesChanged => "attributes_changed",
        WatchEventKind.GroupMemberAdded => "member_added",
        WatchEventKind.GroupMemberRemoved => "member_removed",
        WatchEventKind.GroupTypeChanged => "type_changed",
        WatchEventKind.AdminCountSet => "admincount_set",
        WatchEventKind.SidHistoryChanged => "sidhistory_changed",
        WatchEventKind.DelegationChanged => "delegation_changed",
        WatchEventKind.ServicePrincipalNameAdded => "spn_added",
        WatchEventKind.KeyCredentialChanged => "keycredential_changed",
        WatchEventKind.PrimaryGroupChanged => "primarygroup_changed",
        WatchEventKind.SecurityDescriptorChanged => "acl_changed",
        WatchEventKind.ComputerNameSpoofing => "name_spoofing",
        WatchEventKind.ManagedAccountLinkChanged => "managed_account_link_changed",
        WatchEventKind.CertificateMappingChanged => "certificate_mapping_changed",
        WatchEventKind.ManagedPasswordReadersChanged => "password_readers_changed",
        WatchEventKind.DomainPolicyChanged or WatchEventKind.PasswordSettingsChanged or WatchEventKind.GroupPolicyChanged => "policy_changed",
        WatchEventKind.GroupPolicyLinkChanged => "link_changed",
        WatchEventKind.FsmoRoleChanged => "fsmo_changed",
        WatchEventKind.CertificateTemplateChanged => "template_changed",
        WatchEventKind.TrustChanged or WatchEventKind.SchemaChanged => "attributes_changed",
        WatchEventKind.AccountExpiring => "expiring",
        WatchEventKind.AccountExpired => "expired",
        WatchEventKind.PasswordExpiring => "password_expiring",
        _ => Snake(kind.ToString()),
    };

    /// <summary>"DirectoryUnavailable" → "directory_unavailable".</summary>
    public static string Snake(string pascal)
    {
        ArgumentNullException.ThrowIfNull(pascal);
        var builder = new StringBuilder(pascal.Length + 8);
        for (var i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (char.IsUpper(c) && i > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}
