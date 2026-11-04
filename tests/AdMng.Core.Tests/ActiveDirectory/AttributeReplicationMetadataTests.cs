using AdMng.Core.ActiveDirectory;

namespace AdMng.Core.Tests.ActiveDirectory;

public class AttributeReplicationMetadataTests
{
    private const string Dc02Ntds = "CN=NTDS Settings,CN=DC02,CN=Servers,CN=Kraków,CN=Sites,CN=Configuration,DC=contoso,DC=test";

    private static string Value(string attribute, int version, string time, string? dsa) =>
        "<DS_REPL_ATTR_META_DATA>\n"
        + $"\t<pszAttributeName>{attribute}</pszAttributeName>\n"
        + $"\t<dwVersion>{version}</dwVersion>\n"
        + $"\t<ftimeLastOriginatingChange>{time}</ftimeLastOriginatingChange>\n"
        + "\t<uuidLastOriginatingDsaInvocationID>2f0f0f0f-1111-2222-3333-444455556666</uuidLastOriginatingDsaInvocationID>\n"
        + "\t<usnOriginatingChange>41022</usnOriginatingChange>\n"
        + "\t<usnLocalChange>41022</usnLocalChange>\n"
        + $"\t<pszLastOriginatingDsaDN>{dsa}</pszLastOriginatingDsaDN>\n"
        + "</DS_REPL_ATTR_META_DATA>\n\0";

    [Fact]
    public void TryParse_ReadsNameVersionTimeAndOriginatingDsa_WithTrailingNul()
    {
        Assert.True(AttributeReplicationMetadata.TryParse(Value("lockoutTime", 7, "2026-11-03T07:34:01Z", Dc02Ntds), out var entry));

        Assert.Equal("lockoutTime", entry!.AttributeName);
        Assert.Equal(7, entry.Version);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 7, 34, 1, TimeSpan.Zero), entry.LastOriginatingChange);
        Assert.Equal(Dc02Ntds, entry.OriginatingDsaDn);
        Assert.Equal("CN=DC02,CN=Servers,CN=Kraków,CN=Sites,CN=Configuration,DC=contoso,DC=test", entry.OriginatingServerDn);
        Assert.Equal("lockoutTime", entry.ToString());
    }

    [Fact]
    public void TryParse_RepairsUnescapedAmpersandInTheDsaDn()
    {
        var dsa = "CN=NTDS Settings,CN=DC03,CN=Servers,CN=Łódź & okolice,CN=Sites,CN=Configuration,DC=contoso,DC=test";

        Assert.True(AttributeReplicationMetadata.TryParse(Value("lockoutTime", 2, "2026-11-03T07:34:01Z", dsa), out var entry));
        Assert.Equal(dsa, entry!.OriginatingDsaDn);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<DS_REPL_ATTR_META_DATA><pszAttributeName>lockoutTime</pszAttributeName>")]
    [InlineData("<!DOCTYPE x [<!ENTITY a 'b'>]><DS_REPL_ATTR_META_DATA><pszAttributeName>&a;</pszAttributeName></DS_REPL_ATTR_META_DATA>")]
    [InlineData("<DS_REPL_ATTR_META_DATA><pszAttributeName>lockoutTime</pszAttributeName><dwVersion>x</dwVersion><ftimeLastOriginatingChange>2026-11-03T07:34:01Z</ftimeLastOriginatingChange></DS_REPL_ATTR_META_DATA>")]
    public void TryParse_RejectsMalformedValues(string? value)
    {
        Assert.False(AttributeReplicationMetadata.TryParse(value, out var entry));
        Assert.Null(entry);
    }

    [Fact]
    public void TryParse_WithoutDsa_HasNoServer()
    {
        Assert.True(AttributeReplicationMetadata.TryParse(Value("lockoutTime", 1, "1601-01-01T00:00:00Z", null), out var entry));

        Assert.Null(entry!.OriginatingDsaDn);
        Assert.Null(entry.OriginatingServerDn);
    }

    [Fact]
    public void Find_PicksTheAttributeCaseInsensitively_AndSkipsBrokenValues()
    {
        var values = new[]
        {
            "not xml",
            Value("badPwdCount", 3, "2026-11-03T07:30:00Z", Dc02Ntds),
            Value("lockoutTime", 4, "2026-11-03T07:34:01Z", Dc02Ntds),
        };

        var entry = AttributeReplicationMetadata.Find(values, "LOCKOUTTIME");

        Assert.NotNull(entry);
        Assert.Equal(4, entry.Version);
        Assert.Null(AttributeReplicationMetadata.Find(values, "pwdLastSet"));
    }

    [Fact]
    public void TryParse_RejectsOversizedValues()
    {
        var value = Value("lockoutTime", 1, "2026-11-03T07:34:01Z", new string('x', AttributeReplicationMetadata.MaxValueLength));

        Assert.False(AttributeReplicationMetadata.TryParse(value, out _));
    }
}
