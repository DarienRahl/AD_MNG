using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Attribution;

namespace AdMng.Watchdog.Core.Tests.Attribution;

public class SecurityEventCatalogTests
{
    [Fact]
    public void IngestionSet_IsTheListOfSection11_2_WithoutOnDemandIds()
    {
        Assert.Equal(56, SecurityEventCatalog.IngestionSet.Count);
        Assert.Equal(SecurityEventCatalog.IngestionSet.Count, SecurityEventCatalog.IngestionSet.Distinct().Count());
        Assert.DoesNotContain(SecurityEventCatalog.IngestionSet, c => SecurityEventCatalog.OnDemandSet.Contains(c));
        Assert.Contains(1102, SecurityEventCatalog.IngestionSet);
        Assert.Contains(4719, SecurityEventCatalog.IngestionSet);
        Assert.Contains(4794, SecurityEventCatalog.IngestionSet);
    }

    [Fact]
    public void EveryCandidateId_IsIngested()
    {
        foreach (var kind in Enum.GetValues<WatchEventKind>())
        {
            foreach (var objectKind in Enum.GetValues<WatchObjectKind>())
            {
                Assert.All(SecurityEventCatalog.CandidateCodes(kind, objectKind), c => Assert.Contains(c, SecurityEventCatalog.IngestionSet));
            }
        }
    }

    [Theory]
    [InlineData(WatchEventKind.UserCreated, WatchObjectKind.User, new[] { 4720, 5137 })]
    [InlineData(WatchEventKind.ComputerCreated, WatchObjectKind.Computer, new[] { 4741, 5137 })]
    [InlineData(WatchEventKind.ServiceAccountCreated, WatchObjectKind.ServiceAccount, new[] { 4741, 5137 })]
    [InlineData(WatchEventKind.GroupCreated, WatchObjectKind.Group, new[] { 4727, 4731, 4754, 4744, 4749, 4759, 5137 })]
    [InlineData(WatchEventKind.OrganizationalUnitCreated, WatchObjectKind.OrganizationalUnit, new[] { 5137 })]
    [InlineData(WatchEventKind.ObjectDeleted, WatchObjectKind.User, new[] { 4726, 5141 })]
    [InlineData(WatchEventKind.ObjectDeleted, WatchObjectKind.Computer, new[] { 4743, 5141 })]
    [InlineData(WatchEventKind.ObjectDeleted, WatchObjectKind.OrganizationalUnit, new[] { 5141 })]
    [InlineData(WatchEventKind.ObjectRestored, WatchObjectKind.User, new[] { 5138 })]
    [InlineData(WatchEventKind.ObjectMoved, WatchObjectKind.User, new[] { 5139 })]
    [InlineData(WatchEventKind.AccountDisabled, WatchObjectKind.User, new[] { 4725, 4738, 5136 })]
    [InlineData(WatchEventKind.AccountEnabled, WatchObjectKind.Computer, new[] { 4722, 4742, 5136 })]
    [InlineData(WatchEventKind.AccountUnlocked, WatchObjectKind.User, new[] { 4767, 5136 })]
    [InlineData(WatchEventKind.PasswordChanged, WatchObjectKind.User, new[] { 4724, 4723 })]
    [InlineData(WatchEventKind.AccountNameChanged, WatchObjectKind.User, new[] { 4781, 4738, 5136 })]
    [InlineData(WatchEventKind.SidHistoryChanged, WatchObjectKind.User, new[] { 4765, 4766, 4738, 5136 })]
    [InlineData(WatchEventKind.GroupMemberAdded, WatchObjectKind.Group, new[] { 4728, 4732, 4756, 4751, 4746, 4761, 5136 })]
    [InlineData(WatchEventKind.GroupMemberRemoved, WatchObjectKind.Group, new[] { 4729, 4733, 4757, 4752, 4747, 4762, 5136 })]
    [InlineData(WatchEventKind.GroupTypeChanged, WatchObjectKind.Group, new[] { 4764, 5136 })]
    [InlineData(WatchEventKind.KeyCredentialChanged, WatchObjectKind.User, new[] { 5136 })]
    [InlineData(WatchEventKind.DomainControllerAdded, WatchObjectKind.DomainController, new[] { 5137 })]
    [InlineData(WatchEventKind.DomainControllerRemoved, WatchObjectKind.DomainController, new[] { 5141 })]
    public void CandidateCodes_FollowTheTableOfSection11_2(WatchEventKind kind, WatchObjectKind objectKind, int[] expected) =>
        Assert.Equal(expected, SecurityEventCatalog.CandidateCodes(kind, objectKind));

    [Theory]
    [InlineData(WatchEventKind.AccountExpiring)]
    [InlineData(WatchEventKind.MassDeletion)]
    [InlineData(WatchEventKind.HealthResync)]
    [InlineData(WatchEventKind.ObjectAppeared)]
    [InlineData(WatchEventKind.ObjectDisappeared)]
    [InlineData(WatchEventKind.LockoutExpired)]
    [InlineData(WatchEventKind.AccountLockedOut)]
    [InlineData(WatchEventKind.WatchdogConfigurationChanged)]
    [InlineData(WatchEventKind.SecurityLogCleared)]
    [InlineData(WatchEventKind.Test)]
    public void NotAttributableKinds_HaveNoCandidates(WatchEventKind kind)
    {
        Assert.False(SecurityEventCatalog.IsAttributable(kind));
        Assert.Empty(SecurityEventCatalog.DirectoryCodes(kind));
    }

    [Fact]
    public void PrivilegeKinds_InheritTheirAttribution()
    {
        Assert.True(SecurityEventCatalog.InheritsAttribution(WatchEventKind.PrivilegeGranted));
        Assert.True(SecurityEventCatalog.IsAttributable(WatchEventKind.PrivilegeRevoked));
        Assert.Empty(SecurityEventCatalog.CandidateCodes(WatchEventKind.PrivilegeGranted, WatchObjectKind.User));
    }

    [Fact]
    public void SamFieldMapping_CoversSection11_2()
    {
        Assert.Equal("homeDrive", SecurityEventCatalog.SamFieldAttributes["HomePath"]);
        Assert.Equal("userAccountControl", SecurityEventCatalog.SamFieldAttributes["NewUacValue"]);
        Assert.Equal("servicePrincipalName", SecurityEventCatalog.SamFieldAttributes["ServicePrincipalNames"]);
        Assert.Equal(18, SecurityEventCatalog.SamFieldAttributes.Count);
        Assert.Superset(SecurityEventCatalog.SamFieldAttributes.Keys.ToHashSet(), SecurityEventCatalog.RetainedFields.ToHashSet());
    }
}
