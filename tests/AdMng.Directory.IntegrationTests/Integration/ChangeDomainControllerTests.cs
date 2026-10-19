using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Directory.IntegrationTests.Infrastructure;

namespace AdMng.Directory.IntegrationTests.Integration;

/// <summary>
/// "Change domain controller" against the lab: <see cref="LdapDirectoryConnector.ConnectToServerAsync"/> opens a new
/// session on another name of the lab DC with the options and the in-memory credential of an open session. The lab has
/// one DC, reachable as <see cref="LabSettings.Server"/> and as <c>localhost</c>.
/// </summary>
public sealed class ChangeDomainControllerTests
{
    private const string OtherName = "localhost";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConnectToServer_OpensANewSession_WithTheOptionsAndCredentialOfTheOpenOne()
    {
        LabSettings.RequireLab();
        var source = await LabFixture.ConnectAsync(LabSettings.Plain(LabSettings.HelpdeskUser), LabSettings.HelpdeskPassword, cancellationToken: Ct);
        IDirectorySession? target = null;
        try
        {
            target = await new LdapDirectoryConnector().ConnectToServerAsync(source, OtherName, Ct);

            Assert.NotSame(source, target);
            Assert.Equal(source.Info.DefaultNamingContext, target.Info.DefaultNamingContext, ignoreCase: true);
            Assert.Equal(source.Info.Server, target.Info.Server, ignoreCase: true);
            Assert.Equal(source.Info.Port, target.Info.Port);
            Assert.Equal(source.Info.Authentication, target.Info.Authentication);
            Assert.Equal(source.Info.Security, target.Info.Security);
            Assert.Equal(source.Info.BoundIdentity, target.Info.BoundIdentity, ignoreCase: true);
            Assert.NotEmpty(target.Schema.Attributes);
            Assert.Equal(OtherName, ((LdapDirectorySession)target).Pool.Factory.Host);
            Assert.Equal(OtherName, ((LdapDirectorySession)target).Pool.Factory.Settings.Options.Server);

            // The source session keeps working, and the new one does not depend on it: closing the source clears only its
            // own credential, the new session still opens pooled connections with its copy.
            Assert.NotNull(await source.GetAsync(LabSettings.DomainDn, [Attr.Name], Ct));
            await source.DisposeAsync();
            var reads = Enumerable.Range(0, 6).Select(_ => target.GetAsync(LabSettings.EmployeesOu, [Attr.Name], Ct));
            Assert.All(await Task.WhenAll(reads), Assert.NotNull);
        }
        finally
        {
            await source.DisposeAsync();
            if (target is not null)
            {
                await target.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task ConnectToServer_OverLdaps_KeepsTheEncryptedChannel()
    {
        LabSettings.RequireLab();
        await using var source = await LabFixture.ConnectAsync(LabSettings.Tls(LabSettings.AdminUser), LabSettings.AdminPassword, cancellationToken: Ct);

        await using var target = await new LdapDirectoryConnector().ConnectToServerAsync(source, OtherName, Ct);

        Assert.Equal(636, target.Info.Port);
        Assert.Equal(TransportSecurity.Tls, target.Info.Security);
        Assert.True(((LdapDirectorySession)target).IsChannelEncrypted);
        Assert.NotNull(await target.GetAsync(LabSettings.DomainDn, [Attr.Name], Ct));
    }

    [Fact]
    public async Task ConnectToServer_UnreachableDomainController_LeavesTheOpenSessionWorking()
    {
        LabSettings.RequireLab();
        await using var source = await LabFixture.ConnectAsync(LabSettings.Plain(LabSettings.AdminUser), LabSettings.AdminPassword, cancellationToken: Ct);

        var error = await Assert.ThrowsAsync<DirectoryException>(() =>
            new LdapDirectoryConnector().ConnectToServerAsync(source, "dc-nieistniejący.invalid", Ct));

        Assert.Equal(DirectoryErrorKind.ServerUnavailable, error.Kind);
        Assert.NotNull(await source.GetAsync(LabSettings.DomainDn, [Attr.Name], Ct));
    }

    [Fact]
    public async Task ConnectToServer_FromAClosedSession_IsRefused()
    {
        LabSettings.RequireLab();
        var source = await LabFixture.ConnectAsync(LabSettings.Plain(LabSettings.AdminUser), LabSettings.AdminPassword, cancellationToken: Ct);
        await source.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => new LdapDirectoryConnector().ConnectToServerAsync(source, OtherName, Ct));
    }

    [Fact]
    public async Task ConnectToServer_Cancelled_OpensNothing()
    {
        LabSettings.RequireLab();
        await using var source = await LabFixture.ConnectAsync(LabSettings.Plain(LabSettings.AdminUser), LabSettings.AdminPassword, cancellationToken: Ct);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LdapDirectoryConnector().ConnectToServerAsync(source, OtherName, cancelled.Token));
        Assert.NotNull(await source.GetAsync(LabSettings.DomainDn, [Attr.Name], Ct));
    }
}
