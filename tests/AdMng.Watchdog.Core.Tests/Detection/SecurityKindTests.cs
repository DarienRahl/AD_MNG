using AdMng.Core.ActiveDirectory;
using AdMng.Testing;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Testing.Scenarios;

namespace AdMng.Watchdog.Core.Tests.Detection;

/// <summary>Security kinds (400–410) end to end through the fake directory.</summary>
public class SecurityKindTests
{
    private static readonly Guid ReplicationGetChangesAll = new("1131f6ad-9c07-11d1-f79f-00c04fc2dcd2");

    [Fact]
    public async Task AdminCountSet_IsHighOutsideThePrivilegeState()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        await scenario.ReplaceAsync(scenario.Company.LukaszWisniewski, Attr.AdminCount, 1);
        var set = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(WatchEventKind.AdminCountSet, set.Kind);
        Assert.Equal(Severity.High, set.Severity);
        Assert.Equal(AttributionStatus.Pending, set.Attribution.Status);
    }

    [Fact]
    public async Task AdminCountSet_IsInfoForAPrivilegedPrincipal()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        await scenario.AddValuesAsync(scenario.Builder.DomainAdminsDn, Attr.Member, scenario.Company.LukaszWisniewski);
        await scenario.CycleEventsAsync();

        await scenario.ReplaceAsync(scenario.Company.LukaszWisniewski, Attr.AdminCount, 1);
        var set = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(WatchEventKind.AdminCountSet, set.Kind);
        Assert.Contains(EventTags.Privileged, set.Tags);
        Assert.Equal(Severity.Info, set.Severity);
    }

    [Fact]
    public async Task SidHistoryChanged_IsCritical()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        scenario.Directory.SetValues(scenario.Company.PawelLewandowski, Attr.SidHistory, "S-1-5-21-1111111111-2222222222-3333333333-512");
        var changed = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(WatchEventKind.SidHistoryChanged, changed.Kind);
        Assert.Equal(Severity.Critical, changed.Severity);
        Assert.Equal(["S-1-5-21-1111111111-2222222222-3333333333-512"], Assert.Single(changed.Changes).NewValues);
    }

    [Fact]
    public async Task DelegationChanged_ConstrainedIsHighAndUnconstrainedOnAComputerCritical()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        await scenario.ReplaceAsync(scenario.Company.Computer1, Attr.AllowedToDelegateTo, "cifs/fs01.contoso.test");
        await scenario.ReplaceAsync(scenario.Company.Computer2, Attr.UserAccountControl,
            (int)(UserAccountControl.WorkstationTrustAccount | UserAccountControl.TrustedForDelegation));
        var events = await scenario.CycleEventsAsync();

        Assert.Equal(Severity.High, Assert.Single(events, e => e.Object.Dn == scenario.Company.Computer1 && e.Kind == WatchEventKind.DelegationChanged).Severity);
        Assert.Equal(Severity.Critical, Assert.Single(events, e => e.Object.Dn == scenario.Company.Computer2 && e.Kind == WatchEventKind.DelegationChanged).Severity);
    }

    [Fact]
    public async Task ServicePrincipalNameAdded_OnAUserIsHighAndRemovalIsAnAttributeChange()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        await scenario.AddValuesAsync(scenario.Company.GrazynaDabrowska, Attr.ServicePrincipalName, "HTTP/raporty.contoso.test");
        var added = Assert.Single(await scenario.CycleEventsAsync());
        await scenario.ClearAsync(scenario.Company.GrazynaDabrowska, Attr.ServicePrincipalName);
        var removed = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(WatchEventKind.ServicePrincipalNameAdded, added.Kind);
        Assert.Equal(Severity.High, added.Severity);
        Assert.Equal(WatchEventKind.AttributesChanged, removed.Kind);
    }

    [Fact]
    public async Task KeyCredentialChanged_ComputerFirstValueIsLowAndUserMedium()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        var keyHex = string.Concat(Enumerable.Repeat("0A1B2C3D", 8));

        await scenario.ReplaceAsync(scenario.Company.Laptop, WatchdogAttributes.KeyCredentialLink, $"B:{keyHex.Length}:{keyHex}:{scenario.Company.Laptop}");
        await scenario.ReplaceAsync(scenario.Company.MalgorzataWojcik, WatchdogAttributes.KeyCredentialLink, $"B:{keyHex.Length}:{keyHex}:{scenario.Company.MalgorzataWojcik}");
        var events = await scenario.CycleEventsAsync();

        var computer = Assert.Single(events, e => e.Object.Dn == scenario.Company.Laptop);
        Assert.Equal(WatchEventKind.KeyCredentialChanged, computer.Kind);
        Assert.Equal(Severity.Low, computer.Severity);
        Assert.Equal(Severity.Medium, Assert.Single(events, e => e.Object.Dn == scenario.Company.MalgorzataWojcik).Severity);
    }

    [Fact]
    public async Task RenameOfAUserWithKeyCredentials_IsOnlyObjectRenamed()
    {
        await using var scenario = new DetectionScenario();
        var keyHex = string.Concat(Enumerable.Repeat("FFEE", 16));
        scenario.Directory.SeedAttributes(scenario.Company.MalgorzataWojcik, new Dictionary<string, object?>
        {
            [WatchdogAttributes.KeyCredentialLink] = $"B:{keyHex.Length}:{keyHex}:{scenario.Company.MalgorzataWojcik}",
        });
        await scenario.BaselineAsync();

        await scenario.MoveAsync(scenario.Company.MalgorzataWojcik, null, "CN=Małgorzata Wójcik-Zając");

        Assert.Equal(WatchEventKind.ObjectRenamed, Assert.Single(await scenario.CycleEventsAsync()).Kind);
    }

    [Fact]
    public async Task SecurityDescriptorChanged_OnAdminSdHolderNamesTheAddedRight()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        var builder = scenario.Builder;
        var helpdesk = $"{builder.DomainSid}-1300";
        var descriptor = FakeSecurityDescriptor.Build(
            $"{builder.DomainSid}-{WellKnownRids.DomainAdmins}",
            $"{builder.DomainSid}-{WellKnownRids.DomainAdmins}",
            FakeAce.FullControl($"{builder.DomainSid}-{WellKnownRids.DomainAdmins}"),
            FakeAce.FullControl("S-1-5-32-544"),
            FakeAce.ExtendedRight(helpdesk, ReplicationGetChangesAll));

        await scenario.ReplaceAsync(builder.AdminSdHolderDn, Attr.NtSecurityDescriptor, descriptor);
        var changed = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(WatchEventKind.SecurityDescriptorChanged, changed.Kind);
        Assert.Equal(Severity.Critical, changed.Severity);
        var change = Assert.Single(changed.Changes);
        Assert.Single(change.NewValues.Except(change.OldValues));
        Assert.Contains(helpdesk, change.NewValues.Except(change.OldValues).Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecurityDescriptorRewriteWithReorderedAces_IsNoEvent()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        var builder = scenario.Builder;
        var reordered = FakeSecurityDescriptor.Build(
            $"{builder.DomainSid}-{WellKnownRids.DomainAdmins}",
            $"{builder.DomainSid}-{WellKnownRids.DomainAdmins}",
            FakeAce.FullControl("S-1-5-32-544"),
            FakeAce.FullControl($"{builder.DomainSid}-{WellKnownRids.DomainAdmins}"));

        await scenario.ReplaceAsync(builder.AdminSdHolderDn, Attr.NtSecurityDescriptor, reordered);

        Assert.Empty(await scenario.CycleEventsAsync());
    }

    [Fact]
    public async Task ComputerNameSpoofing_WhenAComputerTakesADomainControllerName()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        await scenario.ReplaceAsync(scenario.Company.Computer2, Attr.SamAccountName, "DC01");
        var events = await scenario.CycleEventsAsync();

        var spoofing = Assert.Single(events, e => e.Kind == WatchEventKind.ComputerNameSpoofing);
        Assert.Equal(Severity.Critical, spoofing.Severity);
    }

    [Fact]
    public async Task ManagedAccountLinkChanged_IsCriticalForAPrivilegedPredecessor()
    {
        string? dmsa = null;
        await using var scenario = new DetectionScenario(seed: b => dmsa = b.AddServiceAccount("dmsa-erp01", ObjectClasses.DelegatedManagedServiceAccount));
        await scenario.BaselineAsync();

        await scenario.ReplaceAsync(dmsa!, WatchdogAttributes.ManagedAccountPrecededByLink, scenario.Builder.AdministratorDn);
        var linked = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(WatchEventKind.ManagedAccountLinkChanged, linked.Kind);
        Assert.Equal(Severity.Critical, linked.Severity);
    }

    [Fact]
    public async Task CertificateMappingChanged_IsHighAndCriticalForPrivileged()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        await scenario.ReplaceAsync(scenario.Company.JanKowalski, WatchdogAttributes.AltSecurityIdentities, "X509:<I>DC=test,DC=contoso,CN=CA<SR>1234");
        await scenario.ReplaceAsync(scenario.Builder.AdministratorDn, WatchdogAttributes.AltSecurityIdentities, "X509:<I>DC=test,DC=contoso,CN=CA<SR>9999");
        var events = await scenario.CycleEventsAsync();

        Assert.Equal(Severity.High, Assert.Single(events, e => e.Object.Dn == scenario.Company.JanKowalski).Severity);
        var admin = Assert.Single(events, e => e.Object.Dn == scenario.Builder.AdministratorDn);
        Assert.Equal(WatchEventKind.CertificateMappingChanged, admin.Kind);
        Assert.Equal(Severity.Critical, admin.Severity);
    }

    [Fact]
    public async Task ManagedPasswordReadersChanged_ListsTheAddedReader()
    {
        string? gmsa = null;
        await using var scenario = new DetectionScenario(seed: b => gmsa = b.AddServiceAccount("gmsa-iis01"));
        await scenario.BaselineAsync();
        var reader = $"{scenario.Builder.DomainSid}-1500";
        var readers = FakeSecurityDescriptor.Build("S-1-5-32-544", null, FakeAce.FullControl(reader));

        await scenario.ReplaceAsync(gmsa!, WatchdogAttributes.GroupMsaMembership, readers);
        var changed = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(WatchEventKind.ManagedPasswordReadersChanged, changed.Kind);
        Assert.Equal(Severity.High, changed.Severity);
        Assert.Contains(changed.Changes.SelectMany(c => c.NewValues), v => v.Contains(reader, StringComparison.Ordinal));
    }
}
