using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("linux")]

namespace AdMng.Watchdog.Keytab;

/// <summary>
/// admng-watchdog-keytab (docs/dev/watchdog-linux-design.md 4.4). <c>KRB5_CONFIG</c> names the tool's krb5.conf (unit
/// environment, default <c>/etc/admng-watchdog/krb5.conf</c>); the tool builds its krb5 contexts from that file itself and
/// never uses the process credential cache. No top-level <c>Program</c> type: test projects reference this assembly next
/// to the service's.
/// </summary>
internal static class EntryPoint
{
    public static int Main(string[] args) =>
        KeytabTool.Run(args, KeytabToolHost.FromConsole(), Environment.GetEnvironmentVariable("KRB5_CONFIG"));
}
