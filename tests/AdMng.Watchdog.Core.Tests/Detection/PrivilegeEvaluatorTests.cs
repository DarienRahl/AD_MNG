using AdMng.Core.ActiveDirectory;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Testing.Scenarios;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Watchdog.Core.Tests.Detection;

/// <summary>PrivilegeGranted / PrivilegeRevoked (303, 304) and the PrivilegeState (section 10.8).</summary>
public class PrivilegeEvaluatorTests
{
    [Fact]
    public async Task DirectMember_IsGrantedCriticalAndTaggedPrivileged()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        await scenario.AddValuesAsync(scenario.Builder.DomainAdminsDn, Attr.Member, scenario.Company.JanKowalski);
        var events = await scenario.CycleEventsAsync();

        var added = Assert.Single(events, e => e.Kind == WatchEventKind.GroupMemberAdded);
        Assert.DoesNotContain(EventTags.AdminCountOrphan, added.Tags);
        var granted = Assert.Single(events, e => e.Kind == WatchEventKind.PrivilegeGranted);
        Assert.Equal(scenario.Company.JanKowalski, granted.Object.Dn);
        Assert.Equal(scenario.Builder.DomainAdminsDn, granted.Member!.Dn);
        Assert.Empty(granted.Via);
        Assert.Equal(Severity.Critical, granted.Severity);
        Assert.Contains(scenario.Store.Privileges!.Principals, p => p.PrincipalKey == scenario.Snapshot(scenario.Company.JanKowalski)!.ObjectGuid.ToString("N"));

