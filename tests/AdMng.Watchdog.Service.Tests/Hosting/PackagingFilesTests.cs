using System.Text.RegularExpressions;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Secrets;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace AdMng.Watchdog.Service.Tests.Hosting;

/// <summary>The shipped Linux package tree (packaging/admng-watchdog) agrees with the code and the design (stage B design
/// 11–14, 16): unit settings the service relies on, exit codes, paths, users, the shipped configuration.</summary>
public sealed partial class PackagingFilesTests
{
    private const string Package = "packaging/admng-watchdog/";

    [Fact]
    public void ShippedConfiguration_BindsToTheLinuxDefaults_AndFailsCheckConfigUntilTheSidsAreReplaced()
    {
        var options = Bind(RepositoryFiles.ReadAllText(Package + "etc/appsettings.json"));

        Assert.Equal(ServiceOptions.DefaultLinuxDataDirectory, options.DataDirectory);
        Assert.Equal(CertificateOptions.DefaultLinuxPath, options.Hosting.Certificate.Path);
        Assert.Equal(CertificateOptions.DefaultLinuxKeyPath, options.Hosting.Certificate.KeyPath);
        Assert.Equal("https://admng-wd.contoso.com:8443", options.Hosting.Urls);
        Assert.Equal(["10.0.20.0/24"], options.Hosting.AllowedRemoteSubnets);
        Assert.Equal(1095, options.Retention.MinEventsDays);
        Assert.Equal(KerberosBootstrapOptions.DefaultKeytabStatusFile, options.Kerberos.KeytabStatusFile);
        Assert.Equal("CONTOSO.COM", options.Kerberos.Realm);

        var issues = ServiceOptionsValidator.Validate(options, "Production");
        var placeholder = Assert.Single(issues, i => i.Code == "placeholderSid");
        Assert.Equal(IssueSeverity.Error, placeholder.Severity);
        Assert.StartsWith("Replace the role group SIDs", placeholder.Message, StringComparison.Ordinal);

        var replaced = Bind(RepositoryFiles.ReadAllText(Package + "etc/appsettings.json")
            .Replace(ServiceOptionsValidator.PlaceholderSid, "S-1-5-21-1004336348-1177238915-682003330-1105", StringComparison.Ordinal));
        Assert.DoesNotContain(ServiceOptionsValidator.Validate(replaced, "Production"), i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void ServiceUnit_CarriesWhatTheServiceReliesOn()
    {
        var unit = Unit("admng-watchdog.service");

        Assert.Equal("notify", Single(unit, "Type"));
        Assert.Equal("main", Single(unit, "NotifyAccess"));
        Assert.Equal("120s", Single(unit, "WatchdogSec"));
        Assert.Equal("0", Single(unit, "StartLimitIntervalSec"));
        Assert.Equal("on-failure", Single(unit, "Restart"));
        Assert.Equal("0077", Single(unit, "UMask"));
        Assert.Equal("0", Single(unit, "LimitCORE"));
        Assert.Equal("admng-watchdog", Single(unit, "User"));
        Assert.Equal("admng-keytab", Single(unit, "SupplementaryGroups"));
        Assert.Equal("admng-watchdog-failed.service", Single(unit, "OnFailure"));
        Assert.Equal("/usr/lib/admng-watchdog/AdMng.Watchdog --contentRoot /usr/lib/admng-watchdog", Single(unit, "ExecStart"));
        Assert.Equal(DataProtectionMasterKey.CredentialName + ":/etc/admng-watchdog/credentials/dp-key.cred", Single(unit, "LoadCredentialEncrypted"));
        Assert.Equal("ConditionPathExists=" + KerberosBootstrapOptions.DefaultKeytabPath, unit.Single(l => l.StartsWith("ConditionPathExists=", StringComparison.Ordinal)));
        Assert.Contains("Environment=KRB5_KTNAME=FILE:" + KerberosBootstrapOptions.DefaultKeytabPath, unit);
        Assert.Contains("Environment=ASPNETCORE_ENVIRONMENT=Production", unit);
        Assert.Equal("admng-watchdog", Single(unit, "StateDirectory"));
        Assert.Equal(ServiceOptions.DefaultLinuxDataDirectory, "/var/lib/" + Single(unit, "StateDirectory"));
    }

    /// <summary>Every start-up exit code that needs a human (and systemd's 243/CREDENTIALS) stops the restarts.</summary>
    [Fact]
    public void ServiceUnit_StopsRestarting_OnExactlyTheHumanExitCodes()
    {
        int[] expected =
        [
            StartupExitCodes.InvalidSettings, StartupExitCodes.Migration, StartupExitCodes.Certificate, StartupExitCodes.DataDirectory,
            StartupExitCodes.BootstrapFile, StartupExitCodes.DevelopmentAuthentication, StartupExitCodes.PrivilegedServiceAccount,
            StartupExitCodes.MissingCredential, StartupExitCodes.Keytab, 243,
        ];

        var codes = Single(Unit("admng-watchdog.service"), "RestartPreventExitStatus").Split(' ').Select(int.Parse);

        Assert.Equal(expected, codes);
        Assert.DoesNotContain(StartupExitCodes.Fatal, codes);
    }

    [Fact]
    public void FailedUnit_LogsAtCrit()
    {
        var unit = Unit("admng-watchdog-failed.service");

        Assert.Equal("oneshot", Single(unit, "Type"));
        Assert.Contains("--priority=crit", Single(unit, "ExecStart"), StringComparison.Ordinal);
    }

    [Fact]
    public void SysusersAndTmpfiles_CreateTheUsersAndFoldersOfSection11()
    {
        var sysusers = Lines(Package + "sysusers.d/admng-watchdog.conf");
        var tmpfiles = Lines(Package + "tmpfiles.d/admng-watchdog.conf");

        Assert.Equal(
            ["u admng-watchdog - \"AdMng Watchdog\" /var/lib/admng-watchdog /usr/sbin/nologin", "g admng-keytab -", "m admng-watchdog admng-keytab", "m openwec admng-keytab"],
            sysusers);
        Assert.Contains("d " + ServiceOptions.DefaultLinuxDataDirectory + " 0700 admng-watchdog admng-watchdog -", tmpfiles);
        Assert.Contains("d /var/lib/admng-watchdog/keys 0700 admng-watchdog admng-watchdog -", tmpfiles);
        Assert.Contains("d /var/lib/admng-watchdog/backup 0700 admng-watchdog admng-watchdog -", tmpfiles);
        Assert.Contains("d /var/lib/admng-watchdog-keytab 0750 root admng-watchdog -", tmpfiles);
        Assert.Contains("z " + KerberosBootstrapOptions.DefaultKeytabPath + " 0640 root admng-keytab -", tmpfiles);
    }

    [Fact]
    public void MaintainerScripts_UseThePathsOfTheCode()
    {
        var functions = RepositoryFiles.ReadAllText(Package + "bin/admng-watchdog-functions");
        var postrm = RepositoryFiles.ReadAllText(Package + "debian/postrm");

        Assert.Contains("ADMNG_VARIANT=\"$ADMNG_ETC/dp-key.variant\"", functions, StringComparison.Ordinal);
        Assert.Equal("/etc/admng-watchdog/dp-key.variant", DataProtectionKeySource.DefaultVariantFile);
        Assert.Equal("/etc/admng-watchdog/appsettings.json", ConfigurationSetup.LinuxBootstrapFilePath);
        Assert.Contains("ADMNG_DEFAULT_CRT=\"$ADMNG_ETC/tls/admng-wd.crt\"", functions, StringComparison.Ordinal);
        Assert.Equal("/etc/admng-watchdog/tls/admng-wd.crt", CertificateOptions.DefaultLinuxPath);
        Assert.Contains("--with-key=host+tpm2 --tpm2-pcrs=", functions, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"systemd-creds encrypt[^\n]*--with-key=auto"), functions);
        Assert.Contains(HostCredentialKeys.Tpm2Host, functions, StringComparison.Ordinal);
        // Purge never deletes the history (ADR-0008).
        var purge = postrm[postrm.IndexOf("purge)", StringComparison.Ordinal)..];
        Assert.Contains("rm -rf /etc/admng-watchdog /var/lib/admng-watchdog-keytab /run/admng-watchdog", purge, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"rm -rf[^\n]*/var/lib/admng-watchdog(\s|$)"), purge);
    }

