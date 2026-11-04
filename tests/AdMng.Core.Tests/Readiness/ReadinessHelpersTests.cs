using AdMng.Core.Readiness;
using AdMng.Testing;

namespace AdMng.Core.Tests.Readiness;

/// <summary>The pure helpers of the readiness check: the folder ACL of Policies, DFSR or FRS, the clock skew, the functional levels.</summary>
public sealed class ReadinessHelpersTests
{
    private const string DomainSid = FakeDirectorySession.DefaultDomainSid;
    private const string JanKowalskiSid = DomainSid + "-1104";
    private const string DomainAdminsSid = DomainSid + "-512";
    private const string GroupPolicyCreatorOwnersSid = DomainSid + "-520";
    private const string AdministratorsSid = "S-1-5-32-544";
    private const string SystemSid = "S-1-5-18";
    private const string CreatorOwnerSid = "S-1-3-0";

    /// <summary>FILE_GENERIC_READ | FILE_GENERIC_EXECUTE (Read &amp; execute).</summary>
    private const int ReadAndExecute = 0x001200A9;

    /// <summary>Modify (read, write, execute, delete).</summary>
    private const int Modify = 0x001301BF;

    private const int FullControl = 0x001F01FF;

    /// <summary>The default ACL of <c>Policies</c> on a Windows Server DC (Authenticated Users read, the creators modify).</summary>
    private static byte[] PoliciesAcl(params FakeAce[] extra) => FakeSecurityDescriptor.Build(
        AdministratorsSid,
        null,
        [
            .. extra,
            new FakeAce(FakeAce.AccessAllowed, 0x02, ReadAndExecute, FolderWriteAccess.AuthenticatedUsersSid),
            new FakeAce(FakeAce.AccessAllowed, 0x02, FullControl, SystemSid),
            new FakeAce(FakeAce.AccessAllowed, 0x02, FullControl, AdministratorsSid),
            new FakeAce(FakeAce.AccessAllowed, 0x02, Modify, GroupPolicyCreatorOwnersSid),
            new FakeAce(FakeAce.AccessAllowed, 0x0A, unchecked((int)0x10000000), CreatorOwnerSid),
        ]);

    [Fact]
    public void PoliciesAcl_LetsAdministratorsAndCreatorsWrite_ButNotAnOrdinaryAccount()
    {
        var acl = PoliciesAcl();
        var administrator = FolderWriteAccess.NetworkToken(DomainSid + "-500", [DomainAdminsSid, AdministratorsSid]);
        var creator = FolderWriteAccess.NetworkToken(JanKowalskiSid, [DomainSid + "-513", GroupPolicyCreatorOwnersSid]);
        var helpdesk = FolderWriteAccess.NetworkToken(JanKowalskiSid, [DomainSid + "-513"]);

        Assert.Equal(FolderWriteVerdict.Granted, FolderWriteAccess.Evaluate(acl, administrator));
        Assert.Equal(FolderWriteVerdict.Granted, FolderWriteAccess.Evaluate(acl, creator));
        Assert.Equal(FolderWriteVerdict.NotGranted, FolderWriteAccess.Evaluate(acl, helpdesk));
    }

    [Fact]
    public void InheritOnlyCreatorOwner_GrantsNothingOnTheFolderItself()
    {
        // CREATOR OWNER (inherit only, GENERIC_ALL) is replaced by the creator on the children: it gives no account the folder.
        var acl = FakeSecurityDescriptor.Build(AdministratorsSid, null, new FakeAce(FakeAce.AccessAllowed, 0x0A, unchecked((int)0x10000000), JanKowalskiSid));

        Assert.Equal(FolderWriteVerdict.NotGranted, FolderWriteAccess.Evaluate(acl, FolderWriteAccess.NetworkToken(JanKowalskiSid, [])));
    }

    [Fact]
    public void GenericRights_AreMappedToTheFileRights()
    {
        var token = FolderWriteAccess.NetworkToken(JanKowalskiSid, []);

        Assert.Equal(FolderWriteVerdict.Granted, FolderWriteAccess.Evaluate(
            FakeSecurityDescriptor.Build(AdministratorsSid, null, new FakeAce(FakeAce.AccessAllowed, 0, 0x40000000, JanKowalskiSid)), token));
        Assert.Equal(FolderWriteVerdict.Granted, FolderWriteAccess.Evaluate(
            FakeSecurityDescriptor.Build(AdministratorsSid, null, new FakeAce(FakeAce.AccessAllowed, 0, 0x10000000, FolderWriteAccess.EveryoneSid)), token));
        Assert.Equal(FolderWriteVerdict.NotGranted, FolderWriteAccess.Evaluate(
            FakeSecurityDescriptor.Build(AdministratorsSid, null, new FakeAce(FakeAce.AccessAllowed, 0, unchecked((int)0x80000000), JanKowalskiSid)), token));
    }

    [Fact]
    public void ADenyBeforeTheAllow_TakesTheRightAway_ButNotADenyAfterIt()
    {
        var token = FolderWriteAccess.NetworkToken(JanKowalskiSid, [GroupPolicyCreatorOwnersSid]);
        var denyFirst = FakeSecurityDescriptor.Build(AdministratorsSid, null,
            new FakeAce(FakeAce.AccessDenied, 0, FolderWriteAccess.AddSubdirectory, JanKowalskiSid),
            new FakeAce(FakeAce.AccessAllowed, 0, Modify, GroupPolicyCreatorOwnersSid));
        var denyAfter = FakeSecurityDescriptor.Build(AdministratorsSid, null,
            new FakeAce(FakeAce.AccessAllowed, 0, Modify, GroupPolicyCreatorOwnersSid),
            new FakeAce(FakeAce.AccessDenied, 0, FolderWriteAccess.AddSubdirectory, JanKowalskiSid));

        Assert.Equal(FolderWriteVerdict.NotGranted, FolderWriteAccess.Evaluate(denyFirst, token));
        Assert.Equal(FolderWriteVerdict.Granted, FolderWriteAccess.Evaluate(denyAfter, token));
    }

