using System.Net;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Testing;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Service.Auth;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Watchdog.Service.Tests.Auth;

/// <summary>
/// Linux role resolution (stage B design 6.2) through the whole pipeline: the Test scheme stands in for Kestrel's
/// Negotiate handler (an identity "Kerberos" named <c>name@REALM</c>, nothing else), <see cref="KerberosSidClaimsTransformation"/>
/// resolves objectSid and tokenGroups in a fake directory, <see cref="RoleCache"/> keeps them, and the unchanged
/// <see cref="SidRoleHandler"/> decides. The real Kerberos path is <c>KerberosRoleTests</c> on the Samba lab.
/// </summary>
public sealed class KerberosRoleResolutionTests : IAsyncLifetime
{
    private const string Realm = "CONTOSO.TEST";

    private readonly FakeDirectoryBuilder _builder = new();
    private readonly SwitchableSessionProvider _sessions;
    private readonly WatchdogFactory _factory;
    private readonly string _viewer;
    private readonly string _admin;
    private readonly string _outsider;
    private readonly string _denied;

    public KerberosRoleResolutionTests()
    {
        _viewer = _builder.AddUser("Łucja", "Nowak", attributes: TokenGroups(TestRoles.ViewerGroup, TestRoles.OtherGroup));
        _admin = _builder.AddUser("Jan", "Kowalski", attributes: TokenGroups(TestRoles.AdminGroup));
        _outsider = _builder.AddUser("Paweł", "Zieliński", attributes: TokenGroups(TestRoles.OtherGroup));
        _denied = _builder.AddUser("Zofia", "Wójcik", attributes: new Dictionary<string, object?>
        {
            [Attr.ObjectSid] = TestRoles.DeniedUser,
            [Attr.TokenGroups] = new object[] { AdConvert.SidToBytes(TestRoles.AdminGroup) },
        });
        _sessions = new SwitchableSessionProvider(_builder.Build());
        _factory = new WatchdogFactory(
            new Dictionary<string, string?> { ["Watchdog:Kerberos:Realm"] = Realm },
            services =>
            {
                AuthenticationSetup.AddLinuxRoleResolution(services);
                services.AddSingleton<IDirectorySessionProvider>(_sessions);
            });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task ViewerAdminOutsiderAndDenied_AreResolvedFromTheDirectory()
    {
        using var viewer = Kerberos("lucja.nowak@" + Realm);
        var me = await ApiJson.ReadAsync<MeDto>(await viewer.GetAsync(WatchdogRoutes.Me, Ct));
        Assert.Equal([WatchdogRole.Viewer], me.Roles);
        Assert.Equal((await _builder.Build().GetAsync(_viewer, [Attr.ObjectSid], Ct))!.ObjectSid, me.Sid);
        Assert.Equal("lucja.nowak@" + Realm, me.Account);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(WatchdogRoutes.ConfigAudit, Ct)).StatusCode);

