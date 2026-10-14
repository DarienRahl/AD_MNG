using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Tests.Infrastructure;

namespace AdMng.Watchdog.Service.Tests.Hosting;

/// <summary>Data-folder ACL and bootstrap-file guard (spec 19.5) – Windows only, skipped elsewhere.</summary>
public sealed class WindowsOnlyTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public void DataFolder_IsCreatedWithTheProtectedDacl()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows ACLs.");
        using var root = new TestDataDirectory();
        var path = Path.Combine(root.Path, "dane");

        DataDirectoryAcl.CreateProtected(path);

        Assert.Empty(DataDirectoryAcl.Verify([path]));
        Assert.True(new DirectoryInfo(path).GetAccessControl().AreAccessRulesProtected);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void UsersAccess_IsADeviation()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows ACLs.");
        using var root = new TestDataDirectory();
        var path = Path.Combine(root.Path, "dane");
        DataDirectoryAcl.CreateProtected(path);
        var info = new DirectoryInfo(path);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        info.SetAccessControl(security);

        var problems = DataDirectoryAcl.Verify([path]);

        Assert.Contains(problems, p => p.Contains("S-1-5-32-545", StringComparison.Ordinal));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void BootstrapFile_WritableByTheCurrentUser_IsDetected()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows ACLs.");
        using var root = new TestDataDirectory();
        var file = Path.Combine(root.Path, "appsettings.json");
        File.WriteAllText(file, "{}");

        Assert.True(BootstrapFileGuard.IsWritableByCurrentIdentity(file));
        Assert.False(BootstrapFileGuard.IsWritableByCurrentIdentity(Path.Combine(root.Path, "brak.json")));
    }
}
