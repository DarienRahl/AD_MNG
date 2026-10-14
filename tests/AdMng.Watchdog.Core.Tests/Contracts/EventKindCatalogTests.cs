using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Tests.Contracts;

public class EventKindCatalogTests
{
    [Fact]
    public void EveryKind_HasExactlyOneEntry()
    {
        var kinds = Enum.GetValues<WatchEventKind>();

        Assert.Equal(kinds.Length, EventKindCatalog.All.Count);
        Assert.Equal(kinds.Order(), EventKindCatalog.All.Select(i => i.Kind).Order());
        Assert.All(kinds, kind => Assert.Equal(kind, EventKindCatalog.Get(kind).Kind));
    }

    [Fact]
    public void OnlyTheOptionalKinds_AreNotRecordedByDefault()
    {
        var optional = EventKindCatalog.All.Where(i => !i.RecordedByDefault).Select(i => i.Kind).Order();

        Assert.Equal(
            [WatchEventKind.ContactCreated, WatchEventKind.LockoutExpired, WatchEventKind.PasswordSprayingSuspected],
            optional);
    }

    [Fact]
    public void HealthKinds_AreHealthCategoryAndFlagged()
    {
        var health = EventKindCatalog.All.Where(i => i.IsHealth).Select(i => i.Kind).ToHashSet();

        Assert.All(EventKindCatalog.All, i => Assert.Equal(i.Category == EventCategory.Health, i.IsHealth));
        Assert.Contains(WatchEventKind.WatchdogConfigurationChanged, health);
        Assert.Contains(WatchEventKind.Test, health);
        Assert.All(health, kind => Assert.True((int)kind >= 900));
    }

    [Theory]
    [InlineData(WatchEventKind.UserCreated, EventCategory.Lifecycle, Severity.Low)]
    [InlineData(WatchEventKind.ComputerCreated, EventCategory.Lifecycle, Severity.Info)]
    [InlineData(WatchEventKind.ServiceAccountCreated, EventCategory.Lifecycle, Severity.Medium)]
    [InlineData(WatchEventKind.ObjectDisappeared, EventCategory.Lifecycle, Severity.Low)]
    [InlineData(WatchEventKind.AccountEnabled, EventCategory.Account, Severity.Low)]
    [InlineData(WatchEventKind.PasswordAgeReset, EventCategory.Account, Severity.Medium)]
    [InlineData(WatchEventKind.AccountNameChanged, EventCategory.Account, Severity.Medium)]
    [InlineData(WatchEventKind.GroupTypeChanged, EventCategory.Membership, Severity.Medium)]
    [InlineData(WatchEventKind.PrivilegeGranted, EventCategory.Membership, Severity.Critical)]
    [InlineData(WatchEventKind.PrivilegeRevoked, EventCategory.Membership, Severity.Medium)]
    [InlineData(WatchEventKind.SidHistoryChanged, EventCategory.Security, Severity.Critical)]
    [InlineData(WatchEventKind.SecurityDescriptorChanged, EventCategory.Security, Severity.Critical)]
    [InlineData(WatchEventKind.ComputerNameSpoofing, EventCategory.Security, Severity.Critical)]
    [InlineData(WatchEventKind.MassLockout, EventCategory.Security, Severity.Medium)]
    [InlineData(WatchEventKind.MassDeletion, EventCategory.Security, Severity.Critical)]
    [InlineData(WatchEventKind.TrustChanged, EventCategory.Domain, Severity.Critical)]
    [InlineData(WatchEventKind.DomainControllerAdded, EventCategory.Domain, Severity.Critical)]
    [InlineData(WatchEventKind.GroupPolicyChanged, EventCategory.Domain, Severity.Info)]
    [InlineData(WatchEventKind.AccountExpiring, EventCategory.Expiry, Severity.Info)]
    [InlineData(WatchEventKind.SecurityLogCleared, EventCategory.AuditLog, Severity.Critical)]
    [InlineData(WatchEventKind.DsrmPasswordChanged, EventCategory.AuditLog, Severity.High)]
    [InlineData(WatchEventKind.HealthResync, EventCategory.Health, Severity.Medium)]
    [InlineData(WatchEventKind.HealthBaselineCompleted, EventCategory.Health, Severity.Info)]
    public void Defaults_FollowTheSpecificationTable(WatchEventKind kind, EventCategory category, Severity severity)
    {
        var info = EventKindCatalog.Get(kind);

        Assert.Equal(category, info.Category);
        Assert.Equal(severity, info.DefaultSeverity);
    }

    [Theory]
    [InlineData(WatchObjectKind.User, Severity.Medium)]
    [InlineData(WatchObjectKind.Group, Severity.Medium)]
    [InlineData(WatchObjectKind.ServiceAccount, Severity.Medium)]
    [InlineData(WatchObjectKind.Computer, Severity.Low)]
    [InlineData(WatchObjectKind.Contact, Severity.Info)]
    [InlineData(WatchObjectKind.OrganizationalUnit, Severity.High)]
    [InlineData(WatchObjectKind.GroupPolicy, Severity.Medium)]
    [InlineData(WatchObjectKind.Trust, Severity.Medium)]
    [InlineData(WatchObjectKind.PasswordSettings, Severity.Medium)]
    public void ObjectDeleted_SeverityDependsOnTheObjectKind(WatchObjectKind objectKind, Severity expected)
    {
        Assert.Equal(expected, EventKindCatalog.DefaultSeverity(WatchEventKind.ObjectDeleted, objectKind));
    }

    [Fact]
    public void OtherObjectKindDependentDefaults()
    {
        Assert.Equal(Severity.High, EventKindCatalog.DefaultSeverity(WatchEventKind.MassDeletion, WatchObjectKind.Computer));
        Assert.Equal(Severity.Critical, EventKindCatalog.DefaultSeverity(WatchEventKind.MassDeletion, WatchObjectKind.User));
        Assert.Equal(Severity.High, EventKindCatalog.DefaultSeverity(WatchEventKind.GroupPolicyLinkChanged, WatchObjectKind.Domain));
        Assert.Equal(Severity.Medium, EventKindCatalog.DefaultSeverity(WatchEventKind.GroupPolicyLinkChanged, WatchObjectKind.OrganizationalUnit));
        Assert.Equal(Severity.Low, EventKindCatalog.DefaultSeverity(WatchEventKind.UserCreated, WatchObjectKind.User));
    }

    [Fact]
    public void SecurityLogOnlyKinds_AreFlagged()
    {
        var kinds = EventKindCatalog.All.Where(i => i.NeedsSecurityLog).Select(i => i.Kind).Order();

        Assert.Equal(
            [WatchEventKind.PasswordSprayingSuspected, WatchEventKind.SecurityLogCleared, WatchEventKind.AuditPolicyChanged, WatchEventKind.DsrmPasswordChanged],
            kinds);
    }

    [Fact]
    public void Get_RejectsUndefinedValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EventKindCatalog.Get((WatchEventKind)12345));
    }
}
