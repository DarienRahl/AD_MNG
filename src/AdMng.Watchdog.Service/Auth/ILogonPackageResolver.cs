using System.Security.Claims;

namespace AdMng.Watchdog.Service.Auth;

/// <summary>
/// The security package that really authenticated a caller the server only reports as <c>Negotiate</c> (HTTP.sys maps
/// every SPNEGO logon to that type, whether Kerberos or NTLM was used underneath).
/// </summary>
public interface ILogonPackageResolver
{
    /// <summary><c>"Kerberos"</c>, <c>"NTLM"</c>, another package name, or null when it cannot be determined.</summary>
    string? Resolve(ClaimsIdentity identity);
}
