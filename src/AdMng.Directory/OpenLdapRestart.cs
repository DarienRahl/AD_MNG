using System.DirectoryServices.Protocols;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AdMng.Directory;

/// <summary>
/// libldap interop for Linux and macOS: <c>LDAP_OPT_RESTART</c>, so that a signal which interrupts libldap's wait for
/// the server makes it wait again instead of ending the request with LDAP_SERVER_DOWN (81) on a healthy connection.
/// </summary>
/// <remarks>
/// <para>
/// libldap waits for answers in <c>poll()</c>, which a signal handled by the process interrupts (<c>EINTR</c> – poll is
/// never restarted, whatever <c>SA_RESTART</c> says). Without the option libldap reports that as "server down", also
/// for a request the server already received and still carries out: an Add that "failed" so had created its object
/// (and repeating it would fail with entryAlreadyExists). In .NET such signals are routine: the exit of every child
/// process sends <c>SIGCHLD</c> to the process, by preference to the thread that started the child – a thread pool
/// thread that may by then wait in libldap (CI: "Add failed: ServerUnavailable (LDAP 81)" while other tests ran
/// ldapsearch or samba-tool; the Watchdog service runs smbclient). And the waits are long: S.DS.P passes a 2 x 32 bit
/// <c>struct timeval</c> to <c>ldap_result</c>, libldap reads 2 x 64 bit, which often turns even the zero timeout of
/// S.DS.P's asynchronous polling into a wait until the answer arrives.
/// </para>
/// <para>
/// With the option libldap repeats an interrupted wait for the rest of its timeout (also while connecting, in the TLS
/// handshake and in binds). It cannot come from <c>ldap.conf</c> and S.DS.P does not expose it, so
/// <see cref="LdapConnectionFactory"/> sets it on every handle before the bind. Windows: WinLDAP receives answers on
/// its own threads, without signals.
/// </para>
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal static partial class OpenLdapRestart
{
    private const string LibraryName = "libldap.so.2";
    private const int OptionRestart = 0x0009; // LDAP_OPT_RESTART: set with NULL (off) or any other pointer (on)
    private const int Success = 0;
    private const int Off = 0;
    private const int On = 1;
    private const int Unknown = 2;

    static OpenLdapRestart()
    {
        // The same libldap instance as S.DS.P.
        OpenLdapTls.EnsureLibraryResolver();
    }

    /// <summary>Makes libldap restart the waits of this connection that a signal interrupts (before the bind).</summary>
    /// <returns>False when libldap or the native handle cannot be reached; the option then stays off.</returns>
    public static bool TryEnable(LdapConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return OpenLdapTls.WithHandle(connection, static handle =>
        {
            var on = 1;
            return SetIntOption(handle, OptionRestart, ref on) == Success;
        }) ?? false;
    }

    /// <summary>The connection's <c>LDAP_OPT_RESTART</c>; null when it cannot be read.</summary>
    public static bool? IsEnabled(LdapConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var state = OpenLdapTls.WithHandle(connection, static handle =>
        {
            var value = 0;
            if (GetIntOption(handle, OptionRestart, ref value) != Success)
            {
                return Unknown;
            }

            return value == 0 ? Off : On; // libldap reports a boolean option that is on as -1
        });
        return state switch
        {
            On => true,
            Off => false,
            _ => null,
        };
    }

    [LibraryImport(LibraryName, EntryPoint = "ldap_get_option")]
    private static partial int GetIntOption(IntPtr ld, int option, ref int value);

    [LibraryImport(LibraryName, EntryPoint = "ldap_set_option")]
    private static partial int SetIntOption(IntPtr ld, int option, ref int value);
}
