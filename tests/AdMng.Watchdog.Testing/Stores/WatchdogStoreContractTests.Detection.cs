using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
using Xunit;

namespace AdMng.Watchdog.Testing.Stores;

// IDetectionStore
public abstract partial class WatchdogStoreContractTests<TStore>
{
    [Fact]
    public Task Commit_StoresStateAndSnapshots_AndAssignsAscendingEventIds() => WithStoreAsync(async store =>
    {
        var user = Snapshot();
        var stored = await store.CommitAsync(
            new DetectionCommit
            {
                NewState = State(highestUsn: 4711),
                Upserts = [user],
                Events = [Event(WatchEventKind.UserCreated, user), Event(WatchEventKind.AccountEnabled, user)],
            },
            Ct);

        Assert.Equal(2, stored.Count);
        Assert.All(stored, e => Assert.True(e.Id > 0));
        Assert.True(stored[0].Id < stored[1].Id);
        Assert.Equal(stored[1].Id, await store.GetLatestIdAsync(Ct));
        Assert.Equal(4711, (await store.GetDcStateAsync(Domain, Ct))?.HighestUsn);
        Assert.Equal(1, await store.CountLiveObjectsAsync(Ct));
        Assert.Equal(user.Dn, (await store.FindByDnAsync(user.Dn, Ct))?.Dn);
    });

    [Fact]
    public Task Snapshot_AttributeLookup_StaysCaseInsensitiveAfterRoundTrip() => WithStoreAsync(async store =>
    {
        var user = Snapshot();
        await store.CommitAsync(new DetectionCommit { NewState = State(highestUsn: 10), Upserts = [user] }, Ct);

        var loaded = (await store.GetSnapshotsAsync([user.ObjectGuid], Ct))[user.ObjectGuid];

        Assert.Equal("Księgowość", Assert.Single(loaded.Attributes["DEPARTMENT"].Values));
    });

    [Fact]
    public Task Snapshot_RoundTripsEveryColumn() => WithStoreAsync(async store =>
    {
        var user = Snapshot() with
        {
            CreationPendingUntil = Now.AddMinutes(2),
            Attributes = new Dictionary<string, SnapshotAttribute>(StringComparer.OrdinalIgnoreCase)
            {
                ["manager"] = new()
                {
                    Values = ["guid:1c4f3e2d-5a6b-4c7d-8e9f-a0b1c2d3e4f5"],
                    DisplayValues = ["CN=Łukasz Wiśniewski,OU=Zarząd,DC=contoso,DC=test"],
                    Kind = AttributeValueKind.Dn,
                    Version = 3,
                    ChangedAt = Now.AddMinutes(-1),
                },
            },
        };
        await store.CommitAsync(new DetectionCommit { Upserts = [user] }, Ct);

        var loaded = (await store.GetSnapshotsAsync([user.ObjectGuid], Ct))[user.ObjectGuid];

        Assert.Equal(user.Dn, loaded.Dn);
        Assert.Equal(user.Kind, loaded.Kind);
        Assert.Equal(user.ObjectClasses, loaded.ObjectClasses);
        Assert.Equal(user.Sid, loaded.Sid);
        Assert.Equal(user.SamAccountName, loaded.SamAccountName);
        Assert.Equal(user.Name, loaded.Name);
        Assert.Equal(user.DisplayName, loaded.DisplayName);
        Assert.Equal(user.UsnCreated, loaded.UsnCreated);
        Assert.Equal(user.UsnChanged, loaded.UsnChanged);
        Assert.Equal(user.WhenCreated, loaded.WhenCreated);
        Assert.Equal(user.CreationPendingUntil, loaded.CreationPendingUntil);
        Assert.False(loaded.IsDeleted);
        var manager = loaded.Attributes["Manager"];
        Assert.Equal(["guid:1c4f3e2d-5a6b-4c7d-8e9f-a0b1c2d3e4f5"], manager.Values);
        Assert.Equal(["CN=Łukasz Wiśniewski,OU=Zarząd,DC=contoso,DC=test"], manager.DisplayValues);
        Assert.Equal(AttributeValueKind.Dn, manager.Kind);
        Assert.Equal(3, manager.Version);
        Assert.Equal(Now.AddMinutes(-1), manager.ChangedAt);
    });

