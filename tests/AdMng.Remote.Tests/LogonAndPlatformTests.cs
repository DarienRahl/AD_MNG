using System.Net;
using System.Security;
using AdMng.Core.Computers;
using AdMng.Core.Computers.Remote;
using AdMng.Core.Directory;
using AdMng.Directory;
using AdMng.Testing;

namespace AdMng.Remote.Tests;

public class LogonAndPlatformTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RemoteTarget Target => new("CN=KRK-PC101,OU=Komputery,OU=Firma,DC=contoso,DC=test", "KRK-PC101", "krk-pc101.contoso.test", ComputerType.Workstation);

    private static SecureString Secret(string text)
    {
        var secret = new SecureString();
        foreach (var c in text)
        {
            secret.AppendChar(c);
        }

        return secret;
    }

    [Theory]
    [InlineData(@"CONTOSO\adm.kowalski", "contoso.test", "adm.kowalski", "CONTOSO")]
    [InlineData("adm.kowalski@contoso.test", "contoso.test", "adm.kowalski@contoso.test", null)]
    [InlineData("adm.kowalski", "contoso.test", "adm.kowalski", "contoso.test")]
    [InlineData("adm.kowalski", null, "adm.kowalski", null)]
    public void LogonParts_SplitTheTypedName(string typed, string? dnsDomain, string user, string? domain) =>
        Assert.Equal((user, domain), SessionLogons.LogonParts(typed, dnsDomain));

    [Fact]
    public void LogonParts_OfADistinguishedName_IsNone()
    {
        Assert.Null(SessionLogons.LogonParts("CN=Jan Kowalski,OU=IT,DC=contoso,DC=test", "contoso.test"));
        using var password = Secret("P@ssw0rd!2026");
        var ex = Assert.Throws<RemoteActionException>(() => SessionLogon.ForCredential("CN=Jan Kowalski,OU=IT,DC=contoso,DC=test", null, password));
        Assert.Equal(RemoteErrorKind.NotSupported, ex.Kind);
    }

    [Fact]
    public void ForCredential_CopiesThePassword_ReadOnly_NeverShown()
    {
        using var password = Secret("Zażółć-gęślą-2026!");
        using var logon = SessionLogon.ForCredential(@"CONTOSO\adm.kowalski", "contoso.test", password);

        Assert.False(logon.IsWindowsAccount);
        Assert.Equal(@"CONTOSO\adm.kowalski", logon.ToString());
        Assert.NotSame(password, logon.Password);
        Assert.True(logon.Password!.IsReadOnly());
        Assert.Equal("Zażółć-gęślą-2026!", new NetworkCredential(string.Empty, logon.Password).Password);
        Assert.DoesNotContain("Zażółć", logon.ToString(), StringComparison.Ordinal);
        logon.Dispose();
        Assert.Throws<ObjectDisposedException>(() => logon.Password.Length);
    }

    [Fact]
    public void Of_AWindowsAccountSession_IsTheWindowsAccount_AnotherFakeIsRefused()
    {
        var session = new FakeDirectoryBuilder().Session;
        using (var logon = SessionLogons.Of(session))
        {
            Assert.True(logon.IsWindowsAccount);
            Assert.Equal("(Windows account)", logon.ToString());
        }

        session.Info = session.Info with { Authentication = AuthenticationMode.Credentials };
        Assert.Equal(RemoteErrorKind.NotSupported, Assert.Throws<RemoteActionException>(() => SessionLogons.Of(session)).Kind);
    }

    [Fact]
    public async Task Cim_IsNotSupportedOffWindows()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Checks the guard of the other platforms.");
        var session = new FakeDirectoryBuilder().Session;

        var ex = await Assert.ThrowsAsync<RemoteActionException>(() => new CimRemoteComputerClient().ConnectAsync(Target, session, new RemoteOptions(), Ct));

        Assert.Equal(RemoteErrorKind.NotSupported, ex.Kind);
    }

    [Fact]
    public async Task Launcher_RemoteDesktopNeverUsesTheCredential_AndIsNotSupportedOffWindows()
    {
        var launcher = new WindowsLocalToolLauncher();
        var session = new FakeDirectoryBuilder().Session;
        Assert.False(launcher.UsesWindowCredential(LocalTool.ComputerManagement, session));
        session.Info = session.Info with { Authentication = AuthenticationMode.Credentials };
        Assert.True(launcher.UsesWindowCredential(LocalTool.ComputerManagement, session));
        Assert.True(launcher.UsesWindowCredential(LocalTool.EventViewer, session));
        Assert.False(launcher.UsesWindowCredential(LocalTool.RemoteDesktop, session));

        // An address is refused on every platform (Kerberos needs the name).
        var address = await Assert.ThrowsAsync<RemoteActionException>(() => launcher.LaunchAsync(LocalTool.RemoteDesktop, "10.20.5.17", session, Ct));
        Assert.Equal(RemoteErrorKind.NotSupported, address.Kind);
        if (!OperatingSystem.IsWindows())
        {
            var ex = await Assert.ThrowsAsync<RemoteActionException>(() => launcher.LaunchAsync(LocalTool.EventViewer, "krk-pc101.contoso.test", session, Ct));
            Assert.Equal(RemoteErrorKind.NotSupported, ex.Kind);
        }
    }

    [Fact]
    public async Task Resolver_LocalhostResolves_InvalidNamesGiveNothing()
    {
        var resolver = new DnsHostAddressResolver();

        var addresses = await resolver.ResolveAsync("localhost", Ct);

        Assert.Contains(addresses, IPAddress.IsLoopback);
        Assert.Empty(await resolver.ResolveAsync("10.20.5.17", Ct));
        Assert.Empty(await resolver.ResolveAsync("not a host", Ct));
    }

    [Fact]
    public async Task Ping_RefusesAnAddress()
    {
        var ex = await Assert.ThrowsAsync<RemoteActionException>(() => new IcmpPingService().PingAsync("10.20.5.17", 4, TimeSpan.FromSeconds(2), Ct));

        Assert.Equal(RemoteErrorKind.NotSupported, ex.Kind);
    }
}
