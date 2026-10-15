using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Testing.Scenarios;

namespace AdMng.Watchdog.Core.Tests.Detection;

/// <summary>
/// Scope changes and container moves (sections 10.6, 10.7): exclusions added by configuration, OUs moved into and out
/// of excluded subtrees with their descendants, chained renames in one poll, the PreviousDn of a child that leaves a
/// renamed OU, the outOfScope tag of ObjectAppeared and the creation test of a Resync.
/// </summary>
public class ScopeTests
{
    private static readonly TimeSpan ToNextReconciliation = TimeSpan.FromHours(17);

    [Fact]
    public async Task ExclusionAddedByConfiguration_RemovesTheRowsWithoutMoveEvents()
    {
        string? archive = null;
        var archived = new List<string>();
        await using var scenario = new DetectionScenario(seed: b =>
        {
            archive = b.AddOu("Archiwum", b.DomainDn);
            archived.Add(b.AddUser("Stary", "Pracownik", archive));
            archived.Add(b.AddUser("Svc", "Stary", archive, new Dictionary<string, object?> { [Attr.ServicePrincipalName] = "HTTP/stary.contoso.test" }));
        });
        await scenario.BaselineAsync();
        var guids = archived.Select(dn => scenario.Snapshot(dn)!.ObjectGuid).ToList();

        scenario.Config.Update(c => c with { Directory = c.Directory with { ExcludedSubtrees = [archive!] } });
        scenario.Advance(ToNextReconciliation);
        var result = await scenario.RunCycleAsync();
        var events = scenario.TakeEvents();

        Assert.Equal(DetectionMode.Reconciliation, result.Mode);
        Assert.DoesNotContain(events, e => e.Kind == WatchEventKind.ObjectMoved);
        Assert.Empty(events);
        Assert.DoesNotContain(scenario.Store.Snapshots, s => guids.Contains(s.ObjectGuid) || s.Dn == archive);
    }

    [Fact]
    public async Task ExclusionAddedByConfiguration_AChangeThereIsSilent()
    {
        string? archive = null;
        string? user = null;
        await using var scenario = new DetectionScenario(seed: b =>
        {
            archive = b.AddOu("Archiwum", b.DomainDn);
            user = b.AddUser("Stary", "Pracownik", archive);
        });
        await scenario.BaselineAsync();
        scenario.Config.Update(c => c with { Directory = c.Directory with { ExcludedSubtrees = [archive!] } });

        await scenario.ReplaceAsync(user!, Attr.Title, "Emeryt");

        Assert.Empty(await scenario.CycleEventsAsync());
        Assert.Null(scenario.Snapshot(user!));
    }

    [Fact]
    public async Task OuMovedIntoAnExcludedSubtree_TakesItsDescendantsAlong()
    {
        string? archive = null;
        string? department = null;
        string? user = null;
        string? service = null;
        await using var scenario = new DetectionScenario(seed: b =>
        {
            archive = b.AddOu("Archiwum", b.DomainDn);
            department = b.AddOu("Dział", b.DomainDn);
            user = b.AddUser("Żaneta", "Ślęzak", department);
            service = b.AddUser("Svc", "Działowy", department, new Dictionary<string, object?> { [Attr.ServicePrincipalName] = "HTTP/dzial.contoso.test" });
        });
        scenario.Config.Update(c => c with { Directory = c.Directory with { ExcludedSubtrees = [archive!] } });
        await scenario.BaselineAsync();
        var guids = new[] { user!, service! }.Select(dn => scenario.Snapshot(dn)!.ObjectGuid).ToList();

        var moved = await scenario.MoveAsync(department!, archive);
        var events = await scenario.CycleEventsAsync();

        var move = Assert.Single(events);
        Assert.Equal(WatchEventKind.ObjectMoved, move.Kind);
        Assert.Equal(department, move.PreviousDn);
        Assert.Equal(moved, move.Object.Dn);
        Assert.Contains(EventTags.OutOfScope, move.Tags);
        Assert.DoesNotContain(scenario.Store.Snapshots, s => guids.Contains(s.ObjectGuid));

        await scenario.ReplaceAsync(DistinguishedName.Parse(moved).Child("CN", "Żaneta Ślęzak").ToString(), Attr.Title, "Archiwalna");
        Assert.Empty(await scenario.CycleEventsAsync());
        scenario.Advance(ToNextReconciliation);
        Assert.Empty(await scenario.CycleEventsAsync());
    }

