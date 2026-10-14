using AdMng.Core.ActiveDirectory;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Detection;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Watchdog.Core.Tests.Detection;

public class SeverityResolverTests
{
    private const string DomainSid = "S-1-5-21-3623811015-3361044348-30300820";
    private static readonly SeverityResolver Resolver = new();
    private static readonly SeverityContext Context = new()
    {
        DomainDn = "DC=contoso,DC=test",
        ProtectRemovalSids = new HashSet<string> { $"{DomainSid}-525" },
        DisabledAccountsSubtrees = [AdMng.Core.Ldap.DistinguishedName.Parse("OU=Wyłączone,DC=contoso,DC=test")],
    };

    private static WatchEvent Event(WatchEventKind kind, WatchObjectKind objectKind = WatchObjectKind.User, string[]? tags = null,
        IReadOnlyList<AttributeChange>? changes = null, string dn = "CN=Jan Kowalski,OU=IT,DC=contoso,DC=test", string? sid = null) => new()
    {
        EventId = Guid.NewGuid(),
        Kind = kind,
        Object = new ObjectRef { Dn = dn, Kind = objectKind, Sid = sid ?? $"{DomainSid}-1105" },
        Changes = changes ?? [],
        DetectedAt = DateTimeOffset.UnixEpoch,
        Source = DetectionSource.IncrementalPoll,
        Tags = tags ?? [],
    };

    private static EventAttribution Resolved(string actorSid, string? reason = null) => new()
    {
        Status = AttributionStatus.Resolved,
        Confidence = AttributionConfidence.High,
        Actor = new PrincipalRef { Sid = actorSid },
        Reason = reason,
    };

    private static AttributeChange Change(string attribute) => new() { Attribute = attribute };

    [Fact]
    public void HrAttributeChangeOfAPrivilegedUser_StaysInfo_LogonSettingsAreHigh()
    {
        Assert.Equal(Severity.Info, Resolver.Resolve(Event(WatchEventKind.AttributesChanged, tags: [EventTags.Privileged], changes: [Change(Attr.Department)]), Context));
        Assert.Equal(Severity.High, Resolver.Resolve(Event(WatchEventKind.AttributesChanged, tags: [EventTags.Privileged], changes: [Change(Attr.ScriptPath)]), Context));
        Assert.Equal(Severity.Medium, Resolver.Resolve(Event(WatchEventKind.AttributesChanged, changes: [Change(Attr.ScriptPath)]), Context));
        Assert.Equal(Severity.High, Resolver.Resolve(Event(WatchEventKind.AttributesChanged, tags: [EventTags.ServiceAccount], changes: [Change(Attr.ServicePrincipalName)]), Context));
    }

    [Fact]
    public void PrivilegedPasswordChange_IsProvisionallyHigh_OwnChangeInfo_ResetHigh()
    {
        var change = Event(WatchEventKind.PasswordChanged, tags: [EventTags.Privileged]);

        Assert.Equal(Severity.High, Resolver.Resolve(change, Context));
        Assert.Equal(Severity.Info, Resolver.ResolveWithAttribution(change, Resolved($"{DomainSid}-1105"), [EventTags.SelfChange], Context));
        Assert.Equal(Severity.High, Resolver.ResolveWithAttribution(change, Resolved($"{DomainSid}-500"), [EventTags.PasswordReset], Context));
        Assert.Equal(Severity.High, Resolver.ResolveWithAttribution(change, new EventAttribution { Status = AttributionStatus.Unknown, Reason = AttributionReasons.NoMatch }, null, Context));
        Assert.Equal(Severity.Info, Resolver.Resolve(Event(WatchEventKind.PasswordChanged), Context));
    }

    [Fact]
    public void KrbtgtPasswordChange_IsExactlyMedium()
    {
        Assert.Equal(Severity.Medium, Resolver.Resolve(Event(WatchEventKind.PasswordChanged, tags: [EventTags.Krbtgt]), Context));
        Assert.Equal(Severity.Medium, Resolver.Resolve(Event(WatchEventKind.PasswordChanged, tags: [EventTags.Krbtgt, EventTags.Privileged]), Context));
    }

