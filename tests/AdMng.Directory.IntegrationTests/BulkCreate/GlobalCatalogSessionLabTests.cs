using System.Net;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Directory.IntegrationTests.Infrastructure;
using AdMng.Testing;

namespace AdMng.Directory.IntegrationTests.BulkCreate;

/// <summary>
/// The global-catalog session of the bulk creation wizard against the lab DC (docs/dev/phase3-bulk-create.md 2.2, 2.11):
/// <see cref="LdapDirectoryConnector.ConnectGlobalCatalogAsync"/> opens the GC port of the window's DC with the window's
/// in-memory credential, a forest-wide search uses base <c>""</c> (or the documented fallback, the forest root naming
/// context), and the session is kept as a read-only reader. It proves the port and the base only – the lab forest has one
/// domain, so the <c>Forest</c> uniqueness scope is a pilot item (2.11). Skipped without <c>ADMNG_TEST_LDAP_SERVER</c>.
/// </summary>
public sealed class GlobalCatalogSessionLabTests(LabFixture lab)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConnectGlobalCatalog_OpensTheGcPortOfTheSameDc_WithTheWindowsCredential()
    {
        LabSettings.RequireLab();
        await using var window = await LabFixture.ConnectAsync(LabSettings.Plain(LabSettings.HelpdeskUser), LabSettings.HelpdeskPassword, cancellationToken: Ct);

        await using var gc = await new LdapDirectoryConnector().ConnectGlobalCatalogAsync(window, Ct);

        // Samba 4.19 answers isGlobalCatalogReady: TRUE on its RootDSE (the wizard opens the GC session only then).
        Assert.True(window.Info.IsGlobalCatalogReady);
        Assert.NotSame(window, gc);
        Assert.True(gc.Info.IsGlobalCatalog);
        Assert.False(window.Info.IsGlobalCatalog);
        Assert.Equal(3268, gc.Info.Port);
        Assert.Equal(window.Info.Server, gc.Info.Server, ignoreCase: true);
        Assert.Equal(((LdapDirectorySession)window).Pool.Factory.Host, ((LdapDirectorySession)gc).Pool.Factory.Host, ignoreCase: true);
        Assert.Equal(window.Info.DefaultNamingContext, gc.Info.DefaultNamingContext, ignoreCase: true);
        Assert.Equal(window.Info.BoundIdentity, gc.Info.BoundIdentity, ignoreCase: true);
        Assert.Equal(window.Info.Security, gc.Info.Security);
        Assert.Equal(window.Info.IsChannelEncrypted, gc.Info.IsChannelEncrypted);

        // The window keeps working and the GC session does not depend on it.
        await window.DisposeAsync();
        Assert.NotNull(await gc.GetAsync(LabSettings.DomainDn, [Attr.Name], Ct));
    }

    [Fact]
    public async Task ConnectGlobalCatalog_OverLdaps_UsesPort3269()
    {
        LabSettings.RequireLab();
        await using var window = await LabFixture.ConnectAsync(LabSettings.Tls(LabSettings.AdminUser), LabSettings.AdminPassword, cancellationToken: Ct);

        await using var gc = await new LdapDirectoryConnector().ConnectGlobalCatalogAsync(window, Ct);

        Assert.Equal(3269, gc.Info.Port);
        Assert.Equal(TransportSecurity.Tls, gc.Info.Security);
        Assert.True(gc.Info.IsChannelEncrypted);
    }

    [Fact]
    public async Task ForestWideSearch_FindsASeededUsersUpn()
    {
        LabSettings.RequireLab();
        var user = await lab.FindSeededUserAsync(LabSettings.EmployeesOu, Attr.UserPrincipalName);
        var upn = user.GetString(Attr.UserPrincipalName);
        Assert.False(string.IsNullOrEmpty(upn));
        await using var window = await LabFixture.ConnectAsync(LabSettings.Plain(LabSettings.AdminUser), LabSettings.AdminPassword, cancellationToken: Ct);
        await using var gc = await new LdapDirectoryConnector().ConnectGlobalCatalogAsync(window, Ct);
        var reader = ReadOnlyDirectoryReader.Of(gc);

        var (found, baseUsed) = await FindForestWideAsync(reader, gc.Info, LdapFilter.Equal(Attr.UserPrincipalName, upn!));

        // Pinned: Samba's GC port accepts the empty base (the fallback to the forest root naming context is not needed).
        Assert.Equal(user.DistinguishedName, found, ignoreCase: true);
        Assert.Equal(string.Empty, baseUsed);
    }

    [Fact]
    public async Task ForestWideSearch_ByPrefix_ReadsOnlyTheAttribute()
    {
        LabSettings.RequireLab();
        var user = await lab.FindSeededUserAsync(LabSettings.EmployeesOu, Attr.SamAccountName);
        var sam = user.GetString(Attr.SamAccountName)!;
        await using var window = await LabFixture.ConnectAsync(LabSettings.Plain(LabSettings.AdminUser), LabSettings.AdminPassword, cancellationToken: Ct);
        await using var gc = await new LdapDirectoryConnector().ConnectGlobalCatalogAsync(window, Ct);

        var values = new List<string>();
        await foreach (var entry in ReadOnlyDirectoryReader.Of(gc).SearchAsync(
            new DirectorySearch { BaseDn = string.Empty, Scope = SearchScope.Subtree, Filter = LdapFilter.StartsWith(Attr.SamAccountName, sam[..2]), Attributes = [Attr.SamAccountName], PageSize = 1000 },
            Ct))
        {
            values.Add(entry.GetString(Attr.SamAccountName) ?? string.Empty);
        }

        Assert.Contains(sam, values, StringComparer.OrdinalIgnoreCase);
        Assert.All(values, v => Assert.StartsWith(sam[..2], v, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TheGcSession_IsKeptAsAReadOnlyReader()
    {
        LabSettings.RequireLab();
        await using var window = await LabFixture.ConnectAsync(LabSettings.Plain(LabSettings.AdminUser), LabSettings.AdminPassword, cancellationToken: Ct);
        await using var gc = await new LdapDirectoryConnector().ConnectGlobalCatalogAsync(window, Ct);

        var reader = ReadOnlyDirectoryReader.Of(gc);

        Assert.IsNotAssignableFrom<IDirectorySession>(reader);
        Assert.NotNull(await reader.GetAsync(LabSettings.DomainDn, [Attr.Name], Ct));
        reader.Detach();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.GetAsync(LabSettings.DomainDn, [Attr.Name], Ct));
    }

    [Fact]
    public async Task ConnectGlobalCatalog_FromAClosedSession_IsRefused()
    {
        LabSettings.RequireLab();
        var window = await LabFixture.ConnectAsync(LabSettings.Plain(LabSettings.AdminUser), LabSettings.AdminPassword, cancellationToken: Ct);
        await window.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => new LdapDirectoryConnector().ConnectGlobalCatalogAsync(window, Ct));
    }

    [Fact]
    public async Task ConnectGlobalCatalog_OfAnotherConnectorsSession_IsNotSupported()
    {
        await using var fake = new FakeDirectoryBuilder().Build();

        await Assert.ThrowsAsync<NotSupportedException>(() => new LdapDirectoryConnector().ConnectGlobalCatalogAsync(fake, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => new LdapDirectoryConnector().ConnectGlobalCatalogAsync(null!, Ct));
    }

    /// <summary>
    /// The forest-wide search of 2.2: base <c>""</c> (the whole global catalog); a server that refuses the empty base, or
    /// returns nothing from it, gets the forest root naming context (subtree). Returns the DN found and the base used.
    /// </summary>
    private static async Task<(string? Dn, string Base)> FindForestWideAsync(IDirectoryReader reader, SessionInfo info, string filter)
    {
        foreach (var baseDn in new[] { string.Empty, info.RootDomainNamingContext ?? info.DefaultNamingContext })
        {
            try
            {
                await foreach (var entry in reader.SearchAsync(
                    new DirectorySearch { BaseDn = baseDn, Scope = SearchScope.Subtree, Filter = filter, Attributes = [Attr.UserPrincipalName], SizeLimit = 2 },
                    Ct))
                {
                    return (entry.DistinguishedName, baseDn);
                }
            }
            catch (DirectoryException ex) when (ex.Kind is not (DirectoryErrorKind.ServerUnavailable or DirectoryErrorKind.Cancelled))
            {
                TestContext.Current.SendDiagnosticMessage($"Base '{baseDn}' refused: {ex.Kind}.");
            }
        }

        return (null, "(none)");
    }
}

/// <summary>The settings of the global-catalog session (no lab needed).</summary>
public sealed class GlobalCatalogSettingsTests
{
    private static readonly NetworkCredential Password = new(string.Empty, "Tajne!Hasło1");

    private static ConnectionSettings Settings(TransportSecurity security, int? port = null) => ConnectionSettings.Create(
        new ConnectionOptions
        {
            Domain = "contoso.test",
            Server = "dc01.contoso.test",
            Port = port,
            Authentication = AuthenticationMode.SimpleBind,
            Security = security,
            AllowInsecureSimpleBind = security == TransportSecurity.None,
            UserName = "łukasz.wiśniewski@contoso.test",
            MaxConnections = 3,
        },
        Password,
        isWindows: false);

    [Theory]
    [InlineData(TransportSecurity.None, null, 3268)]
    [InlineData(TransportSecurity.StartTls, null, 3268)]
    [InlineData(TransportSecurity.Tls, null, 3269)]
    [InlineData(TransportSecurity.Tls, 1636, 3269)]
    [InlineData(TransportSecurity.None, 3389, 3268)]
    public void ForGlobalCatalog_TheGcPort_OfTheSameServer(TransportSecurity security, int? port, int expected)
    {
        var source = Settings(security, port);

        var gc = source.ForServer("dc01.contoso.test").ForGlobalCatalog();

        Assert.Equal(expected, gc.Port);
        Assert.Equal(source.Options with { GlobalCatalog = true, Port = null }, gc.Options);
        Assert.Equal(source.AuthType, gc.AuthType);
        Assert.Equal(source.Account!.ToString(), gc.Account!.ToString());
        Assert.NotSame(source.Credential, gc.Credential);
        Assert.Equal(source.Credential!.UserName, gc.Credential!.UserName);
        Assert.Equal("Tajne!Hasło1", gc.Credential.Password);
        Assert.DoesNotContain("Tajne", gc.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ForGlobalCatalog_SurvivesTheWindowsSession()
    {
        var source = Settings(TransportSecurity.Tls);
        var gc = source.ForServer("dc01.contoso.test").ForGlobalCatalog();

        source.ClearCredential();

        Assert.Equal("Tajne!Hasło1", gc.Credential!.Password);
    }
}
