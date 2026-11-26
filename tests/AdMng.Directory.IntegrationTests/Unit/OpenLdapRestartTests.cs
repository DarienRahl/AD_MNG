using System.DirectoryServices.Protocols;

namespace AdMng.Directory.IntegrationTests.Unit;

/// <summary>
/// libldap ends a connection when a signal interrupts its wait for an answer, unless <c>LDAP_OPT_RESTART</c> is on - and it
/// is off in a new handle. The lab runs failed now and then with "server unavailable" on the first request of a
/// connection, with the system error 4 (<c>EINTR</c>) and nothing on the server's side. Only Linux and macOS use libldap.
/// </summary>
public sealed class OpenLdapRestartTests
{
    [Fact]
    public void AHandleMadeAfterTheOptionIsSet_RestartsAWaitThatASignalInterrupted()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // The option is global and stays for the process: what is proven is that a handle made after it is set restarts.
        OpenLdapTls.EnableRestartOnSignals();
        using var connection = new LdapConnection(new LdapDirectoryIdentifier("127.0.0.1", 1, false, false));

        Assert.True(OpenLdapTls.IsRestartEnabled(connection));
    }

    [Fact]
    public void EnablingItTwice_ChangesNothing()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        OpenLdapTls.EnableRestartOnSignals();
        OpenLdapTls.EnableRestartOnSignals();
        using var connection = new LdapConnection(new LdapDirectoryIdentifier("127.0.0.1", 1, false, false));

        Assert.True(OpenLdapTls.IsRestartEnabled(connection));
    }
}
