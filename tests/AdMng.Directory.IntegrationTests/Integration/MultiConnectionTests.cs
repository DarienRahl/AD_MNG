using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Comparison;
using AdMng.Core.Directory;
using AdMng.Core.Formatting;
using AdMng.Core.Ldap;
using AdMng.Directory.IntegrationTests.Infrastructure;
using AdMng.Presentation.Comparison;
using AdMng.Presentation.Connections;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;

namespace AdMng.Directory.IntegrationTests.Integration;

/// <summary>
/// Two connection windows with two credentials against the lab, at engine level (design note docs/dev/multi-window.md
/// 10.5): two <see cref="SessionManager"/>s with their <see cref="ConnectionContext"/>s in one
/// <see cref="ConnectionRegistry"/> over the real <see cref="LdapDirectoryConnector"/> - the Administrator and the
/// read-only account <c>odczyt.test</c> - and the comparison of objects between them. The lab has one domain, so this
/// covers two identities, two independent pooled sessions and real read-right differences; another domain is covered by
/// the fakes of the Presentation tests.
/// </summary>
public sealed class MultiConnectionTests(LabFixture lab)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TwoCredentials_TwoIndependentSessions()
    {
        LabSettings.RequireLab();
        await using var windows = await Windows.OpenAsync();
        var a = windows.A.Sessions.Current!;
        var b = windows.B.Sessions.Current!;

        Assert.Equal($"{LabSettings.NetBiosName}\\{Account(LabSettings.AdminUser)}", a.Info.BoundIdentity, ignoreCase: true);
        Assert.Equal($"{LabSettings.NetBiosName}\\{Account(LabSettings.ReaderUser)}", b.Info.BoundIdentity, ignoreCase: true);
        Assert.NotSame(a, b);
        Assert.Equal(2, windows.Registry.Connections.Count);
        Assert.NotEmpty(await SearchNamesAsync(a, lab.TestOu));
        Assert.NotEmpty(await SearchNamesAsync(b, lab.TestOu));

        // Closing A's window leaves B working, with its own credential.
        await windows.A.DisposeAsync();
        Assert.Null(windows.A.Context.Pin());
        Assert.Equal([windows.B.Context.Id], windows.Registry.Connections.Select(c => c.Id));
        Assert.NotEmpty(await SearchNamesAsync(b, lab.TestOu));
        Assert.NotEmpty(await SearchNamesAsync(b, LabSettings.EmployeesOu));
    }

    [Fact]
    public async Task TheSameObject_ThroughTwoIdentities()
    {
        LabSettings.RequireLab();
        var data = await CreateDataAsync();
        await using var windows = await Windows.OpenAsync();
        using var a = windows.A.Context.Pin()!;
        using var b = windows.B.Context.Pin()!;

        var source = (await ComparisonReader.ReadAsync(a.Reader, data.First, Ct))!;
        var match = await ObjectMatcher.MatchAsync(b.Reader, source.Object, ObjectMatchRule.Automatic, null, Ct);
        var target = (await ComparisonReader.ReadAsync(b.Reader, match.Match!.DistinguishedName, Ct))!;
        var rows = ObjectDiffer.Compare(source, target);

        Assert.Equal((MatchStatus.Found, ObjectMatchRule.SamAccountName), (match.Status, match.Rule));
        Assert.True(DistinguishedName.AreEqual(data.First, target.DistinguishedName));
        Assert.Equal(source.ObjectGuid, target.ObjectGuid);
        Assert.NotNull(source.ObjectGuid);

        // Read rights differ, values never do: a difference is an attribute only the Administrator can read.
        Assert.All(rows.Where(r => r.IsDifference && !r.IsVolatile), r => Assert.Equal(AttributeComparisonState.OnlyA, r.State));
        Assert.Equal(AttributeComparisonState.Same, rows.Single(r => r.Name == Attr.Department).State);

        var membership = await MembershipAsync(a, source, b, target);
        Assert.Equal((MembershipKind.Direct, MembershipKind.Direct), Kinds(membership, data.InnerGroupSam));
        Assert.Equal((MembershipKind.Nested, MembershipKind.Nested), Kinds(membership, data.OuterGroupSam));
        Assert.Equal(MembershipComparisonState.InBoth, membership[$"sam:{data.OuterGroupSam.ToUpperInvariant()}"].State);
        Assert.Equal(MembershipComparisonState.InBoth, membership["rid:513"].State);
    }

    [Fact]
    public async Task DifferentObjects_ByExplicitDn()
    {
        LabSettings.RequireLab();
        var data = await CreateDataAsync();
        await using var windows = await Windows.OpenAsync();
        using var a = windows.A.Context.Pin()!;
        using var b = windows.B.Context.Pin()!;

        var source = (await ComparisonReader.ReadAsync(a.Reader, data.First, Ct))!;
        var match = await ObjectMatcher.MatchAsync(b.Reader, source.Object, ObjectMatchRule.Explicit, data.Second, Ct);
        var target = (await ComparisonReader.ReadAsync(b.Reader, match.Match!.DistinguishedName, Ct))!;
        var rows = ObjectDiffer.Compare(source, target);

        Assert.True(DistinguishedName.AreEqual(data.Second, target.DistinguishedName));
        Assert.NotEqual(source.ObjectGuid, target.ObjectGuid);
        Assert.Equal(AttributeComparisonState.Different, rows.Single(r => r.Name == Attr.Department).State);
        Assert.Equal(AttributeComparisonState.OnlyA, rows.Single(r => r.Name == Attr.Description).State);

        var membership = await MembershipAsync(a, source, b, target);
        Assert.Equal(MembershipComparisonState.OnlyA, membership[$"sam:{data.InnerGroupSam.ToUpperInvariant()}"].State);
        var outer = membership[$"sam:{data.OuterGroupSam.ToUpperInvariant()}"];
        Assert.Equal(MembershipComparisonState.InBothDifferently, outer.State);
        Assert.Equal((MembershipKind.Nested, MembershipKind.Direct), (outer.A!.Kind, outer.B!.Kind));

        // An explicit DN outside the target's domain partition is refused without a read.
        var outside = await ObjectMatcher.MatchAsync(b.Reader, source.Object, ObjectMatchRule.Explicit, "CN=Jan Kowalski,DC=fabrikam,DC=test", Ct);
        Assert.Equal(MatchStatus.OutsideNamingContext, outside.Status);
    }

    [Fact]
    public async Task TheComparison_UsesTheSessionsPinnedDc()
    {
        LabSettings.RequireLab();
        await using var windows = await Windows.OpenAsync();
        using var a = windows.A.Context.Pin()!;
        using var b = windows.B.Context.Pin()!;

        // Rule 4: each side reads through its own window's session, never another DC or a new bind.
        Assert.Equal(windows.A.Sessions.Current!.Info.Server, a.Info.Server);
        Assert.Equal(windows.B.Sessions.Current!.Info.Server, b.Info.Server);
        Assert.Equal(windows.A.Sessions.Current.Info.BoundIdentity, a.Info.BoundIdentity);
        Assert.Equal(windows.B.Sessions.Current.Info.BoundIdentity, b.Info.BoundIdentity);
        Assert.False(b.Reader is IDirectorySession);
        Assert.NotNull(await b.Reader.GetAsync(LabSettings.DomainDn, [Attr.Name], Ct));
        Assert.Equal(1, windows.Connector.ConnectsOf(LabSettings.AdminUser));
        Assert.Equal(1, windows.Connector.ConnectsOf(LabSettings.ReaderUser));
    }

    private static string Account(string userPrincipalName) => userPrincipalName.Split('@')[0];

    private static (MembershipKind? A, MembershipKind? B) Kinds(IReadOnlyDictionary<string, MembershipComparison> rows, string sam)
    {
        var row = rows[$"sam:{sam.ToUpperInvariant()}"];
        return (row.A?.Kind, row.B?.Kind);
    }

    private static async Task<IReadOnlyDictionary<string, MembershipComparison>> MembershipAsync(
        ConnectionReadContext a, ObjectSnapshot source, ConnectionReadContext b, ObjectSnapshot target)
    {
        var policyA = await a.Policy;
        var policyB = await b.Policy;
        var directA = await ComparisonReader.ReadDirectMembershipAsync(a.Reader, source, policyA.DomainSid, Ct);
        var directB = await ComparisonReader.ReadDirectMembershipAsync(b.Reader, target, policyB.DomainSid, Ct);
        var nestedA = await ComparisonReader.ReadNestedMembershipAsync(a.Reader, source, directA, Ct);
        var nestedB = await ComparisonReader.ReadNestedMembershipAsync(b.Reader, target, directB, Ct);
        return MembershipComparer.Compare(directA.Entries.Concat(nestedA.Entries), directB.Entries.Concat(nestedB.Entries)).ToDictionary(r => r.Key);
    }

    private static async Task<List<string>> SearchNamesAsync(IDirectorySession session, string baseDn)
    {
        var names = new List<string>();
        var search = new DirectorySearch { BaseDn = baseDn, Scope = SearchScope.OneLevel, Attributes = [Attr.Name], SizeLimit = 5 };
        await foreach (var obj in session.SearchAsync(search, Ct))
        {
            names.Add(obj.Name);
        }

        return names;
    }

    /// <summary>
    /// In a fresh OU of the run: the users "Zażółć Gęślą" (department Księgowość, a description) and "Jaźń Żółtowska"
    /// (department Sprzedaż), the groups GG_Porównanie_Wewn ∈ GG_Porównanie_Zewn, the first user in the inner group and the
    /// second user directly in the outer group. The run OU is removed with everything in it after the run.
    /// </summary>
    private async Task<ComparisonData> CreateDataAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var ou = await lab.CreateOuAsync($"Porównanie {suffix}");
        var first = await lab.CreateUserAsync(ou, $"Zażółć Gęślą {suffix}", new Dictionary<string, string>
        {
            [Attr.Department] = "Księgowość",
            [Attr.Description] = "Pierwszy użytkownik porównania – ąćęłńóśźż",
        });
        var second = await lab.CreateUserAsync(ou, $"Jaźń Żółtowska {suffix}", new Dictionary<string, string> { [Attr.Department] = "Sprzedaż" });
        var innerSam = $"GG_Porownanie_Wewn_{suffix}";
        var outerSam = $"GG_Porownanie_Zewn_{suffix}";
        var inner = await CreateGroupAsync(ou, $"GG_Porównanie_Wewn {suffix}", innerSam);
        var outer = await CreateGroupAsync(ou, $"GG_Porównanie_Zewn {suffix}", outerSam);
        await lab.Admin.ModifyAsync(inner, [AttributeModification.Add(Attr.Member, first)], null, Ct);
        await lab.Admin.ModifyAsync(outer, [AttributeModification.Add(Attr.Member, inner, second)], null, Ct);
        return new ComparisonData(first, second, innerSam, outerSam);
    }

    private async Task<string> CreateGroupAsync(string parentDn, string cn, string sam)
    {
        var dn = DistinguishedName.Parse(parentDn).Child("CN", cn).ToString();
        await lab.Admin.AddAsync(
            dn,
            new Dictionary<string, IReadOnlyList<object>>
            {
                [Attr.ObjectClass] = ["top", ObjectClasses.Group],
                [Attr.SamAccountName] = [sam],
                [Attr.GroupType] = [GroupTypes.Compose(GroupScope.Global, security: true).ToString(CultureInfo.InvariantCulture)],
            },
            Ct);
        return dn;
    }

    private sealed record ComparisonData(string First, string Second, string InnerGroupSam, string OuterGroupSam);

    /// <summary>Two connection windows (session manager + connection context each) in one registry.</summary>
    private sealed class Windows : IAsyncDisposable
    {
        private Windows(CountingConnector connector, ConnectionRegistry registry, Window a, Window b)
        {
            Connector = connector;
            Registry = registry;
            A = a;
            B = b;
        }

        public CountingConnector Connector { get; }

        public ConnectionRegistry Registry { get; }

        public Window A { get; }

        public Window B { get; }

        public static async Task<Windows> OpenAsync()
        {
            var connector = new CountingConnector(new LdapDirectoryConnector());
            var registry = new ConnectionRegistry(new InlineDispatcher());
            var localizer = new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL"));
            var formatters = new DelegateAttributeFormatterFactory(schema => new AttributeFormatter(schema));
            var a = new Window(new SessionManager(connector, formatters, localizer), registry);
            var b = new Window(new SessionManager(connector, formatters, localizer), registry);
            await a.Sessions.ConnectAsync(LabSettings.Tls(LabSettings.AdminUser), LabSettings.Password(LabSettings.AdminPassword), Ct);
            await b.Sessions.ConnectAsync(LabSettings.Tls(LabSettings.ReaderUser), LabSettings.Password(LabSettings.ReaderPassword), Ct);
            return new Windows(connector, registry, a, b);
        }

        public async ValueTask DisposeAsync()
        {
            await A.DisposeAsync();
            await B.DisposeAsync();
        }
    }

    /// <summary>One connection window: its session manager and its connection context (what the DI scope holds).</summary>
    private sealed class Window : IAsyncDisposable
    {
        private bool _disposed;

        public Window(SessionManager sessions, ConnectionRegistry registry)
        {
            Sessions = sessions;
            Context = new ConnectionContext(sessions, registry);
        }

        public SessionManager Sessions { get; }

        public ConnectionContext Context { get; }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Context.Dispose();
            await Sessions.DisposeAsync();
        }
    }

    /// <summary>The real connector, counting the binds (the comparison must never open a session of its own).</summary>
    private sealed class CountingConnector(IDirectoryConnector inner) : IDirectoryConnector
    {
        private readonly List<string> _users = [];

        public int ConnectsOf(string user)
        {
            lock (_users)
            {
                return _users.Count(u => string.Equals(u, user, StringComparison.OrdinalIgnoreCase));
            }
        }

        public string? GetComputerDomain() => inner.GetComputerDomain();

        public Task<IReadOnlyList<DomainControllerInfo>> FindDomainControllersAsync(string domain, CancellationToken cancellationToken) =>
            inner.FindDomainControllersAsync(domain, cancellationToken);

        public Task<IDirectorySession> ConnectAsync(ConnectionOptions options, System.Net.NetworkCredential? credential, CancellationToken cancellationToken)
        {
            lock (_users)
            {
                _users.Add(options.UserName ?? string.Empty);
            }

            return inner.ConnectAsync(options, credential, cancellationToken);
        }

        public Task<IDirectorySession> ConnectToServerAsync(IDirectorySession current, string server, CancellationToken cancellationToken) =>
            inner.ConnectToServerAsync(current, server, cancellationToken);
    }

    /// <summary>Runs dispatched work inline (the engine-level test has no UI thread).</summary>
    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }
}
