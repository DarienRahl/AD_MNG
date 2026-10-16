using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Tests.Detection;

public class UacFlagClassifierTests
{
    private static readonly UacFlagClassifier Classifier = new(new AttributeChangeBuilder(200, 100));
    private static readonly SeverityResolver Resolver = new();

    private const UserAccountControl Normal = UserAccountControl.NormalAccount;
    private const UserAccountControl Workstation = UserAccountControl.WorkstationTrustAccount;
    private const UserAccountControl DomainController = UserAccountControl.ServerTrustAccount | UserAccountControl.TrustedForDelegation;

    private static SnapshotAttribute Uac(UserAccountControl value) =>
        new() { Values = [((int)value).ToString(CultureInfo.InvariantCulture)], Kind = AttributeValueKind.Flags };

    private static (WatchEventKind Kind, Severity Severity)[] Classify(UserAccountControl before, UserAccountControl after, WatchObjectKind objectKind = WatchObjectKind.User, params string[] tags) =>
        Classifier.Classify(Uac(before), Uac(after))
            .Select(d => (d.Kind, Resolver.Resolve(Event(d, objectKind, tags), baseSeverity: d.BaseSeverity)))
            .ToArray();

    private static WatchEvent Event(EventDraft draft, WatchObjectKind kind, string[] tags) => new()
    {
        EventId = Guid.NewGuid(),
        Kind = draft.Kind,
        Object = new ObjectRef { Dn = "CN=Jan Kowalski,OU=IT,DC=contoso,DC=test", Kind = kind },
        Changes = draft.Changes,
        DetectedAt = DateTimeOffset.UnixEpoch,
        Source = DetectionSource.IncrementalPoll,
        Tags = tags,
    };

    [Fact]
    public void Disable_And_Enable()
    {
        Assert.Equal([(WatchEventKind.AccountDisabled, Severity.Info)], Classify(Normal, Normal | UserAccountControl.AccountDisable));
        Assert.Equal([(WatchEventKind.AccountEnabled, Severity.Low)], Classify(Normal | UserAccountControl.AccountDisable, Normal));
    }

    [Fact]
    public void TrustedForDelegation_IsCriticalOnANonDc_NoEventOnADc_MediumWhenCleared()
    {
        Assert.Equal([(WatchEventKind.DelegationChanged, Severity.Critical)], Classify(Workstation, Workstation | UserAccountControl.TrustedForDelegation, WatchObjectKind.Computer));
        Assert.Empty(Classify(UserAccountControl.ServerTrustAccount, DomainController, WatchObjectKind.Computer));
        Assert.Equal([(WatchEventKind.DelegationChanged, Severity.Medium)], Classify(Workstation | UserAccountControl.TrustedForDelegation, Workstation, WatchObjectKind.Computer));
    }

    [Fact]
    public void TrustedToAuthForDelegation_IsHigh()
    {
        Assert.Equal([(WatchEventKind.DelegationChanged, Severity.High)], Classify(Normal, Normal | UserAccountControl.TrustedToAuthForDelegation));
        Assert.Equal([(WatchEventKind.DelegationChanged, Severity.Medium)], Classify(Normal | UserAccountControl.TrustedToAuthForDelegation, Normal));
    }

    [Fact]
    public void ServerTrustAccount_SetIsDomainControllerAdded_ClearedIsHighFlagChange()
    {
        var set = Classify(Workstation, UserAccountControl.ServerTrustAccount, WatchObjectKind.Computer);
        Assert.Contains((WatchEventKind.DomainControllerAdded, Severity.Critical), set);

        var cleared = Classify(UserAccountControl.ServerTrustAccount, Workstation, WatchObjectKind.Computer);
        Assert.Contains((WatchEventKind.AccountFlagsChanged, Severity.High), cleared);
    }

    [Theory]
    [InlineData(UserAccountControl.PasswordNotRequired)]
    [InlineData(UserAccountControl.DontRequirePreauth)]
    [InlineData(UserAccountControl.EncryptedTextPasswordAllowed)]
    [InlineData(UserAccountControl.UseDesKeyOnly)]
    public void RiskyBits_AreHighWhenSet_LowWhenCleared(UserAccountControl bit)
    {
        Assert.Equal([(WatchEventKind.AccountFlagsChanged, Severity.High)], Classify(Normal, Normal | bit));
        Assert.Equal([(WatchEventKind.AccountFlagsChanged, Severity.Low)], Classify(Normal | bit, Normal));
    }

    [Fact]
    public void DontExpirePassword_IsMediumWhenSet()
    {
        Assert.Equal([(WatchEventKind.AccountFlagsChanged, Severity.Medium)], Classify(Normal, Normal | UserAccountControl.DontExpirePassword));
        Assert.Equal([(WatchEventKind.AccountFlagsChanged, Severity.Low)], Classify(Normal | UserAccountControl.DontExpirePassword, Normal));
    }

    [Theory]
    [InlineData(UserAccountControl.SmartcardRequired)]
    [InlineData(UserAccountControl.NotDelegated)]
    public void ProtectionBits_ClearedOnAPrivilegedAccount_AreHigh(UserAccountControl bit)
    {
        Assert.Equal([(WatchEventKind.AccountFlagsChanged, Severity.Low)], Classify(Normal, Normal | bit));
        Assert.Equal([(WatchEventKind.AccountFlagsChanged, Severity.Low)], Classify(Normal | bit, Normal));
        Assert.Contains((WatchEventKind.AccountFlagsChanged, Severity.High), Classify(Normal | bit, Normal, WatchObjectKind.User, EventTags.Privileged));
    }

    [Fact]
    public void OtherBits_AreLow_AndOneEventCarriesAllFlags()
    {
        var drafts = Classifier.Classify(Uac(Normal), Uac(Normal | UserAccountControl.Script | UserAccountControl.PasswordCantChange | UserAccountControl.AccountDisable));

        var flags = Assert.Single(drafts, d => d.Kind == WatchEventKind.AccountFlagsChanged);
        Assert.Equal(["SCRIPT", "PASSWD_CANT_CHANGE"], flags.Changes.Single().FlagsAdded);
        Assert.Equal(["ACCOUNTDISABLE"], Assert.Single(drafts, d => d.Kind == WatchEventKind.AccountDisabled).Changes.Single().FlagsAdded);
    }

    [Fact]
    public void Created_RiskyBitsAgainstAnEmptyBaseline()
    {
        var risky = Classifier.ClassifyCreated(Uac(Normal | UserAccountControl.DontRequirePreauth | UserAccountControl.DontExpirePassword));
        var notRequiredDisabled = Classifier.ClassifyCreated(Uac(Normal | UserAccountControl.PasswordNotRequired | UserAccountControl.AccountDisable));
        var plain = Classifier.ClassifyCreated(Uac(Normal));

        var flagEvent = Assert.Single(risky);
        Assert.Equal(Severity.High, Resolver.Resolve(Event(flagEvent, WatchObjectKind.User, [])));
        Assert.Equal(Severity.Low, Resolver.Resolve(Event(Assert.Single(notRequiredDisabled), WatchObjectKind.User, [])));
        Assert.Empty(plain);
    }
}