    [Fact]
    public Task Commit_UpsertsAWatermarkPerDcIdentity() => WithStoreAsync(async store =>
    {
        var otherDsa = DsServiceName.Replace("CN=DC01", "CN=DC02", StringComparison.Ordinal);
        var otherInvocation = Guid.Parse("0b1c2d3e-4f50-4617-8293-a4b5c6d7e8f9");
        await store.CommitAsync(new DetectionCommit { NewState = State(highestUsn: 100) }, Ct);
        Clock.Advance(TimeSpan.FromMinutes(1));
        await store.CommitAsync(new DetectionCommit { NewState = State(highestUsn: 500) with { DsServiceName = otherDsa, InvocationId = otherInvocation } }, Ct);

        var first = await store.GetWatermarkAsync(DsServiceName.ToUpperInvariant(), InvocationId, Ct);
        var second = await store.GetWatermarkAsync(otherDsa, otherInvocation, Ct);

        Assert.NotNull(first);
        Assert.Equal(100, first.HighestUsn);
        Assert.Equal(Now, first.UpdatedAt);
        Assert.NotNull(second);
        Assert.Equal(500, second.HighestUsn);
        Assert.Equal(Now.AddMinutes(1), second.UpdatedAt);
        Assert.Null(await store.GetWatermarkAsync(DsServiceName, otherInvocation, Ct));
        Assert.Equal(500, (await store.GetDcStateAsync(Domain, Ct))?.HighestUsn);
    });

    [Fact]
    public Task ChunkCommit_WithoutNewState_LeavesTheDcStateUnchanged() => WithStoreAsync(async store =>
    {
        await store.CommitAsync(new DetectionCommit { NewState = State(highestUsn: 10) }, Ct);

        await store.CommitAsync(new DetectionCommit { Upserts = [Snapshot()] }, Ct);

        var state = await store.GetDcStateAsync(Domain, Ct);
        Assert.NotNull(state);
        Assert.Equal(10, state.HighestUsn);
        Assert.Equal(InvocationId, state.InvocationId);
        Assert.Equal("dc01.contoso.test", state.DcHostName);
        Assert.Equal(DsServiceName, state.DsServiceName);
        Assert.Equal(Now, state.LastPollAt);
        Assert.Equal(1, await store.CountLiveObjectsAsync(Ct));
    });

    [Fact]
    public Task MembershipRelevantCommit_SetsMembershipsChangedAt_AndALaterStateKeepsIt() => WithStoreAsync(async store =>
    {
        await store.CommitAsync(new DetectionCommit { NewState = State(highestUsn: 10) }, Ct);
        Assert.Null((await store.GetDcStateAsync(Domain, Ct))?.MembershipsChangedAt);

        Clock.Advance(TimeSpan.FromMinutes(3));
        await store.CommitAsync(new DetectionCommit { MembershipRelevant = true }, Ct);
        Clock.Advance(TimeSpan.FromMinutes(3));
        await store.CommitAsync(new DetectionCommit { NewState = State(highestUsn: 20) }, Ct);

        var state = await store.GetDcStateAsync(Domain, Ct);
        Assert.Equal(Now.AddMinutes(3), state?.MembershipsChangedAt);
        Assert.Equal(20, state?.HighestUsn);
    });