    [Fact]
    public async Task OuMovedIntoAnExcludedSubtree_AChildMovedAwayFirstIsAnOrdinaryMove()
    {
        string? archive = null;
        string? department = null;
        string? leaving = null;
        string? staying = null;
        await using var scenario = new DetectionScenario(seed: b =>
        {
            archive = b.AddOu("Archiwum", b.DomainDn);
            department = b.AddOu("Dział", b.DomainDn);
            leaving = b.AddUser("Łucja", "Przeniesiona", department);
            staying = b.AddUser("Żaneta", "Ślęzak", department);
        });
        scenario.Config.Update(c => c with { Directory = c.Directory with { ExcludedSubtrees = [archive!] } });
        await scenario.BaselineAsync();
        var stayingGuid = scenario.Snapshot(staying!)!.ObjectGuid;

        var newHome = await scenario.MoveAsync(leaving!, scenario.Company.SalesOu);
        await scenario.MoveAsync(department!, archive);
        var events = await scenario.CycleEventsAsync();

        Assert.Equal(2, events.Count);
        Assert.Single(events, e => e.Kind == WatchEventKind.ObjectMoved && e.PreviousDn == department && e.Tags.Contains(EventTags.OutOfScope));
        var userMove = Assert.Single(events, e => e.Kind == WatchEventKind.ObjectMoved && e.PreviousDn == leaving);
        Assert.Equal(newHome, userMove.Object.Dn);
        Assert.DoesNotContain(EventTags.OutOfScope, userMove.Tags);
        Assert.NotNull(scenario.Snapshot(newHome));
        Assert.DoesNotContain(scenario.Store.Snapshots, s => s.ObjectGuid == stayingGuid);
    }

