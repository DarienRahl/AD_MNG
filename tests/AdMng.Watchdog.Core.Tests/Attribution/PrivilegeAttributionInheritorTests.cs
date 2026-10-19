using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Core.Tests.Rules.Fakes;
using static AdMng.Watchdog.Core.Tests.Rules.TestEvents;

namespace AdMng.Watchdog.Core.Tests.Attribution;

public class PrivilegeAttributionInheritorTests
{
    private const string AdminsDn = "CN=Domain Admins,CN=Users,DC=contoso,DC=test";
    private const string HelpdeskDn = "CN=Helpdesk Kraków,OU=Grupy,DC=contoso,DC=test";
    private static readonly Guid AdminsGuid = Guid.Parse("0b4c22e6-6c1e-4b7a-9d2a-2f0b8f3a1001");
    private static readonly Guid HelpdeskGuid = Guid.Parse("0b4c22e6-6c1e-4b7a-9d2a-2f0b8f3a1002");

    private readonly FakeEventStore _events = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WatchEvent Privilege(WatchEventKind kind = WatchEventKind.PrivilegeGranted, Guid? changeSet = null, IReadOnlyList<ObjectRef>? via = null,
        params string[] tags) => Create(kind, tags: tags) with
    {
        ChangeSetId = changeSet ?? Guid.Empty,
        Member = new ObjectRef { Dn = AdminsDn, Kind = WatchObjectKind.Group, ObjectGuid = AdminsGuid },
        Via = via ?? [],
    };

    private static WatchEvent MemberChange(Guid groupGuid, string groupDn, WatchEventKind kind = WatchEventKind.GroupMemberAdded, Guid? member = null,
        Guid? changeSet = null, DateTimeOffset? at = null) =>
        Create(kind, groupDn, WatchObjectKind.Group, detectedAt: at, objectGuid: groupGuid) with
        {
            ChangeSetId = changeSet ?? Guid.NewGuid(),
            Member = new ObjectRef { Dn = UserDn, Kind = WatchObjectKind.User, ObjectGuid = member ?? UserGuid },
            Attribution = Resolved(AdminSid),
        };

    [Fact]
    public async Task TheMembershipOfTheSameChangeSet_IsTheCause()
    {
        var changeSet = Guid.NewGuid();
        var cause = _events.Add(MemberChange(AdminsGuid, AdminsDn, changeSet: changeSet));
        var privilege = _events.Add(Privilege(changeSet: changeSet));

        Assert.Equal(cause.EventId, (await new PrivilegeAttributionInheritor(_events).FindCauseAsync(privilege, Ct))!.EventId);
    }

    [Fact]
    public async Task ARestoredObject_IsPreferred()
    {
        var changeSet = Guid.NewGuid();
        _events.Add(MemberChange(AdminsGuid, AdminsDn, changeSet: changeSet));
        var restored = _events.Add(Create(WatchEventKind.ObjectRestored) with { ChangeSetId = changeSet });
        var privilege = _events.Add(Privilege(changeSet: changeSet, tags: EventTags.Restored));

        Assert.Equal(restored.EventId, (await new PrivilegeAttributionInheritor(_events).FindCauseAsync(privilege, Ct))!.EventId);
    }

    [Fact]
    public async Task WithoutAChangeSet_TheFirstEdgeOfTheNestingPathIsUsed()
    {
        _events.Add(MemberChange(HelpdeskGuid, HelpdeskDn, member: Guid.NewGuid(), at: Now.AddMinutes(-5)));
        var cause = _events.Add(MemberChange(HelpdeskGuid, HelpdeskDn, at: Now.AddMinutes(-10)));
        _events.Add(MemberChange(HelpdeskGuid, HelpdeskDn, at: Now.AddDays(-2)));
        var via = new[] { new ObjectRef { Dn = HelpdeskDn, Kind = WatchObjectKind.Group, ObjectGuid = HelpdeskGuid } };
        var privilege = _events.Add(Privilege(via: via));

        Assert.Equal(cause.EventId, (await new PrivilegeAttributionInheritor(_events).FindCauseAsync(privilege, Ct))!.EventId);
    }

    [Fact]
    public async Task ADirectRevocation_UsesThePrivilegedGroup()
    {
        var cause = _events.Add(MemberChange(AdminsGuid, AdminsDn, WatchEventKind.GroupMemberRemoved, at: Now.AddMinutes(-1)));
        _events.Add(MemberChange(AdminsGuid, AdminsDn, WatchEventKind.GroupMemberAdded, at: Now.AddMinutes(-2)));

        var found = await new PrivilegeAttributionInheritor(_events).FindCauseAsync(_events.Add(Privilege(WatchEventKind.PrivilegeRevoked)), Ct);

        Assert.Equal(cause.EventId, found!.EventId);
    }

    private const string ItOpsDn = "CN=IT-Ops Łódź,OU=Grupy,DC=contoso,DC=test";
    private const string AdministratorsDn = "CN=Administrators,CN=Builtin,DC=contoso,DC=test";
    private static readonly Guid ItOpsGuid = Guid.Parse("0b4c22e6-6c1e-4b7a-9d2a-2f0b8f3a1003");
    private static readonly Guid AdministratorsGuid = Guid.Parse("0b4c22e6-6c1e-4b7a-9d2a-2f0b8f3a1004");
    private static readonly string AttackerSid = DomainSid + "-6666";
    private static readonly string HelpdeskSid = DomainSid + "-2201";

    private static ObjectRef GroupRef(Guid guid, string dn) => new() { Dn = dn, Kind = WatchObjectKind.Group, ObjectGuid = guid };

