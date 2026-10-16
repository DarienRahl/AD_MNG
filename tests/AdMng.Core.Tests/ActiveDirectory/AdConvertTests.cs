using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Model;

namespace AdMng.Core.Tests.ActiveDirectory;

public class AdConvertTests
{
    [Fact]
    public void FileTime_RoundTripsAndTreatsSentinelsAsNever()
    {
        var when = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        var ft = AdConvert.ToFileTime(when);

        Assert.Equal(when, AdConvert.FromFileTime(ft));
        Assert.Null(AdConvert.FromFileTime(0));
        Assert.Null(AdConvert.FromFileTime(long.MaxValue));
        Assert.Null(AdConvert.FromFileTime("9223372036854775807"));
        Assert.Null(AdConvert.FromFileTime("not a number"));
    }

    [Theory]
    [InlineData("20261013141500.0Z", "2026-10-13T14:15:00+00:00")]
    [InlineData("20261013141500Z", "2026-10-13T14:15:00+00:00")]
    [InlineData("20261013141500.5Z", "2026-10-13T14:15:00.5+00:00")]
    [InlineData("20261013141500+0200", "2026-10-13T12:15:00+00:00")]
    public void GeneralizedTime_Parses(string value, string expected)
    {
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), AdConvert.FromGeneralizedTime(value));
    }

    [Fact]
    public void GeneralizedTime_Formats()
    {
        Assert.Equal("20261013141500.0Z", AdConvert.ToGeneralizedTime(new DateTimeOffset(2026, 10, 13, 16, 15, 0, TimeSpan.FromHours(2))));
    }

    [Fact]
    public void Sid_RoundTrips()
    {
        const string sid = "S-1-5-21-2051451492-2870577725-837575597-1105";
        var bytes = AdConvert.SidToBytes(sid);

        Assert.Equal(28, bytes.Length);
        Assert.Equal(sid, AdConvert.SidToString(bytes));
        Assert.Equal(1105u, AdConvert.GetRid(sid));
        Assert.Equal("S-1-5-32-544", AdConvert.SidToString(AdConvert.SidToBytes("S-1-5-32-544")));
    }

    [Fact]
    public void UnicodePwd_IsQuotedUtf16()
    {
        Assert.Equal(new byte[] { 0x22, 0, 0x61, 0, 0x22, 0 }, AdConvert.EncodeUnicodePwd("a"));
    }

    [Fact]
    public void GroupType_DecodesAndComposes()
    {
        Assert.Equal(GroupScope.Global, GroupTypes.GetScope(-2147483646));
        Assert.True(GroupTypes.IsSecurity(-2147483646));
        Assert.Equal(GroupScope.Universal, GroupTypes.GetScope(8));
        Assert.False(GroupTypes.IsSecurity(8));
        Assert.Equal(-2147483644, GroupTypes.Compose(GroupScope.DomainLocal, security: true));
        Assert.Equal(2, GroupTypes.Compose(GroupScope.Global, security: false));
    }

    [Theory]
    [InlineData("0000052D: SvcErr: DSID-031A1254, problem 5003 (WILL_NOT_PERFORM), data 0", 0x52D)]
    [InlineData("80090308: LdapErr: DSID-0C09050F, comment: AcceptSecurityContext error, data 775, v4f7c", 0x775)]
    [InlineData("00000005: SecErr: DSID-03152857, problem 4003 (INSUFF_ACCESS_RIGHTS), data 0", 0x5)]
    [InlineData("no code here", null)]
    public void ParseWin32Error_ExtractsAdCodes(string message, int? expected)
    {
        Assert.Equal(expected, DirectoryException.ParseWin32Error(message));
    }

    [Theory]
    [InlineData(53, 0x52D, DirectoryErrorKind.PasswordPolicy)]
    [InlineData(49, 0x52E, DirectoryErrorKind.InvalidCredentials)]
    [InlineData(49, 0x775, DirectoryErrorKind.AccountRestriction)]
    [InlineData(50, null, DirectoryErrorKind.AccessDenied)]
    [InlineData(51, null, DirectoryErrorKind.Busy)]
    [InlineData(81, null, DirectoryErrorKind.ServerUnavailable)]
    [InlineData(68, null, DirectoryErrorKind.AlreadyExists)]
    public void Classify_MapsResultCodes(int code, int? win32, DirectoryErrorKind expected)
    {
        Assert.Equal(expected, DirectoryException.Classify(code, win32));
    }

    [Theory]
    [InlineData(new[] { "top", "person", "organizationalPerson", "user", "computer" }, ObjectKind.Computer)]
    [InlineData(new[] { "top", "person", "organizationalPerson", "user" }, ObjectKind.User)]
    [InlineData(new[] { "top", "person", "organizationalPerson", "user", "inetOrgPerson" }, ObjectKind.InetOrgPerson)]
    [InlineData(new[] { "top", "group" }, ObjectKind.Group)]
    [InlineData(new[] { "top", "person", "organizationalPerson", "contact" }, ObjectKind.Contact)]
    [InlineData(new[] { "top", "organizationalUnit" }, ObjectKind.OrganizationalUnit)]
    [InlineData(new[] { "top", "container" }, ObjectKind.Container)]
    [InlineData(new[] { "top", "domain", "domainDNS" }, ObjectKind.Domain)]
    [InlineData(new[] { "top", "person", "organizationalPerson", "user", "computer", "msDS-GroupManagedServiceAccount" }, ObjectKind.GroupManagedServiceAccount)]
    public void ObjectKinds_ResolveMostSpecificClass(string[] classes, ObjectKind expected)
    {
        Assert.Equal(expected, ObjectKinds.FromObjectClasses(classes));
    }

    [Fact]
    public void DirectoryObject_ExposesTypedAccessors()
    {
        var obj = new DirectoryObject("CN=Jan,OU=IT,DC=x", [
            new AttributeValues("objectClass", ["top", "person", "organizationalPerson", "user"]),
            new AttributeValues("userAccountControl", ["514"]),
            new AttributeValues("msDS-User-Account-Control-Computed", ["16"]),
            new AttributeValues("member;range=0-1", ["CN=a,DC=x", "CN=b,DC=x"]),
            new AttributeValues("objectSid", [AdConvert.SidToBytes("S-1-5-21-1-2-3-1105")]),
            new AttributeValues("whenCreated", ["20261013141500.0Z"]),
        ]);

        Assert.Equal(ObjectKind.User, obj.Kind);
        Assert.True(obj.IsDisabled);
        Assert.True(obj.IsLockedOut);
        Assert.Equal(2, obj.GetStrings("member").Count);
        Assert.Equal("S-1-5-21-1-2-3-1105", obj.ObjectSid);
        Assert.Equal("Jan", obj.Name);
        Assert.Equal(2026, obj.GetGeneralizedTime("whenCreated")!.Value.Year);

        var updated = obj.With([new AttributeValues("userAccountControl", ["512"])]);
        Assert.False(updated.IsDisabled);
        Assert.True(obj.IsDisabled);
    }
}
