using AdMng.Watchdog.Keytab.Native;

namespace AdMng.Watchdog.Keytab;

/// <summary>
/// The realm's KDCs from the tool's krb5.conf (design 4.8) and per-KDC profiles: an overlay file that pins
/// <c>kdc</c>, <c>primary_kdc</c> and the kpasswd server of the realm (subsection marked final, so the main file's KDC
/// list is not consulted) – MIT has no API to address one KDC. Overlays hold no secret and live in a private temporary
/// folder that is removed on dispose.
/// </summary>
internal sealed class KerberosConfig : IDisposable
{
    public const string DefaultPath = "/etc/admng-watchdog/krb5.conf";

    private readonly Dictionary<string, string> _overlays = new(StringComparer.OrdinalIgnoreCase);
    private string? _overlayDirectory;

    private KerberosConfig(string path, string realm, IReadOnlyList<string> kdcs, string primary, string? passwordServer)
    {
        Path = path;
        Realm = realm;
        Kdcs = kdcs;
        PrimaryKdc = primary;
        PasswordServer = passwordServer;
    }

    /// <summary>The profile path list (<c>KRB5_CONFIG</c> syntax).</summary>
    public string Path { get; }

    public string Realm { get; }

    /// <summary>The <c>kdc</c> relations of the realm, primary first.</summary>
    public IReadOnlyList<string> Kdcs { get; }

    /// <summary><c>primary_kdc</c> (or <c>master_kdc</c>), else the first <c>kdc</c> – the PDC emulator.</summary>
    public string PrimaryKdc { get; }

    /// <summary><c>kpasswd_server</c>, else <c>admin_server</c>; null = the KDC itself.</summary>
    public string? PasswordServer { get; }

    /// <summary><c>default_realm</c> of the configuration.</summary>
    public static string? DefaultRealm(string path)
    {
        EnsureReadable(path);
        return Krb5Context.ProfileValues(path, "libdefaults", "default_realm").FirstOrDefault();
    }

    /// <exception cref="InvalidOperationException">The configuration lists no KDC for the realm.</exception>
    public static KerberosConfig Load(string path, string realm)
    {
        EnsureReadable(path);
        var kdcs = Krb5Context.ProfileValues(path, "realms", realm, "kdc").Select(k => k.Trim()).Where(k => k.Length > 0).ToList();
        if (kdcs.Count == 0)
        {
            throw new InvalidOperationException($"The Kerberos configuration lists no kdc for the realm {realm} (DNS lookups are off).");
        }

        var primary = First(path, realm, "primary_kdc") ?? First(path, realm, "master_kdc") ?? kdcs[0];
        var passwordServer = First(path, realm, "kpasswd_server") ?? First(path, realm, "admin_server");
        var ordered = kdcs.Where(k => string.Equals(k, primary, StringComparison.OrdinalIgnoreCase)).Concat(
            kdcs.Where(k => !string.Equals(k, primary, StringComparison.OrdinalIgnoreCase))).ToList();
        if (!ordered.Contains(primary, StringComparer.OrdinalIgnoreCase))
        {
            ordered.Insert(0, primary);
        }

        return new KerberosConfig(path, realm, ordered, primary, passwordServer);
    }

    /// <summary>A profile path that sends every request for the realm to <paramref name="kdc"/> only.</summary>
    public string ProfileFor(string kdc)
    {
        if (_overlays.TryGetValue(kdc, out var existing))
        {
            return existing;
        }

        _overlayDirectory ??= Directory.CreateTempSubdirectory("admng-keytab-").FullName;
        var file = System.IO.Path.Combine(_overlayDirectory, $"kdc{_overlays.Count}.conf");
        var lines = new List<string>
        {
            "[realms]",
            $"    {Realm} = {{",
            $"        kdc = {kdc}",
            $"        primary_kdc = {kdc}",
        };
        if (PasswordServer is not null)
        {
            lines.Add($"        kpasswd_server = {PasswordServer}");
        }

        lines.Add("    }*");
        File.WriteAllLines(file, lines);
        var profile = file + ":" + Path;
        _overlays[kdc] = profile;
        return profile;
    }

    public void Dispose()
    {
        if (_overlayDirectory is not null)
        {
            try
            {
                Directory.Delete(_overlayDirectory, recursive: true);
            }
            catch (IOException)
            {
            }

            _overlayDirectory = null;
        }
    }

    private static string? First(string path, string realm, string relation) =>
        Krb5Context.ProfileValues(path, "realms", realm, relation).Select(v => v.Trim()).FirstOrDefault(v => v.Length > 0);

    private static void EnsureReadable(string path)
    {
        foreach (var file in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!File.Exists(file))
            {
                throw new FileNotFoundException($"The Kerberos configuration {file} does not exist (KRB5_CONFIG / --krb5-config).");
            }
        }
    }
}
