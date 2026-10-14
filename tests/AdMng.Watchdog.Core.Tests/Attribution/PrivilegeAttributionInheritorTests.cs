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
