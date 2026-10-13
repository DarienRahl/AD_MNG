using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Directory.IntegrationTests.Infrastructure;

namespace AdMng.Directory.IntegrationTests.Integration;

public sealed class VirtualListTests(LabFixture lab)
{
    private static readonly DirectorySearch Users = new()
    {
        BaseDn = LabSettings.CompanyOu,
        Filter = LdapFilter.And(LdapFilter.Equal(Attr.ObjectCategory, "person"), LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.User)),
        Attributes = [Attr.SamAccountName],
        Sort = new SortKey(Attr.SamAccountName),
    };

    [Fact]
    public async Task Window_ByOffset_MatchesTheSortedList()
    {
        LabSettings.RequireLab();
        var all = await SortedNamesAsync();

        var page = await lab.Admin.GetVirtualListPageAsync(
            Users,
            new VirtualListRequest { Offset = 10, Count = 20 },
            TestContext.Current.CancellationToken);

        Assert.Equal(10, page.Offset);
        Assert.Equal(all.Count, page.TotalCount);
        Assert.Equal(all.Skip(10).Take(20), page.Items.Select(i => i.GetString(Attr.SamAccountName)!));

        // Next window, passing the server context and the known count.
        var next = await lab.Admin.GetVirtualListPageAsync(
            Users,
            new VirtualListRequest { Offset = 30, Count = 20, ContextId = page.ContextId, EstimatedCount = page.TotalCount },
            TestContext.Current.CancellationToken);
        Assert.Equal(30, next.Offset);
        Assert.Equal(all.Skip(30).Take(20), next.Items.Select(i => i.GetString(Attr.SamAccountName)!));
    }

    [Fact]
    public async Task Window_AtTheEnd_IsShort()
    {
        LabSettings.RequireLab();
        var all = await SortedNamesAsync();

        var page = await lab.Admin.GetVirtualListPageAsync(
            Users,
            new VirtualListRequest { Offset = all.Count - 5, Count = 20 },
            TestContext.Current.CancellationToken);

        Assert.Equal(all.Count - 5, page.Offset);
        Assert.Equal(all.TakeLast(5), page.Items.Select(i => i.GetString(Attr.SamAccountName)!));
    }

    [Fact]
    public async Task Window_BySeekValue_JumpsToTheFirstMatch()
    {
        LabSettings.RequireLab();
        var all = await SortedNamesAsync();
        var expectedIndex = all.FindIndex(n => string.Compare(n, "m", StringComparison.OrdinalIgnoreCase) >= 0);

        var page = await lab.Admin.GetVirtualListPageAsync(
            Users,
            new VirtualListRequest { SeekValue = "m", Count = 10 },
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedIndex, page.Offset);
        Assert.Equal(all.Count, page.TotalCount);
        Assert.StartsWith("m", page.Items[0].GetString(Attr.SamAccountName), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(all.Skip(expectedIndex).Take(10), page.Items.Select(i => i.GetString(Attr.SamAccountName)!));
    }

    [Fact]
    public async Task ContextIds_AreBoundToTheirSession()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var first = await lab.Admin.GetVirtualListPageAsync(Users, new VirtualListRequest { Count = 5 }, ct);
        await using var other = await LabFixture.ConnectAsync(LabSettings.Plain(LabSettings.AdminUser), LabSettings.AdminPassword, cancellationToken: ct);

        // A context of another session (other connections) is not sent - the window is served without it.
        var page = await other.GetVirtualListPageAsync(
            Users,
            new VirtualListRequest { Offset = 5, Count = 5, ContextId = first.ContextId },
            ct);
        var garbage = await lab.Admin.GetVirtualListPageAsync(
            Users,
            new VirtualListRequest { Offset = 5, Count = 5, ContextId = [1, 2, 3] },
            ct);

        Assert.Equal(5, page.Items.Count);
        Assert.Equal(
            page.Items.Select(i => i.GetString(Attr.SamAccountName)),
            garbage.Items.Select(i => i.GetString(Attr.SamAccountName)));
    }

    [Fact]
    public async Task VirtualList_RequiresASortKey()
    {
        LabSettings.RequireLab();
        await Assert.ThrowsAsync<ArgumentException>(() => lab.Admin.GetVirtualListPageAsync(
            Users with { Sort = null },
            new VirtualListRequest(),
            TestContext.Current.CancellationToken));
    }

    /// <summary>The same list sorted by the server (paged search with the sort control).</summary>
    private async Task<List<string>> SortedNamesAsync()
    {
        var names = new List<string>();
        await foreach (var user in lab.Admin.SearchAsync(Users with { PageSize = 1000 }, TestContext.Current.CancellationToken))
        {
            names.Add(user.GetString(Attr.SamAccountName)!);
        }

        return names;
    }
}
