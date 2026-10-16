using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Directory.IntegrationTests.Infrastructure;

namespace AdMng.Directory.IntegrationTests.Integration;

public sealed class SearchTests(LabFixture lab)
{
    private static readonly string UsersFilter = LdapFilter.And(
        LdapFilter.Equal(Attr.ObjectCategory, "person"),
        LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.User));

    [Fact]
    public async Task PagedSearch_ReturnsAllUsersOfTheCompany()
    {
        LabSettings.RequireLab();
        var search = new DirectorySearch
        {
            BaseDn = LabSettings.CompanyOu,
            Filter = UsersFilter,
            Attributes = [Attr.SamAccountName, Attr.ObjectClass, Attr.ObjectGuid, Attr.ObjectSid, Attr.UserAccountControl, Attr.Department],
            PageSize = 100,
        };

        var users = await Collect(lab.Admin.SearchAsync(search, TestContext.Current.CancellationToken));

        Assert.True(users.Count > 400, $"Only {users.Count} users returned.");
        Assert.Equal(users.Count, users.Select(u => u.DistinguishedName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(users, u =>
        {
            Assert.Equal(ObjectKind.User, u.Kind);
            Assert.NotNull(u.ObjectGuid);
            Assert.StartsWith("S-1-5-21-", u.ObjectSid, StringComparison.Ordinal);
            Assert.IsType<byte[]>(u.Attributes[Attr.ObjectGuid][0]);
            Assert.IsType<string>(u.Attributes[Attr.SamAccountName][0]);
            Assert.NotNull(u.AccountControl);
        });
        Assert.Contains(users, u => u.GetString(Attr.Department) == "Księgowość");
    }

    [Fact]
    public async Task PagedSearch_StreamsTheFirstPageBeforeTheEnumerationCompletes()
    {
        LabSettings.RequireLab();
        var logs = new CapturingLoggerFactory();
        await using var session = await LabFixture.ConnectAsync(
            LabSettings.Plain(LabSettings.AdminUser),
            LabSettings.AdminPassword,
            logs,
            cancellationToken: TestContext.Current.CancellationToken);
        var search = new DirectorySearch { BaseDn = LabSettings.CompanyOu, Filter = UsersFilter, PageSize = 100 };

        var pagesBefore = PageEvents(logs); // the connect itself ran paged searches (schema)
        int? pagesWhenFirstEntryArrived = null;
        var count = 0;
        await foreach (var _ in session.SearchAsync(search, TestContext.Current.CancellationToken))
        {
            pagesWhenFirstEntryArrived ??= PageEvents(logs) - pagesBefore;
            count++;
        }

        Assert.Equal(1, pagesWhenFirstEntryArrived);
        Assert.True(count > 400);
        Assert.Equal((count + 99) / 100, PageEvents(logs) - pagesBefore);
    }

    [Fact]
    public async Task SizeLimit_StopsAfterTheRequestedNumberOfEntries()
    {
        LabSettings.RequireLab();
        var search = new DirectorySearch { BaseDn = LabSettings.CompanyOu, Filter = UsersFilter, PageSize = 100, SizeLimit = 150 };

        var users = await Collect(lab.Admin.SearchAsync(search, TestContext.Current.CancellationToken));

        Assert.Equal(150, users.Count);

        // The abandoned server side result set does not affect the next search on the same session.
        var next = await Collect(lab.Admin.SearchAsync(search with { SizeLimit = 5 }, TestContext.Current.CancellationToken));
        Assert.Equal(5, next.Count);
    }

    [Fact]
    public async Task Cancellation_AbandonsTheSearch_AndTheSessionStaysUsable()
    {
        LabSettings.RequireLab();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var search = new DirectorySearch { BaseDn = LabSettings.CompanyOu, Filter = UsersFilter, PageSize = 50 };
        var received = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in lab.Admin.SearchAsync(search, cts.Token))
            {
                if (++received == 60)
                {
                    await cts.CancelAsync();
                }
            }
        });

        Assert.InRange(received, 60, 100);
        var again = await Collect(lab.Admin.SearchAsync(search with { SizeLimit = 10 }, TestContext.Current.CancellationToken));
        Assert.Equal(10, again.Count);
    }

    [Fact]
    public async Task CancelledToken_FailsImmediately()
    {
        LabSettings.RequireLab();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lab.Admin.GetAsync(LabSettings.DomainDn, [], cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in lab.Admin.SearchAsync(new DirectorySearch { BaseDn = LabSettings.DomainDn }, cts.Token))
            {
            }
        });
    }

    [Fact]
    public async Task OneLevelSearch_WithServerSort()
    {
        LabSettings.RequireLab();
        var search = new DirectorySearch
        {
            BaseDn = LabSettings.EmployeesOu,
            Scope = SearchScope.OneLevel,
            Filter = LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.OrganizationalUnit),
            Attributes = [Attr.Ou, Attr.ObjectClass],
            Sort = new SortKey(Attr.Ou, Descending: true),
        };

        var ous = await Collect(lab.Admin.SearchAsync(search, TestContext.Current.CancellationToken));

        Assert.Equal(7, ous.Count);
        Assert.Contains(ous, o => o.Name == "Księgowość");
        Assert.All(ous, o => Assert.Equal(ObjectKind.OrganizationalUnit, o.Kind));
        var names = ous.Select(o => o.GetString(Attr.Ou)!).ToList();
        Assert.Equal(names.OrderByDescending(n => n, StringComparer.CurrentCultureIgnoreCase), names);
    }

    [Fact]
    public async Task EmptyAttributeList_ReturnsOnlyDistinguishedNames()
    {
        LabSettings.RequireLab();
        var search = new DirectorySearch { BaseDn = LabSettings.EmployeesOu, Scope = SearchScope.OneLevel, SizeLimit = 3 };

        var entries = await Collect(lab.Admin.SearchAsync(search, TestContext.Current.CancellationToken));

        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.Empty(e.Attributes));
    }

    [Fact]
    public async Task GetAsync_ReadsOneObject_AndReturnsNullWhenMissing()
    {
        LabSettings.RequireLab();
        var domain = await lab.Admin.GetAsync(LabSettings.DomainDn, [Attr.ObjectClass, Attr.Dc, "maxPwdAge"], TestContext.Current.CancellationToken);
        Assert.NotNull(domain);
        Assert.Equal(ObjectKind.Domain, domain.Kind);
        Assert.Equal("contoso", domain.GetString(Attr.Dc), ignoreCase: true);

        var missing = await lab.Admin.GetAsync($"OU=Nie ma takiej,{LabSettings.DomainDn}", [Attr.Name], TestContext.Current.CancellationToken);
        Assert.Null(missing);
    }

    [Fact]
    public async Task SecurityDescriptor_IsReadWithTheSdFlagsControl()
    {
        LabSettings.RequireLab();
        var ou = await lab.Admin.GetAsync(LabSettings.EmployeesOu, [Attr.NtSecurityDescriptor], TestContext.Current.CancellationToken);

        var descriptor = ou?.GetBytes(Attr.NtSecurityDescriptor);
        Assert.NotNull(descriptor);
        Assert.True(descriptor.Length > 20);
        Assert.Equal(1, descriptor[0]); // SECURITY_DESCRIPTOR revision

        var search = new DirectorySearch
        {
            BaseDn = LabSettings.EmployeesOu,
            Scope = SearchScope.Base,
            Attributes = [Attr.NtSecurityDescriptor],
            ReadSecurityDescriptor = true,
        };
        var viaSearch = Assert.Single(await Collect(lab.Admin.SearchAsync(search, TestContext.Current.CancellationToken)));
        Assert.NotNull(viaSearch.GetBytes(Attr.NtSecurityDescriptor));
    }

    [Fact]
    public async Task AttributeScopedQuery_ReturnsGroupMembers()
    {
        LabSettings.RequireLab();
        var group = $"CN=GG_IT,OU=Bezpieczeństwa,OU=Grupy,{LabSettings.CompanyOu}";
        var search = new DirectorySearch
        {
            BaseDn = group,
            Scope = SearchScope.Base,
            AttributeScopedQuery = Attr.Member,
            Attributes = [Attr.SamAccountName, Attr.Department],
            PageSize = 20,
        };

        var members = await Collect(lab.Admin.SearchAsync(search, TestContext.Current.CancellationToken));
        var memberValues = (await lab.Admin.GetAsync(group, [Attr.Member], TestContext.Current.CancellationToken))!.GetStrings(Attr.Member);

        Assert.NotEmpty(members);
        Assert.Equal(memberValues.Count, members.Count);
        Assert.All(members, m => Assert.Equal("IT", m.GetString(Attr.Department)));
    }

    [Fact]
    public async Task DiacriticsInBaseDn_AreHandled()
    {
        LabSettings.RequireLab();
        var accounting = $"OU=Księgowość,{LabSettings.EmployeesOu}";
        var search = new DirectorySearch { BaseDn = accounting, Filter = UsersFilter, Attributes = [Attr.Department, Attr.DisplayName] };

        var users = await Collect(lab.Admin.SearchAsync(search, TestContext.Current.CancellationToken));

        Assert.NotEmpty(users);
        Assert.All(users, u =>
        {
            Assert.EndsWith(accounting, u.DistinguishedName, StringComparison.OrdinalIgnoreCase);
            Assert.True(u.Dn.IsDescendantOf(DistinguishedName.Parse(accounting)));
            Assert.Equal("Księgowość", u.GetString(Attr.Department));
        });
    }

    [Fact]
    public async Task InvalidFilter_IsInvalidSyntax()
    {
        LabSettings.RequireLab();
        var search = new DirectorySearch { BaseDn = LabSettings.DomainDn, Filter = "(&(objectClass=user)" };

        var error = await Assert.ThrowsAsync<DirectoryException>(async () =>
            await Collect(lab.Admin.SearchAsync(search, TestContext.Current.CancellationToken)));

        Assert.Equal(DirectoryErrorKind.InvalidSyntax, error.Kind);
    }

    [Fact]
    public async Task ParallelSearches_ShareThePool()
    {
        LabSettings.RequireLab();
        var search = new DirectorySearch { BaseDn = LabSettings.CompanyOu, Filter = UsersFilter, PageSize = 50, SizeLimit = 120 };

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            Collect(lab.Admin.SearchAsync(search, TestContext.Current.CancellationToken))));

        Assert.All(results, r => Assert.Equal(120, r.Count));
        var pool = ((LdapDirectorySession)lab.Admin).Pool;
        Assert.InRange(pool.Count, 1, 4);
    }

    [Fact]
    public void InvalidSearchArguments_AreRejectedBeforeSending()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        Assert.Throws<ArgumentOutOfRangeException>(() => lab.Admin.SearchAsync(new DirectorySearch { BaseDn = LabSettings.DomainDn, PageSize = 0 }, ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => lab.Admin.SearchAsync(new DirectorySearch { BaseDn = LabSettings.DomainDn, SizeLimit = -1 }, ct));
        Assert.Throws<ArgumentException>(() => lab.Admin.SearchAsync(
            new DirectorySearch { BaseDn = LabSettings.DomainDn, AttributeScopedQuery = Attr.Member, Scope = SearchScope.Subtree },
            ct));
    }

    private static int PageEvents(CapturingLoggerFactory logs) => logs.WithEventId(1205).Count;

    private static async Task<List<DirectoryObject>> Collect(IAsyncEnumerable<DirectoryObject> source)
    {
        var list = new List<DirectoryObject>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }
}
