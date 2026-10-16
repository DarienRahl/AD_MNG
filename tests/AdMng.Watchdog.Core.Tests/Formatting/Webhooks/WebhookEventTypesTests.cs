using System.Text.RegularExpressions;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Formatting.Webhooks;

namespace AdMng.Watchdog.Core.Tests.Formatting.Webhooks;

public sealed partial class WebhookEventTypesTests
{
    [Theory]
    [InlineData(WatchEventKind.UserCreated, WatchObjectKind.User, "ad.user.created")]
    [InlineData(WatchEventKind.ServiceAccountCreated, WatchObjectKind.ServiceAccount, "ad.service_account.created")]
    [InlineData(WatchEventKind.ObjectDeleted, WatchObjectKind.GroupPolicy, "ad.gpo.deleted")]
    [InlineData(WatchEventKind.ObjectMoved, WatchObjectKind.OrganizationalUnit, "ad.ou.moved")]
    [InlineData(WatchEventKind.AccountLockedOut, WatchObjectKind.User, "ad.user.locked_out")]
    [InlineData(WatchEventKind.PasswordMustChangeSet, WatchObjectKind.User, "ad.user.password_must_change")]
    [InlineData(WatchEventKind.GroupMemberAdded, WatchObjectKind.Group, "ad.group.member_added")]
    [InlineData(WatchEventKind.PrivilegeGranted, WatchObjectKind.User, "ad.privilege.granted")]
    [InlineData(WatchEventKind.PrivilegeRevoked, WatchObjectKind.Computer, "ad.privilege.revoked")]
    [InlineData(WatchEventKind.SecurityDescriptorChanged, WatchObjectKind.Domain, "ad.domain.acl_changed")]
    [InlineData(WatchEventKind.ServicePrincipalNameAdded, WatchObjectKind.User, "ad.user.spn_added")]
    [InlineData(WatchEventKind.DomainPolicyChanged, WatchObjectKind.Domain, "ad.domain.policy_changed")]
    [InlineData(WatchEventKind.PasswordSettingsChanged, WatchObjectKind.PasswordSettings, "ad.pso.policy_changed")]
    [InlineData(WatchEventKind.GroupPolicyLinkChanged, WatchObjectKind.OrganizationalUnit, "ad.ou.link_changed")]
    [InlineData(WatchEventKind.DomainControllerAdded, WatchObjectKind.DomainController, "ad.dc.added")]
    [InlineData(WatchEventKind.DomainControllerRemoved, WatchObjectKind.DomainController, "ad.dc.removed")]
    [InlineData(WatchEventKind.FsmoRoleChanged, WatchObjectKind.Configuration, "ad.forest.fsmo_changed")]
    [InlineData(WatchEventKind.CertificateTemplateChanged, WatchObjectKind.PublicKeyInfrastructure, "ad.pki.template_changed")]
    [InlineData(WatchEventKind.AccountExpiring, WatchObjectKind.User, "ad.user.expiring")]
    [InlineData(WatchEventKind.PasswordExpiring, WatchObjectKind.User, "ad.user.password_expiring")]
    [InlineData(WatchEventKind.MassLockout, WatchObjectKind.Domain, "ad.burst.mass_lockout")]
    [InlineData(WatchEventKind.PasswordSprayingSuspected, WatchObjectKind.Domain, "ad.burst.password_spraying")]
    [InlineData(WatchEventKind.SecurityLogCleared, WatchObjectKind.DomainController, "ad.dc.security_log_cleared")]
    [InlineData(WatchEventKind.DsrmPasswordChanged, WatchObjectKind.Computer, "ad.dc.dsrm_password_changed")]
    [InlineData(WatchEventKind.HealthDirectoryUnavailable, WatchObjectKind.Watchdog, "watchdog.health.directory_unavailable")]
    [InlineData(WatchEventKind.HealthBaselineCompleted, WatchObjectKind.Watchdog, "watchdog.health.baseline_completed")]
    [InlineData(WatchEventKind.WatchdogConfigurationChanged, WatchObjectKind.Watchdog, "watchdog.configuration_changed")]
    [InlineData(WatchEventKind.Test, WatchObjectKind.Watchdog, "watchdog.test")]
    public void Types_FollowTheNamingScheme(WatchEventKind kind, WatchObjectKind objectKind, string expected) =>
        Assert.Equal(expected, WebhookEventTypes.For(kind, objectKind));

    [Fact]
    public void EveryKindAndObjectKind_GivesALowerSnakeType()
    {
        foreach (var kind in Enum.GetValues<WatchEventKind>())
        {
            foreach (var objectKind in Enum.GetValues<WatchObjectKind>())
            {
                Assert.Matches(TypePattern(), WebhookEventTypes.For(kind, objectKind));
            }
        }
    }

    [Fact]
    public void Snake_SplitsPascalCase() => Assert.Equal("event_log_gap", WebhookEventTypes.Snake("EventLogGap"));

    [GeneratedRegex("^(ad|watchdog)(\\.[a-z][a-z0-9_]*){1,2}$")]
    private static partial Regex TypePattern();
}