    [Fact]
    public void OnlyAddFile_IsNotEnough_ForANewGpoFolder()
    {
        var acl = FakeSecurityDescriptor.Build(AdministratorsSid, null, new FakeAce(FakeAce.AccessAllowed, 0, FolderWriteAccess.AddFile, JanKowalskiSid));
        var token = FolderWriteAccess.NetworkToken(JanKowalskiSid, []);

        Assert.Equal(FolderWriteVerdict.NotGranted, FolderWriteAccess.Evaluate(acl, token));
        Assert.Equal(FolderWriteVerdict.Granted, FolderWriteAccess.Evaluate(acl, token, FolderWriteAccess.AddFile));
    }

    [Fact]
    public void MissingOrMalformedDescriptor_IsUnknown()
    {
        var token = FolderWriteAccess.NetworkToken(JanKowalskiSid, []);

        Assert.Equal(FolderWriteVerdict.Unknown, FolderWriteAccess.Evaluate(null, token));
        Assert.Equal(FolderWriteVerdict.Unknown, FolderWriteAccess.Evaluate([1, 2, 3], token));
        Assert.Equal(FolderWriteVerdict.Unknown, FolderWriteAccess.Evaluate(FakeSecurityDescriptor.Build(AdministratorsSid, null), token));
    }

    [Fact]
    public void NetworkToken_HoldsTheAccount_ItsGroups_AndTheWellKnownSids()
    {
        var token = FolderWriteAccess.NetworkToken(JanKowalskiSid, [DomainAdminsSid, " ", DomainAdminsSid.ToLowerInvariant()]);

        Assert.Equal(6, token.Count);
        Assert.Contains(JanKowalskiSid, token);
        Assert.Contains(DomainAdminsSid, token);
        Assert.Contains(FolderWriteAccess.EveryoneSid, token);
        Assert.Contains(FolderWriteAccess.NetworkSid, token);
        Assert.Contains(FolderWriteAccess.AuthenticatedUsersSid, token);
        Assert.Contains(FolderWriteAccess.ThisOrganizationSid, token);
    }

    [Theory]
    [InlineData(48, false, false, SysvolReplicationState.Dfsr)]
    [InlineData(48, true, true, SysvolReplicationState.Dfsr)]
    [InlineData(32, true, true, SysvolReplicationState.MigrationRedirected)]
    [InlineData(16, true, true, SysvolReplicationState.MigrationPrepared)]
    [InlineData(0, false, true, SysvolReplicationState.MigrationStarted)]
    [InlineData(0, true, false, SysvolReplicationState.Dfsr)]
    [InlineData(null, false, true, SysvolReplicationState.Frs)]
    [InlineData(null, true, true, SysvolReplicationState.Frs)]
    [InlineData(null, true, false, SysvolReplicationState.Dfsr)]
    [InlineData(null, false, false, SysvolReplicationState.Unknown)]
    [InlineData(64, true, false, SysvolReplicationState.Unknown)]
    public void SysvolReplication_IsToldFromTheMigrationStateAndTheObjects(int? flags, bool dfsrGroup, bool frsReplicaSet, SysvolReplicationState expected) =>
        Assert.Equal(expected, SysvolReplication.Classify(flags, dfsrGroup, frsReplicaSet));

    [Fact]
    public void SysvolReplication_ReadsTheObjectsOfTheDomainsSystemContainer()
    {
        const string Domain = "DC=contoso,DC=test";

        Assert.Equal("CN=DFSR-GlobalSettings,CN=System,DC=contoso,DC=test", SysvolReplication.GlobalSettingsDn(Domain));
        Assert.Equal("CN=Domain System Volume,CN=DFSR-GlobalSettings,CN=System,DC=contoso,DC=test", SysvolReplication.DfsrGroupDn(Domain));
        Assert.Equal("CN=Domain System Volume (SYSVOL share),CN=File Replication Service,CN=System,DC=contoso,DC=test", SysvolReplication.FrsReplicaSetDn(Domain));
    }

    [Theory]
    [InlineData(0, ClockSkewLevel.Ok)]
    [InlineData(-119, ClockSkewLevel.Ok)]
    [InlineData(120, ClockSkewLevel.Ok)]
    [InlineData(121, ClockSkewLevel.Warning)]
    [InlineData(-299, ClockSkewLevel.Warning)]
    [InlineData(300, ClockSkewLevel.Error)]
    [InlineData(-3600, ClockSkewLevel.Error)]
    public void ClockSkew_FailsAtTheKerberosLimit_AndWarnsBefore(int seconds, ClockSkewLevel expected) =>
        Assert.Equal(expected, ReadinessRules.ClassifySkew(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(10, FunctionalLevelVerdict.Ok, "Windows Server 2025")]
    [InlineData(7, FunctionalLevelVerdict.Ok, "Windows Server 2016")]
    [InlineData(6, FunctionalLevelVerdict.Old, "Windows Server 2012 R2")]
    [InlineData(3, FunctionalLevelVerdict.Old, "Windows Server 2008")]
    [InlineData(2, FunctionalLevelVerdict.TooOld, "Windows Server 2003")]
    [InlineData(8, FunctionalLevelVerdict.Ok, null)]
    public void FunctionalLevels_HaveNamesAndVerdicts(int level, FunctionalLevelVerdict verdict, string? name)
    {
        Assert.Equal(verdict, ReadinessRules.ClassifyLevel(level));
        Assert.Equal(name, ReadinessRules.LevelName(level));
    }
}
