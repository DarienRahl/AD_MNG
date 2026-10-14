using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Directory.IntegrationTests.Infrastructure;

namespace AdMng.Directory.IntegrationTests.Integration;

/// <summary>
/// Extended DN control (spec 4.2, D3): DN-syntax values come back as <c>&lt;GUID=…&gt;;&lt;SID=…&gt;;dn</c> and parse to the
/// referenced objects' identities, the entry DN itself stays plain, and ranged reads keep the extended form on every
/// chunk. Everything is created in a throwaway OU below the fixture's test OU.
/// </summary>
public sealed class ExtendedDnTests(LabFixture lab)
{
    private const int LargeGroupMembers = 1600;

    [Fact]
    public async Task Search_ReturnsMemberAndManagerValuesWithGuidAndSid_AndAPlainEntryDn()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var ou = await lab.CreateOuAsync(TestNames.Unique("Rozszerzone DN"));
        var managerDn = await lab.CreateUserAsync(ou, "Łucja Kierownik");
        var userDn = await lab.CreateUserAsync(ou, "Zofia Żak", new Dictionary<string, string> { [Attr.Manager] = managerDn });
        var groupDn = DistinguishedName.Parse(ou).Child("CN", "GG_Księgowość").ToString();
        await lab.Admin.AddAsync(
            groupDn,
            new Dictionary<string, IReadOnlyList<object>>
            {
                [Attr.ObjectClass] = ["top", ObjectClasses.Group],
                [Attr.SamAccountName] = [TestNames.SamAccountName()],
                [Attr.Member] = [userDn],
            },
            ct);
        var user = await ReadIdentityAsync(userDn, ct);
        var manager = await ReadIdentityAsync(managerDn, ct);

        var group = await SearchOneAsync(groupDn, Attr.Member, ct);
        var user2 = await SearchOneAsync(userDn, Attr.Manager, ct);

        Assert.True(DistinguishedName.AreEqual(groupDn, group.DistinguishedName), "the entry DN must be stripped to the plain form");
        Assert.DoesNotContain("<GUID=", group.DistinguishedName, StringComparison.OrdinalIgnoreCase);
        var member = Parse(Assert.Single(group.GetStrings(Attr.Member)));
        Assert.Equal(user.Guid, member.Guid);
        Assert.Equal(user.Sid, member.Sid);
        Assert.True(DistinguishedName.AreEqual(userDn, member.Dn));