        using var admin = Kerberos("Jan.Kowalski@contoso.test");     // realm and name compare case-insensitively
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(WatchdogRoutes.ConfigAudit, Ct)).StatusCode);

        using var outsider = Kerberos("pawel.zielinski@" + Realm);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(WatchdogRoutes.Me, Ct)).StatusCode);

        using var denied = Kerberos("zofia.wojcik@" + Realm);
        Assert.Equal(HttpStatusCode.Forbidden, (await denied.GetAsync(WatchdogRoutes.Me, Ct)).StatusCode);
        Assert.Equal(4, _sessions.Lookups);
    }

    [Fact]
    public async Task SidsOfTheTicketOrClient_AreIgnored_OnlyTheDirectoryCounts()
    {
        using var forged = _factory.Client("pawel.zielinski@" + Realm, TestRoles.AdminGroup);
        forged.DefaultRequestHeaders.Add(TestAuthenticationHandler.PrimaryGroupHeader, TestRoles.AdminGroup);

        Assert.Equal(HttpStatusCode.Forbidden, (await forged.GetAsync(WatchdogRoutes.Me, Ct)).StatusCode);
    }

    [Theory]
    [InlineData("jan.kowalski@EVIL.TEST")]          // foreign realm
    [InlineData("HTTP/jan.kowalski@CONTOSO.TEST")]  // service principal, not a sAMAccountName
    [InlineData("jan*@CONTOSO.TEST")]               // filter metacharacter
    [InlineData("jan.kowalski")]                    // no realm
    [InlineData("nikt.taki@CONTOSO.TEST")]          // no such account
    public async Task UnresolvablePrincipals_GetNoRole(string principal)
    {
        using var client = Kerberos(principal);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(WatchdogRoutes.Me, Ct)).StatusCode);
        Assert.Equal(principal.StartsWith("nikt", StringComparison.Ordinal) ? 1 : 0, _sessions.Lookups);
    }

    [Fact]
    public async Task ForeignRealm_IsLoggedWithTheRealmOnly()
    {
        using var client = Kerberos("jan.kowalski@EVIL.TEST");

        await client.GetAsync(WatchdogRoutes.Me, Ct);

        var entry = Assert.Single(_factory.Log.Entries, e => e.EventId.Id == 2502);
        Assert.Contains("EVIL.TEST", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("jan.kowalski", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NtlmIdentities_AreNotResolved()
    {
        using var client = Kerberos("jan.kowalski@" + Realm);
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.AuthTypeHeader, "NTLM");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(WatchdogRoutes.Me, Ct)).StatusCode);
        Assert.Equal(0, _sessions.Lookups);
    }

    [Fact]
    public async Task Cache_KeepsRolesForRoleCacheSeconds_ThenSeesGroupChanges()
    {
        using var client = Kerberos("pawel.zielinski@" + Realm);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(WatchdogRoutes.Me, Ct)).StatusCode);

        _builder.Build().SetValues(_outsider, Attr.TokenGroups, AdConvert.SidToBytes(TestRoles.ViewerGroup));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(WatchdogRoutes.Me, Ct)).StatusCode);
        Assert.Equal(1, _sessions.Lookups);

        _factory.Clock.Advance(TimeSpan.FromSeconds(301));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(WatchdogRoutes.Me, Ct)).StatusCode);
        Assert.Equal(2, _sessions.Lookups);
    }

    [Fact]
    public async Task NegativeEntries_LiveSixtySeconds()
    {
        using var client = Kerberos("nikt.taki@" + Realm);
        await client.GetAsync(WatchdogRoutes.Me, Ct);
        await client.GetAsync(WatchdogRoutes.Me, Ct);
        Assert.Equal(1, _sessions.Lookups);

        _factory.Clock.Advance(TimeSpan.FromSeconds(61));
        await client.GetAsync(WatchdogRoutes.Me, Ct);

        Assert.Equal(2, _sessions.Lookups);
    }

    [Fact]
    public async Task LdapOutage_UsesStaleEntriesForMaxStaleSeconds_Then503()
    {
        using var admin = Kerberos("jan.kowalski@" + Realm);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(WatchdogRoutes.Status, Ct)).StatusCode);
        _sessions.Available = false;

        _factory.Clock.Advance(TimeSpan.FromSeconds(300 + 3500));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(WatchdogRoutes.Status, Ct)).StatusCode);

        _factory.Clock.Advance(TimeSpan.FromSeconds(101));
        using var response = await admin.GetAsync(WatchdogRoutes.Status, Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(ApiProblem.RoleLookupUnavailable, (await ApiJson.ProblemAsync(response)).GetProperty("type").GetString());
        Assert.Equal(TimeSpan.FromSeconds(WatchdogAuthorizationResultHandler.RetryAfterSeconds), response.Headers.RetryAfter?.Delta);
        Assert.Contains(_factory.Log.Entries, e => e.EventId.Id == 2505);
        Assert.Contains(_factory.Log.Entries, e => e.EventId.Id == 2506);
    }

    [Fact]
    public async Task LdapOutage_WithoutCache_Is503_ButHealthStillAnswers()
    {
        _sessions.Available = false;
        using var viewer = Kerberos("lucja.nowak@" + Realm);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await viewer.GetAsync(WatchdogRoutes.Me, Ct)).StatusCode);
        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, (await viewer.GetAsync(WatchdogRoutes.Health, Ct)).StatusCode);
    }

    [Fact]
    public async Task LdapOutage_StaleEntriesStillHonourDeniedSids()
    {
        using var denied = Kerberos("zofia.wojcik@" + Realm);
        Assert.Equal(HttpStatusCode.Forbidden, (await denied.GetAsync(WatchdogRoutes.Me, Ct)).StatusCode);
        _sessions.Available = false;
        _factory.Clock.Advance(TimeSpan.FromSeconds(600));

        Assert.Equal(HttpStatusCode.Forbidden, (await denied.GetAsync(WatchdogRoutes.Me, Ct)).StatusCode);
        Assert.Contains(_factory.Log.Entries, e => e.EventId.Id == 2505);
    }

    [Fact]
    public async Task TransientLdapErrors_AreReportedToTheProvider()
    {
        _builder.Build().FailNext(AdMng.Testing.FakeOperationKind.Search, null, DirectoryErrorKind.ServerUnavailable);
        using var viewer = Kerberos("lucja.nowak@" + Realm);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await viewer.GetAsync(WatchdogRoutes.Me, Ct)).StatusCode);
        Assert.Equal(1, _sessions.Failures);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(WatchdogRoutes.Me, Ct)).StatusCode);
    }

    [Theory]
    [InlineData("jan.kowalski@CONTOSO.TEST", "contoso.test", true, "jan.kowalski@CONTOSO.TEST")]
    [InlineData("WD01A2B3$@CONTOSO.TEST", "CONTOSO.TEST", true, "wd01a2b3$@CONTOSO.TEST")]
    [InlineData("łucja.nowak@CONTOSO.TEST", "CONTOSO.TEST", true, "łucja.nowak@CONTOSO.TEST")]
    [InlineData("user@sub@CONTOSO.TEST", "CONTOSO.TEST", false, "")]
    [InlineData("abcdefghijklmnopqrstu@CONTOSO.TEST", "CONTOSO.TEST", false, "")]   // 21 characters
    [InlineData("a=b@CONTOSO.TEST", "CONTOSO.TEST", false, "")]
    [InlineData("jan@CONTOSO.TEST", null, false, "")]
    [InlineData("@CONTOSO.TEST", "CONTOSO.TEST", false, "")]
    [InlineData("jan@", "CONTOSO.TEST", false, "")]
    public void PrincipalSyntax(string principal, string? realm, bool valid, string key)
    {
        Assert.Equal(valid, KerberosSidClaimsTransformation.TryParsePrincipal(principal, realm, out _, out var actual, out _));
        Assert.Equal(key, actual);
    }

    [Fact]
    public async Task Transformation_IsIdempotent()
    {
        var transformation = _factory.Services.GetServices<Microsoft.AspNetCore.Authentication.IClaimsTransformation>().OfType<KerberosSidClaimsTransformation>().Single();
        var identity = new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "lucja.nowak@" + Realm)], "Kerberos");

        var once = await transformation.TransformAsync(new System.Security.Claims.ClaimsPrincipal(identity));
        var twice = await transformation.TransformAsync(once);

        Assert.Same(once, twice);
        Assert.Single(twice.Claims, c => c.Type == KerberosSidClaimsTransformation.SidSourceClaim && c.Value == KerberosSidClaimsTransformation.LdapSource);
        Assert.Equal(2, twice.Claims.Count(c => c.Type == System.Security.Claims.ClaimTypes.GroupSid));
        Assert.Equal(1, _sessions.Lookups);
    }

    private HttpClient Kerberos(string principal) => _factory.Client(principal);

    private static Dictionary<string, object?> TokenGroups(params string[] sids) => new()
    {
        [Attr.TokenGroups] = sids.Select(s => (object)AdConvert.SidToBytes(s)).ToArray(),
    };

    /// <summary>The fake directory behind a provider that can be switched off (LDAP outage) and counts lookups.</summary>
    private sealed class SwitchableSessionProvider(IDirectorySession session) : IDirectorySessionProvider
    {
        private int _lookups;
        private int _failures;

        public bool Available { get; set; } = true;

        public int Lookups => Volatile.Read(ref _lookups);

        public int Failures => Volatile.Read(ref _failures);

        public DirectoryConnectionState State => Available ? DirectoryConnectionState.Connected : DirectoryConnectionState.Failed;

        public string? CurrentServer => session.Info.Server;

        public ValueTask<IDirectorySession> GetSessionAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _lookups);
            return Available
                ? ValueTask.FromResult(session)
                : ValueTask.FromException<IDirectorySession>(new DirectoryException(DirectoryErrorKind.ServerUnavailable, "DC down (test)."));
        }

        public void ReportFailure(IDirectorySession session, Exception error) => Interlocked.Increment(ref _failures);
    }
}
