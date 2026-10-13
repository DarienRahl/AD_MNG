using System.Diagnostics;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Directory.IntegrationTests.Infrastructure;

namespace AdMng.Directory.IntegrationTests.Integration;

public sealed class ConnectTests(LabFixture lab)
{
    [Fact]
    public void SessionInfo_DescribesTheDomainController()
    {
        LabSettings.RequireLab();
        var info = lab.Admin.Info;

        Assert.Equal(LabSettings.DomainDn, info.DefaultNamingContext, ignoreCase: true);
        Assert.Equal($"CN=Configuration,{LabSettings.DomainDn}", info.ConfigurationNamingContext, ignoreCase: true);
        Assert.Equal($"CN=Schema,CN=Configuration,{LabSettings.DomainDn}", info.SchemaNamingContext, ignoreCase: true);
        Assert.Equal(LabSettings.DomainDn, info.RootDomainNamingContext, ignoreCase: true);
        Assert.Equal(LabSettings.DomainDns, info.DomainDnsName, ignoreCase: true);
        Assert.Equal(LabSettings.NetBiosName, info.DomainNetBiosName, ignoreCase: true);
        Assert.False(string.IsNullOrWhiteSpace(info.Server));
        Assert.Equal(389, info.Port);
        Assert.Equal(AuthenticationMode.SimpleBind, info.Authentication);
        Assert.Equal(TransportSecurity.None, info.Security);
        Assert.False(info.IsGlobalCatalog);
        Assert.True(info.DomainFunctionality >= 2);
        Assert.True(info.ForestFunctionality >= 2);
        Assert.True(info.ConnectedAt > DateTimeOffset.UtcNow.AddMinutes(-30));
        Assert.True(info.Supports(LdapControls.PagedResults));
        Assert.True(info.Supports(LdapControls.ServerSort));
        Assert.True(info.Supports(LdapControls.VirtualListView));
        Assert.True(info.Supports(LdapControls.TreeDelete));
        Assert.False(info.Supports("1.2.3.4.5.6.7.8.9"));
    }