    [Fact]
    public void AdminCountSet_AfterPrivilegeGrantedIsInfo_OtherwiseHigh_SystemActorInfo()
    {
        Assert.Equal(Severity.Info, Resolver.Resolve(Event(WatchEventKind.AdminCountSet, tags: [EventTags.Privileged]), Context));
        var orphan = Event(WatchEventKind.AdminCountSet);
        Assert.Equal(Severity.High, Resolver.Resolve(orphan, Context));
        Assert.Equal(Severity.Info, Resolver.ResolveWithAttribution(orphan, Resolved("S-1-5-18", AttributionReasons.System), null, Context));
        Assert.Equal(Severity.High, Resolver.ResolveWithAttribution(orphan, Resolved($"{DomainSid}-500"), null, Context));
    }

    [Fact]
    public void KeyCredential_Matrix()
    {
        AttributeChange Keys(int before, int after) => new() { Attribute = WatchdogAttributes.KeyCredentialLink, OldCount = before, NewCount = after };

        Assert.Equal(Severity.Low, Resolver.Resolve(Event(WatchEventKind.KeyCredentialChanged, WatchObjectKind.Computer, changes: [Keys(0, 1)]), Context));
        Assert.Equal(Severity.High, Resolver.Resolve(Event(WatchEventKind.KeyCredentialChanged, WatchObjectKind.Computer, changes: [Keys(1, 2)]), Context));
        Assert.Equal(Severity.High, Resolver.Resolve(Event(WatchEventKind.KeyCredentialChanged, WatchObjectKind.Computer, [EventTags.DomainController], [Keys(0, 1)]), Context));
        Assert.Equal(Severity.High, Resolver.Resolve(Event(WatchEventKind.KeyCredentialChanged, WatchObjectKind.Computer, [EventTags.Privileged], [Keys(0, 1)]), Context));
        Assert.Equal(Severity.High, Resolver.Resolve(Event(WatchEventKind.KeyCredentialChanged, tags: [EventTags.Privileged], changes: [Keys(0, 1)]), Context));

        var user = Event(WatchEventKind.KeyCredentialChanged, changes: [Keys(0, 1)]);
        Assert.Equal(Severity.Medium, Resolver.Resolve(user, Context));
        Assert.Equal(Severity.Medium, Resolver.ResolveWithAttribution(user, Resolved($"{DomainSid}-1105"), null, Context));
        Assert.Equal(Severity.Medium, Resolver.ResolveWithAttribution(user, Resolved($"{DomainSid}-1500"), [EventTags.SyncAccount], Context));
        Assert.Equal(Severity.High, Resolver.ResolveWithAttribution(user, Resolved($"{DomainSid}-1500"), null, Context));
    }

    [Fact]
    public void TagBumps_NeverLower()
    {
        Assert.Equal(Severity.High, Resolver.Resolve(Event(WatchEventKind.AccountDisabled, tags: [EventTags.Privileged]), Context));
        Assert.Equal(Severity.High, Resolver.Resolve(Event(WatchEventKind.ObjectDeleted, WatchObjectKind.Computer, [EventTags.ServiceAccount]), Context));
        Assert.Equal(Severity.Medium, Resolver.Resolve(Event(WatchEventKind.ComputerCreated, WatchObjectKind.Computer, [EventTags.CreatedByNonAdmin]), Context));
        Assert.Equal(Severity.Info, Resolver.Resolve(Event(WatchEventKind.ComputerCreated, WatchObjectKind.Computer, [EventTags.DefaultContainer]), Context));
        Assert.Equal(Severity.High, Resolver.Resolve(Event(WatchEventKind.AccountEnabled, tags: [EventTags.LongDisabled]), Context));
        Assert.Equal(Severity.Info, Resolver.Resolve(Event(WatchEventKind.AccountDisabled, tags: [EventTags.AdminCountOrphan]), Context));
        Assert.Equal(Severity.Critical, Resolver.Resolve(Event(WatchEventKind.SidHistoryChanged, tags: [EventTags.Reconciliation]), Context));
    }

    [Fact]
    public void MovedOutOfADisabledSubtree_IsHigh_IntoItIsInfo()
    {
        var outOf = Event(WatchEventKind.ObjectMoved, tags: [EventTags.DisabledOu]) with { PreviousDn = "CN=Jan Kowalski,OU=Wyłączone,DC=contoso,DC=test" };
        var into = Event(WatchEventKind.ObjectMoved, tags: [EventTags.DisabledOu], dn: "CN=Jan Kowalski,OU=Wyłączone,DC=contoso,DC=test")
            with { PreviousDn = "CN=Jan Kowalski,OU=IT,DC=contoso,DC=test" };

        Assert.Equal(Severity.High, Resolver.Resolve(outOf, Context));
        Assert.Equal(Severity.Info, Resolver.Resolve(into, Context));
    }

