using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.IntegrationTests.Infrastructure;

namespace AdMng.Watchdog.IntegrationTests.Detection;

/// <summary>
/// Group membership read with the Extended DN control (spec 10.8, 22.4): direct additions, effective privileged access
/// through a nested group, a 1600-member group (Windows ranges at 1500 – Samba returns all values, the reader handles
/// both) and the rename of a member, which rewrites the value without bumping the group (Appendix B) and is no change.
/// </summary>
public sealed class MembershipTests(ReadOnlyEngineFixture engine, LabFixture lab) : IClassFixture<ReadOnlyEngineFixture>
{
    private const int LargeGroupSize = 1600;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private EngineHost Host => engine.Host;

    [Fact]
    public async Task AddedMember_IsGroupMemberAdded()
    {
        WatchdogLab.RequireLab();
        var ou = await lab.CreateTestOuAsync("Grupy", Ct);
        var group = await lab.Writer.CreateGroupAsync(ou, "Projekt Wisła", Ct);
        var user = await lab.Writer.CreateUserAsync(ou, "Małgorzata", "Wójcik", TestNames.Password(), cancellationToken: Ct);
        await Host.RunDetectionAsync(Ct);
        var mark = await Host.MarkAsync(Ct);

        await lab.Writer.AddMembersAsync(group, [user.Dn], Ct);
        await Host.RunDetectionAsync(Ct);

        var added = EventAssert.Single(await Host.EventsAsync(ou, mark, Ct), WatchEventKind.GroupMemberAdded, group);
        var member = Assert.IsType<ObjectRef>(added.Member);
        Assert.True(DistinguishedName.AreEqual(user.Dn, member.Dn), $"member {member.Dn}");
        Assert.Equal(user.SamAccountName, member.SamAccountName);
        Assert.NotNull(member.ObjectGuid);
    }

    [Fact]
    public async Task MemberOfANestedPrivilegedGroup_IsPrivilegeGrantedWithVia()
    {
        WatchdogLab.RequireLab();
        var ou = await lab.CreateTestOuAsync("Uprzywilejowani", Ct);
        var critical = await lab.Writer.CreateGroupAsync(ou, "Administratorzy Krytyczni", Ct);
        var operators = await lab.Writer.CreateGroupAsync(ou, "Operatorzy Zmian", Ct);
        await lab.Writer.AddMembersAsync(critical, [operators], Ct);
        var user = await lab.Writer.CreateUserAsync(ou, "Jadwiga", "Kowalska", TestNames.Password(), cancellationToken: Ct);
        await Host.SaveConfigurationAsync(c => c with
        {
            PrivilegedGroups = c.PrivilegedGroups with { AdditionalCriticalDns = [.. c.PrivilegedGroups.AdditionalCriticalDns, critical] },
        }, Ct);
        await Host.RunDetectionAsync(Ct);
        var mark = await Host.MarkAsync(Ct);

        await lab.Writer.AddMembersAsync(operators, [user.Dn], Ct);
        await Host.RunDetectionAsync(Ct);

        var events = await Host.EventsAsync(ou, mark, Ct);
        EventAssert.Single(events, WatchEventKind.GroupMemberAdded, operators);
        var granted = EventAssert.Single(events, WatchEventKind.PrivilegeGranted, user.Dn);
        Assert.Equal(Severity.Critical, granted.Severity);
        Assert.True(DistinguishedName.AreEqual(critical, granted.Member?.Dn), $"privileged group {granted.Member?.Dn}");
        Assert.Contains(granted.Via, v => DistinguishedName.AreEqual(operators, v.Dn));
    }

    [Fact]
    public async Task GroupWith1600Members_IsReadCompletelyWithExtendedDns()
    {
        WatchdogLab.RequireLab();
        var ou = await lab.CreateTestOuAsync("Duża grupa", Ct);
        var contactsOu = await lab.Writer.CreateOuAsync(ou, "Kontakty", null, Ct);
        var group = await lab.Writer.CreateGroupAsync(ou, "Wszyscy Kontrahenci", Ct);
        var contacts = new List<string>(LargeGroupSize);
        for (var i = 0; i < LargeGroupSize; i++)
        {
            contacts.Add(await lab.Writer.CreateContactAsync(contactsOu, $"Kontrahent Łódź {i:D4}", Ct));
        }

        await Host.RunDetectionAsync(Ct);
        var mark = await Host.MarkAsync(Ct);

        foreach (var chunk in contacts.Chunk(400))
        {
            await lab.Writer.AddMembersAsync(group, chunk, Ct);
        }

        await Host.RunDetectionAsync(Ct);

        var added = (await Host.EventsAsync(ou, mark, Ct)).Where(e => e.Kind == WatchEventKind.GroupMemberAdded).ToList();
        Assert.Equal(LargeGroupSize, added.Count);
        Assert.All(added, e => Assert.NotNull(e.Member?.ObjectGuid));
        var snapshot = Assert.IsType<Core.Model.SnapshotObject>(await Host.Snapshots.FindByDnAsync(group, Ct));
        var members = await Host.Snapshots.GetMembersAsync(snapshot.ObjectGuid, Ct);
        Assert.Equal(LargeGroupSize, members.Count);
        Assert.All(members, m => Assert.NotNull(m.ObjectGuid));

        // Renaming a member rewrites the group's value without a USN bump of the group: no membership change.
        mark = await Host.MarkAsync(Ct);
        await lab.Writer.MoveAsync(contacts[0], null, "CN=Kontrahent Łódź – po zmianie nazwy", Ct);
        await Host.RunDetectionAsync(Ct);
        Assert.DoesNotContain(await Host.EventsAsync(ou, mark, Ct), e => e.Kind is WatchEventKind.GroupMemberAdded or WatchEventKind.GroupMemberRemoved);
        Assert.Equal(LargeGroupSize, (await Host.Snapshots.GetMembersAsync(snapshot.ObjectGuid, Ct)).Count);
    }
}