    [Fact]
    public Task BaselineCommit_MarksBaselineCompletedAtOnce() => WithStoreAsync(async store =>
    {
        Clock.Advance(TimeSpan.FromMinutes(10));
        await store.CommitAsync(new DetectionCommit { NewState = State(highestUsn: 10), IsBaseline = true }, Ct);
        Clock.Advance(TimeSpan.FromMinutes(10));
        await store.CommitAsync(new DetectionCommit { NewState = State(highestUsn: 20) }, Ct);
        await store.CommitAsync(new DetectionCommit { NewState = State(highestUsn: 30), IsBaseline = true }, Ct);

        Assert.Equal(Now.AddMinutes(10), (await store.GetDcStateAsync(Domain, Ct))?.BaselineCompletedAt);
    });

    [Fact]
    public Task Deletion_MarksTheRowDeleted_AndRemovesItFromEveryMemberList() => WithStoreAsync(async store =>
    {
        var user = Snapshot() with { CreationPendingUntil = Now.AddMinutes(1) };
        var groupA = Snapshot("CN=Księgowa,OU=Grupy,DC=contoso,DC=test", WatchObjectKind.Group);
        var groupB = Snapshot("CN=Wszyscy,OU=Grupy,DC=contoso,DC=test", WatchObjectKind.Group);
        var other = Member(Snapshot("CN=Jan Kowalski,OU=Pracownicy,DC=contoso,DC=test"));
        await store.CommitAsync(
            new DetectionCommit
            {
                Upserts = [user, groupA, groupB],
                Memberships = new Dictionary<Guid, IReadOnlyList<MemberReference>>
                {
                    [groupA.ObjectGuid] = [Member(user), other],
                    [groupB.ObjectGuid] = [Member(user)],
                },
            },
            Ct);

        await store.CommitAsync(new DetectionCommit { Deletions = [(user.ObjectGuid, Now.AddMinutes(1))], MembershipRelevant = true }, Ct);

        var row = (await store.GetSnapshotsAsync([user.ObjectGuid], Ct))[user.ObjectGuid];
        Assert.True(row.IsDeleted);
        Assert.Equal(Now.AddMinutes(1), row.DeletedAt);
        Assert.Null(row.CreationPendingUntil);
        Assert.Null(await store.FindByDnAsync(user.Dn, Ct));
        Assert.Null(await store.FindBySidAsync(user.Sid!, Ct));
        Assert.Equal(2, await store.CountLiveObjectsAsync(Ct));
        Assert.DoesNotContain(user.ObjectGuid, await ToListAsync(store.GetLiveObjectGuidsAsync(Ct)));
        Assert.Equal([other.Key], (await store.GetMembersAsync(groupA.ObjectGuid, Ct)).Select(m => m.Key));
        Assert.Empty(await store.GetMembersAsync(groupB.ObjectGuid, Ct));
        Assert.Empty(await store.GetGroupsContainingAsync(Member(user).Key, Ct));
        Assert.Empty(await store.GetPendingCreationsAsync(Now.AddDays(1), Ct));
    });

    [Fact]
    public Task Upsert_OfADeletedGuid_MakesTheObjectLiveAgain() => WithStoreAsync(async store =>
    {
        var user = Snapshot();
        await store.CommitAsync(new DetectionCommit { Upserts = [user] }, Ct);
        await store.CommitAsync(new DetectionCommit { Deletions = [(user.ObjectGuid, Now)] }, Ct);

        await store.CommitAsync(new DetectionCommit { Upserts = [user] }, Ct);

        Assert.False((await store.GetSnapshotsAsync([user.ObjectGuid], Ct))[user.ObjectGuid].IsDeleted);
        Assert.NotNull(await store.FindByDnAsync(user.Dn, Ct));
        Assert.Equal(1, await store.CountLiveObjectsAsync(Ct));
    });