    [Fact]
    public void ProtectedUsersRemoval_IsHigh()
    {
        var removal = Event(WatchEventKind.GroupMemberRemoved, WatchObjectKind.Group, sid: $"{DomainSid}-525");

        Assert.Equal(Severity.High, Resolver.Resolve(removal, Context));
        Assert.Equal(Severity.Info, Resolver.Resolve(removal with { Object = removal.Object with { Sid = $"{DomainSid}-1200" } }, Context));
    }

    [Fact]
    public void ExpiryWithinSevenDays_OfPrivilegedAccounts_IsHigh()
    {
        var expiring = Event(WatchEventKind.PasswordExpiring, tags: [EventTags.Privileged]) with
        {
            Expiry = new ExpiryInfo(ExpiryKind.Password, DateTimeOffset.UnixEpoch, new DateOnly(2026, 10, 21), 7, 7),
        };

        Assert.Equal(Severity.High, Resolver.Resolve(expiring, Context));
        Assert.Equal(Severity.Info, Resolver.Resolve(expiring with { Expiry = expiring.Expiry! with { DaysLeft = 14 } }, Context));
        Assert.Equal(Severity.Info, Resolver.Resolve(expiring with { Tags = [] }, Context));
    }

    [Fact]
    public void BaseSeverities_ByKindAndObject()
    {
        Assert.Equal(Severity.High, Resolver.Resolve(Event(WatchEventKind.ObjectDeleted, WatchObjectKind.OrganizationalUnit), Context));
        Assert.Equal(Severity.Low, Resolver.Resolve(Event(WatchEventKind.ObjectDeleted, WatchObjectKind.Computer), Context));
        Assert.Equal(Severity.High, Resolver.Resolve(Event(WatchEventKind.GroupPolicyLinkChanged, WatchObjectKind.OrganizationalUnit, dn: "OU=Domain Controllers,DC=contoso,DC=test"), Context));
        Assert.Equal(Severity.Medium, Resolver.Resolve(Event(WatchEventKind.GroupPolicyLinkChanged, WatchObjectKind.OrganizationalUnit, dn: "OU=Sprzedaż,DC=contoso,DC=test"), Context));
        Assert.Equal(Severity.High, Resolver.Resolve(Event(WatchEventKind.DomainControllerAdded, WatchObjectKind.DomainController, [EventTags.OtherDomain]), Context));
        Assert.Equal(Severity.Critical, Resolver.Resolve(Event(WatchEventKind.DomainControllerAdded, WatchObjectKind.DomainController), Context));
        Assert.Equal(Severity.Medium, Resolver.Resolve(Event(WatchEventKind.DomainControllerRemoved, WatchObjectKind.DomainController, [EventTags.OtherDomain]), Context));
        Assert.Equal(Severity.Critical, Resolver.Resolve(Event(WatchEventKind.CertificateMappingChanged, tags: [EventTags.Privileged]), Context));
        Assert.Equal(Severity.High, Resolver.Resolve(Event(WatchEventKind.PrivilegeGranted), Context, Severity.High));
        Assert.Equal(Severity.Medium, Resolver.Resolve(Event(WatchEventKind.GroupPolicyChanged, WatchObjectKind.GroupPolicy,
            dn: "CN={31B2F340-016D-11D2-945F-00C04FB984F9},CN=Policies,CN=System,DC=contoso,DC=test"), Context));
    }

    [Fact]
    public void AccountExpirationClearedToNever_IsMedium()
    {
        AttributeChange Expires(string before, string after) => new() { Attribute = Attr.AccountExpires, OldValues = [before], NewValues = [after] };

        Assert.Equal(Severity.Medium, Resolver.Resolve(Event(WatchEventKind.AccountExpirationChanged, changes: [Expires("134050000000000000", "9223372036854775807")]), Context));
        Assert.Equal(Severity.Info, Resolver.Resolve(Event(WatchEventKind.AccountExpirationChanged, changes: [Expires("9223372036854775807", "134050000000000000")]), Context));
    }

    [Fact]
    public void ResolveWithAttribution_KeepsNonDependentKinds()
    {
        var sidHistory = Event(WatchEventKind.SidHistoryChanged) with { Severity = Severity.Critical };

        Assert.Equal(Severity.Critical, Resolver.ResolveWithAttribution(sidHistory, Resolved("S-1-5-18", AttributionReasons.System)));
        Assert.Equal(3, SeverityResolver.AttributionDependentKinds.Count);
    }
}