        var managerValue = Parse(Assert.Single(user2.GetStrings(Attr.Manager)));
        Assert.Equal(manager.Guid, managerValue.Guid);
        Assert.Equal(manager.Sid, managerValue.Sid);
        Assert.True(DistinguishedName.AreEqual(managerDn, managerValue.Dn));
    }

    [Fact]
    public async Task Search_WithoutTheControl_ReturnsPlainDnValues()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var ou = await lab.CreateOuAsync(TestNames.Unique("Zwykłe DN"));
        var managerDn = await lab.CreateUserAsync(ou, "Bożena Kierowniczka");
        var userDn = await lab.CreateUserAsync(ou, "Jan Wiśniewski", new Dictionary<string, string> { [Attr.Manager] = managerDn });

        var user = await lab.Admin.GetAsync(userDn, [Attr.Manager], ct);

        Assert.NotNull(user);
        var value = Assert.Single(user.GetStrings(Attr.Manager));
        Assert.False(value.StartsWith('<'));
        Assert.True(DistinguishedName.AreEqual(managerDn, value));
    }

    [Fact]
    public async Task RangedRead_OfALargeGroup_KeepsTheExtendedFormOnEveryChunk()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var ou = await lab.CreateOuAsync(TestNames.Unique("Duża grupa"));
        var contacts = new string[LargeGroupMembers];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, LargeGroupMembers),
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (i, _) => contacts[i] = await lab.CreateContactAsync(ou, $"Kontakt {i:D4} Źdźbło"));
        var groupDn = DistinguishedName.Parse(ou).Child("CN", "GG_Wielka_Ćma").ToString();
        await lab.Admin.AddAsync(
            groupDn,
            new Dictionary<string, IReadOnlyList<object>>
            {
                [Attr.ObjectClass] = ["top", ObjectClasses.Group],
                [Attr.SamAccountName] = [TestNames.SamAccountName()],
                [Attr.Member] = [.. contacts],
            },
            ct);
        var expected = new HashSet<string>(contacts.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        var options = new AttributeReadOptions { ExtendedDn = true };

        // Through the interface (what the watchdog's MembershipReader calls) and with small explicit chunks.
        var all = await Collect(lab.Admin.GetAllValuesAsync(groupDn, Attr.Member, options, ct));
        var session = (LdapDirectorySession)lab.Admin;
        var chunked = await Collect(session.GetAllValuesAsync(groupDn, Attr.Member, chunkSize: 500, options, ct));

        foreach (var values in new[] { all, chunked })
        {
            Assert.Equal(LargeGroupMembers, values.Count);
            var parsed = values.Select(Parse).ToList();
            Assert.All(parsed, p => Assert.NotNull(p.Guid));
            Assert.All(parsed, p => Assert.Null(p.Sid));   // contacts are not security principals
            Assert.Equal(LargeGroupMembers, parsed.Select(p => p.Guid).Distinct().Count());
            Assert.True(expected.SetEquals(parsed.Select(p => Normalize(p.Dn))));
        }

        // The GUIDs are the members' objectGUIDs.
        var sample = await ReadIdentityAsync(contacts[1234], ct);
        Assert.Contains(all.Select(Parse), p => p.Guid == sample.Guid);
    }

    [Fact]
    public async Task RangedRead_WithIncludeDeleted_ReadsATombstone()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var ou = await lab.CreateOuAsync(TestNames.Unique("Usunięte"));
        var contactDn = await lab.CreateContactAsync(ou, "Kontakt Usunięty Ąę");
        var guid = (await ReadIdentityAsync(contactDn, ct)).Guid;
        await lab.Admin.DeleteAsync(contactDn, subtree: false, ct);
        var tombstone = await FindTombstoneAsync(guid, ct);

        // Windows AD answers noSuchObject for a tombstone without the Show Deleted control; Samba 4.19 returns it
        // for a base read anyway, so only the positive path (both controls on the ranged request) is asserted.
        var options = new AttributeReadOptions { IncludeDeleted = true, ExtendedDn = true };
        var classes = await Collect(lab.Admin.GetAllValuesAsync(tombstone, Attr.ObjectClass, options, ct));

        Assert.Contains(ObjectClasses.Contact, classes, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<DirectoryObject> SearchOneAsync(string dn, string attribute, CancellationToken ct)
    {
        var search = new DirectorySearch { BaseDn = dn, Scope = SearchScope.Base, Attributes = [attribute], ExtendedDn = true };
        DirectoryObject? found = null;
        await foreach (var entry in lab.Admin.SearchAsync(search, ct))
        {
            Assert.Null(found);
            found = entry;
        }

        return found ?? throw new InvalidOperationException("The object was not found.");
    }

    private async Task<(Guid Guid, string? Sid)> ReadIdentityAsync(string dn, CancellationToken ct)
    {
        var entry = await lab.Admin.GetAsync(dn, [Attr.ObjectGuid, Attr.ObjectSid], ct);
        Assert.NotNull(entry);
        return (entry.ObjectGuid ?? throw new InvalidOperationException("objectGUID missing."), entry.ObjectSid);
    }

    private async Task<string> FindTombstoneAsync(Guid guid, CancellationToken ct)
    {
        var search = new DirectorySearch
        {
            BaseDn = $"CN=Deleted Objects,{LabSettings.DomainDn}",
            Scope = SearchScope.OneLevel,
            Filter = LdapFilter.EqualGuid(Attr.ObjectGuid, guid),
            IncludeDeleted = true,
        };
        await foreach (var entry in lab.Admin.SearchAsync(search, ct))
        {
            return entry.DistinguishedName;
        }

        throw new InvalidOperationException("The tombstone was not found.");
    }

    private static ExtendedDistinguishedName Parse(string value)
    {
        Assert.True(ExtendedDistinguishedName.TryParse(value, out var parsed), "not an extended DN value");
        Assert.StartsWith("<GUID=", value, StringComparison.OrdinalIgnoreCase);
        return parsed;
    }

    private static string Normalize(string dn) => DistinguishedName.Parse(dn).ToString();

    private static async Task<List<string>> Collect(IAsyncEnumerable<string> source)
    {
        var list = new List<string>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }
}
