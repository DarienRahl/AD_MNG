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
