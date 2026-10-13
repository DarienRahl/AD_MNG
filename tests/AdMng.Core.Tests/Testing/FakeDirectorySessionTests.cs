using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Testing;

namespace AdMng.Core.Tests.Testing;

/// <summary>The fake must behave like a DC; these tests pin the semantics other tests rely on.</summary>
public class FakeDirectorySessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (FakeDirectoryBuilder Builder, SampleCompany Company) Create()
    {
        var builder = new FakeDirectoryBuilder();
        return (builder, builder.AddSampleCompany());
    }

    private static async Task<List<DirectoryObject>> SearchAsync(FakeDirectorySession session, string filter, string? baseDn = null, SearchScope scope = SearchScope.Subtree, params string[] attributes)
    {
        var result = new List<DirectoryObject>();
        var search = new DirectorySearch { BaseDn = baseDn ?? session.Info.DefaultNamingContext, Filter = filter, Scope = scope, Attributes = attributes };
        await foreach (var item in session.SearchAsync(search, Ct))
        {
            result.Add(item);
        }

        return result;
    }

    [Fact]
    public async Task Builder_CreatesWellKnownObjects()
    {
        var builder = new FakeDirectoryBuilder();
        var session = builder.Build();

        var admin = await session.GetAsync(builder.AdministratorDn, [Attr.ObjectSid, Attr.AdminCount, Attr.IsCriticalSystemObject, Attr.MemberOf], Ct);
        var dc = await session.GetAsync(builder.DomainControllerDn, [Attr.UserAccountControl, Attr.ObjectClass], Ct);

        Assert.NotNull(admin);
        Assert.Equal(500u, AdConvert.GetRid(admin.ObjectSid));
        Assert.True(admin.IsProtectedByAdminSdHolder);
        Assert.True(admin.GetBoolean(Attr.IsCriticalSystemObject));
        Assert.Contains(builder.DomainAdminsDn, admin.GetStrings(Attr.MemberOf));
        Assert.Equal(ObjectKind.Computer, dc!.Kind);
        Assert.True(((UserAccountControl)dc.GetInt32(Attr.UserAccountControl)!.Value).HasFlag(UserAccountControl.ServerTrustAccount));
        Assert.Equal(502u, AdConvert.GetRid(session.GetValues(builder.KrbtgtDn, Attr.ObjectSid)[0]));
    }

    [Fact]
    public async Task Get_IsCaseInsensitiveAndReturnsServerDn()
    {
        var (builder, company) = Create();
        var session = builder.Session;

        var user = await session.GetAsync(company.JanKowalski.ToUpperInvariant(), [Attr.SamAccountName, Attr.DisplayName], Ct);
        var dnOnly = await session.GetAsync(company.JanKowalski, [], Ct);
        var missing = await session.GetAsync("CN=Nobody," + company.ItOu, [Attr.Name], Ct);

        Assert.NotNull(user);
        Assert.Equal(company.JanKowalski, user.DistinguishedName);
        Assert.Equal("jan.kowalski", user.GetString(Attr.SamAccountName));
        Assert.Equal(2, user.Attributes.Count);
        Assert.Empty(dnOnly!.Attributes);
        Assert.Null(missing);
        await Assert.ThrowsAsync<DirectoryException>(() => session.GetAsync("not a dn", [], Ct));
    }

    [Fact]
    public async Task Builder_DerivesSamAccountNameWithoutDiacritics()
    {
        var (builder, company) = Create();

        var user = await builder.Session.GetAsync(company.ZanetaSzymanska, [Attr.SamAccountName, Attr.UserPrincipalName, Attr.LockoutTime], Ct);

        Assert.Equal("zaneta.szymanska", user!.GetString(Attr.SamAccountName));
        Assert.Equal("zaneta.szymanska@contoso.test", user.GetString(Attr.UserPrincipalName));
        Assert.True(user.IsLockedOut);
    }

    [Fact]
    public async Task Search_SupportsScopes()
    {
        var (builder, company) = Create();
        var session = builder.Session;

        var baseScope = await SearchAsync(session, "(objectClass=*)", company.EmployeesOu, SearchScope.Base);
        var oneLevel = await SearchAsync(session, "(objectClass=*)", company.EmployeesOu, SearchScope.OneLevel);
        var subtree = await SearchAsync(session, "(objectClass=user)", company.EmployeesOu);

        Assert.Single(baseScope);
        Assert.Equal(["OU=IT", "OU=Księgowość", "OU=Sprzedaż"], oneLevel.Select(o => o.Dn.Leaf.ToString()));
        Assert.Equal(8, subtree.Count);
        await Assert.ThrowsAsync<DirectoryException>(() => SearchAsync(session, "(objectClass=*)", "OU=Missing," + builder.DomainDn));
    }

    [Theory]
    [InlineData("(sn=Kowalski)", 1)]
    [InlineData("(SN=kowalski)", 1)]
    [InlineData("(sn=Ko*)", 2)]
    [InlineData("(sn=*ski)", 4)]
    [InlineData("(sn=*a*sk*)", 3)]
    [InlineData("(l=Warszawa)", 3)]
    [InlineData("(&(objectClass=user)(!(l=Warszawa)))", 5)]
    [InlineData("(&(l=Warszawa)(department=IT))", 1)]
    [InlineData("(|(l=Kraków)(l=Gdańsk))", 2)]
    [InlineData("(manager=*)", 3)]
    [InlineData("(userAccountControl:1.2.840.113556.1.4.803:=2)", 1)]
    [InlineData("(userAccountControl:1.2.840.113556.1.4.804:=65538)", 2)]
    [InlineData("(lockoutTime>=1)", 1)]
    [InlineData("(anr=Żan)", 1)]
    [InlineData("(anr=jan.kow)", 1)]
    [InlineData("(anr=Kowalski Jan)", 1)]
    [InlineData("(anr==Paweł)", 1)]
    [InlineData("(sAMAccountType=805306368)", 8)]
    [InlineData("(objectCategory=person)", 8)]
    [InlineData("(sn=\\2a)", 0)]
    public async Task Search_EvaluatesFilters(string filter, int expected)
    {
        var (builder, company) = Create();

        var results = await SearchAsync(builder.Session, filter, company.EmployeesOu);

        Assert.Equal(expected, results.Count);
    }

    [Fact]
    public async Task Search_SupportsMembershipAndInChainRules()
    {
        var (builder, company) = Create();
        var session = builder.Session;

        var direct = await SearchAsync(session, LdapFilter.Equal(Attr.MemberOf, company.GroupAll), company.CompanyOu);
        var nested = await SearchAsync(session, LdapFilter.And("(objectClass=user)", LdapFilter.InChain(Attr.MemberOf, company.GroupAll)), company.CompanyOu);
        var groups = await SearchAsync(session, ObjectKind.Group.ToLdapFilter(), company.GroupsOu);

        Assert.Equal(3, direct.Count);
        Assert.Equal(8, nested.Count);
        Assert.Equal(4, groups.Count);
    }

    [Fact]
    public async Task Search_MatchesBinaryGuidFilters()
    {
        var (builder, company) = Create();
        var jan = await builder.Session.GetAsync(company.JanKowalski, [Attr.ObjectGuid], Ct);

        var found = await SearchAsync(builder.Session, LdapFilter.EqualGuid(Attr.ObjectGuid, jan!.ObjectGuid!.Value));

        Assert.Equal(company.JanKowalski, Assert.Single(found).DistinguishedName);
    }

    [Theory]
    [InlineData("(cn=a")]
    [InlineData("(&)")]
    [InlineData("(cn=a\\zz)")]
    [InlineData("(cn=a)(cn=b)")]
    public async Task Search_RejectsMalformedFilters(string filter)
    {
        var (builder, _) = Create();

        var error = await Assert.ThrowsAsync<DirectoryException>(() => SearchAsync(builder.Session, filter));

        Assert.Equal(87, error.LdapResultCode);
    }

    [Fact]
    public async Task Search_SortsAndLimits()
    {
        var (builder, company) = Create();
        var search = new DirectorySearch
        {
            BaseDn = company.EmployeesOu,
            Filter = "(objectClass=user)",
            Attributes = [Attr.Sn],
            Sort = new SortKey(Attr.Sn, Descending: true),
            SizeLimit = 3,
        };

        var result = new List<string>();
        await foreach (var item in builder.Session.SearchAsync(search, Ct))
        {
            result.Add(item.GetString(Attr.Sn)!);
        }

        Assert.Equal(["Wójcik", "Wiśniewski", "Szymańska"], result);
    }

    [Fact]
    public async Task VirtualList_ReturnsWindowsAndSeeks()
    {
        var (builder, company) = Create();
        var search = new DirectorySearch { BaseDn = company.EmployeesOu, Filter = "(objectClass=user)", Attributes = [Attr.Sn], Sort = new SortKey(Attr.Sn) };

        var first = await builder.Session.GetVirtualListPageAsync(search, new VirtualListRequest { Offset = 0, Count = 3 }, Ct);
        var seek = await builder.Session.GetVirtualListPageAsync(search, new VirtualListRequest { SeekValue = "L", Count = 2 }, Ct);

        Assert.Equal(8, first.TotalCount);
        Assert.Equal(["Dąbrowska", "Kaźmierczak", "Kowalski"], first.Items.Select(i => i.GetString(Attr.Sn)));
        Assert.Equal(["Lewandowski", "Szymańska"], seek.Items.Select(i => i.GetString(Attr.Sn)));
        Assert.Equal(4, seek.Offset);
        await Assert.ThrowsAsync<ArgumentException>(() => builder.Session.GetVirtualListPageAsync(search with { Sort = null }, new VirtualListRequest(), Ct));
    }

    [Fact]
    public async Task Modify_FollowsLdapAddDeleteSemantics()
    {
        var (builder, company) = Create();
        var session = builder.Session;
        var dn = company.JanKowalski;
        await session.ModifyAsync(dn, [AttributeModification.Add(Attr.OtherTelephone, "111", "222")], null, Ct);

        var exists = await Assert.ThrowsAsync<DirectoryException>(() => session.ModifyAsync(dn, [AttributeModification.Add(Attr.OtherTelephone, "111")], null, Ct));
        var missing = await Assert.ThrowsAsync<DirectoryException>(() => session.ModifyAsync(dn, [AttributeModification.Delete(Attr.OtherTelephone, "999")], null, Ct));
        await session.ModifyAsync(dn, [AttributeModification.Add(Attr.OtherTelephone, "111"), AttributeModification.Delete(Attr.OtherTelephone, "999")], ModifyOptions.Idempotent, Ct);
        var clearMissing = await Assert.ThrowsAsync<DirectoryException>(() => session.ModifyAsync(dn, [AttributeModification.Delete(Attr.Info)], null, Ct));

        Assert.Equal(DirectoryErrorKind.AttributeOrValueExists, exists.Kind);
        Assert.Equal(DirectoryErrorKind.NoSuchAttribute, missing.Kind);
        Assert.Equal(DirectoryErrorKind.NoSuchAttribute, clearMissing.Kind);
        Assert.Equal(["111", "222"], session.GetValues(dn, Attr.OtherTelephone));
    }

    [Fact]
    public async Task Modify_IsAtomicAndValidatesSchema()
    {
        var (builder, company) = Create();
        var session = builder.Session;
        var dn = company.JanKowalski;

        async Task<DirectoryErrorKind> Fails(params AttributeModification[] modifications) =>
            (await Assert.ThrowsAsync<DirectoryException>(() => session.ModifyAsync(dn, modifications, null, Ct))).Kind;

        Assert.Equal(DirectoryErrorKind.NoSuchAttribute, await Fails(AttributeModification.Replace(Attr.Department, "Nowy"), AttributeModification.Delete(Attr.Title, "nie ma")));
        Assert.Equal("IT", session.GetValues(dn, Attr.Department).Single());
        Assert.Equal(DirectoryErrorKind.ConstraintViolation, await Fails(AttributeModification.Replace(Attr.Department, new string('x', 65))));
        Assert.Equal(DirectoryErrorKind.ConstraintViolation, await Fails(AttributeModification.Add(Attr.Mail, "drugi@contoso.test")));
        Assert.Equal(DirectoryErrorKind.UnwillingToPerform, await Fails(AttributeModification.Replace(Attr.MemberOf, company.GroupSales)));
        Assert.Equal(DirectoryErrorKind.UnwillingToPerform, await Fails(AttributeModification.Replace(Attr.WhenCreated, "20260101000000.0Z")));
        Assert.Equal(DirectoryErrorKind.InvalidSyntax, await Fails(AttributeModification.Replace(Attr.CountryCode, "abc")));
        Assert.Equal(DirectoryErrorKind.ObjectClassViolation, await Fails(AttributeModification.Replace(Attr.Location, "Warszawa")));
        Assert.Equal(DirectoryErrorKind.ObjectClassViolation, await Fails(AttributeModification.Clear(Attr.SamAccountName)));
        Assert.Equal(DirectoryErrorKind.NoSuchObject, await Fails(AttributeModification.Replace(Attr.Manager, "CN=Nikt," + company.ItOu)));
        Assert.Equal(DirectoryErrorKind.Unknown, await Fails(AttributeModification.Replace("noSuchAttribute", "x")));
        Assert.Equal(67, (await Assert.ThrowsAsync<DirectoryException>(() => session.ModifyAsync(dn, [AttributeModification.Replace(Attr.Cn, "X")], null, Ct))).LdapResultCode);
    }

    [Fact]
    public async Task Modify_EnforcesSpecialPasswordAndLockoutSemantics()
    {
        var time = new ManualTimeProvider();
        var builder = new FakeDirectoryBuilder(timeProvider: time);
        var company = builder.AddSampleCompany();
        var session = builder.Session;

        await session.ModifyAsync(company.JanKowalski, [AttributeModification.Replace(Attr.PwdLastSet, "-1")], null, Ct);
        await session.ModifyAsync(company.ZanetaSzymanska, [AttributeModification.Replace(Attr.LockoutTime, "0")], null, Ct);
        var bad = await Assert.ThrowsAsync<DirectoryException>(() =>
            session.ModifyAsync(company.JanKowalski, [AttributeModification.Replace(Attr.PwdLastSet, "12345")], null, Ct));

        Assert.Equal(AdConvert.ToFileTime(time.GetUtcNow()).ToString(System.Globalization.CultureInfo.InvariantCulture), session.GetValues(company.JanKowalski, Attr.PwdLastSet).Single());
        Assert.Equal("0", session.GetValues(company.ZanetaSzymanska, Attr.LockoutTime).Single());
        Assert.Equal(DirectoryErrorKind.UnwillingToPerform, bad.Kind);
    }

    [Fact]
    public async Task Membership_MaintainsBackLinksAndPrimaryGroupRule()
    {
        var (builder, company) = Create();
        var session = builder.Session;

        await session.ModifyAsync(company.GroupSales, [AttributeModification.Add(Attr.Member, company.JanKowalski.ToLowerInvariant())], ModifyOptions.Idempotent, Ct);
        var memberOf = session.GetValues(company.JanKowalski, Attr.MemberOf);
        await session.ModifyAsync(company.GroupIt, [AttributeModification.Delete(Attr.Member, company.JanKowalski)], null, Ct);
        var primary = await Assert.ThrowsAsync<DirectoryException>(() =>
            session.ModifyAsync(builder.DomainUsersDn, [AttributeModification.Add(Attr.Member, company.JanKowalski)], ModifyOptions.Idempotent, Ct));

        Assert.Equal([company.GroupIt, company.GroupSales], memberOf);
        Assert.Equal([company.GroupSales], session.GetValues(company.JanKowalski, Attr.MemberOf));
        Assert.Equal(DirectoryErrorKind.AlreadyExists, primary.Kind);
        Assert.Equal([company.LukaszWisniewski], session.GetValues(company.JanKowalski, Attr.DirectReports));
    }

    [Fact]
    public async Task Move_RekeysDescendantsAndUpdatesReferences()
    {
        var (builder, company) = Create();
        var session = builder.Session;
        var archive = builder.AddOu("Archiwum", company.CompanyOu);

        var newDn = await session.MoveAsync(company.ItOu, archive, "OU=Informatyka", Ct);
        var jan = "CN=Jan Kowalski," + newDn;

        Assert.Equal("OU=Informatyka,OU=Archiwum,OU=Firma,DC=contoso,DC=test", newDn);
        Assert.True(session.Exists(jan));
        Assert.False(session.Exists(company.JanKowalski));
        Assert.Contains(jan, session.GetValues(company.GroupIt, Attr.Member));
        Assert.Equal(jan, session.GetValues("CN=Łukasz Wiśniewski," + newDn, Attr.Manager).Single());
        Assert.Equal("Informatyka", session.GetValues(newDn, Attr.Ou).Single());
        Assert.Equal("Informatyka", session.GetValues(newDn, Attr.Name).Single());
    }

    [Fact]
    public async Task Move_RejectsInvalidTargets()
    {
        var (builder, company) = Create();
        var session = builder.Session;

        var collision = await Assert.ThrowsAsync<DirectoryException>(() => session.MoveAsync(company.JanKowalski, null, "CN=Łukasz Wiśniewski", Ct));
        var below = await Assert.ThrowsAsync<DirectoryException>(() => session.MoveAsync(company.EmployeesOu, company.ItOu, null, Ct));
        var missing = await Assert.ThrowsAsync<DirectoryException>(() => session.MoveAsync(company.JanKowalski, "OU=Brak," + builder.DomainDn, null, Ct));
        var caseOnly = await session.MoveAsync(company.JanKowalski, null, "CN=jan kowalski", Ct);

        Assert.Equal(DirectoryErrorKind.AlreadyExists, collision.Kind);
        Assert.Equal(DirectoryErrorKind.UnwillingToPerform, below.Kind);
        Assert.Equal(DirectoryErrorKind.NoSuchObject, missing.Kind);
        Assert.Equal("CN=jan kowalski," + company.ItOu, caseOnly);
    }

    [Fact]
    public async Task Delete_RequiresSubtreeAndSupportsRecycleBinRestore()
    {
        var (builder, company) = Create();
        var session = builder.Session;

        var nonLeaf = await Assert.ThrowsAsync<DirectoryException>(() => session.DeleteAsync(company.ItOu, subtree: false, Ct));
        await session.DeleteAsync(company.JanKowalski, subtree: false, Ct);
        var deleted = await SearchAsync(session, "(&(isDeleted=TRUE)(msDS-LastKnownRDN=Jan Kowalski))", builder.DomainDn, SearchScope.Subtree, Attr.LastKnownParent);

        Assert.Equal(DirectoryErrorKind.NotAllowedOnNonLeaf, nonLeaf.Kind);
        Assert.False(session.Exists(company.JanKowalski));
        Assert.DoesNotContain(company.JanKowalski, session.GetValues(company.GroupIt, Attr.Member));
        Assert.Empty(deleted);

        var search = new DirectorySearch
        {
            BaseDn = builder.DomainDn,
            Filter = "(&(isDeleted=TRUE)(msDS-LastKnownRDN=Jan Kowalski))",
            Attributes = [Attr.LastKnownParent],
            IncludeDeleted = true,
        };
        var tombstones = new List<DirectoryObject>();
        await foreach (var item in session.SearchAsync(search, Ct))
        {
            tombstones.Add(item);
        }

        var tombstone = Assert.Single(tombstones);
        Assert.Equal(company.ItOu, tombstone.GetString(Attr.LastKnownParent));
        await session.RestoreDeletedAsync(tombstone.DistinguishedName, company.JanKowalski, Ct);
        Assert.True(session.Exists(company.JanKowalski));
        Assert.Contains(company.JanKowalski, session.GetValues(company.GroupIt, Attr.Member));
    }

    [Fact]
    public async Task Delete_SubtreeRemovesEverythingBelow()
    {
        var (builder, company) = Create();
        var session = builder.Session;

        await session.DeleteAsync(company.EmployeesOu, subtree: true, Ct);

        Assert.False(session.Exists(company.ItOu));
        Assert.False(session.Exists(company.PawelLewandowski));
        Assert.Empty(session.GetValues(company.GroupSales, Attr.Member));
    }

    [Fact]
    public async Task Add_AppliesDomainControllerDefaults()
    {
        var (builder, company) = Create();
        var session = builder.Session;
        var dn = "CN=Anna Nowak," + company.SalesOu;

        await session.AddAsync(dn, new Dictionary<string, IReadOnlyList<object>>
        {
            [Attr.ObjectClass] = ["user"],
            [Attr.SamAccountName] = ["anna.nowak"],
            [Attr.GivenName] = ["Anna"],
        }, Ct);
        var created = await session.GetAsync(dn, [Attr.ObjectClass, Attr.ObjectSid, Attr.UserAccountControl, Attr.SamAccountType, Attr.Cn, Attr.ObjectCategory], Ct);
        var duplicate = await Assert.ThrowsAsync<DirectoryException>(() =>
            session.AddAsync(dn, new Dictionary<string, IReadOnlyList<object>> { [Attr.ObjectClass] = ["user"] }, Ct));
        var orphan = await Assert.ThrowsAsync<DirectoryException>(() =>
            session.AddAsync("CN=X,OU=Brak," + builder.DomainDn, new Dictionary<string, IReadOnlyList<object>> { [Attr.ObjectClass] = ["user"] }, Ct));

        Assert.Equal(["top", "person", "organizationalPerson", "user"], created!.GetStrings(Attr.ObjectClass));
        Assert.StartsWith(FakeDirectorySession.DefaultDomainSid + "-", created.ObjectSid);
        Assert.Equal(546, created.GetInt32(Attr.UserAccountControl));
        Assert.Equal(SamAccountType.UserObject, created.GetInt32(Attr.SamAccountType));
        Assert.Equal("Anna Nowak", created.GetString(Attr.Cn));
        Assert.StartsWith("CN=Person,CN=Schema", created.GetString(Attr.ObjectCategory));
        Assert.Equal(DirectoryErrorKind.AlreadyExists, duplicate.Kind);
        Assert.Equal(DirectoryErrorKind.NoSuchObject, orphan.Kind);
    }

    [Fact]
    public async Task ResetPassword_RecordsPasswordAndEnforcesRules()
    {
        var (builder, company) = Create();
        var session = builder.Session;
        session.PasswordPolicy = p => p.Length >= 12;

        await session.ResetPasswordAsync(company.JanKowalski, "Bardzo$ilneHasło1", Ct);
        var policy = await Assert.ThrowsAsync<DirectoryException>(() => session.ResetPasswordAsync(company.JanKowalski, "krótkie", Ct));
        var group = await Assert.ThrowsAsync<DirectoryException>(() => session.ResetPasswordAsync(company.GroupIt, "Bardzo$ilneHasło1", Ct));
        session.Info = session.Info with { Security = TransportSecurity.None };
        var plain = await Assert.ThrowsAsync<DirectoryException>(() => session.ResetPasswordAsync(company.JanKowalski, "Bardzo$ilneHasło1", Ct));

        var reset = Assert.Single(session.PasswordResets);
        Assert.Equal("Bardzo$ilneHasło1", reset.Password);
        Assert.DoesNotContain("Bardzo", reset.ToString(), StringComparison.Ordinal);
        Assert.Equal(DirectoryErrorKind.PasswordPolicy, policy.Kind);
        Assert.Equal(DirectoryErrorKind.UnwillingToPerform, group.Kind);
        Assert.Equal(DirectoryErrorKind.UnwillingToPerform, plain.Kind);
        Assert.All(session.Operations, o => Assert.DoesNotContain("Bardzo", o.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailNext_InjectsTransientFailuresForOneDn()
    {
        var (builder, company) = Create();
        var session = builder.Session;
        session.FailNext(FakeOperationKind.Modify, company.JanKowalski, DirectoryErrorKind.Busy, times: 2);
        var modification = AttributeModification.Replace(Attr.Title, "Kierownik IT");

        await session.ModifyAsync(company.PawelLewandowski, [modification], null, Ct);
        var first = await Assert.ThrowsAsync<DirectoryException>(() => session.ModifyAsync(company.JanKowalski, [modification], null, Ct));
        await Assert.ThrowsAsync<DirectoryException>(() => session.ModifyAsync(company.JanKowalski, [modification], null, Ct));
        await session.ModifyAsync(company.JanKowalski, [modification], null, Ct);

        Assert.True(first.IsTransient);
        Assert.Equal(51, first.LdapResultCode);
        Assert.Equal(4, session.Writes.Count);
        Assert.Equal(2, session.Writes.Count(w => w.Error == DirectoryErrorKind.Busy));
    }

    [Fact]
    public async Task Permissions_AreConfigurablePerObject()
    {
        var (builder, company) = Create();
        var session = builder.Session;
        var permissions = new EffectivePermissions
        {
            IsKnown = true,
            WritableAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Attr.Department },
        };
        session.SetPermissions(company.JanKowalski, permissions);

        var jan = await session.GetEffectivePermissionsAsync(company.JanKowalski.ToUpperInvariant(), Ct);
        var other = await session.GetEffectivePermissionsAsync(company.PawelLewandowski, Ct);
        var constructed = await session.GetAsync(company.JanKowalski, [Attr.AllowedAttributesEffective], Ct);

        Assert.True(jan.CanWrite("department"));
        Assert.False(jan.CanWrite(Attr.Title));
        Assert.False(other.IsKnown);
        Assert.Equal([Attr.Department], constructed!.GetStrings(Attr.AllowedAttributesEffective));
    }

    [Fact]
    public async Task Watch_NotifiesAboutChildChanges()
    {
        var (builder, company) = Create();
        var session = builder.Session;
        var changed = new List<string>();

        var watch = await session.WatchAsync(company.ItOu, changed.Add, Ct);
        await session.ModifyAsync(company.JanKowalski, [AttributeModification.Replace(Attr.Title, "Kierownik")], null, Ct);
        await session.ModifyAsync(company.PawelLewandowski, [AttributeModification.Replace(Attr.Title, "Prezes")], null, Ct);
        await watch!.DisposeAsync();
        await session.ModifyAsync(company.JanKowalski, [AttributeModification.Replace(Attr.Title, "Dyrektor")], null, Ct);

        Assert.Equal([company.JanKowalski], changed);
    }

    [Fact]
    public async Task Latency_AllowsObservingParallelism()
    {
        var (builder, company) = Create();
        var session = builder.Session;
        session.Latency = TimeSpan.FromMilliseconds(30);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => session.GetAsync(company.JanKowalski, [Attr.Name], Ct)));

        Assert.True(session.MaxObservedConcurrency >= 2);
    }

    [Fact]
    public async Task GetAllValues_ReturnsAllMembers()
    {
        var (builder, company) = Create();
        var values = new List<string>();

        await foreach (var value in builder.Session.GetAllValuesAsync(company.GroupAccounting, Attr.Member, Ct))
        {
            values.Add(value);
        }

        Assert.Equal([company.MalgorzataWojcik, company.GrazynaDabrowska, company.ZbigniewKazmierczak], values);
    }

    [Fact]
    public async Task MaxValueRange_ReturnsRangedDescriptionsLikeAd()
    {
        var (builder, company) = Create();
        var session = builder.Session;
        session.MaxValueRange = 2;

        var group = await session.GetAsync(company.GroupAccounting, [Attr.Member, Attr.SamAccountName], Ct);
        var found = Assert.Single(await SearchAsync(session, "(sAMAccountName=GG_Ksiegowosc)", attributes: Attr.Member));
        var all = new List<string>();
        await foreach (var value in session.GetAllValuesAsync(company.GroupAccounting, Attr.Member, Ct))
        {
            all.Add(value);
        }

        Assert.True(group!.TryGet(Attr.Member, out var members));
        Assert.Equal("member;range=0-1", members.Name);
        Assert.Equal([company.MalgorzataWojcik, company.GrazynaDabrowska], members.Strings);
        Assert.Equal("GG_Ksiegowosc", group.GetString(Attr.SamAccountName));
        Assert.Equal(2, found.GetStrings(Attr.Member).Count);
        Assert.Equal(3, all.Count);
        Assert.Equal(3, session.Snapshot(company.GroupAccounting)!.GetStrings(Attr.Member).Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.MaxValueRange = 0);
    }

    [Fact]
    public async Task DisposedSession_RejectsCalls()
    {
        var (builder, company) = Create();
        await builder.Session.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => builder.Session.GetAsync(company.JanKowalski, [], Ct));
    }
}
