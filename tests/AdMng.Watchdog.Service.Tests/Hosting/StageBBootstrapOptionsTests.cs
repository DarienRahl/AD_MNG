using System.Text;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Service.Api;
using AdMng.Watchdog.Service.Hosting;
using Microsoft.Extensions.Configuration;

using RoleOptions = AdMng.Watchdog.Service.Hosting.AuthorizationOptions;

namespace AdMng.Watchdog.Service.Tests.Hosting;

/// <summary>
/// The bootstrap settings of stage B (docs/dev/watchdog-linux-design.md 4.7, 5.3, 6.2, 6.3, 7, 8.3, 14.1, 14.2): the shape
/// of the shipped Linux <c>appsettings.json</c>, the defaults, which sections apply only at restart, and the exit codes.
/// </summary>
public sealed class StageBBootstrapOptionsTests
{
    /// <summary>The "Watchdog" section of the shipped /etc/admng-watchdog/appsettings.json (14.1), with real-looking SIDs.</summary>
    private const string Shipped = """
        {
          "Watchdog": {
            "DataDirectory": "/var/lib/admng-watchdog",
            "AllowInsecureDataDirectoryAcl": false,
            "AllowPrivilegedServiceAccount": false,
            "Authentication": "Negotiate",
            "Hosting": {
              "Server": "Kestrel",
              "Urls": "https://admng-wd.contoso.com:8443",
              "Certificate": { "Path": "/etc/admng-watchdog/tls/admng-wd.crt", "KeyPath": "/etc/admng-watchdog/tls/admng-wd.key" },
              "RequireKerberos": true,
              "AllowedRemoteSubnets": [ "10.0.20.0/24" ],
              "MonitoringSubnets": []
            },
            "Authorization": {
              "AdminSids": [ "S-1-5-21-1004336348-1177238915-682003330-1601" ],
              "ViewerSids": [ "S-1-5-21-1004336348-1177238915-682003330-1602" ],
              "DeniedSids": [],
              "ViewerDetailLevel": "Standard",
              "AllowTier0Suppression": false,
              "RoleCacheSeconds": 300,
              "RoleCacheMaxStaleSeconds": 3600
            },
            "Notifications": { "OAuthSmtpHosts": [ "smtp.office365.com" ] },
            "Directory": {
              "Domain": "contoso.com",
              "Servers": [ "DC01.contoso.com", "DC02.contoso.com" ],
              "PreferPdc": true,
              "Authentication": "CurrentUser",
              "Security": "SignAndSeal"
            },
            "Kerberos": {
              "Realm": "CONTOSO.COM",
              "ServicePrincipal": "ADMNG-WD$@CONTOSO.COM",
              "KeytabStatusFile": "/var/lib/admng-watchdog-keytab/status.json"
            },
            "Collector": {
              "Kind": "OpenWec",
              "OutputDirectory": "/var/lib/openwec/out",
              "FileName": "security.jsonl",
              "SubscriptionUuid": "4f7c2e1a-9b3d-4c55-8e21-6a0d9c7b3e10",
              "ProgressFile": "/run/admng-watchdog/wec-progress.json",
              "AdditionalSourceAddresses": [ "10.0.1.12" ],
              "MaxLogonRecordsPerDcPerMinute": 20000
            },
            "Retention": { "MinEventsDays": 1095 },
            "Sysvol": {
              "Enabled": true,
              "SmbClientPath": "/usr/bin/smbclient",
              "ConfigFile": "/etc/admng-watchdog/smb-client.conf",
              "InvocationTimeoutSeconds": 60
            }
          }
        }
        """;

    [Fact]
    public void TheShippedLinuxFile_BindsEverySetting()
    {
        var options = Bind(Shipped);

        Assert.False(options.AllowPrivilegedServiceAccount);
        Assert.Equal(CertificateOptions.DefaultLinuxPath, options.Hosting.Certificate.Path);
        Assert.Equal(CertificateOptions.DefaultLinuxKeyPath, options.Hosting.Certificate.KeyPath);
        Assert.Equal(300, options.Authorization.RoleCacheSeconds);
        Assert.Equal(3600, options.Authorization.RoleCacheMaxStaleSeconds);
        Assert.Equal(["DC01.contoso.com", "DC02.contoso.com"], options.Directory.Servers);
        Assert.Equal("CONTOSO.COM", options.Kerberos.Realm);
        Assert.Equal("ADMNG-WD$@CONTOSO.COM", options.Kerberos.ServicePrincipal);
        Assert.Equal(KerberosBootstrapOptions.DefaultKeytabStatusFile, options.Kerberos.KeytabStatusFile);
        Assert.Equal(CollectorKind.OpenWec, options.Collector.Kind);
        Assert.Equal(CollectorBootstrapOptions.DefaultSubscriptionUuid, options.Collector.SubscriptionUuid);
        Assert.Equal("/var/lib/openwec/out", options.Collector.OutputDirectory);
        Assert.Equal("security.jsonl", options.Collector.FileName);
        Assert.Equal("/run/admng-watchdog/wec-progress.json", options.Collector.ProgressFile);
        Assert.Equal(["10.0.1.12"], options.Collector.AdditionalSourceAddresses);
        Assert.Equal(20_000, options.Collector.MaxLogonRecordsPerDcPerMinute);
        Assert.Equal(1095, options.Retention.MinEventsDays);
        Assert.True(options.Sysvol.Enabled);
        Assert.Equal("/usr/bin/smbclient", options.Sysvol.SmbClientPath);
        Assert.Equal("/etc/admng-watchdog/smb-client.conf", options.Sysvol.ConfigFile);
        Assert.Equal(60, options.Sysvol.InvocationTimeoutSeconds);
    }

