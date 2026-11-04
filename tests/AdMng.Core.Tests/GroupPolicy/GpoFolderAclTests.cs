using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.Security;
using AdMng.Testing;

namespace AdMng.Core.Tests.GroupPolicy;

public class GpoFolderAclTests
{
    private const string DomainAdmins = "S-1-5-21-1-2-3-512";
    private const string EnterpriseAdmins = "S-1-5-21-1-2-3-519";
    private const string Editors = "S-1-5-21-1-2-3-1130";
    private const string AuthenticatedUsers = "S-1-5-11";
    private const string CreatorOwner = "S-1-3-0";
    private const string System = "S-1-5-18";
    private const string EnterpriseDcs = "S-1-5-9";
    private const string PreWindows2000 = "S-1-5-32-554";

    /// <summary>The DACL samba-tool gpo create (and GPMC) gives a new GPO container.</summary>
    private static byte[] DefaultGpcDescriptor() => FakeSecurityDescriptor.Build(DomainAdmins, DomainAdmins,
        new FakeAce(FakeAce.AccessAllowed, 0x02, 0x000F00FF, DomainAdmins),
        new FakeAce(FakeAce.AccessAllowed, 0x02, 0x000F00FF, EnterpriseAdmins),
        new FakeAce(FakeAce.AccessAllowed, 0x0A, 0x000F00FF, CreatorOwner),
        new FakeAce(FakeAce.AccessAllowed, 0x02, 0x00020094, AuthenticatedUsers),
        new FakeAce(FakeAce.AccessAllowedObject, 0x02, 0x00000100, AuthenticatedUsers, GroupPolicySecurityFilter.ApplyGroupPolicyRight),
        new FakeAce(FakeAce.AccessAllowed, 0x02, 0x000F00FF, System),
        new FakeAce(FakeAce.AccessAllowed, 0x02, 0x00020094, EnterpriseDcs),
        new FakeAce(FakeAce.AccessAllowed, 0x02, 0x00020094, PreWindows2000),
        new FakeAce(FakeAce.AccessDenied, 0x02, 0x00000020, Editors));

    [Fact]
    public void ToFileMask_MapsDirectoryRightsAsSamba()
    {
        // Read (0x20094): read control + list + read property + list object → read & execute.
        Assert.Equal(0x001200A9, GpoFolderAcl.ToFileMask(0x00020094));

        // Edit settings, delete, modify security (0xF00FF) → full control of files minus write owner bits as Samba maps them.
        Assert.Equal(0x001F01FF, GpoFolderAcl.ToFileMask(0x000F00FF));

        // Write property only.
        Assert.Equal(0x00100116, GpoFolderAcl.ToFileMask(0x20));

        // Apply Group Policy (control access) has no file right.
        Assert.Equal(0, GpoFolderAcl.ToFileMask(0x100));
    }

    [Fact]
    public void MapAces_KeepsAllowAces_DropsDenyPreWindows2000AndObjectOnlyAces()
    {
        var aces = GpoFolderAcl.MapAces(DefaultGpcDescriptor())!;

        Assert.Equal(
        [
            new GpoFolderAcl.FileAce(DomainAdmins, 0x03, 0x001F01FF),
            new GpoFolderAcl.FileAce(EnterpriseAdmins, 0x03, 0x001F01FF),
            new GpoFolderAcl.FileAce(CreatorOwner, 0x0B, 0x001F01FF),
            new GpoFolderAcl.FileAce(AuthenticatedUsers, 0x03, 0x001200A9),
            new GpoFolderAcl.FileAce(System, 0x03, 0x001F01FF),
            new GpoFolderAcl.FileAce(EnterpriseDcs, 0x03, 0x001200A9),
        ], aces);
    }

    [Fact]
    public void Build_IsAProtectedDaclOnlyDescriptor_ThatParsesBack()
    {
        var descriptor = GpoFolderAcl.FromDirectoryDescriptor(DefaultGpcDescriptor())!;

        Assert.True(SecurityDescriptorSummary.TryParse(descriptor, out var summary));
        Assert.Null(summary!.Owner);
        Assert.Equal(6, summary.Aces.Count);
        Assert.All(summary.Aces, a => Assert.True(a.IsAllow));
        Assert.Equal(0x1000, BitConverter.ToUInt16(descriptor, 2) & 0x1000);
        Assert.True(GpoFolderAcl.IsConsistent(descriptor, DefaultGpcDescriptor()));
    }

    [Fact]
    public void IsConsistent_DetectsAMissingOrExtraAce()
    {
        var expected = GpoFolderAcl.MapAces(DefaultGpcDescriptor())!;
        var missing = GpoFolderAcl.Build([.. expected.Skip(1)]);
        var extra = GpoFolderAcl.Build([.. expected, new GpoFolderAcl.FileAce(Editors, 0x03, 0x001301BF)]);

        Assert.False(GpoFolderAcl.IsConsistent(missing, DefaultGpcDescriptor()));
        Assert.False(GpoFolderAcl.IsConsistent(extra, DefaultGpcDescriptor()));
        Assert.False(GpoFolderAcl.IsConsistent(null, DefaultGpcDescriptor()));
        Assert.Null(GpoFolderAcl.FromDirectoryDescriptor([1, 2, 3]));
    }
}
