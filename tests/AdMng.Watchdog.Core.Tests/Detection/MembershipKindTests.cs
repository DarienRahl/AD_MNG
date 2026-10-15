using AdMng.Core.ActiveDirectory;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Testing.Scenarios;

namespace AdMng.Watchdog.Core.Tests.Detection;

/// <summary>Membership kinds (300–302) and member-list storage (section 10.8).</summary>
public class MembershipKindTests
{
    [Fact]
    public async Task GroupMemberAddedAndRemoved_OneEventPerMember()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        await scenario.AddValuesAsync(scenario.Company.GroupIt, Attr.Member, scenario.Company.MalgorzataWojcik, scenario.Company.PawelLewandowski);
        await scenario.RemoveValuesAsync(scenario.Company.GroupIt, Attr.Member, scenario.Company.LukaszWisniewski);
        var events = await scenario.CycleEventsAsync();

        var added = events.Where(e => e.Kind == WatchEventKind.GroupMemberAdded).ToList();
        Assert.Equal(2, added.Count);
        Assert.All(added, e => Assert.Equal(scenario.Company.GroupIt, e.Object.Dn));
        Assert.Contains(added, e => e.Member!.Dn == scenario.Company.MalgorzataWojcik && e.Member.ObjectGuid is not null);
        var removed = Assert.Single(events, e => e.Kind == WatchEventKind.GroupMemberRemoved);
        Assert.Equal(scenario.Company.LukaszWisniewski, removed.Member!.Dn);
        Assert.Equal(3, events.Count);
        Assert.Single(events.Select(e => e.ChangeSetId).Distinct());
    }

    [Fact]
    public async Task RangedMemberLists_AreReadCompletely()
    {
        await using var scenario = new DetectionScenario(seed: b => b.Session.MaxValueRange = 5);
        await scenario.BaselineAsync();
        var users = Enumerable.Range(1, 12).Select(i => scenario.Builder.AddUser("Użytkownik", $"Testowy{i:D2}", scenario.Company.SalesOu)).ToArray();
        await scenario.SettleAsync();

        await scenario.AddValuesAsync(scenario.Company.GroupSales, Attr.Member, users);
        var events = await scenario.CycleEventsAsync();

        Assert.Equal(12, events.Count(e => e.Kind == WatchEventKind.GroupMemberAdded));
        var guid = scenario.Snapshot(scenario.Company.GroupSales)!.ObjectGuid;
        Assert.Equal(15, (await scenario.Store.GetMembersAsync(guid, TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task RenamedMember_IsNotAMembershipChange()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        var renamed = await scenario.MoveAsync(scenario.Company.JanKowalski, null, "CN=Jan Kowalski-Nowak");
        var events = await scenario.CycleEventsAsync();

        Assert.Equal(WatchEventKind.ObjectRenamed, Assert.Single(events).Kind);
        var group = scenario.Snapshot(scenario.Company.GroupIt)!.ObjectGuid;
        var jan = scenario.Snapshot(renamed)!.ObjectGuid.ToString("N");
        Assert.Contains(await scenario.Store.GetMembersAsync(group, TestContext.Current.CancellationToken), m => m.Key == jan);

        await scenario.RemoveValuesAsync(scenario.Company.GroupIt, Attr.Member, renamed);
        var removed = Assert.Single(await scenario.CycleEventsAsync());
        Assert.Equal(WatchEventKind.GroupMemberRemoved, removed.Kind);
        Assert.Equal(renamed, removed.Member!.Dn);
    }

    [Fact]
    public async Task PrivilegedAndWatchListOnly_TracksOnlyThoseGroupsButStillFindsNestedPrivileges()
    {
        string? watched = null;
        await using var scenario = new DetectionScenario(seed: b => watched = b.AddGroup("GG_Kadry_Płace", b.UsersDn));
        scenario.Config.Update(c => c with
        {
            Directory = c.Directory with { MembershipTracking = MembershipTracking.PrivilegedAndWatchListOnly },
            PrivilegedGroups = c.PrivilegedGroups with { WatchListDns = [watched!] },
        });
        await scenario.BaselineAsync();

        await scenario.AddValuesAsync(scenario.Company.GroupIt, Attr.Member, scenario.Company.PawelLewandowski);
        await scenario.AddValuesAsync(watched!, Attr.Member, scenario.Company.MalgorzataWojcik);
        await scenario.AddValuesAsync(scenario.Builder.DomainAdminsDn, Attr.Member, scenario.Company.GroupIt);
        var events = await scenario.CycleEventsAsync();

        Assert.DoesNotContain(events, e => e.Kind == WatchEventKind.GroupMemberAdded && e.Object.Dn == scenario.Company.GroupIt);
        Assert.Single(events, e => e.Kind == WatchEventKind.GroupMemberAdded && e.Object.Dn == watched);
        Assert.Single(events, e => e.Kind == WatchEventKind.GroupMemberAdded && e.Object.Dn == scenario.Builder.DomainAdminsDn);
        var granted = events.Where(e => e.Kind == WatchEventKind.PrivilegeGranted).Select(e => e.Object.Dn).ToList();
        Assert.Contains(scenario.Company.JanKowalski, granted);
        Assert.Contains(scenario.Company.PawelLewandowski, granted);
    }

    [Fact]
    public async Task PrivilegedAndWatchListOnly_FirstTrackedReadOfANestedGroup_ReportsNoExistingMembers()
    {
        string? adam = null;
        string? celina = null;
        string? nested = null;
        await using var scenario = WatchListOnly(b =>
        {
            adam = b.AddUser("Adam", "Pierwszy", b.UsersDn);
            celina = b.AddUser("Celina", "Trzecia", b.UsersDn);
            nested = b.AddGroup("GG_Zagnieżdżona", b.UsersDn, members: [adam, celina]);
        });
        await scenario.BaselineAsync();
        await scenario.AddValuesAsync(scenario.Builder.DomainAdminsDn, Attr.Member, nested!);
        var granted = (await scenario.CycleEventsAsync()).Where(e => e.Kind == WatchEventKind.PrivilegeGranted).Select(e => e.Object.Dn).ToList();
        Assert.Equal(new[] { adam, celina, nested }.Order(StringComparer.Ordinal), granted.Order(StringComparer.Ordinal));

        // The nested group is now tracked (it is in Domain Admins' stored list); its first read stores the list silently.
        await scenario.ReplaceAsync(nested!, Attr.Description, "Grupa administratorów oddziału");
        var touched = await scenario.CycleEventsAsync();
        Assert.DoesNotContain(touched, e => e.Kind is WatchEventKind.GroupMemberAdded or WatchEventKind.PrivilegeGranted or WatchEventKind.PrivilegeRevoked);

        await scenario.AddValuesAsync(nested!, Attr.Member, scenario.Company.JanKowalski);
        var added = await scenario.CycleEventsAsync();
        Assert.Equal(scenario.Company.JanKowalski, Assert.Single(added, e => e.Kind == WatchEventKind.GroupMemberAdded).Member!.Dn);
        Assert.Equal(scenario.Company.JanKowalski, Assert.Single(added, e => e.Kind == WatchEventKind.PrivilegeGranted).Object.Dn);
    }

    [Fact]
    public async Task PrivilegedAndWatchListOnly_NestedGroupReAdded_GrantsItsCurrentMembers()
    {
        string? adam = null;
        string? barbara = null;
        string? nested = null;
        await using var scenario = WatchListOnly(b =>
        {
            adam = b.AddUser("Adam", "Pierwszy", b.UsersDn);
            barbara = b.AddUser("Barbara", "Druga", b.UsersDn);
            nested = b.AddGroup("GG_Zagnieżdżona", b.UsersDn, members: [adam]);
        });
        await scenario.BaselineAsync();
        await scenario.AddValuesAsync(scenario.Builder.DomainAdminsDn, Attr.Member, nested!);
        await scenario.CycleEventsAsync();
        await scenario.ReplaceAsync(nested!, Attr.Description, "x");
        await scenario.CycleEventsAsync();
        await scenario.RemoveValuesAsync(scenario.Builder.DomainAdminsDn, Attr.Member, nested!);
        Assert.Equal(2, (await scenario.CycleEventsAsync()).Count(e => e.Kind == WatchEventKind.PrivilegeRevoked));

        // While it is outside Domain Admins, Barbara joins the (no longer tracked) group.
        await scenario.AddValuesAsync(nested!, Attr.Member, barbara!);
        await scenario.CycleEventsAsync();
        await scenario.AddValuesAsync(scenario.Builder.DomainAdminsDn, Attr.Member, nested!);
        var readded = await scenario.CycleEventsAsync();

        var granted = readded.Where(e => e.Kind == WatchEventKind.PrivilegeGranted).Select(e => e.Object.Dn).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { adam, barbara, nested }.Order(StringComparer.Ordinal), granted);
    }

    [Fact]
    public async Task PrivilegedAndWatchListOnly_RestoredMember_IsNotInsertedIntoAnUntrackedGroup()
    {
        string? adam = null;
        string? ewa = null;
        string? nested = null;
        await using var scenario = WatchListOnly(b =>
        {
            adam = b.AddUser("Adam", "Pierwszy", b.UsersDn);
            ewa = b.AddUser("Ewa", "Stała", b.UsersDn);
            nested = b.AddGroup("GG_Zagnieżdżona", b.UsersDn, members: [adam, ewa]);
        });
        await scenario.BaselineAsync();
        await scenario.DeleteAsync(adam!);
        await scenario.CycleEventsAsync();
        await scenario.RestoreAsync(scenario.Directory.DeletedDistinguishedNames.Last(), adam!);
        await scenario.CycleEventsAsync();

        await scenario.AddValuesAsync(scenario.Builder.DomainAdminsDn, Attr.Member, nested!);
        var granted = (await scenario.CycleEventsAsync()).Where(e => e.Kind == WatchEventKind.PrivilegeGranted).Select(e => e.Object.Dn).ToList();

        // The group's list was never stored: the evaluator reads it live and finds both members, not only the restored one.
        Assert.Contains(adam, granted);
        Assert.Contains(ewa, granted);
    }

    [Fact]
    public async Task SwitchingToAllGroups_FirstReadOfAFormerlyUntrackedGroupIsSilent()
    {
        await using var scenario = WatchListOnly(null);
        await scenario.BaselineAsync();
        scenario.Config.Update(c => c with { Directory = c.Directory with { MembershipTracking = MembershipTracking.AllGroups } });

        await scenario.ReplaceAsync(scenario.Company.GroupSales, Attr.Description, "Dział sprzedaży – Łódź");
        var events = await scenario.CycleEventsAsync();

        Assert.Equal(WatchEventKind.AttributesChanged, Assert.Single(events).Kind);
        Assert.NotEmpty(await scenario.Store.GetMembersAsync(scenario.Snapshot(scenario.Company.GroupSales)!.ObjectGuid, TestContext.Current.CancellationToken));
    }

    private static DetectionScenario WatchListOnly(Action<AdMng.Testing.FakeDirectoryBuilder>? seed) => new(
        seed: seed,
        configuration: new WatchdogConfiguration { Directory = new DirectorySettings { MembershipTracking = MembershipTracking.PrivilegedAndWatchListOnly } });

    [Fact]
    public async Task GroupTypeChanged_IsMedium()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        await scenario.ReplaceAsync(scenario.Company.GroupSales, Attr.GroupType, GroupTypes.Compose(GroupScope.Universal, true));
        var changed = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(WatchEventKind.GroupTypeChanged, changed.Kind);
        Assert.Equal(Severity.Medium, changed.Severity);
    }

    [Fact]
    public async Task NewGroup_ReportsItsMembersAtSettleTime()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        var group = scenario.Builder.AddGroup("GG_Zarząd", scenario.Company.GroupsOu, members: [scenario.Company.PawelLewandowski]);
        await scenario.CycleEventsAsync();
        await scenario.AddValuesAsync(group, Attr.Member, scenario.Company.JanKowalski);
        var events = await scenario.SettleAsync();

        Assert.Single(events, e => e.Kind == WatchEventKind.GroupCreated);
        var members = events.Where(e => e.Kind == WatchEventKind.GroupMemberAdded).Select(e => e.Member!.Dn).Order(StringComparer.Ordinal).ToList();
        Assert.Equal([scenario.Company.JanKowalski, scenario.Company.PawelLewandowski], members);
        Assert.Single(events.Select(e => e.ChangeSetId).Distinct());
    }
}
