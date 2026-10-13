using System.DirectoryServices.Protocols;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AdMng.Directory;

/// <summary>
/// Lab-only "accept any server certificate" for LDAPS / StartTLS on Linux and macOS.
/// </summary>
/// <remarks>
/// S.DS.P on non-Windows platforms sits on OpenLDAP's libldap and does not support
/// <see cref="LdapSessionOptions.VerifyServerCertificate"/>. libldap copies its global options into every new
/// handle, so the global <c>LDAP_OPT_X_TLS_REQUIRE_CERT</c> is set to <c>never</c> while exactly one
/// <see cref="LdapConnection"/> is created and restored immediately afterwards; a fresh TLS context
/// (<see cref="LdapSessionOptions.StartNewTlsSessionContext"/>) then makes the handle use its own, relaxed
/// settings. Other connections of the process keep verifying certificates. The alternative - the environment
/// variable <c>LDAPTLS_REQCERT=never</c> - only works when it is set before libldap initializes and affects the
/// whole process. Production deployments trust the DC certificate (system CA store or
/// <see cref="LdapSessionOptions.TrustedCertificatesDirectory"/>) instead.
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal static partial class OpenLdapTls
{
    private const string LibraryName = "libldap.so.2";
    private const int OptionRequireCertificate = 0x6006; // LDAP_OPT_X_TLS_REQUIRE_CERT
    private const int RequireCertificateNever = 0; // LDAP_OPT_X_TLS_NEVER
    private const int Success = 0;

    private static readonly Lock Gate = new();

    static OpenLdapTls()
    {
        // Same fallback as S.DS.P itself, so that both use the same libldap instance (shared global options).
        NativeLibrary.SetDllImportResolver(typeof(OpenLdapTls).Assembly, ResolveLibrary);
    }

    /// <summary>Creates a connection whose TLS layer does not validate the server certificate.</summary>
    /// <exception cref="PlatformNotSupportedException">libldap could not be configured.</exception>
    public static LdapConnection CreateWithoutCertificateValidation(Func<LdapConnection> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        LdapConnection connection;
        lock (Gate)
        {
            var previous = 0;
            try
            {
                if (GetIntOption(IntPtr.Zero, OptionRequireCertificate, ref previous) != Success)
                {
                    throw new PlatformNotSupportedException("Could not read the libldap TLS options.");
                }

                var never = RequireCertificateNever;
                if (SetIntOption(IntPtr.Zero, OptionRequireCertificate, ref never) != Success)
                {
                    throw new PlatformNotSupportedException("Could not relax libldap certificate validation.");
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                throw new PlatformNotSupportedException("libldap is not available; cannot disable certificate validation.", ex);
            }

            try
            {
                connection = factory();
            }
            finally
            {
                SetIntOption(IntPtr.Zero, OptionRequireCertificate, ref previous);
            }
        }

        try
        {
            // New per-handle TLS context built from the handle's (relaxed) options.
            connection.SessionOptions.StartNewTlsSessionContext();
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        return connection;
    }

    private static IntPtr ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LibraryName)
        {
            return IntPtr.Zero;
        }

        foreach (var candidate in new[] { LibraryName, "libldap-2.6.so.0", "libldap-2.5.so.0", "libldap-2.4.so.2", "libldap.dylib" })
        {
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    [LibraryImport(LibraryName, EntryPoint = "ldap_get_option")]
    private static partial int GetIntOption(IntPtr ld, int option, ref int value);

    [LibraryImport(LibraryName, EntryPoint = "ldap_set_option")]
    private static partial int SetIntOption(IntPtr ld, int option, ref int value);
}
