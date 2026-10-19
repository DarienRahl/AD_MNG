using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace AdMng.Watchdog.Service.Auth;

/// <summary>
/// Reads the package from the caller's Windows logon token: first the token source (the Kerberos package names its
/// network-logon tokens <c>Kerberos</c>, NTLM <c>NtLmSsp</c>), then the logon session (<c>LsaGetLogonSessionData</c>,
/// what <see cref="WindowsIdentity.AuthenticationType"/> reports for a token without an explicit type). Null when neither
/// answers or the identity is not a Windows identity – the caller then treats the logon as "not Kerberos".
/// </summary>
public sealed partial class WindowsLogonPackageResolver : ILogonPackageResolver
{
    private const int TokenSourceClass = 7;   // TOKEN_INFORMATION_CLASS.TokenSource

    public string? Resolve(ClaimsIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!OperatingSystem.IsWindows() || identity is not WindowsIdentity windows)
        {
            return null;
        }

        return FromTokenSource(windows) ?? FromLogonSession(windows);
    }

    /// <summary>Maps a token source name to the package (null for anything else).</summary>
    public static string? PackageOfTokenSource(string? sourceName)
    {
        var name = sourceName?.TrimEnd('\0', ' ');
        if (string.Equals(name, KerberosOnlyEvents.Kerberos, StringComparison.OrdinalIgnoreCase))
        {
            return KerberosOnlyEvents.Kerberos;
        }

        return name is not null && name.StartsWith("NtLm", StringComparison.OrdinalIgnoreCase) ? "NTLM" : null;
    }

    [SupportedOSPlatform("windows")]
    private static string? FromTokenSource(WindowsIdentity identity)
    {
        try
        {
            var token = identity.AccessToken;   // owned by the identity: not disposed here
            if (!GetTokenInformation(token, TokenSourceClass, out var source, Marshal.SizeOf<TokenSourceNative>(), out _))
            {
                return null;
            }

            Span<byte> name = stackalloc byte[8];
            BitConverter.TryWriteBytes(name, source.SourceName);
            return PackageOfTokenSource(System.Text.Encoding.ASCII.GetString(name));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? FromLogonSession(WindowsIdentity identity)
    {
        try
        {
            var token = identity.AccessToken;   // owned by the identity: not disposed here
            using var probe = new WindowsIdentity(token.DangerousGetHandle());   // no type given: read from the logon session
            var package = probe.AuthenticationType;
            return string.IsNullOrEmpty(package) || string.Equals(package, "Negotiate", StringComparison.OrdinalIgnoreCase) ? null : package;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, out TokenSourceNative information, int length, out int returnLength);

    /// <summary>TOKEN_SOURCE: CHAR SourceName[8]; LUID SourceIdentifier.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenSourceNative
    {
        public ulong SourceName;
        public long SourceIdentifier;
    }
}