    [Fact]
    public Task Removal_DeletesTheRowAndItsOwnMemberList_ButNotItsMemberships() => WithStoreAsync(async store =>
    {
        var group = Snapshot("CN=Serwisanci,OU=Grupy,DC=contoso,DC=test", WatchObjectKind.Group);
        var parent = Snapshot("CN=Wszyscy,OU=Grupy,DC=contoso,DC=test", WatchObjectKind.Group);
        var user = Snapshot();
        await store.CommitAsync(
            new DetectionCommit
            {
                Upserts = [group, parent, user],
                Memberships = new Dictionary<Guid, IReadOnlyList<MemberReference>>
                {
                    [group.ObjectGuid] = [Member(user)],
                    [parent.ObjectGuid] = [Member(group)],
                },
            },
            Ct);

        await store.CommitAsync(new DetectionCommit { Removals = [group.ObjectGuid] }, Ct);

        Assert.Empty(await store.GetSnapshotsAsync([group.ObjectGuid], Ct));
        Assert.Empty(await store.GetMembersAsync(group.ObjectGuid, Ct));
        Assert.Equal([parent.ObjectGuid], await store.GetGroupsContainingAsync(Member(group).Key, Ct));
        Assert.Equal(2, await store.CountLiveObjectsAsync(Ct));
    });

    [Fact]
    public Task Memberships_AreReplacedPerGroup_AndQueryableByMemberKey() => WithStoreAsync(async store =>
    {
        var group = Snapshot("CN=Księgowa,OU=Grupy,DC=contoso,DC=test", WatchObjectKind.Group);
        var jan = Member(Snapshot("CN=Jan Kowalski,OU=Pracownicy,DC=contoso,DC=test"));
        var zofia = Member(Snapshot());
        var foreign = new MemberReference { Key = "sid:S-1-5-21-111-222-333-1105", Sid = "S-1-5-21-111-222-333-1105", Dn = "CN=S-1-5-21-111-222-333-1105,CN=ForeignSecurityPrincipals,DC=contoso,DC=test" };
        await store.CommitAsync(new DetectionCommit
        {
            Upserts = [group],
            Memberships = new Dictionary<Guid, IReadOnlyList<MemberReference>> { [group.ObjectGuid] = [jan, foreign] },
        }, Ct);

        await store.CommitAsync(new DetectionCommit
        {
            Memberships = new Dictionary<Guid, IReadOnlyList<MemberReference>> { [group.ObjectGuid] = [zofia, foreign] },
        }, Ct);

        var members = await store.GetMembersAsync(group.ObjectGuid, Ct);
        Assert.Equal(new[] { zofia.Key, foreign.Key }.Order(StringComparer.Ordinal), members.Select(m => m.Key).Order(StringComparer.Ordinal));
        Assert.Contains(members, m => m.Key == foreign.Key && m.Sid == foreign.Sid && m.Dn == foreign.Dn && m.ObjectGuid is null);
        Assert.Contains(members, m => m.Key == zofia.Key && m.ObjectGuid == zofia.ObjectGuid);
        Assert.Empty(await store.GetGroupsContainingAsync(jan.Key, Ct));
        Assert.Equal([group.ObjectGuid], await store.GetGroupsContainingAsync(zofia.Key.ToUpperInvariant(), Ct));
        var edges = await store.GetAllMembershipsAsync(Ct);
        Assert.Equal(2, edges.Count);
        Assert.All(edges, e => Assert.Equal(group.ObjectGuid, e.GroupGuid));
    });

