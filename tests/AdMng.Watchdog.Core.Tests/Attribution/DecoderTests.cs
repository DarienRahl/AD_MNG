using AdMng.Core.ActiveDirectory;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Testing.Fixtures.SecurityEvents;

namespace AdMng.Watchdog.Core.Tests.Attribution;

public class DecoderTests
{
    [Theory]
    [InlineData("%%1792", "<value changed, but not displayed>")]
    [InlineData("%%1793", "<value not set>")]
    [InlineData("%%1794", "<never>")]
    [InlineData("%%1797", "All")]
    [InlineData("%%14674", "ValueAdded")]
    [InlineData("%%14675", "ValueDeleted")]
    [InlineData("%%14676", "AD DS")]
    [InlineData("%%14677", "AD LDS")]
    [InlineData("%%14678", "Yes")]
    [InlineData("%%14679", "No")]
    [InlineData("%%14680", "ValueAddedWithExpirationTime")]
    [InlineData("%%14681", "ValueDeletedWithExpirationTime")]
    [InlineData("%%14688", "ValueAutoDeletedWithExpirationTime")]
    [InlineData("%%9999", "%%9999")]
    [InlineData("50%", "50%")]
    [InlineData("zwykła wartość", "zwykła wartość")]
    public void InsertionStrings(string raw, string expected) => Assert.Equal(expected, InsertionStringDecoder.Decode(raw));

    [Fact]
    public void InsertionStrings_NullStaysNull() => Assert.Null(InsertionStringDecoder.Decode(null));

    [Fact]
    public void UacList_ReportsSamBits()
    {
        Assert.Equal((0x15u, 0u), InsertionStringDecoder.UacListBits("%%2080 %%2082 %%2084"));
        Assert.Equal((0x200u, 0x4u), InsertionStringDecoder.UacListBits("%%2050 %%2089"));
        Assert.Equal((0u, 0u), InsertionStringDecoder.UacListBits(null));
    }

    [Fact]
    public void MicrosoftSamples_VerifyTheAcbInterpretation()
    {
        // 4720: NewUacValue 0x15 = DISABLED | PWNOTREQ | NORMAL – the UserAccountControl list says the same bits were set.
        Assert.True(SamAccountControlDecoder.TryParse("0x15", out var created));
        Assert.Equal(["DISABLED", "PWNOTREQ", "NORMAL"], SamAccountControlDecoder.AcbNames(created));
        Assert.Equal(InsertionStringDecoder.UacListBits("%%2080 %%2082 %%2084").Set, created);
        Assert.Equal((uint)(UserAccountControl.AccountDisable | UserAccountControl.PasswordNotRequired | UserAccountControl.NormalAccount),
            SamAccountControlDecoder.ToLdap(created));

        // 4738: 0x15 → 0x211: PWNOTREQ cleared, PWNOEXP set – %%2050 (2048+2) and %%2089 (2080+9).
        Assert.True(SamAccountControlDecoder.TryParse("0x211", out var changed));
        var (set, cleared) = SamAccountControlDecoder.LdapChanges(created, changed);
        Assert.Equal((uint)UserAccountControl.DontExpirePassword, set);
        Assert.Equal((uint)UserAccountControl.PasswordNotRequired, cleared);
        Assert.Equal(["DONT_EXPIRE_PASSWORD"], SamAccountControlDecoder.LdapNames(set));
        Assert.Equal(["PASSWD_NOTREQD"], SamAccountControlDecoder.LdapNames(cleared));
        var list = InsertionStringDecoder.UacListBits("%%2050 %%2089");
        Assert.Equal(changed & ~created, list.Set);
        Assert.Equal(created & ~changed, list.Cleared);
    }

    [Theory]
    [InlineData(0x2000u, (uint)UserAccountControl.TrustedForDelegation)]
    [InlineData(0x10000u, (uint)UserAccountControl.DontRequirePreauth)]
    [InlineData(0x40000u, (uint)UserAccountControl.TrustedToAuthForDelegation)]
    [InlineData(0x100u, (uint)UserAccountControl.ServerTrustAccount)]
    [InlineData(0x80u, (uint)UserAccountControl.WorkstationTrustAccount)]
    [InlineData(0x800u, (uint)UserAccountControl.EncryptedTextPasswordAllowed)]
    [InlineData(0x8000u, (uint)UserAccountControl.UseDesKeyOnly)]
    public void AcbBits_MapToTheLdapBits(uint acb, uint ldap) => Assert.Equal(ldap, SamAccountControlDecoder.ToLdap(acb));

    [Theory]
    [InlineData("0x0", true, 0u)]
    [InlineData("0X15", true, 0x15u)]
    [InlineData("21", true, 21u)]
    [InlineData("", false, 0u)]
    [InlineData("0xZZ", false, 0u)]
    [InlineData(null, false, 0u)]
    public void TryParse(string? value, bool ok, uint expected)
    {
        Assert.Equal(ok, SamAccountControlDecoder.TryParse(value, out var acb));
        Assert.Equal(expected, acb);
    }

    [Fact]
    public void FixturesUseTheSampleDomain() =>
        Assert.All(SecurityEventFixtures.All, x => Assert.Contains(SecurityEventFixtures.Computer, x, StringComparison.Ordinal));
}
