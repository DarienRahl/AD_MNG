using System.Security.Cryptography;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Secrets;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Watchdog.Service.Tests.Hosting;

/// <summary>The POSIX start-up checks of the Linux service and their exit codes (stage B design 10, 11, 14.1). The
/// platform attribute only satisfies the analyzer; each Linux test skips elsewhere.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class LinuxStartupChecksTests
{
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    [Fact]
    public void Stat_ReportsOwnerModeAndType()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        using var directory = new TestDataDirectory();
        var file = directory.File("watchdog.db");
        File.WriteAllText(file, "x");
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        File.CreateSymbolicLink(directory.File("link"), file);

        var status = PosixPermissionCheck.Stat(file)!;
        var link = PosixPermissionCheck.Stat(directory.File("link"))!;

        Assert.Equal(PosixPermissionCheck.EffectiveUserId(), status.Uid);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, status.Mode);
        Assert.False(status.IsDirectory);
        Assert.True(PosixPermissionCheck.Stat(directory.Path)!.IsDirectory);
        Assert.True(link.IsSymbolicLink);
        Assert.Null(PosixPermissionCheck.Stat(directory.File("missing")));
        Assert.False(PosixPermissionCheck.HasPosixAcl(file));
    }

    [Fact]
    public void DataDirectory_MustBePrivate_ToTheServiceUser()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        using var directory = new TestDataDirectory();
        var data = PrivateDataDirectory(directory);
        var database = Path.Combine(data.Path, "watchdog.db");
        File.WriteAllText(database, "x");
        File.SetUnixFileMode(database, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var uid = PosixPermissionCheck.EffectiveUserId();

        Assert.Empty(PosixPermissionCheck.CheckDataDirectory(data.ProtectedPaths(), uid));

        File.SetUnixFileMode(database, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        File.SetUnixFileMode(data.KeysPath, Private | UnixFileMode.GroupExecute);
        File.CreateSymbolicLink(database + "-wal", "/tmp/elsewhere");

        Assert.Equal(["watchdog.db:mode", "watchdog.db-wal:symlink", "keys:mode"], PosixPermissionCheck.CheckDataDirectory(data.ProtectedPaths(), uid));
        Assert.Contains("watchdog.db:owner", PosixPermissionCheck.CheckDataDirectory(data.ProtectedPaths(), uid + 1));
    }

    [Theory]
    [InlineData("ext4", false)]
    [InlineData("xfs", false)]
    [InlineData("nfs4", true)]
    [InlineData("cifs", true)]
    [InlineData("fuse.sshfs", true)]
    [InlineData("v9fs", true)]
    [InlineData(null, false)]
    public void NetworkFileSystems_AreRecognised(string? type, bool network)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        Assert.Equal(network, PosixPermissionCheck.IsNetworkFileSystem(type));
    }

    [Fact]
    public void BootstrapFile_WritableByTheService_IsAProblem()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        using var directory = new TestDataDirectory();
        var file = directory.File("appsettings.json");
        File.WriteAllText(file, "{}");

        var problems = PosixPermissionCheck.CheckBootstrapFile(file);

        Assert.Contains("appsettings.json:writable", problems);
        Assert.Contains("folder:writable", problems);
        Assert.Empty(PosixPermissionCheck.CheckBootstrapFile(directory.File("missing.json")));
    }

    [Fact]
    public void Keytab_MustNotBeWritableByGroupOrOthers()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        using var directory = new TestDataDirectory();
        var keytab = directory.File("admng-wd.keytab");
        File.WriteAllBytes(keytab, [5, 2]);
        File.SetUnixFileMode(keytab, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);

        Assert.Equal(["keytab:missing"], PosixPermissionCheck.CheckKeytab(directory.File("missing")));
        Assert.Contains("keytab:mode", PosixPermissionCheck.CheckKeytab(keytab));
        Assert.Equal(PosixPermissionCheck.EffectiveUserId() == 0, !PosixPermissionCheck.CheckKeytab(keytab).Contains("keytab:owner"));
        Assert.Equal("/etc/x.keytab", StartupChecks.KeytabPath("FILE:/etc/x.keytab"));
        Assert.Equal(KerberosBootstrapOptions.DefaultKeytabPath, StartupChecks.KeytabPath(null));
    }

    [Fact]
    public void Hardening_ReadsUmaskAndCoreLimit_FromProc()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        using var directory = new TestDataDirectory();
        var status = directory.File("status");
        var limits = directory.File("limits");
        File.WriteAllText(status, "Name:\tAdMng.Watchdog\nUmask:\t0022\nState:\tS (sleeping)\n");
        File.WriteAllText(limits, "Limit                     Soft Limit           Hard Limit           Units\nMax core file size        unlimited            unlimited            bytes\n");

        Assert.Equal(0x12, PosixPermissionCheck.ReadUmask(status));
        Assert.True(PosixPermissionCheck.CoreDumpsEnabled(limits));
        Assert.Equal(["umask", "coreDumps"], PosixPermissionCheck.CheckHardening(0x12, coreDumps: true, directory.File("no-credentials")));
        Assert.Empty(PosixPermissionCheck.CheckHardening(PosixPermissionCheck.ExpectedUmask, coreDumps: false, directory.File("no-credentials")));
        Assert.NotNull(PosixPermissionCheck.ReadUmask());
        Assert.NotNull(PosixPermissionCheck.CoreDumpsEnabled());
    }

    [Fact]
    public async Task Production_WithAnOpenDataFolder_RefusesToStart_WithExitCode5()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        using var directory = new TestDataDirectory();
        File.SetUnixFileMode(directory.Path, Private | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        await using var factory = new WatchdogFactory(environment: "Production", dataDirectory: directory);

        Assert.NotNull(Record.Exception(() => factory.Services));

        Assert.Equal(StartupExitCodes.DataDirectory, factory.Terminator.ExitCode);
        Assert.Contains(factory.Log.Entries, e => e.EventId.Id == EventLogIds.StorageProblem && e.Message.Contains(":mode", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Production_WithoutTheCredential_RefusesToStart_WithExitCode9()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        using var directory = new TestDataDirectory();
        PrivateDataDirectory(directory);
        await using var factory = new WatchdogFactory(environment: "Production", dataDirectory: directory);

        Assert.NotNull(Record.Exception(() => factory.Services));

        Assert.Equal(StartupExitCodes.MissingCredential, factory.Terminator.ExitCode);
        Assert.Contains(factory.Log.Entries, e => e.EventId.Id == EventLogIds.FatalStartupCheck && e.Message.Contains("dp-key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Production_WithTheCredential_Starts_AndReportsItsVariant()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        using var directory = new TestDataDirectory();
        PrivateDataDirectory(directory);
        var variant = directory.File("dp-key.variant");
        File.WriteAllText(variant, HostCredentialKeys.Host);
        var key = DataProtectionMasterKey.FromBytes(RandomNumberGenerator.GetBytes(DataProtectionMasterKey.KeyLength));
        await using var factory = new WatchdogFactory(
            environment: "Production",
            dataDirectory: directory,
            services: services => services.AddSingleton(new DataProtectionKeySource(() => key, variant)));

        var health = factory.Services.GetRequiredService<IHealthRegistry>();

        Assert.Null(factory.Terminator.ExitCode);
        var check = Assert.Single(health.Checks, c => c.Code == HealthCheckCodes.HostCredentialKey);
        Assert.Equal(HealthState.Ok, check.State);
        Assert.Equal([HostCredentialKeys.Host], check.Args);
        Assert.Equal(HostCredentialKeys.Host, health.Host?.CredentialKey);
        Assert.DoesNotContain(health.Checks, c => c.Code == "storage.acl");
    }

    [Fact]
    public async Task Development_WithAnOpenDataFolder_IsAHealthWarning()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        using var directory = new TestDataDirectory();
        File.SetUnixFileMode(directory.Path, Private | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        await using var factory = new WatchdogFactory(dataDirectory: directory);

        var health = factory.Services.GetRequiredService<IHealthRegistry>();

        Assert.Equal(HealthState.Warning, Assert.Single(health.Checks, c => c.Code == "storage.acl").State);
    }

    [Fact]
    public async Task ShippedPlaceholderSids_RefuseToStart_WithExitCode2()
    {
        await using var factory = new WatchdogFactory(new Dictionary<string, string?>
        {
            ["Watchdog:Authorization:AdminSids:0"] = ServiceOptionsValidator.PlaceholderSid,
        });

        Assert.NotNull(Record.Exception(() => factory.Services));

        Assert.Equal(StartupExitCodes.InvalidSettings, factory.Terminator.ExitCode);
        Assert.Contains(factory.Log.Entries, e => e.Message.Contains("Replace the role group SIDs", StringComparison.Ordinal));
    }

    private static DataDirectory PrivateDataDirectory(TestDataDirectory directory)
    {
        File.SetUnixFileMode(directory.Path, Private);
        var data = new DataDirectory(directory.Path);
        data.Ensure();
        return data;
    }
}