    /// <summary><paramref name="member"/> added to (removed from) the group <paramref name="group"/> by <paramref name="actorSid"/>.</summary>
    private static WatchEvent Edge(ObjectRef group, ObjectRef member, string actorSid, DateTimeOffset at, WatchEventKind kind = WatchEventKind.GroupMemberAdded) =>
        Create(kind, group.Dn, WatchObjectKind.Group, detectedAt: at, objectGuid: group.ObjectGuid) with
        {
            ChangeSetId = Guid.NewGuid(),
            Member = member,
            Attribution = Resolved(actorSid),
        };

    private static WatchEvent PrivilegeOf(ObjectRef root, IReadOnlyList<ObjectRef> via, WatchEventKind kind = WatchEventKind.PrivilegeGranted) =>
        Create(kind) with { ChangeSetId = Guid.NewGuid(), Member = root, Via = via };   // its own change set (ObjectProcessor.Emit)

    private static ObjectRef Bob => new() { Dn = UserDn, Kind = WatchObjectKind.User, ObjectGuid = UserGuid };

    [Fact]
    public async Task NestingAGroupIntoTheRoot_IsTheCause_NotAnEarlierAdditionToThatGroup()
    {
        // attribution-rules-2: helpdesk added Bob to IT-Ops yesterday; today an attacker nests IT-Ops into Domain Admins.
        var itOps = GroupRef(ItOpsGuid, ItOpsDn);
        var admins = GroupRef(AdminsGuid, AdminsDn);
        _events.Add(Edge(itOps, Bob, HelpdeskSid, Now.AddDays(-1)));
        var nesting = _events.Add(Edge(admins, itOps, AttackerSid, Now));
        var privilege = _events.Add(PrivilegeOf(admins, [itOps]));

        var cause = await new PrivilegeAttributionInheritor(_events).FindCauseAsync(privilege, Ct);

        Assert.Equal(nesting.EventId, cause!.EventId);
        Assert.Equal(AttackerSid, cause.Attribution.Actor!.Sid);
    }

    [Fact]
    public async Task APathOfTwoGroups_FindsTheEdgeOfThePrincipal()
    {
        // Bob added to IT-Ops; IT-Ops ∈ Domain Admins ∈ Administrators: the Administrators event has Via [Domain Admins, IT-Ops].
        var itOps = GroupRef(ItOpsGuid, ItOpsDn);
        var admins = GroupRef(AdminsGuid, AdminsDn);
        var administrators = GroupRef(AdministratorsGuid, AdministratorsDn);
        var added = _events.Add(Edge(itOps, Bob, HelpdeskSid, Now));
        var privilege = _events.Add(PrivilegeOf(administrators, [admins, itOps]));

        Assert.Equal(added.EventId, (await new PrivilegeAttributionInheritor(_events).FindCauseAsync(privilege, Ct))!.EventId);
    }

    [Fact]
    public async Task AMembershipChangeLongBeforeThePrivilegeEvent_IsNotItsCause()
    {
        // Better unresolved than the wrong person: a day-old addition did not cause a privilege detected now.
        var admins = GroupRef(AdminsGuid, AdminsDn);
        _events.Add(Edge(admins, Bob, HelpdeskSid, Now.AddHours(-20)));
        var privilege = _events.Add(PrivilegeOf(admins, []));

        Assert.Null(await new PrivilegeAttributionInheritor(_events).FindCauseAsync(privilege, Ct));
    }

    [Fact]
    public async Task APrimaryGroupChange_OfThePrincipal_IsACause()
    {
        var admins = GroupRef(AdminsGuid, AdminsDn);
        var primary = _events.Add(Create(WatchEventKind.PrimaryGroupChanged, detectedAt: Now) with { ChangeSetId = Guid.NewGuid(), Attribution = Resolved(AttackerSid) });
        var privilege = _events.Add(PrivilegeOf(admins, []));

        Assert.Equal(primary.EventId, (await new PrivilegeAttributionInheritor(_events).FindCauseAsync(privilege, Ct))!.EventId);
    }

    [Fact]
    public async Task ARevocation_FindsTheRemovalOnAnyEdge()
    {
        var itOps = GroupRef(ItOpsGuid, ItOpsDn);
        var admins = GroupRef(AdminsGuid, AdminsDn);
        var removal = _events.Add(Edge(admins, itOps, AdminSid, Now.AddSeconds(-30), WatchEventKind.GroupMemberRemoved));
        _events.Add(Edge(itOps, Bob, HelpdeskSid, Now.AddSeconds(-20)));   // an addition does not revoke
        var privilege = _events.Add(PrivilegeOf(admins, [itOps], WatchEventKind.PrivilegeRevoked));

        Assert.Equal(removal.EventId, (await new PrivilegeAttributionInheritor(_events).FindCauseAsync(privilege, Ct))!.EventId);
    }

    [Fact]
    public async Task NoCause_IsNull() =>
        Assert.Null(await new PrivilegeAttributionInheritor(_events).FindCauseAsync(_events.Add(Privilege()), Ct));

    [Fact]
    public void Inherit_CopiesTheAttributionAndStampsIt()
    {
        var inherited = PrivilegeAttributionInheritor.Inherit(Resolved(AdminSid) with { ResolvedAt = null }, Now);

        Assert.Equal(AttributionStatus.Resolved, inherited.Status);
        Assert.Equal(AdminSid, inherited.Actor!.Sid);
        Assert.Equal(Now, inherited.ResolvedAt);
    }
}