    [Fact]
    public async Task OuMovedOutOfAnExcludedSubtree_AdoptsItsDescendantsSilently()
    {
        string? archive = null;
        string? department = null;
        await using var scenario = new DetectionScenario(seed: b =>
        {
            archive = b.AddOu("Archiwum", b.DomainDn);
            department = b.AddOu("Dział", archive);
            b.AddUser("Żaneta", "Ślęzak", department);
            b.AddGroup("GG_Dział", department, members: [b.AdministratorDn]);
        });
        scenario.Config.Update(c => c with { Directory = c.Directory with { ExcludedSubtrees = [archive!] } });
        await scenario.BaselineAsync();

        var restored = await scenario.MoveAsync(department!, scenario.Builder.DomainDn);
        var events = await scenario.CycleEventsAsync();

        var appeared = Assert.Single(events);
        Assert.Equal(WatchEventKind.ObjectAppeared, appeared.Kind);
        Assert.Equal(restored, appeared.Object.Dn);
        Assert.Contains(EventTags.OutOfScope, appeared.Tags);
        var user = DistinguishedName.Parse(restored).Child("CN", "Żaneta Ślęzak").ToString();
        var group = DistinguishedName.Parse(restored).Child("CN", "GG_Dział").ToString();
        Assert.NotNull(scenario.Snapshot(user));
        Assert.Single(await scenario.Store.GetMembersAsync(scenario.Snapshot(group)!.ObjectGuid, TestContext.Current.CancellationToken));

        await scenario.ReplaceAsync(user, Attr.Title, "Kierowniczka");
        Assert.Equal(WatchEventKind.AttributesChanged, Assert.Single(await scenario.CycleEventsAsync()).Kind);
        scenario.Advance(ToNextReconciliation);
        Assert.Empty(await scenario.CycleEventsAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ShiftRenameInOnePoll_RewritesChildrenToTheirRealDn(bool alfaFirst)
    {
        string? alfa = null;
        string? beta = null;
        string? anna = null;
        string? bogdan = null;
        await using var scenario = new DetectionScenario(seed: b =>
        {
            if (alfaFirst)
            {
                alfa = b.AddOu("Alfa", b.DomainDn);
                beta = b.AddOu("Beta", b.DomainDn);
            }
            else
            {
                beta = b.AddOu("Beta", b.DomainDn);
                alfa = b.AddOu("Alfa", b.DomainDn);
            }

            anna = b.AddUser("Anna", "Alfowska", alfa);
            bogdan = b.AddUser("Bogdan", "Betowski", beta);
        });
        await scenario.BaselineAsync();
        var annaGuid = scenario.Snapshot(anna!)!.ObjectGuid;
        var bogdanGuid = scenario.Snapshot(bogdan!)!.ObjectGuid;

        var gamma = await scenario.MoveAsync(beta!, null, "OU=Gamma");
        var newBeta = await scenario.MoveAsync(alfa!, null, "OU=Beta");
        var events = await scenario.CycleEventsAsync();

        Assert.Equal(2, events.Count(e => e.Kind == WatchEventKind.ObjectRenamed));
        Assert.Equal(2, events.Count);
        var rows = scenario.Store.Snapshots.ToDictionary(s => s.ObjectGuid);
        Assert.Equal(DistinguishedName.Parse(newBeta).Child("CN", "Anna Alfowska").ToString(), rows[annaGuid].Dn);
        Assert.Equal(DistinguishedName.Parse(gamma).Child("CN", "Bogdan Betowski").ToString(), rows[bogdanGuid].Dn);

        await scenario.ReplaceAsync(rows[annaGuid].Dn, Attr.Title, "Kierowniczka");
        Assert.Equal(WatchEventKind.AttributesChanged, Assert.Single(await scenario.CycleEventsAsync()).Kind);
        scenario.Advance(ToNextReconciliation);
        Assert.Empty(await scenario.CycleEventsAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SwapInAReplacementOu_RewritesChildrenToTheirRealDn(bool newFirst)
    {
        string? sales = null;
        string? salesNew = null;
        string? veteran = null;
        string? newcomer = null;
        await using var scenario = new DetectionScenario(seed: b =>
        {
            if (newFirst)
            {
                salesNew = b.AddOu("Sprzedaż_nowa", b.DomainDn);
                sales = b.AddOu("Sprzedaż", b.DomainDn);
            }
            else
            {
                sales = b.AddOu("Sprzedaż", b.DomainDn);
                salesNew = b.AddOu("Sprzedaż_nowa", b.DomainDn);
            }

            veteran = b.AddUser("Stary", "Handlowiec", sales);
            newcomer = b.AddUser("Nowy", "Handlowiec", salesNew);
        });
        await scenario.BaselineAsync();
        var veteranGuid = scenario.Snapshot(veteran!)!.ObjectGuid;
        var newcomerGuid = scenario.Snapshot(newcomer!)!.ObjectGuid;

        var old = await scenario.MoveAsync(sales!, null, "OU=Sprzedaż_stara");
        var current = await scenario.MoveAsync(salesNew!, null, "OU=Sprzedaż");
        await scenario.CycleEventsAsync();

        var rows = scenario.Store.Snapshots.ToDictionary(s => s.ObjectGuid);
        Assert.Equal(DistinguishedName.Parse(old).Child("CN", "Stary Handlowiec").ToString(), rows[veteranGuid].Dn);
        Assert.Equal(DistinguishedName.Parse(current).Child("CN", "Nowy Handlowiec").ToString(), rows[newcomerGuid].Dn);
        scenario.Advance(ToNextReconciliation);
        Assert.Empty(await scenario.CycleEventsAsync());
    }

    [Fact]
    public async Task ChildMovedOutOfAnOuRenamedInTheSamePoll_PreviousDnIsItsRealOldDn()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        var original = scenario.Company.MalgorzataWojcik;

        var moved = await scenario.MoveAsync(original, scenario.Company.SalesOu);
        await scenario.MoveAsync(scenario.Company.AccountingOu, null, "OU=Finanse");
        var events = await scenario.CycleEventsAsync();

        var move = Assert.Single(events, e => e.Kind == WatchEventKind.ObjectMoved);
        Assert.Equal(original, move.PreviousDn);
        Assert.Equal(moved, move.Object.Dn);
    }

    [Fact]
    public async Task OccupiedNewName_KeepsTheStoredDnOfAChildThatLeftTheFormerOccupant()
    {
        string? alfa = null;
        string? beta = null;
        string? bob = null;
        await using var scenario = new DetectionScenario(seed: b =>
        {
            alfa = b.AddOu("Alfa", b.DomainDn);
            beta = b.AddOu("Beta", b.DomainDn);
            bob = b.AddUser("Bob", "Betowski", beta);
        });
        await scenario.BaselineAsync();

        // Bob leaves OU=Beta, OU=Beta is deleted and OU=Alfa takes its name – all in one poll.
        var moved = await scenario.MoveAsync(bob!, scenario.Company.SalesOu);
        await scenario.DeleteAsync(beta!);
        await scenario.MoveAsync(alfa!, null, "OU=Beta");
        var events = await scenario.CycleEventsAsync();

        var move = Assert.Single(events, e => e.Kind == WatchEventKind.ObjectMoved);
        Assert.Equal(bob, move.PreviousDn);
        Assert.Equal(moved, move.Object.Dn);
    }

    [Fact]
    public async Task ObjectAppeared_IsTaggedOutOfScopeOnlyWhenItWasMovedIn()
    {
        string? hidden = null;
        await using var scenario = new DetectionScenario(seed: b =>
            hidden = b.AddUser("Ukryty", "Użytkownik", b.SystemDn));
        await scenario.BaselineAsync();
        Assert.Null(scenario.Snapshot(hidden!));

        // Moved in from CN=System (out of scope) – no ExcludedSubtrees configured.
        var movedIn = await scenario.MoveAsync(hidden!, scenario.Company.SalesOu);
        var appeared = Assert.Single(await scenario.CycleEventsAsync());
        Assert.Equal(WatchEventKind.ObjectAppeared, appeared.Kind);
        Assert.Equal(movedIn, appeared.Object.Dn);
        Assert.Contains(EventTags.OutOfScope, appeared.Tags);

        // A row lost without a move (e.g. removed after ObjectDisappeared) re-appears untagged, exclusions or not.
        scenario.Config.Update(c => c with { Directory = c.Directory with { ExcludedSubtrees = [scenario.Company.ItOu] } });
        var guid = scenario.Snapshot(scenario.Company.PawelLewandowski)!.ObjectGuid;
        await scenario.Store.CommitAsync(new DetectionCommit { Removals = [guid] }, TestContext.Current.CancellationToken);
        scenario.Advance(TimeSpan.FromMinutes(1));
        await scenario.ReplaceAsync(scenario.Company.PawelLewandowski, Attr.Title, "Kierownik sprzedaży");
        var reappeared = Assert.Single(await scenario.CycleEventsAsync());
        Assert.Equal(WatchEventKind.ObjectAppeared, reappeared.Kind);
        Assert.DoesNotContain(EventTags.OutOfScope, reappeared.Tags);
    }

    [Fact]
    public async Task Resync_ObjectCreatedShortlyBeforeTheLastPollButNotReplicated_IsCreated()
    {
        const string dc02 = "dc02.contoso.test";
        await using var scenario = new DetectionScenario(seed: b => b.AddDomainController(dc02, new Guid("b0c1d2e3-0000-4000-8000-000000000002")));
        await scenario.BaselineAsync();
        scenario.Advance(TimeSpan.FromMinutes(1));

        // Created on a branch DC at 10:01; the PDC polled at 10:05 without it (replication latency), then the PDC failed.
        var user = scenario.Builder.AddUser("Oddział", "Nowy", scenario.Company.SalesOu,
            new Dictionary<string, object?> { [Attr.UserAccountControl] = (int)(UserAccountControl.NormalAccount | UserAccountControl.DontRequirePreauth) });
        scenario.Directory.HideAttributes(user, Attr.UsnChanged);
        scenario.Advance(TimeSpan.FromMinutes(4));
        Assert.Empty(await scenario.CycleEventsAsync());
        scenario.Advance(TimeSpan.FromMinutes(1));

        scenario.Directory.SwitchServer(dc02);
        var events = await scenario.SettleAsync();

        Assert.Single(events, e => e.Kind == WatchEventKind.UserCreated && e.Object.Dn == user);
        Assert.Single(events, e => e.Kind == WatchEventKind.AccountFlagsChanged && e.Object.Dn == user);
        Assert.DoesNotContain(events, e => e.Kind == WatchEventKind.ObjectAppeared);
    }
}
