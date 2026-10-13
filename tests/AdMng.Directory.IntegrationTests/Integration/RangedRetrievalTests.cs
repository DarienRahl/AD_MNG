using System.Diagnostics;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Directory.IntegrationTests.Infrastructure;

namespace AdMng.Directory.IntegrationTests.Integration;

public sealed class RangedRetrievalTests(LabFixture lab)
{
    private const int MemberCount = 1600;

    [Fact]
    public async Task LargeGroup_AllMembersAreReturned()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var stopwatch = Stopwatch.StartNew();
        var ou = await lab.CreateOuAsync(TestNames.Unique("Zakres"));
        var contacts = new string[MemberCount];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, MemberCount),
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (i, _) => contacts[i] = await lab.CreateContactAsync(ou, $"Kontakt {i:D4} Żółw"));

        // One add request with all 1600 values (more than AD's MaxValRange of 1500).
        var groupDn = DistinguishedName.Parse(ou).Child("CN", "GG_Duża_Grupa").ToString();
        await lab.Admin.AddAsync(
            groupDn,
            new Dictionary<string, IReadOnlyList<object>>
            {
                [Attr.ObjectClass] = ["top", ObjectClasses.Group],
                [Attr.SamAccountName] = [TestNames.SamAccountName()],
                [Attr.Member] = [.. contacts],
            },
            ct);
        TestContext.Current.TestOutputHelper?.WriteLine($"Created {MemberCount} members in {stopwatch.ElapsedMilliseconds} ms.");

        var expected = new HashSet<string>(contacts, StringComparer.OrdinalIgnoreCase);

        var all = await Collect(lab.Admin.GetAllValuesAsync(groupDn, Attr.Member, ct));
        Assert.Equal(MemberCount, all.Count);
        Assert.True(expected.SetEquals(all.Select(Normalize)));

        // Explicit small chunks exercise the "range=x-y" loop regardless of the server's MaxValRange.
        var session = (LdapDirectorySession)lab.Admin;
        var chunked = await Collect(session.GetAllValuesAsync(groupDn, Attr.Member, chunkSize: 500, ct));
        Assert.Equal(MemberCount, chunked.Count);
        Assert.True(expected.SetEquals(chunked.Select(Normalize)));

        // A plain read returns the attribute (possibly ranged, e.g. "member;range=0-1499") under its base name.
        var group = await lab.Admin.GetAsync(groupDn, [Attr.Member], ct);
        Assert.NotNull(group);
        Assert.True(group.TryGet(Attr.Member, out var members));
        Assert.True(members.Count is > 0 and <= MemberCount);
        if (members.Count < MemberCount)
        {
            Assert.True(AttributeRange.TryParse(members.Name, out var range));
            Assert.Equal(0, range.Start);
        }
    }

    [Fact]
    public async Task EmptyOrMissingAttribute_YieldsNothing()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var emptyGroup = $"CN=GG_Pusta_Grupa,OU=Bezpieczeństwa,OU=Grupy,{LabSettings.CompanyOu}";

        Assert.Empty(await Collect(lab.Admin.GetAllValuesAsync(emptyGroup, Attr.Member, ct)));

        var missing = $"CN=Brak,{LabSettings.TestOu}";
        var error = await Assert.ThrowsAsync<DirectoryException>(async () => await Collect(lab.Admin.GetAllValuesAsync(missing, Attr.Member, ct)));
        Assert.Equal(DirectoryErrorKind.NoSuchObject, error.Kind);
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
