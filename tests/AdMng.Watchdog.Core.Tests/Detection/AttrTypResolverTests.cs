using AdMng.Core.Schema;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Testing.Fixtures.Metadata;

namespace AdMng.Watchdog.Core.Tests.Detection;

public class AttrTypResolverTests
{
    [Theory]
    [InlineData(0x00000014u, "2.5.4.20")]
    [InlineData(0x00090008u, "1.2.840.113556.1.4.8")]
    [InlineData(0x0009005Au, "1.2.840.113556.1.4.90")]
    [InlineData(0x000900DDu, "1.2.840.113556.1.4.221")]
    [InlineData(0x000980DDu, "1.2.840.113556.1.4.221")]
    [InlineData(0x0002008Du, "1.2.840.113556.1.2.141")]
    public void OidFromAttid_UsesTheDefaultPrefixTable(uint attrTyp, string oid) => Assert.Equal(oid, AttrTypResolver.OidFromAttid(attrTyp));

    [Theory]
    [InlineData("2.5.4.20", 0x00000014u)]
    [InlineData("1.2.840.113556.1.4.8", 0x00090008u)]
    [InlineData("1.2.840.113556.1.4.90", 0x0009005Au)]
    public void AttidFromOid_IsTheInverse(string oid, uint attrTyp) => Assert.Equal(attrTyp, AttrTypResolver.AttidFromOid(oid));

    [Fact]
    public void UnknownPrefix_IsNull()
    {
        Assert.Null(AttrTypResolver.OidFromAttid(0x00FF0001));
        Assert.Null(AttrTypResolver.AttidFromOid("1.3.6.1.4.1.99999.1"));
    }

    [Fact]
    public void Resolve_UsesMsDsIntIdFirst_ThenTheSchemaOids()
    {
        var schema = new SchemaCatalog(
        [
            new AttributeDefinition { LdapName = "telephoneNumber", AttributeId = "2.5.4.20" },
            new AttributeDefinition { LdapName = "unicodePwd", AttributeId = "1.2.840.113556.1.4.90" },
        ], []);
        var resolver = new AttrTypResolver(schema, new Dictionary<uint, string> { [0x9F3A1C2Bu] = "contoso-NumerKarty" });

        Assert.Equal("telephoneNumber", resolver.Resolve(0x00000014));
        Assert.Equal("unicodePwd", resolver.Resolve(0x0009005A));
        Assert.Equal("contoso-NumerKarty", resolver.Resolve(0x9F3A1C2B));
        Assert.Null(resolver.Resolve(0x00090008));
        Assert.Equal(ReplicationMetadataFixtures.SamAccountNameAttrTyp, AttrTypResolver.AttidFromOid("1.2.840.113556.1.4.221"));
    }
}
