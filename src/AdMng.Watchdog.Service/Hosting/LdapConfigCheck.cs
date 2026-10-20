using System.Globalization;
using AdMng.Core.Directory;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// Start-up check of the private libldap configuration (stage B design 5.1, 5.2, 11 item 5 – exit code 2): with Kerberos
/// on Linux, S.DS.P cannot request signing and sealing, so <c>LDAPCONF</c> must carry <c>SASL_SECPROPS minssf=56</c> (389)
/// or <c>maxssf=0</c> (LDAPS – Windows DCs reject a SASL layer inside TLS), and no per-user <c>ldaprc</c> may override it.
/// libldap applies <c>LDAP&lt;option&gt;</c> environment variables after the files, so <c>LDAPSASL_SECPROPS</c> is checked
/// as the effective value when set; <c>LDAPNOINIT</c> (no configuration at all) is refused.
/// </summary>
public static class LdapConfigCheck
{
    /// <summary>The minimum SSF of a sealed GSSAPI session.</summary>
    public const int MinimumSsf = 56;

    /// <summary>The environment the check reads (tests pass their own).</summary>
    public sealed record Inputs
    {
        public string? LdapConf { get; init; }
        public string? SaslSecPropsVariable { get; init; }
        public string? NoInit { get; init; }
        public string? LdapRc { get; init; }
        public string? Home { get; init; }
        public string? WorkingDirectory { get; init; }

        public static Inputs FromEnvironment() => new()
        {
            LdapConf = Environment.GetEnvironmentVariable("LDAPCONF"),
            SaslSecPropsVariable = Environment.GetEnvironmentVariable("LDAPSASL_SECPROPS"),
            NoInit = Environment.GetEnvironmentVariable("LDAPNOINIT"),
            LdapRc = Environment.GetEnvironmentVariable("LDAPRC"),
            Home = Environment.GetEnvironmentVariable("HOME"),
            WorkingDirectory = Environment.CurrentDirectory,
        };
    }

    /// <summary>Null when the configuration enforces <paramref name="security"/>; otherwise the reason (value-free).</summary>
    public static string? Check(TransportSecurity security, Inputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (security is not (TransportSecurity.SignAndSeal or TransportSecurity.Tls))
        {
            return $"Directory.Security = {security} is not supported with Kerberos on Linux (SignAndSeal on 389 or Tls on 636).";
        }

        if (!string.IsNullOrEmpty(inputs.NoInit))
        {
            return "LDAPNOINIT is set: libldap would ignore LDAPCONF.";
        }

        if (!string.IsNullOrEmpty(inputs.LdapRc))
        {
            return "LDAPRC is set: a per-user ldaprc could override LDAPCONF.";
        }

        foreach (var rc in RcFiles(inputs))
        {
            if (File.Exists(rc))
            {
                return $"{rc} exists: a per-user ldaprc overrides LDAPCONF; remove it.";
            }
        }

        if (string.IsNullOrWhiteSpace(inputs.LdapConf))
        {
            return "LDAPCONF is not set (expected /etc/admng-watchdog/ldap.conf).";
        }

        string? fileValue;
        try
        {
            fileValue = LastValue(File.ReadLines(inputs.LdapConf), "SASL_SECPROPS");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "LDAPCONF is not readable.";
        }

        var secprops = string.IsNullOrWhiteSpace(inputs.SaslSecPropsVariable) ? fileValue : inputs.SaslSecPropsVariable;
        var properties = ParseSecProps(secprops);
        if (security == TransportSecurity.SignAndSeal)
        {
            return properties.TryGetValue("minssf", out var min) && min >= MinimumSsf
                ? null
                : $"SASL_SECPROPS of LDAPCONF lacks minssf={MinimumSsf.ToString(CultureInfo.InvariantCulture)} (or higher): the LDAP session could run unsealed.";
        }

        return properties.TryGetValue("maxssf", out var max) && max == 0
            ? null
            : "Directory.Security = Tls needs SASL_SECPROPS maxssf=0 in LDAPCONF (no SASL layer inside TLS).";
    }

    /// <summary><c>minssf=56,noanonymous</c> → { minssf: 56, noanonymous: -1 }.</summary>
    internal static Dictionary<string, int> ParseSecProps(string? value)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0)
            {
                result[part] = -1;
            }
            else if (int.TryParse(part.AsSpan(equals + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                result[part[..equals].Trim()] = number;
            }
        }

        return result;
    }

    /// <summary>The value of the last <paramref name="option"/> line of an ldap.conf (libldap: later lines win).</summary>
    internal static string? LastValue(IEnumerable<string> lines, string option)
    {
        string? value = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var split = line.IndexOfAny([' ', '\t']);
            if (split > 0 && string.Equals(line[..split], option, StringComparison.OrdinalIgnoreCase))
            {
                value = line[split..].Trim();
            }
        }

        return value;
    }

    private static IEnumerable<string> RcFiles(Inputs inputs)
    {
        foreach (var directory in new[] { inputs.Home, inputs.WorkingDirectory })
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            yield return Path.Combine(directory, "ldaprc");
            yield return Path.Combine(directory, ".ldaprc");
        }
    }
}
