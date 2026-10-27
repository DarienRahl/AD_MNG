using AdMng.Core.ActiveDirectory;
using AdMng.Core.Comparison;

namespace AdMng.Core.Tests.Comparison;

public class ComparisonAttributesTests
{
    /// <summary>A copy of the Watchdog's NoiseAttributes.Never (AdMng.Watchdog.Core is not referenced here).</summary>
    private static readonly string[] WatchdogNever =
    [
        "lastLogon", "lastLogonTimestamp", "lastLogoff", "logonCount", "badPwdCount", "badPasswordTime",
        "whenChanged", "uSNChanged", "uSNCreated", "dSCorePropagationData", "replPropertyMetaData",
        "msDS-ReplAttributeMetaData", "msDS-ReplValueMetaData", "modifyTimeStamp",
        "msDS-LastSuccessfulInteractiveLogonTime", "msDS-LastFailedInteractiveLogonTime", "msDS-FailedInteractiveLogonCount",
        "msDS-FailedInteractiveLogonCountAtLastSuccessfulLogon", "msDS-KeyVersionNumber", "msDS-RevealedUsers",
        "msDS-RevealedList", "msDS-ManagedPasswordId", "msDS-ManagedPasswordPreviousId", "msLAPS-PasswordExpirationTime",
        "ms-Mcs-AdmPwdExpirationTime", "dnsRecord", "dNSTombstoned", "replUpToDateVector", "repsFrom", "repsTo",
        "memberOf",
    ];

    [Fact]
    public void Volatile_IsASupersetOfTheWatchdogNoise_ExceptMemberOf()
    {
        // memberOf has its own tab ("Członkostwo"), so it is not a hidden attribute of the grid.
        Assert.All(WatchdogNever.Where(a => a != Attr.MemberOf), a => Assert.True(ComparisonAttributes.IsVolatile(a), a));
        Assert.False(ComparisonAttributes.IsVolatile(Attr.MemberOf));
    }

    [Theory]
    [InlineData("objectGUID")]
    [InlineData("objectSid")]
    [InlineData("sIDHistory")]
    [InlineData("pwdLastSet")]
    [InlineData("msDS-UserPasswordExpiryTimeComputed")]
    [InlineData("lockoutTime")]
    [InlineData("whenCreated")]
    [InlineData("msTPM-OwnerInformation")]
    [InlineData("MSTPM-TpmInformationForComputer")]
    [InlineData("uSNChanged;range=0-*")]
    public void IdentityTimesCountersAndTpm_AreVolatile(string attribute)
    {
        Assert.True(ComparisonAttributes.IsVolatile(attribute));
    }

    [Theory]
    [InlineData("msDS-User-Account-Control-Computed")]
    [InlineData("userAccountControl")]
    [InlineData("title")]
    public void AccountStateAndOrdinaryAttributes_StayVisible(string attribute)
    {
        Assert.False(ComparisonAttributes.IsVolatile(attribute));
    }

    [Fact]
    public void DnValuedAttributes_ComeFromEitherSchema_ElseFromTheKnownNames()
    {
        Assert.True(ComparisonAttributes.IsDnValued(Attr.Manager, ComparisonTestData.TestSchema, null));
        Assert.True(ComparisonAttributes.IsDnValued("secretary", null, null));
        Assert.False(ComparisonAttributes.IsDnValued(Attr.Title, ComparisonTestData.TestSchema, ComparisonTestData.TestSchema));
        Assert.True(ComparisonAttributes.IsAddress("proxyAddresses"));
        Assert.True(ComparisonAttributes.IsShownOnMembershipTabs(Attr.Member, isGroup: true));
        Assert.False(ComparisonAttributes.IsShownOnMembershipTabs(Attr.Member, isGroup: false));
    }
}