    [Fact]
    public Task DnRewrite_MovesDescendantsAndMemberDns_OnlyBelowTheOldSuffix() => WithStoreAsync(async store =>
    {
        var ou = Snapshot("OU=Sprzedaż,DC=contoso,DC=test", WatchObjectKind.OrganizationalUnit);
        var child = Snapshot("CN=Jan Kowalski,OU=Handlowcy,OU=Sprzedaż,DC=contoso,DC=test");
        var outside = Snapshot("CN=Zofia Żak,OU=Sprzedaż Zagraniczna,DC=contoso,DC=test");
        var group = Snapshot("CN=Handlowcy,OU=Grupy,DC=contoso,DC=test", WatchObjectKind.Group);
        await store.CommitAsync(new DetectionCommit
        {
            Upserts = [ou, child, outside, group],
            Memberships = new Dictionary<Guid, IReadOnlyList<MemberReference>> { [group.ObjectGuid] = [Member(child), Member(outside)] },
        }, Ct);

        await store.CommitAsync(new DetectionCommit
        {
            Upserts = [ou with { Dn = "OU=Sprzedaż Krajowa,DC=contoso,DC=test" }],
            DnRewrites = [new DnRewrite("OU=Sprzedaż,DC=contoso,DC=test", "OU=Sprzedaż Krajowa,DC=contoso,DC=test")],
        }, Ct);

        Assert.Equal("CN=Jan Kowalski,OU=Handlowcy,OU=Sprzedaż Krajowa,DC=contoso,DC=test",
            (await store.GetSnapshotsAsync([child.ObjectGuid], Ct))[child.ObjectGuid].Dn);
        Assert.Equal(outside.Dn, (await store.GetSnapshotsAsync([outside.ObjectGuid], Ct))[outside.ObjectGuid].Dn);
        Assert.NotNull(await store.FindByDnAsync("cn=jan kowalski,ou=handlowcy,ou=sprzedaż krajowa,dc=contoso,dc=test", Ct));
        Assert.Null(await store.FindByDnAsync(child.Dn, Ct));
        var members = await store.GetMembersAsync(group.ObjectGuid, Ct);
        Assert.Contains(members, m => m.Dn == "CN=Jan Kowalski,OU=Handlowcy,OU=Sprzedaż Krajowa,DC=contoso,DC=test");
        Assert.Contains(members, m => m.Dn == outside.Dn);
    });

    [Fact]
    public Task Descendants_AreLiveObjectsStrictlyBelowTheAncestor() => WithStoreAsync(async store =>
    {
        var ou = Snapshot("OU=Księgowość,OU=Pracownicy,DC=contoso,DC=test", WatchObjectKind.OrganizationalUnit);
        var user = Snapshot();
        var nested = Snapshot("CN=Ewa Łącka,OU=Płace,OU=Księgowość,OU=Pracownicy,DC=contoso,DC=test");
        var deleted = Snapshot("CN=Piotr Źródło,OU=Księgowość,OU=Pracownicy,DC=contoso,DC=test");
        var sibling = Snapshot("CN=Adam Gość,OU=Kadry,OU=Pracownicy,DC=contoso,DC=test");
        await store.CommitAsync(new DetectionCommit { Upserts = [ou, user, nested, deleted, sibling] }, Ct);
        await store.CommitAsync(new DetectionCommit { Deletions = [(deleted.ObjectGuid, Now)] }, Ct);

        var descendants = await ToListAsync(store.GetDescendantsAsync("ou=KSIĘGOWOŚĆ,ou=pracownicy,dc=contoso,dc=test", Ct));

        Assert.Equal(new[] { nested.ObjectGuid, user.ObjectGuid }.Order(), descendants.Select(d => d.ObjectGuid).Order());
        Assert.DoesNotContain(descendants, d => d.ObjectGuid == ou.ObjectGuid);
    });

    [Fact]
    public Task PendingCreations_AreLiveRowsWhoseSettleWindowEnded() => WithStoreAsync(async store =>
    {
        var due = Snapshot("CN=Jan Kowalski,OU=Pracownicy,DC=contoso,DC=test") with { CreationPendingUntil = Now.AddMinutes(1) };
        var later = Snapshot("CN=Zofia Żak,OU=Pracownicy,DC=contoso,DC=test") with { CreationPendingUntil = Now.AddMinutes(5) };
        var settled = Snapshot("CN=Ewa Łącka,OU=Pracownicy,DC=contoso,DC=test");
        await store.CommitAsync(new DetectionCommit { Upserts = [due, later, settled] }, Ct);

        var pending = await store.GetPendingCreationsAsync(Now.AddMinutes(1), Ct);

        Assert.Equal(due.ObjectGuid, Assert.Single(pending).ObjectGuid);
        Assert.Equal(2, (await store.GetPendingCreationsAsync(Now.AddMinutes(10), Ct)).Count);
    });

