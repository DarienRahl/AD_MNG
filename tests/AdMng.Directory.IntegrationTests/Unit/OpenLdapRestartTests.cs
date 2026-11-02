using System.DirectoryServices.Protocols;
using System.Runtime.Versioning;

namespace AdMng.Directory.IntegrationTests.Unit;

/// <summary>
/// <c>LDAP_OPT_RESTART</c> through the libldap interop (reflection on S.DS.P's handle field, the libldap imports); the
/// effect against the lab: <c>Integration/SignalTests</c>.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class OpenLdapRestartTests
{
    [Fact]
    public void Restart_IsOffInLibldap_UntilEnabledOnTheHandle()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "libldap interop is only used on Linux and macOS.");
        using var connection = new LdapConnection(new LdapDirectoryIdentifier("127.0.0.1", 389), null, AuthType.Basic);
        using var other = new LdapConnection(new LdapDirectoryIdentifier("127.0.0.1", 389), null, AuthType.Basic);

        // libldap's default: a signal during a wait ends the request with LDAP_SERVER_DOWN.
        Assert.False(OpenLdapRestart.IsEnabled(connection));

        Assert.True(OpenLdapRestart.TryEnable(connection));

        Assert.True(OpenLdapRestart.IsEnabled(connection));
        Assert.False(OpenLdapRestart.IsEnabled(other)); // per handle: libldap's global defaults stay as they are
    }
}
