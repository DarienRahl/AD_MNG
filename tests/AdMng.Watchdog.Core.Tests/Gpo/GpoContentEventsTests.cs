using AdMng.Core.GroupPolicy.Content;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Gpo;

namespace AdMng.Watchdog.Core.Tests.Gpo;

/// <summary>Kinds 510–512, their severities (8.6) and linked scope from the gPLink snapshot.</summary>
public sealed class GpoContentEventsTests
{
    private const string DomainDn = "DC=contoso,DC=com";
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 30, 0, TimeSpan.Zero);
    private static readonly string Link = "[LDAP://cn={6AC1786C-016F-11D2-945F-00C04FB984F9},cn=policies,cn=system,DC=contoso,DC=com;0]";
    private static readonly string OtherLink = "[LDAP://cn={31B2F340-016D-11D2-945F-00C04FB984F9},cn=policies,cn=system,DC=contoso,DC=com;0]";

    [Fact]
    public void LinkScopeFromGpLinks()
    {
        Assert.True(Scope(("DC=contoso,DC=com", OtherLink + Link)).LinkedToDomainHead);
        Assert.True(Scope(("OU=Domain Controllers,DC=contoso,DC=com", Link)).LinkedToDomainControllers);
        var ou = Scope(("OU=Stacje robocze,DC=contoso,DC=com", Link));
        Assert.Equal((true, false), (ou.Linked, ou.Tier0));
        Assert.False(Scope(("DC=contoso,DC=com", OtherLink)).Linked);
        Assert.False(Scope(("DC=contoso,DC=com", null), ("OU=x,DC=contoso,DC=com", "garbage[")).Linked);
        // a disabled (1) or enforced (2) link still counts
        Assert.True(Scope(("DC=contoso,DC=com", Link.Replace(";0]", ";1]", StringComparison.Ordinal))).Tier0);
        Assert.True(Scope(("CN=Warszawa,CN=Sites,CN=Configuration,DC=contoso,DC=com", Link.Replace(";0]", ";2]", StringComparison.Ordinal))).Linked);
    }

    [Theory]
    [InlineData(true, true, false, Severity.Critical)]
    [InlineData(true, false, false, Severity.High)]
    [InlineData(false, true, false, Severity.Medium)]
    [InlineData(false, false, false, Severity.Low)]
    [InlineData(false, false, true, Severity.Critical)]
    public void SettingsChangedSeverity(bool sensitive, bool tier0, bool cpassword, Severity expected)
    {
        var diff = new GpoSettingsDiff { SensitiveChanged = sensitive, GppPasswordPresent = cpassword, Modified = 1 };
        var scope = tier0 ? new GpoLinkScope { Linked = true, LinkedToDomainHead = true } : GpoLinkScope.Unlinked;
        Assert.Equal(expected, GpoSeverityClassifier.SettingsChanged(diff, scope));
    }

    [Fact]
    public void SensitiveChangeInAnOuLinkedGpoIsHigh()
    {
        var diff = new GpoSettingsDiff { SensitiveChanged = true, Modified = 1 };

        Assert.Equal(Severity.High, GpoSeverityClassifier.SettingsChanged(diff, new GpoLinkScope { Linked = true }));
        Assert.Equal(Severity.Medium, GpoSeverityClassifier.SettingsChanged(diff with { SensitiveChanged = false }, new GpoLinkScope { Linked = true }));
    }

    [Fact]
    public void SettingsChangedEvent()
    {
        var changeSet = Guid.CreateVersion7(Now);
        var diff = GpoSettingsDiffer.Diff([], [new GpoSetting { Key = "Computer/Preferences/Groups/{1}/cpassword", Kind = GpoSettingKind.Masked, Category = GpoSettingCategory.PreferenceGroups }]);

        var e = GpoContentEvents.SettingsChanged(Context() with { ChangeSetId = changeSet }, diff);

        Assert.Equal(WatchEventKind.GroupPolicySettingsChanged, e.Kind);
        Assert.Equal(Severity.Critical, e.Severity);
        Assert.Equal([EventTags.GppPassword], e.Tags);
        Assert.Equal(changeSet, e.ChangeSetId);
        Assert.Equal(AttributionStatus.Pending, e.Attribution.Status);
        Assert.Equal(diff.Changes, e.Changes);
        Assert.Equal(WatchObjectKind.GroupPolicy, e.Object.Kind);
    }

    [Fact]
    public void ContentOutOfBandEvent()
    {
        var diff = GpoSettingsDiffer.Diff([], [new GpoSetting { Key = "Computer/Scripts/Startup/0", Value = "x.cmd", Category = GpoSettingCategory.Scripts }]);

        var unlinked = GpoContentEvents.ContentOutOfBand(Context() with { Source = DetectionSource.Reconciliation }, diff);
        var domainHead = GpoContentEvents.ContentOutOfBand(Context() with { Scope = new GpoLinkScope { Linked = true, LinkedToDomainControllers = true } }, diff);

        Assert.Equal((WatchEventKind.GroupPolicyContentOutOfBand, Severity.High, DetectionSource.Reconciliation), (unlinked.Kind, unlinked.Severity, unlinked.Source));
        Assert.Equal(Severity.Critical, domainHead.Severity);
        Assert.Equal((AttributionStatus.Unknown, AttributionReasons.SysvolNotAudited), (unlinked.Attribution.Status, unlinked.Attribution.Reason));
        Assert.Empty(unlinked.Tags);
    }

    [Fact]
    public void PathRedirectedEvent()
    {
        var e = GpoContentEvents.PathRedirected(Context(), "\\\\evil.example\\share\\{6AC1786C-016F-11D2-945F-00C04FB984F9}\u0007");

        Assert.Equal((WatchEventKind.GroupPolicyPathRedirected, Severity.Critical), (e.Kind, e.Severity));
        var change = Assert.Single(e.Changes);
        Assert.Equal("gPCFileSysPath", change.Attribute);
        Assert.Equal(["\\\\evil.example\\share\\{6AC1786C-016F-11D2-945F-00C04FB984F9}�"], change.NewValues);
        Assert.Equal(AttributionStatus.Pending, e.Attribution.Status);
    }

    internal static GpoEventContext Context() => new()
    {
        Gpo = new ObjectRef
        {
            ObjectGuid = Guid.Parse("0f3c7a52-6c1e-4a7e-9a39-3f3b1c2d4e5f"),
            Dn = "CN={6AC1786C-016F-11D2-945F-00C04FB984F9},CN=Policies,CN=System,DC=contoso,DC=com",
            Name = "{6AC1786C-016F-11D2-945F-00C04FB984F9}",
            DisplayName = "Zasady kontrolerów domeny",
            Kind = WatchObjectKind.GroupPolicy,
        },
        Domain = "contoso.com",
        DetectedAt = Now,
    };

    private static GpoLinkScope Scope(params (string Container, string? GpLink)[] containers) =>
        GpoLinkScope.From(GpoFixtures.Gpo, DomainDn, containers);
}