        await scenario.ReplaceAsync(scenario.Company.JanKowalski, Attr.ScriptPath, "logon-admin.cmd");
        var later = Assert.Single(await scenario.CycleEventsAsync());
        Assert.Contains(EventTags.Privileged, later.Tags);
        Assert.Equal(Severity.High, later.Severity);
    }

    [Fact]
    public async Task NestedThreeLevels_GrantsEveryMemberWithThePath()
    {
        string? outer = null;
        string? middle = null;
        await using var scenario = new DetectionScenario(seed: b =>
        {
            middle = b.AddGroup("GG_Administracja_Środkowa", b.UsersDn);
            outer = b.AddGroup("GG_Administracja_Zewnętrzna", b.UsersDn, members: [middle]);
        });
        await scenario.BaselineAsync();
        await scenario.AddValuesAsync(middle!, Attr.Member, scenario.Company.GroupIt);
        await scenario.CycleEventsAsync();

        await scenario.AddValuesAsync(scenario.Builder.DomainAdminsDn, Attr.Member, outer!);
        var granted = (await scenario.CycleEventsAsync()).Where(e => e.Kind == WatchEventKind.PrivilegeGranted).ToList();

        var jan = Assert.Single(granted, e => e.Object.Dn == scenario.Company.JanKowalski);
        Assert.Equal([outer, middle, scenario.Company.GroupIt], jan.Via.Select(v => v.Dn));
        Assert.Contains(granted, e => e.Object.Dn == scenario.Company.LukaszWisniewski);
        Assert.Contains(granted, e => e.Object.Dn == outer);
        Assert.Equal(5, granted.Count);
    }

    [Fact]
    public async Task MembershipCycle_TerminatesAndGrantsOnce()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        await scenario.AddValuesAsync(scenario.Company.GroupIt, Attr.Member, scenario.Company.GroupAll);
        await scenario.CycleEventsAsync();

        await scenario.AddValuesAsync(scenario.Builder.DomainAdminsDn, Attr.Member, scenario.Company.GroupAll);
        var granted = (await scenario.CycleEventsAsync()).Where(e => e.Kind == WatchEventKind.PrivilegeGranted).ToList();

        Assert.Equal(granted.Count, granted.Select(e => e.Object.Dn).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(granted, e => e.Object.Dn == scenario.Company.ZanetaSzymanska);
        Assert.Contains(granted, e => e.Object.Dn == scenario.Company.JanKowalski);
    }

    [Fact]
    public async Task PrimaryGroupDomainAdmins_IsPrimaryGroupChangedAndPrivilegeGrantedViaPrimaryGroup()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        await scenario.ReplaceAsync(scenario.Company.BartlomiejKozlowski, Attr.PrimaryGroupId, WellKnownRids.DomainAdmins);
        var events = await scenario.CycleEventsAsync();

        var primary = Assert.Single(events, e => e.Kind == WatchEventKind.PrimaryGroupChanged);
        Assert.Equal(Severity.High, primary.Severity);
        var granted = Assert.Single(events, e => e.Kind == WatchEventKind.PrivilegeGranted);
        Assert.Equal(scenario.Company.BartlomiejKozlowski, granted.Object.Dn);
        Assert.Single(scenario.Store.Privileges!.Principals, p => p.ViaPrimaryGroup);
    }

    [Fact]
    public async Task RemovedMember_IsRevokedMedium()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        await scenario.AddValuesAsync(scenario.Builder.DomainAdminsDn, Attr.Member, scenario.Company.JanKowalski);
        await scenario.CycleEventsAsync();

        await scenario.RemoveValuesAsync(scenario.Builder.DomainAdminsDn, Attr.Member, scenario.Company.JanKowalski);
        var events = await scenario.CycleEventsAsync();

        var revoked = Assert.Single(events, e => e.Kind == WatchEventKind.PrivilegeRevoked);
        Assert.Equal(Severity.Medium, revoked.Severity);
        Assert.Equal(scenario.Company.JanKowalski, revoked.Object.Dn);
    }

    [Fact]
    public async Task DeletedPrivilegedPrincipal_IsNotRevoked()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        await scenario.AddValuesAsync(scenario.Builder.DomainAdminsDn, Attr.Member, scenario.Company.JanKowalski);
        await scenario.CycleEventsAsync();

        await scenario.DeleteAsync(scenario.Company.JanKowalski);
        var events = await scenario.CycleEventsAsync();

        Assert.DoesNotContain(events, e => e.Kind is WatchEventKind.PrivilegeRevoked or WatchEventKind.GroupMemberRemoved);
        Assert.Equal(Severity.High, Assert.Single(events, e => e.Kind == WatchEventKind.ObjectDeleted).Severity);
    }

    [Fact]
    public async Task DeletedPrivilegedPrincipal_IsNotRevokedWhenTheCycleRepeats()
    {
        string? admin = null;
        await using var scenario = new DetectionScenario(seed: b => admin = b.AddUser("Adam", "Admiński", b.UsersDn));
        await scenario.BaselineAsync();
        await scenario.AddValuesAsync(scenario.Builder.DomainAdminsDn, Attr.Member, admin!);
        await scenario.CycleEventsAsync();
        scenario.Advance(TimeSpan.FromSeconds(15));

        await scenario.DeleteAsync(admin!);
        scenario.Store.FailNextFinalCommit();
        await Assert.ThrowsAsync<IOException>(scenario.RunCycleAsync);
        var crashed = scenario.TakeEvents();
        scenario.Advance(TimeSpan.FromSeconds(15));
        var repeated = await scenario.CycleEventsAsync();

        Assert.Single(crashed.Concat(repeated), e => e.Kind == WatchEventKind.ObjectDeleted);
        Assert.DoesNotContain(repeated, e => e.Kind == WatchEventKind.PrivilegeRevoked);
        Assert.DoesNotContain(scenario.Store.Privileges!.Principals, p => p.PrincipalKey == scenario.Store.Snapshots.Single(s => s.Dn == admin).ObjectGuid.ToString("N"));
    }

    [Fact]
    public async Task DisappearedPrivilegedPrincipal_IsNotRevokedWhenTheReconciliationRepeats()
    {
        string? admin = null;
        await using var scenario = new DetectionScenario(seed: b => admin = b.AddUser("Adam", "Admiński", b.UsersDn));
        await scenario.BaselineAsync();
        await scenario.AddValuesAsync(scenario.Builder.DomainAdminsDn, Attr.Member, admin!);
        await scenario.CycleEventsAsync();
        scenario.Directory.DenyDeletedObjects = true;
        await scenario.DeleteAsync(admin!);
        await scenario.CycleEventsAsync();

        scenario.Advance(TimeSpan.FromHours(17));
        scenario.Store.FailNextFinalCommit();
        await Assert.ThrowsAsync<IOException>(scenario.RunCycleAsync);
        var crashed = scenario.TakeEvents();
        var repeated = await scenario.CycleEventsAsync();

        Assert.Single(crashed.Concat(repeated), e => e.Kind == WatchEventKind.ObjectDisappeared);
        Assert.DoesNotContain(crashed.Concat(repeated), e => e.Kind == WatchEventKind.PrivilegeRevoked);
    }

    [Fact]
    public async Task RemovedMemberOutsideTheSnapshot_IsStillRevoked()
    {
        string? archive = null;
        string? admin = null;
        await using var scenario = new DetectionScenario(seed: b =>
        {
            archive = b.AddOu("Archiwum", b.DomainDn);
            admin = b.AddUser("Stary", "Administrator", archive);
        });
        scenario.Config.Update(c => c with { Directory = c.Directory with { ExcludedSubtrees = [archive!] } });
        await scenario.BaselineAsync();
        await scenario.AddValuesAsync(scenario.Builder.DomainAdminsDn, Attr.Member, admin!);
        Assert.Single(await scenario.CycleEventsAsync(), e => e.Kind == WatchEventKind.PrivilegeGranted);

        await scenario.RemoveValuesAsync(scenario.Builder.DomainAdminsDn, Attr.Member, admin!);

        Assert.Single(await scenario.CycleEventsAsync(), e => e.Kind == WatchEventKind.PrivilegeRevoked);
    }

    [Fact]
    public async Task RestoredPrivilegedPrincipal_IsGrantedWithTagRestored()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        await scenario.AddValuesAsync(scenario.Builder.DomainAdminsDn, Attr.Member, scenario.Company.JanKowalski);
        await scenario.CycleEventsAsync();
        await scenario.DeleteAsync(scenario.Company.JanKowalski);
        await scenario.CycleEventsAsync();

        var tombstone = scenario.Directory.DeletedDistinguishedNames.Single(d => d.Contains("Jan Kowalski", StringComparison.Ordinal));
        await scenario.RestoreAsync(tombstone, scenario.Company.JanKowalski);
        var events = await scenario.CycleEventsAsync();

        var restored = Assert.Single(events, e => e.Kind == WatchEventKind.ObjectRestored);
        var granted = Assert.Single(events, e => e.Kind == WatchEventKind.PrivilegeGranted);
        Assert.Contains(EventTags.Restored, granted.Tags);
        Assert.Equal(restored.ChangeSetId, granted.ChangeSetId);
    }

    [Fact]
    public async Task WatchdogRoleGroups_AreHighTierRoots()
    {
        string? viewers = null;
        string? sid = null;
        await using var scenario = new DetectionScenario(
            seed: b =>
            {
                viewers = b.AddGroup("GG_Watchdog_Czytelnicy", b.UsersDn);
                sid = b.Session.GetValues(viewers, Attr.ObjectSid).Single();
            },
            services: s => s.AddSingleton(new DetectionHostOptions { RoleGroupSids = [sid!] }));
        await scenario.BaselineAsync();

        await scenario.AddValuesAsync(viewers!, Attr.Member, scenario.Company.MalgorzataWojcik);
        var granted = Assert.Single(await scenario.CycleEventsAsync(), e => e.Kind == WatchEventKind.PrivilegeGranted);

        Assert.Equal(Severity.High, granted.Severity);
        Assert.Contains(scenario.Store.Privileges!.Principals, p => p.Tier == PrivilegeTier.High);
    }

    [Fact]
    public async Task CrashAfterTheMembershipChunk_GrantsExactlyOnce()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        scenario.Advance(TimeSpan.FromSeconds(15));
        await scenario.AddValuesAsync(scenario.Builder.DomainAdminsDn, Attr.Member, scenario.Company.PawelLewandowski);

        scenario.Store.FailCommit(2);
        await Assert.ThrowsAsync<IOException>(scenario.RunCycleAsync);
        var events = scenario.TakeEvents().ToList();
        Assert.Single(events, e => e.Kind == WatchEventKind.GroupMemberAdded);
        events.AddRange(await scenario.CycleEventsAsync());

        Assert.Single(events, e => e.Kind == WatchEventKind.PrivilegeGranted);
        Assert.Single(events, e => e.Kind == WatchEventKind.GroupMemberAdded);
    }
}