    [Fact]
    public Task ByKind_ReturnsLiveRowsOfThatKindOnly() => WithStoreAsync(async store =>
    {
        var dc1 = Snapshot("CN=NTDS Settings,CN=DC01,CN=Servers,CN=Warszawa,CN=Sites,CN=Configuration,DC=contoso,DC=test", WatchObjectKind.DomainController);
        var dc2 = Snapshot("CN=NTDS Settings,CN=DC02,CN=Servers,CN=Kraków,CN=Sites,CN=Configuration,DC=contoso,DC=test", WatchObjectKind.DomainController);
        var template = Snapshot("CN=Użytkownik,CN=Certificate Templates,CN=Public Key Services,CN=Services,CN=Configuration,DC=contoso,DC=test", WatchObjectKind.PublicKeyInfrastructure);
        await store.CommitAsync(new DetectionCommit { Upserts = [dc1, dc2, template, Snapshot()] }, Ct);
        await store.CommitAsync(new DetectionCommit { Deletions = [(dc2.ObjectGuid, Now)] }, Ct);

        Assert.Equal(dc1.ObjectGuid, Assert.Single(await store.GetByKindAsync(WatchObjectKind.DomainController, Ct)).ObjectGuid);
        Assert.Equal(template.ObjectGuid, Assert.Single(await store.GetByKindAsync(WatchObjectKind.PublicKeyInfrastructure, Ct)).ObjectGuid);
        Assert.Empty(await store.GetByKindAsync(WatchObjectKind.Configuration, Ct));
    });

    [Fact]
    public Task FindBySid_IsCaseInsensitive_AndIgnoresDeletedRows() => WithStoreAsync(async store =>
    {
        var user = Snapshot();
        await store.CommitAsync(new DetectionCommit { Upserts = [user] }, Ct);

        Assert.Equal(user.ObjectGuid, (await store.FindBySidAsync(user.Sid!.ToLowerInvariant(), Ct))?.ObjectGuid);
        Assert.Null(await store.FindBySidAsync("S-1-5-21-1004336348-1177238915-682003330-9999", Ct));
    });

    [Fact]
    public Task GetSnapshots_ReturnsKnownGuidsOnly_IncludingDeletedRows() => WithStoreAsync(async store =>
    {
        var user = Snapshot();
        var deleted = Snapshot("CN=Piotr Źródło,OU=Pracownicy,DC=contoso,DC=test");
        await store.CommitAsync(new DetectionCommit { Upserts = [user, deleted] }, Ct);
        await store.CommitAsync(new DetectionCommit { Deletions = [(deleted.ObjectGuid, Now)] }, Ct);

        var found = await store.GetSnapshotsAsync([user.ObjectGuid, deleted.ObjectGuid, Guid.NewGuid()], Ct);

        Assert.Equal(2, found.Count);
        Assert.True(found[deleted.ObjectGuid].IsDeleted);
    });

    [Fact]
    public Task PrivilegeState_IsStored_AndANullCommitKeepsIt() => WithStoreAsync(async store =>
    {
        var group = Guid.Parse("0f1e2d3c-4b5a-4968-8776-655443322110");
        var state = new PrivilegeState
        {
            ComputedAt = Now,
            Principals =
            [
                new PrivilegedPrincipal { PrincipalKey = "9b0f4e3c1a2b4c5d8e9f0a1b2c3d4e5f", GroupGuid = group, Tier = PrivilegeTier.Critical, Path = [Guid.Parse("11111111-2222-4333-8444-555555555555")] },
                new PrivilegedPrincipal { PrincipalKey = "sid:S-1-5-21-1-2-3-1107", GroupGuid = group, Tier = PrivilegeTier.High, ViaPrimaryGroup = true },
            ],
        };
        await store.CommitAsync(new DetectionCommit { Privileges = state }, Ct);
        await store.CommitAsync(new DetectionCommit { Upserts = [Snapshot()] }, Ct);

        var loaded = await store.GetPrivilegeStateAsync(Ct);

        Assert.NotNull(loaded);
        Assert.Equal(Now, loaded.ComputedAt);
        Assert.Equal(2, loaded.Principals.Count);
        var critical = loaded.Principals.Single(p => p.Tier == PrivilegeTier.Critical);
        Assert.Equal("9b0f4e3c1a2b4c5d8e9f0a1b2c3d4e5f", critical.PrincipalKey);
        Assert.Equal(group, critical.GroupGuid);
        Assert.Equal([Guid.Parse("11111111-2222-4333-8444-555555555555")], critical.Path);
        Assert.True(loaded.Principals.Single(p => p.Tier == PrivilegeTier.High).ViaPrimaryGroup);
    });

