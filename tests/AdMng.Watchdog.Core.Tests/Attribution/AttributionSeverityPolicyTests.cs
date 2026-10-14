using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Attribution;
using static AdMng.Watchdog.Core.Tests.Attribution.Records;
using static AdMng.Watchdog.Core.Tests.Rules.TestEvents;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Watchdog.Core.Tests.Attribution;

public class AttributionSeverityPolicyTests
{
    private static readonly EventAttribution ByAdmin = Resolved(AdminSid);

    private static Severity? Resolve(WatchEventKind kind, Severity stored, EventAttribution attribution, params string[] tags) =>
        AttributionSeverityPolicy.Resolve(Create(kind, severity: stored), attribution, tags);

    [Fact]
    public void PrivilegedOwnPasswordChange_IsInfo() =>
        Assert.Equal(Severity.Info, Resolve(WatchEventKind.PasswordChanged, Severity.High, ByAdmin, EventTags.Privileged, EventTags.SelfChange));

    [Fact]
    public void PrivilegedPasswordReset_IsHigh() =>
        Assert.Equal(Severity.High, Resolve(WatchEventKind.PasswordChanged, Severity.Medium, ByAdmin, EventTags.Privileged, EventTags.PasswordReset));

    [Theory]
    [InlineData(AttributionStatus.Unknown)]
    [InlineData(AttributionStatus.Ambiguous)]
    public void ServiceAccountPasswordWithoutActor_IsHigh(AttributionStatus status) =>
        Assert.Equal(Severity.High, Resolve(WatchEventKind.PasswordChanged, Severity.Medium, new EventAttribution { Status = status }, EventTags.ServiceAccount));

    [Fact]
    public void OrdinaryPasswordChange_IsUnchanged() =>
        Assert.Null(Resolve(WatchEventKind.PasswordChanged, Severity.Info, ByAdmin, EventTags.PasswordReset));

    [Fact]
    public void Krbtgt_IsExactlyMedium()
    {
        Assert.Equal(Severity.Medium, Resolve(WatchEventKind.PasswordChanged, Severity.High, ByAdmin, EventTags.Privileged, EventTags.Krbtgt, EventTags.PasswordReset));
        Assert.Null(Resolve(WatchEventKind.PasswordChanged, Severity.Medium, ByAdmin, EventTags.Krbtgt));
    }

    [Fact]
    public void AdminCountSetBySdProp_IsInfo()
    {
        var system = ByAdmin with { Reason = AttributionReasons.System };

        Assert.Equal(Severity.Info, Resolve(WatchEventKind.AdminCountSet, Severity.High, system));
        Assert.Null(Resolve(WatchEventKind.AdminCountSet, Severity.High, ByAdmin));
        Assert.Null(Resolve(WatchEventKind.AdminCountSet, Severity.Info, system));
    }

    [Fact]
    public void UserKeyCredentialByAnotherAccount_IsHigh()
    {
        Assert.Equal(Severity.High, Resolve(WatchEventKind.KeyCredentialChanged, Severity.Medium, ByAdmin));
        Assert.Null(Resolve(WatchEventKind.KeyCredentialChanged, Severity.Medium, Resolved(UserSid)));
        Assert.Null(Resolve(WatchEventKind.KeyCredentialChanged, Severity.Medium, ByAdmin, EventTags.SyncAccount));
        Assert.Null(Resolve(WatchEventKind.KeyCredentialChanged, Severity.Medium, Unknown()));
        Assert.Null(Resolve(WatchEventKind.KeyCredentialChanged, Severity.High, ByAdmin, EventTags.Privileged));
    }

    [Fact]
    public void ComputerKeyCredential_IsUnchanged() =>
        Assert.Null(AttributionSeverityPolicy.Resolve(
            Create(WatchEventKind.KeyCredentialChanged, "CN=PC-01,OU=Komputery,DC=contoso,DC=test", WatchObjectKind.Computer, Severity.Low), ByAdmin, []));

    [Fact]
    public void DependentKinds_AreTheThreeOfStepThree() =>
        Assert.Equal([WatchEventKind.PasswordChanged, WatchEventKind.AdminCountSet, WatchEventKind.KeyCredentialChanged],
            AttributionSeverityPolicy.AttributionDependentKinds.Order());
}