    [Fact]
    public void MissingSections_TakeTheDesignDefaults()
    {
        var options = Bind("""{ "Watchdog": { "Directory": { "Domain": "contoso.com" } } }""");

        Assert.False(options.AllowPrivilegedServiceAccount);
        Assert.Null(options.Hosting.Certificate.Path);
        Assert.Null(options.Hosting.Certificate.KeyPath);
        Assert.Empty(options.Directory.Servers);
        Assert.Null(options.Kerberos.Realm);
        Assert.Null(options.Kerberos.ServicePrincipal);
        Assert.Equal("/var/lib/admng-watchdog-keytab/status.json", options.Kerberos.KeytabStatusFile);
        Assert.Equal(Guid.Parse("4f7c2e1a-9b3d-4c55-8e21-6a0d9c7b3e10"), options.Collector.SubscriptionUuid);
        Assert.Empty(options.Collector.AdditionalSourceAddresses);
        Assert.Equal(20_000, options.Collector.MaxLogonRecordsPerDcPerMinute);
        Assert.Equal(1095, options.Retention.MinEventsDays);
        Assert.True(options.Sysvol.Enabled);
        Assert.Equal(300, options.Authorization.RoleCacheSeconds);
        Assert.Equal(3600, options.Authorization.RoleCacheMaxStaleSeconds);
    }

    [Fact]
    public void TheNewHostSections_ApplyAtRestart_WhileTheRoleCacheAppliesLive()
    {
        var startup = Bind(Shipped);
        var live = startup with
        {
            AllowPrivilegedServiceAccount = true,
            Kerberos = new KerberosBootstrapOptions { Realm = "FABRIKAM.COM", ServicePrincipal = "ŁOTR$@FABRIKAM.COM" },
            Collector = new CollectorBootstrapOptions { OutputDirectory = "/tmp/obce", SubscriptionUuid = Guid.NewGuid() },
            Retention = new RetentionBootstrapOptions { MinEventsDays = 7 },
            Sysvol = new SysvolBootstrapOptions { Enabled = false, SmbClientPath = "/tmp/smbclient" },
            Authorization = startup.Authorization with { RoleCacheSeconds = 60, RoleCacheMaxStaleSeconds = 600 },
        };

        var effective = ServiceOptionsFactory.Pin(live, startup);

        Assert.False(effective.AllowPrivilegedServiceAccount);
        Assert.Same(startup.Kerberos, effective.Kerberos);
        Assert.Same(startup.Collector, effective.Collector);
        Assert.Same(startup.Retention, effective.Retention);
        Assert.Same(startup.Sysvol, effective.Sysvol);
        Assert.Equal(["DC01.contoso.com", "DC02.contoso.com"], effective.Directory.Servers);
        Assert.Equal(60, effective.Authorization.RoleCacheSeconds);
        Assert.Equal(600, effective.Authorization.RoleCacheMaxStaleSeconds);
    }

    [Fact]
    public void LinuxExitCodes_AreStable()
    {
        Assert.Equal(8, StartupExitCodes.PrivilegedServiceAccount);
        Assert.Equal(9, StartupExitCodes.MissingCredential);
        Assert.Equal(10, StartupExitCodes.Keytab);
    }

    [Fact]
    public void LinuxHosts_SupportOnlyOpenWec()
    {
        IReadOnlyList<AttributionSourceKind> expected = OperatingSystem.IsLinux()
            ? [AttributionSourceKind.OpenWec, AttributionSourceKind.None]
            : [];

        Assert.Equal(expected, ConfigValidationService.SupportedAttributionSources);
    }

    [Fact]
    public void RoleOptions_DefaultToTheDesignCacheLifetimes()
    {
        var roles = new RoleOptions();

        Assert.Equal(300, roles.RoleCacheSeconds);
        Assert.Equal(3600, roles.RoleCacheMaxStaleSeconds);
    }

    private static ServiceOptions Bind(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        return configuration.GetSection(ServiceOptions.SectionName).Get<ServiceOptions>()
            ?? throw new InvalidOperationException("No Watchdog section.");
    }
}
