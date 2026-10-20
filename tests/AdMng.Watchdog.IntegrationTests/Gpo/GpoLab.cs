using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.IntegrationTests.Infrastructure;
using AdMng.Watchdog.Keytab;
using AdMng.Watchdog.Service.Gpo;

namespace AdMng.Watchdog.IntegrationTests.Gpo;

/// <summary>
/// The SYSVOL side of the GPO content lab tests (stage B design 8, B5 acceptance) on the Samba lab: a private krb5.conf
/// (KDC = the DC host name) and smb-client.conf (the package's settings, state in the temporary folder), an
/// Administrator TGT for the test's own SYSVOL edits (smbclient <c>put</c>, as GPMC would write them), a fresh computer
/// account with a keytab written by <c>admng-watchdog-keytab init</c> (the service's identity), the reader under test, and
/// <c>samba-tool gpo create/del</c> for the test GPO. Everything lives in a temporary folder removed on dispose; the GPO is
/// deleted by the test.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed partial class GpoLab : IDisposable
{
    private const string SmbClient = "/usr/bin/smbclient";

    private GpoLab(string dc, string directory)
    {
        Dc = dc;
        Directory = directory;
        Krb5Config = Path.Combine(directory, "krb5.conf");
        SmbConfig = Path.Combine(directory, "smb-client.conf");
        StateDirectory = Path.Combine(directory, "samba");
        AdminCache = "FILE:" + Path.Combine(directory, "admin.ccache");
        ServiceCache = "FILE:" + Path.Combine(directory, "krb5cc");
        Keytab = Path.Combine(directory, "admng-wd.keytab");
    }

    public string Dc { get; }

    public string Directory { get; }

    public string Krb5Config { get; }

    public string SmbConfig { get; }

    public string StateDirectory { get; }

    public string AdminCache { get; }

    public string ServiceCache { get; }

    public string Keytab { get; }

    public static string Realm => WatchdogLab.DomainDns.ToUpperInvariant();

    public static void Require()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "smbclient and MIT Kerberos are the Linux path.");
        WatchdogLab.RequireLab();
        Assert.SkipUnless(File.Exists(SmbClient), "smbclient is not installed (apt-get install smbclient).");
        Assert.SkipUnless(SambaPython() is not null, "samba-tool is not usable (no Python with the samba module).");
        Assert.SkipUnless(File.Exists("/usr/bin/kinit"), "kinit is not installed (apt-get install krb5-user).");
    }

    /// <summary>Writes the configuration files and obtains the Administrator TGT.</summary>
    public static async Task<GpoLab> CreateAsync(string dc, CancellationToken cancellationToken)
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("admng-gpo-it-").FullName;
        var lab = new GpoLab(dc.ToLowerInvariant(), directory);
        await File.WriteAllTextAsync(lab.Krb5Config, $$"""
            [libdefaults]
                default_realm = {{Realm}}
                dns_lookup_kdc = false
                dns_lookup_realm = false
                dns_canonicalize_hostname = false
                rdns = false
                forwardable = false
                udp_preference_limit = 0
            [realms]
                {{Realm}} = {
                    kdc = {{lab.Dc}}
                    primary_kdc = {{lab.Dc}}
                    kpasswd_server = {{lab.Dc}}
                    admin_server = {{lab.Dc}}
                }
            [domain_realm]
                .{{WatchdogLab.DomainDns}} = {{Realm}}
                {{WatchdogLab.DomainDns}} = {{Realm}}
            """, cancellationToken);
        await File.WriteAllTextAsync(lab.SmbConfig, $"""
            [global]
                workgroup = {WatchdogLab.NetBiosName}
                realm = {Realm}
                security = ads
                client min protocol = SMB3_00
                client signing = required
                client ipc signing = required
                client smb encrypt = desired
                client use kerberos = required
                disable netbios = yes
                name resolve order = host
                lock directory = {lab.StateDirectory}
                state directory = {lab.StateDirectory}
                cache directory = {lab.StateDirectory}
                private dir = {lab.StateDirectory}
                log level = 0
            """, cancellationToken);
        var kinit = await RunAsync("/usr/bin/kinit", [WatchdogLab.AdminUser.Split('@')[0] + "@" + Realm], WatchdogLab.AdminPassword + "\n",
            lab.Environment(lab.AdminCache), cancellationToken);
        Assert.True(kinit.Exit == 0, "kinit as the lab administrator failed: " + kinit.Error);
        return lab;
    }

    /// <summary>KRB5_CONFIG and KRB5CCNAME for a child process.</summary>
    public Dictionary<string, string> Environment(string cache) => new(StringComparer.Ordinal)
    {
        ["KRB5_CONFIG"] = Krb5Config,
        ["KRB5CCNAME"] = cache,
    };

    /// <summary>A fresh computer account in the run's OU with a keytab at <see cref="Keytab"/>; returns its principal.</summary>
    public async Task<string> CreateServiceAccountAsync(LabFixture fixture, CancellationToken cancellationToken)
    {
        var otp = KeytabLab.OneTimePassword();
        var account = await KeytabLab.CreateAccountAsync(fixture, otp, cancellationToken);
        var state = Path.Combine(Directory, "keytab-state");
        System.IO.Directory.CreateDirectory(state);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(otp + "\n"));
        var host = new KeytabToolHost { Out = output, Error = output, Input = input, InputIsTerminal = false, ReplicationPollInterval = TimeSpan.FromSeconds(1) };
        var exit = KeytabTool.Run(
            ["init", "--password-stdin", "--principal", account.Principal, "--fqdn", account.Fqdn, "--krb5-config", Krb5Config,
                "--keytab", Keytab, "--state-dir", state, "--replication-timeout", "0"],
            host,
            null);
        Assert.True(exit == 0, output.ToString());
        return account.Principal;
    }

    /// <summary>The reader under test: real smbclient, the service account's TGT in <see cref="ServiceCache"/>.</summary>
    public (SmbClientSysvolReader Reader, Krb5TicketCache Tickets) CreateReader(string principal, IDirectorySessionProvider sessions, TimeSpan? ticketLifetime = null)
    {
        var tickets = new Krb5TicketCache(
            new Krb5TicketCacheOptions
            {
                Principal = principal,
                ClientKeytab = "FILE:" + Keytab,
                CredentialCache = ServiceCache,
                Krb5Config = Krb5Config,
                TicketLifetime = ticketLifetime,
            },
            TimeProvider.System);
        var reader = new SmbClientSysvolReader(
            new SmbClientProcess(new SmbClientProcessOptions { ExecutablePath = SmbClient, Environment = Environment(ServiceCache) }),
            new SmbClientSysvolOptions { ConfigFile = SmbConfig, StateDirectory = StateDirectory },
            tickets,
            sessions);
        return (reader, tickets);
    }

    // ---- the test's own SYSVOL and GPO edits (as the administrator) -------------------------------------------------------

    /// <summary><c>samba-tool gpo create</c>; returns the GPO GUID.</summary>
    public async Task<Guid> CreateGpoAsync(string displayName, CancellationToken cancellationToken)
    {
        var result = await SambaToolAsync(["gpo", "create", displayName], cancellationToken);
        var match = GpoCreated().Match(result.Output);
        Assert.True(result.Exit == 0 && match.Success, "samba-tool gpo create failed: " + result.Output + result.Error);
        return Guid.Parse(match.Groups[1].Value);
    }

    /// <summary><c>samba-tool gpo del</c> (best effort).</summary>
    public async Task DeleteGpoAsync(Guid gpo, CancellationToken cancellationToken) =>
        await SambaToolAsync(["gpo", "del", Folder(gpo)], cancellationToken);

    public static string Folder(Guid gpo) => gpo.ToString("B", CultureInfo.InvariantCulture).ToUpperInvariant();

    /// <summary>Writes <paramref name="content"/> to <paramref name="relativePath"/> of the GPO folder (parents created).</summary>
    public async Task PutAsync(Guid gpo, string relativePath, byte[] content, CancellationToken cancellationToken)
    {
        var local = Path.Combine(Directory, "upload-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(local, content, cancellationToken);
        try
        {
            var parts = relativePath.Split('/');
            for (var i = 1; i < parts.Length; i++)
            {
                await AdminSmbAsync(gpo, "mkdir \"" + string.Join('/', parts[..i]) + "\"", cancellationToken); // exists: ignored
            }

            var put = await AdminSmbAsync(gpo, "put \"" + local + "\" \"" + relativePath + "\"", cancellationToken);
            Assert.True(put.Exit == 0, "smbclient put failed: " + put.Output + put.Error);
        }
        finally
        {
            File.Delete(local);
        }
    }

    /// <summary>smbclient <c>ls</c> of a GPO folder with the service ccache as it is (no TGT refresh): exit code and stderr.</summary>
    public async Task<(int Exit, string Error)> ListWithServiceCacheAsync(Guid gpo, CancellationToken cancellationToken)
    {
        var result = await RunAsync(SmbClient,
            ["//" + Dc + "/SYSVOL", "-s", SmbConfig, "--use-kerberos=required", "-N", "-m", "SMB3", "-D", WatchdogLab.DomainDns + "/Policies/" + Folder(gpo), "-c", "ls"],
            null, Environment(ServiceCache), cancellationToken);
        return (result.Exit, result.Output + result.Error);
    }

    private Task<ProcessResult> AdminSmbAsync(Guid gpo, string command, CancellationToken cancellationToken) =>
        RunAsync(SmbClient,
            ["//" + Dc + "/SYSVOL", "-s", SmbConfig, "--use-kerberos=required", "-N", "-m", "SMB3", "-D", WatchdogLab.DomainDns + "/Policies/" + Folder(gpo), "-c", command],
            null, Environment(AdminCache), cancellationToken);

    private Task<ProcessResult> SambaToolAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        RunAsync(SambaPython()!, ["/usr/bin/samba-tool", .. arguments, "-H", "ldap://" + Dc, "--use-kerberos=required"], null,
            Environment(AdminCache), cancellationToken);

    private static string? SambaPython() => _sambaPython ??= FindSambaPython();

    private static string? _sambaPython;

    private static string? FindSambaPython()
    {
        foreach (var candidate in new[] { "/usr/bin/python3.12", "/usr/bin/python3" })
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            var start = new ProcessStartInfo(candidate) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("import samba");
            using var process = Process.Start(start)!;
            process.WaitForExit();
            if (process.ExitCode == 0)
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> arguments, string? input, IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start)!;
        await process.StandardInput.WriteAsync(input ?? string.Empty);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, await output, await error);
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [GeneratedRegex(@"created as (\{[0-9A-Fa-f-]{36}\})", RegexOptions.CultureInvariant)]
    private static partial Regex GpoCreated();

    private sealed record ProcessResult(int Exit, string Output, string Error);
}
