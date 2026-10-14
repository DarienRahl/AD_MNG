using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Testing.Fixtures.Metadata;

namespace AdMng.Watchdog.Core.Tests.Detection;

public class ReplicationMetadataXmlParserTests
{
    [Fact]
    public void ParsesWindowsValues_WithNulTerminatorsAndUnescapedAmpersands()
    {
        var metadata = ReplicationMetadataXmlParser.Parse(ReplicationMetadataFixtures.WindowsXmlValues, out var rejected);

        Assert.Equal(0, rejected);
        var department = metadata.Get("department");
        Assert.NotNull(department);
        Assert.Equal(2, department.Version);
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 7, 12, 1, TimeSpan.Zero), department.OriginatingTime);
        Assert.Equal(ReplicationMetadataFixtures.LabDcInvocationId, department.OriginatingInvocationId);
        Assert.Equal(184467, department.UsnOriginating);
        Assert.Equal(184467, department.UsnLocal);
        Assert.StartsWith("CN=NTDS Settings,CN=DC01,", department.OriginatingDsaDn, StringComparison.Ordinal);

        var password = metadata.Get("UNICODEPWD");
        Assert.NotNull(password);
        Assert.Equal(90211, password.UsnOriginating);
        Assert.Equal(184401, password.UsnLocal);
        Assert.Contains("CN=Kraków & Łódź", password.OriginatingDsaDn, StringComparison.Ordinal);

        Assert.Null(metadata.Get("pwdLastSet")!.OriginatingDsaDn);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<DS_REPL_ATTR_META_DATA><pszAttributeName>title</pszAttributeName></DS_REPL_ATTR_META_DATA>")]
    [InlineData("<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><DS_REPL_ATTR_META_DATA><pszAttributeName>&e;</pszAttributeName></DS_REPL_ATTR_META_DATA>")]
    [InlineData("<DS_REPL_ATTR_META_DATA><pszAttributeName>title</pszAttributeName><dwVersion>x</dwVersion><ftimeLastOriginatingChange>2026-10-14T07:12:01Z</ftimeLastOriginatingChange><uuidLastOriginatingDsaInvocationID>845842f4-6f6d-4e7a-a5cd-4f58276592e5</uuidLastOriginatingDsaInvocationID><usnOriginatingChange>1</usnOriginatingChange><usnLocalChange>1</usnLocalChange></DS_REPL_ATTR_META_DATA>")]
    public void MalformedValues_AreRejected(string value) => Assert.False(ReplicationMetadataXmlParser.TryParse(value, out _));

    [Fact]
    public void Parse_CountsRejectedValues()
    {
        var metadata = ReplicationMetadataXmlParser.Parse([.. ReplicationMetadataFixtures.WindowsXmlValues, "<broken"], out var rejected);

        Assert.Equal(1, rejected);
        Assert.Equal(3, metadata.Entries.Count);
    }

    [Fact]
    public void NewestAbove_PicksTheNewestEntryAboveTheWatermark()
    {
        var metadata = ReplicationMetadataXmlParser.Parse(ReplicationMetadataFixtures.WindowsXmlValues, out _);

        Assert.Equal("department", metadata.NewestAbove(["department", "unicodePwd"], 184400)!.Attribute);
        Assert.Equal("unicodePwd", metadata.NewestAbove(["unicodePwd"], 184400)!.Attribute);
        Assert.Null(metadata.NewestAbove(["department", "unicodePwd"], 184467));
    }
}