    [Fact]
    public void BoundIdentity_IsResolvedWithWhoAmI()
    {
        LabSettings.RequireLab();
        var info = lab.Admin.Info;

        Assert.Equal($"{LabSettings.NetBiosName}\\Administrator", info.BoundIdentity, ignoreCase: true);
        Assert.NotNull(info.BoundUserDn);
        Assert.StartsWith("CN=Administrator,", info.BoundUserDn, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(LabSettings.DomainDn, info.BoundUserDn, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OptionalFeatures_AreDetected()
    {
        LabSettings.RequireLab();
        var info = lab.Admin.Info;

        // The Samba lab has neither the Recycle Bin enabled nor Exchange / LAPS schema extensions.
        Assert.False(info.RecycleBinEnabled);
        Assert.Equal(lab.Admin.Schema.FindAttribute(Attr.HideFromAddressLists) is not null, info.HasExchangeSchema);
        Assert.Equal(
            lab.Admin.Schema.FindAttribute("ms-Mcs-AdmPwd") is not null || lab.Admin.Schema.FindAttribute("msLAPS-Password") is not null,
            info.HasLapsSchema);
    }

    [Fact]
    public async Task Connect_OverLdaps_WithUntrustedLabCertificate()
    {
        LabSettings.RequireLab();
        await using var session = await LabFixture.ConnectAsync(
            LabSettings.Tls(LabSettings.AdminUser),
            LabSettings.AdminPassword,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(636, session.Info.Port);
        Assert.Equal(TransportSecurity.Tls, session.Info.Security);
        Assert.True(((LdapDirectorySession)session).IsChannelEncrypted);
        Assert.NotNull(await session.GetAsync(LabSettings.DomainDn, [Attr.Name], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Connect_OverLdaps_WithoutTrustingTheCertificate_Fails()
    {
        LabSettings.RequireLab();
        var options = LabSettings.Tls(LabSettings.AdminUser) with { AllowUntrustedCertificate = false };

        var error = await Assert.ThrowsAsync<DirectoryException>(() => LabFixture.ConnectAsync(
            options,
            LabSettings.AdminPassword,
            cancellationToken: TestContext.Current.CancellationToken));

        // OpenLDAP and WinLDAP report a failed TLS handshake as "server down".
        Assert.Equal(DirectoryErrorKind.ServerUnavailable, error.Kind);
    }

    [Fact]
    public async Task Connect_WithStartTls()
    {
        LabSettings.RequireLab();
        var options = LabSettings.Tls(LabSettings.AdminUser) with { Security = TransportSecurity.StartTls };
        await using var session = await LabFixture.ConnectAsync(
            options,
            LabSettings.AdminPassword,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(389, session.Info.Port);
        Assert.True(((LdapDirectorySession)session).IsChannelEncrypted);
    }

    [Theory]
    [InlineData("CONTOSO\\Administrator")]
    [InlineData("Administrator")]
    public async Task Connect_AcceptsDownLevelAndPlainUserNames(string userName)
    {
        LabSettings.RequireLab();
        await using var session = await LabFixture.ConnectAsync(
            LabSettings.Plain(userName),
            LabSettings.AdminPassword,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal($"{LabSettings.NetBiosName}\\Administrator", session.Info.BoundIdentity, ignoreCase: true);
    }

    [Fact]
    public async Task Connect_WithWrongPassword_IsInvalidCredentials()
    {
        LabSettings.RequireLab();
        var error = await Assert.ThrowsAsync<DirectoryException>(() => LabFixture.ConnectAsync(
            LabSettings.Plain(LabSettings.ReaderUser),
            "definitely-wrong",
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(DirectoryErrorKind.InvalidCredentials, error.Kind);
        Assert.Equal(49, error.LdapResultCode);
        Assert.Equal(0x52E, error.Win32Error);
        Assert.DoesNotContain("definitely-wrong", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_ToClosedPort_IsServerUnavailable()
    {
        LabSettings.RequireLab();
        var options = LabSettings.Plain(LabSettings.AdminUser) with { Port = 1 };
        var stopwatch = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<DirectoryException>(() => LabFixture.ConnectAsync(
            options,
            LabSettings.AdminPassword,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(DirectoryErrorKind.ServerUnavailable, error.Kind);
        Assert.True(error.IsTransient);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Connect_HonoursCancellation()
    {
        LabSettings.RequireLab();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LabFixture.ConnectAsync(
            LabSettings.Plain(LabSettings.AdminUser),
            LabSettings.AdminPassword,
            cancellationToken: cts.Token));
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task SocketShutdown_BreaksExactlyThatConnection()
    {
        LabSettings.RequireLab();
        Assert.SkipWhen(OperatingSystem.IsWindows(), "libldap interop is only used on Linux and macOS.");
        var ct = TestContext.Current.CancellationToken;
        var settings = ConnectionSettings.Create(
            LabSettings.Plain(LabSettings.AdminUser),
            LabSettings.Password(LabSettings.AdminPassword),
            isWindows: false);
        var factory = new LdapConnectionFactory(settings, LabSettings.Server!, LabSettings.DomainDns, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        using var target = await factory.OpenAsync(probeReachability: false, ct);
        using var other = await factory.OpenAsync(probeReachability: false, ct);
        var rootDse = new System.DirectoryServices.Protocols.SearchRequest(
            string.Empty,
            "(objectClass=*)",
            System.DirectoryServices.Protocols.SearchScope.Base,
            "1.1");

        // Guards the reflection on S.DS.P's handle field and the libldap / libc imports.
        Assert.True(OpenLdapTls.TryShutdownSocket(target));

        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            target.SendAsync<System.DirectoryServices.Protocols.SearchResponse>(rootDse, TimeSpan.FromSeconds(5), ct));
        Assert.True(LdapErrors.IsConnectionBroken(error), error.ToString());
        Assert.NotNull(await other.SendAsync<System.DirectoryServices.Protocols.SearchResponse>(rootDse, TimeSpan.FromSeconds(5), ct));
    }

    [Fact]
    public async Task DisposedSession_RejectsOperations()
    {
        LabSettings.RequireLab();
        var session = await LabFixture.ConnectAsync(
            LabSettings.Plain(LabSettings.AdminUser),
            LabSettings.AdminPassword,
            cancellationToken: TestContext.Current.CancellationToken);
        await session.DisposeAsync();
        await session.DisposeAsync(); // idempotent

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => session.GetAsync(LabSettings.DomainDn, [], TestContext.Current.CancellationToken));
        Assert.Throws<ObjectDisposedException>(() => session.SearchAsync(
            new DirectorySearch { BaseDn = LabSettings.DomainDn },
            TestContext.Current.CancellationToken));
    }
}