    [Fact]
    public Task NonDefaultPrimaryGroups_ListLiveAccountsOutside513To515() => WithStoreAsync(async store =>
    {
        var admin = Snapshot("CN=Adm Nowak,OU=Admins,DC=contoso,DC=test", attributes: new Dictionary<string, string> { ["primaryGroupID"] = "512" });
        var regular = Snapshot("CN=Jan Kowalski,OU=Pracownicy,DC=contoso,DC=test", attributes: new Dictionary<string, string> { ["primaryGroupID"] = "513" });
        var computer = Snapshot("CN=WS-001,OU=Komputery,DC=contoso,DC=test", WatchObjectKind.Computer, attributes: new Dictionary<string, string> { ["primaryGroupID"] = "515" });
        var dc = Snapshot("CN=DC01,OU=Domain Controllers,DC=contoso,DC=test", WatchObjectKind.Computer, attributes: new Dictionary<string, string> { ["primaryGroupID"] = "516" });
        var group = Snapshot("CN=Dziwna Grupa,OU=Grupy,DC=contoso,DC=test", WatchObjectKind.Group, attributes: new Dictionary<string, string> { ["primaryGroupID"] = "512" });
        var deleted = Snapshot("CN=Stary Admin,OU=Admins,DC=contoso,DC=test", attributes: new Dictionary<string, string> { ["primaryGroupID"] = "512" });
        await store.CommitAsync(new DetectionCommit { Upserts = [admin, regular, computer, dc, group, deleted] }, Ct);
        await store.CommitAsync(new DetectionCommit { Deletions = [(deleted.ObjectGuid, Now)] }, Ct);

        var result = await store.GetNonDefaultPrimaryGroupsAsync(Ct);

        Assert.Equal(2, result.Count);
        Assert.Contains((admin.ObjectGuid.ToString("N"), 512u), result);
        Assert.Contains((dc.ObjectGuid.ToString("N"), 516u), result);
    });

    [Fact]
    public Task CommittedEvents_AreUnevaluated_AndPendingAttributionIsDueAtDetection() => WithStoreAsync(async store =>
    {
        var user = Snapshot();
        var stored = await store.CommitAsync(new DetectionCommit
        {
            Upserts = [user],
            Events = [Event(WatchEventKind.UserCreated, user, Now.AddSeconds(5)), Event(WatchEventKind.AccountEnabled, user, Now.AddSeconds(5), attribution: AdMng.Watchdog.Contracts.Events.Attribution.NotApplicable)],
        }, Ct);

        Assert.Equal(stored.Select(e => e.Id), (await store.GetUnevaluatedAsync(10, Ct)).Select(e => e.Id));
        Assert.Empty(await store.GetPendingAttributionAsync(Now, 10, Ct));
        Assert.Equal(stored[0].Id, Assert.Single(await store.GetPendingAttributionAsync(Now.AddSeconds(5), 10, Ct)).Id);
    });

    protected static MemberReference Member(SnapshotObject snapshot) => new()
    {
        Key = snapshot.ObjectGuid.ToString("N"),
        ObjectGuid = snapshot.ObjectGuid,
        Sid = snapshot.Sid,
        Dn = snapshot.Dn,
    };

    protected static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }
}
