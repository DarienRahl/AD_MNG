using AdMng.Core.Security;

namespace AdMng.Core.Tests.Security;

/// <summary>The SDDL reader of the schema's defaultSecurityDescriptor (README U4b): aliases, flags, rights, object GUIDs.</summary>
public class SddlTests
{
    private const string DomainSid = "S-1-5-21-1004336348-1177238915-682003330";
    private const string RootSid = "S-1-5-21-111-222-333";

    [Fact]
    public void ParseDacl_ReadsTypesFlagsRightsObjectTypesAndAliases()
    {
        var aces = Sddl.ParseDacl(
            "O:DAG:DAD:AI(A;CI;RPWPCRCCDCLCLORCWOWDSDDTSW;;;DA)(OA;CIIO;CR;00299570-246d-11d0-a768-00aa006e0529;bf967aba-0de6-11d0-a285-00aa003049e2;AO)"
            + "(D;;0x40000;;;S-1-5-21-1004336348-1177238915-682003330-1105)(A;CIID;GA;;;EA)S:AI(AU;SA;CR;;;DU)",
            DomainSid,
            RootSid);

        Assert.Equal(4, aces.Count);
        Assert.Equal((byte)0x00, aces[0].Type);
        Assert.Equal(SecurityDescriptorAce.ContainerInheritFlag, aces[0].Flags);
        Assert.Equal(DirectoryAccessMask.FullControl, aces[0].Mask);
        Assert.Equal($"{DomainSid}-512", aces[0].Sid);

        Assert.Equal((byte)0x05, aces[1].Type);
        Assert.True(aces[1].IsInheritOnly);
        Assert.Equal(DirectoryAccessMask.ControlAccess, aces[1].Mask);
        Assert.Equal(new Guid("00299570-246d-11d0-a768-00aa006e0529"), aces[1].ObjectType);
        Assert.Equal(new Guid("bf967aba-0de6-11d0-a285-00aa003049e2"), aces[1].InheritedObjectType);
        Assert.Equal("S-1-5-32-548", aces[1].Sid);

        Assert.True(aces[2].IsDeny);
        Assert.Equal(DirectoryAccessMask.WriteDacl, aces[2].Mask);
        Assert.Equal($"{DomainSid}-1105", aces[2].Sid);

        Assert.True(aces[3].IsInherited);
        Assert.Equal(DirectoryAccessMask.GenericAll, aces[3].Mask);
        Assert.Equal($"{RootSid}-519", aces[3].Sid);
    }

    [Fact]
    public void ParseDacl_SkipsWhatItCannotResolve_AndNeverThrows()
    {
        // DA needs the domain SID; XX is no alias; ZZ is no right; the last ACE has a broken GUID.
        var aces = Sddl.ParseDacl("D:P(A;;RP;;;DA)(A;;RP;;;XX)(A;;ZZ;;;AU)(OA;;CR;not-a-guid;;AU)(A;;RPLCLORC;;;AU)", domainSid: null);

        var ace = Assert.Single(aces);
        Assert.Equal("S-1-5-11", ace.Sid);
        Assert.Equal(DirectoryAccessMask.MappedRead, ace.Mask);
        Assert.Empty(Sddl.ParseDacl(null, DomainSid));
        Assert.Empty(Sddl.ParseDacl("O:BAG:SY", DomainSid));
        Assert.Empty(Sddl.ParseDacl("D:(A;;RP;;;AU", DomainSid));
    }

    [Theory]
    [InlineData("DA", "S-1-5-21-1004336348-1177238915-682003330-512")]
    [InlineData("PA", "S-1-5-21-1004336348-1177238915-682003330-520")]
    [InlineData("EA", "S-1-5-21-111-222-333-519")]
    [InlineData("RO", "S-1-5-21-111-222-333-498")]
    [InlineData("BA", "S-1-5-32-544")]
    [InlineData("WD", "S-1-1-0")]
    [InlineData("s-1-5-11", "S-1-5-11")]
    public void ResolveSid_KnowsTheAliases(string token, string sid) => Assert.Equal(sid, Sddl.ResolveSid(token, DomainSid, RootSid));

    [Fact]
    public void Normalize_MapsTheGenericRights()
    {
        Assert.Equal(DirectoryAccessMask.FullControl, DirectoryAccessMask.Normalize(DirectoryAccessMask.GenericAll));
        Assert.Equal(DirectoryAccessMask.MappedWrite, DirectoryAccessMask.Normalize(DirectoryAccessMask.GenericWrite));
        Assert.True(DirectoryAccessMask.IsFullControl(0x000F01FF));
        Assert.False(DirectoryAccessMask.IsFullControl(0x000F00FF));
        Assert.True(DirectoryAccessMask.Has(DirectoryAccessMask.GenericWrite, DirectoryAccessMask.WriteProperty));
    }
}
