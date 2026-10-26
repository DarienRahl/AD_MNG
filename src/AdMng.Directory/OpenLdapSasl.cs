using System.DirectoryServices.Protocols;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AdMng.Directory;

/// <summary>
/// libldap interop for SASL (Linux, macOS): the security strength factor (SSF) a bound connection negotiated, and the
/// per-handle SSF limits (fault injection in the lab tests). With Kerberos (SASL GSSAPI) the SSF is the strength of the
/// security layer: 0 = no layer (neither signed nor sealed), 1 = integrity only, ≥ 56 = confidentiality (sealed). S.DS.P
/// cannot report it (<see cref="LdapSessionOptions.Sealing"/> throws on Linux), so the session reads
/// <c>LDAP_OPT_X_SASL_SSF</c> (0x6104 – not 0x6109, which is the buffer size; AdMng Watchdog Linux design 5.1).
/// </summary>
[UnsupportedOSPlatform("windows")]
internal static partial class OpenLdapSasl
{
    /// <summary>The SSF a sealed (confidential) GSSAPI session must reach; libldap's <c>minssf=56</c>.</summary>
    public const int MinimumSealedSsf = 56;

    private const string LibraryName = "libldap.so.2";
    private const int OptionSaslSsf = 0x6104; // LDAP_OPT_X_SASL_SSF, ber_len_t, read-only
    private const int OptionSaslSsfMin = 0x6107; // LDAP_OPT_X_SASL_SSF_MIN, ber_len_t
    private const int OptionSaslSsfMax = 0x6108; // LDAP_OPT_X_SASL_SSF_MAX, ber_len_t
    private const int Success = 0;

    static OpenLdapSasl()
    {
        // The same libldap instance as S.DS.P (and the library name fallback of Debian 12's libldap-2.5).
        OpenLdapTls.EnsureLibraryResolver();
    }

    /// <summary>
    /// The negotiated SASL SSF of a connection after a SASL bind: 0 when no security layer was installed (libldap keeps
    /// no SASL socket context then and the option fails); null when libldap or the native handle cannot be reached.
    /// </summary>
    public static int? TryGetSecurityStrengthFactor(LdapConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return WithHandle(connection, static handle =>
        {
            nuint ssf = 0;
            return GetSizeOption(handle, OptionSaslSsf, ref ssf) == Success ? (int)Math.Min((ulong)ssf, int.MaxValue) : 0;
        });
    }

    /// <summary>
    /// Sets the SSF limits of one connection before its bind (they override <c>SASL_SECPROPS</c> of <c>LDAPCONF</c> for
    /// this handle only). Used by the lab tests to provoke a session below the minimum (also an unsatisfiable pair such
    /// as minimum 56 / maximum 0, which libldap must refuse).
    /// </summary>
    /// <returns>False when the options could not be set.</returns>
    public static bool TrySetSecurityStrengthLimits(LdapConnection connection, int minimum, int maximum)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentOutOfRangeException.ThrowIfNegative(minimum);
        ArgumentOutOfRangeException.ThrowIfNegative(maximum);
        return WithHandle(connection, handle =>
        {
            nuint min = (nuint)minimum;
            nuint max = (nuint)maximum;
            return SetSizeOption(handle, OptionSaslSsfMin, ref min) == Success && SetSizeOption(handle, OptionSaslSsfMax, ref max) == Success;
        }) ?? false;
    }

    private static T? WithHandle<T>(LdapConnection connection, Func<IntPtr, T> action)
        where T : struct
    {
        if (OpenLdapTls.HandleField?.GetValue(connection) is not SafeHandle { IsInvalid: false, IsClosed: false } handle)
        {
            return default;
        }

        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            return action(handle.DangerousGetHandle());
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ObjectDisposedException)
        {
            return default;
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    [LibraryImport(LibraryName, EntryPoint = "ldap_get_option")]
    private static partial int GetSizeOption(IntPtr ld, int option, ref nuint value);

    [LibraryImport(LibraryName, EntryPoint = "ldap_set_option")]
    private static partial int SetSizeOption(IntPtr ld, int option, ref nuint value);
}