    /// <summary>The example ruleset equals the instructions' (3.3) byte for byte (design 16).</summary>
    [Fact]
    public void NftablesExample_EqualsTheInstructions()
    {
        // Line endings normalised: a Windows checkout (core.autocrlf) converts both files alike.
        var instructions = RepositoryFiles.ReadAllText("docs/WATCHDOG-LINUX.md").ReplaceLineEndings("\n");
        var block = NftBlock().Match(instructions);

        Assert.True(block.Success);
        Assert.Equal(block.Groups[1].Value + "\n", RepositoryFiles.ReadAllText(Package + "nftables/admng-watchdog.nft").ReplaceLineEndings("\n"));
    }

    private static ServiceOptions Bind(string json)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        return configuration.GetSection(ServiceOptions.SectionName).Get<ServiceOptions>()!;
    }

    private static List<string> Unit(string name) => Lines(Package + "systemd/" + name);

    private static List<string> Lines(string path) =>
        [.. RepositoryFiles.ReadAllText(path).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0 && !l.StartsWith('#'))];

    private static string Single(IEnumerable<string> unit, string key) =>
        unit.Single(l => l.StartsWith(key + "=", StringComparison.Ordinal))[(key.Length + 1)..];

    [GeneratedRegex("cat > /etc/nftables.conf <<'EOF'\n(.*?)\nEOF\n", RegexOptions.Singleline)]
    private static partial Regex NftBlock();
}
